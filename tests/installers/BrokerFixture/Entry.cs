using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Azure.Data.Tables;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using RepoAgent.Auth;

namespace HostEnrollmentFixture;

internal static class Entry
{
    internal static string DirectoryPath = null!;
    internal static bool SkipAckFault;
    internal static readonly object LogGate = new();
    internal static void Record(object value)
    {
        lock (LogGate) File.AppendAllText(Path.Combine(DirectoryPath, "broker.jsonl"), JsonSerializer.Serialize(value) + "\n");
    }
    public static async Task Main(string[] args)
    {
        DirectoryPath = args[0];
        SkipAckFault = args.Contains("no-ack-fault");
        var endpoint = args[1]; var tableName = args[2]; var port = int.Parse(args[3]);
        var table = new TableClient(new Uri(endpoint), tableName, new TableSharedKeyCredential("devstoreaccount1",
            "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="),
            new TableClientOptions { Retry = { MaxRetries = 0 } });
        if (args[4] == "create") {
            // A successful CREATE, not missing metadata, proves this fixture's
            // table is fresh. Provision both complete Int64 rows before traffic.
            await table.CreateAsync();
            var enrollment = new TableEntity("global:enrollment-v1", "quota") {
                ["RetentionSchemaVersion"]=1L, ["CandidateCount"]=0L,
                ["RetainedEnrollmentCount"]=0L, ["Count"]=0L };
            var issuance = new TableEntity("global:issuance-v1", "quota") {
                ["RetentionSchemaVersion"]=1L, ["RetainedIssuanceCount"]=0L,
                ["Window"]=0L, ["Admissions"]=0L, ["Reads"]=0L,
                ["Signs"]=0L, ["Mints"]=0L, ["Slots"]=0L };
            foreach (var seed in new[] { enrollment, issuance }) {
                await table.AddEntityAsync(seed);
                var verified = (await table.GetEntityAsync<TableEntity>(seed.PartitionKey, seed.RowKey)).Value;
                foreach (var property in seed.Where(p => p.Value is long))
                    if (!verified.TryGetValue(property.Key, out var value) || value is not long number || number != (long)property.Value)
                        throw new InvalidOperationException("Fresh fixture activation did not retain complete Int64 accounting.");
            }
            Record(new { activation="fresh-created-and-verified", tableName });
        } else if (args[4] == "resume") {
            // Restart never creates, seeds, resets or blesses an existing table.
            Record(new { activation="resume-existing-fixture-table", tableName });
        } else throw new InvalidOperationException("Explicit fixture table lifecycle required.");
        var origin = "https://localhost:" + port;
        var github = new GitHubBoundary();
        await using var factory = new BrokerFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string,string?> {
                ["Broker:TableEndpoint"]="https://example.table.core.windows.net", ["Broker:TableName"]=tableName,
                ["Broker:DeploymentEpoch"]="assembled-test", ["Broker:BrokerOrigin"]=origin }));
            builder.ConfigureServices(services => {
                services.RemoveAll<TableClient>(); services.AddSingleton(table);
                services.AddSingleton<IDataProtectionProvider>(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(DirectoryPath,"keys"))));
                services.AddSingleton(new IssuanceLimits());
                services.AddSingleton(new GitHubIssuance(new HttpClient(github), TimeProvider.System));
                services.AddSingleton(new AppJwtSigner(new SigningBoundary(), TimeProvider.System));
                services.AddSingleton<IssuanceStore>(); services.AddSingleton<InstallationTokenIssuer>();
                services.AddSingleton<EnrollmentStore>(); services.AddSingleton<EnrollmentService>();
                services.AddTransient<IStartupFilter, Observation>();
            });
        });
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(DirectoryPath,"cert.pfx"), "test-only");
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate)));
        factory.StartServer();
        Console.WriteLine("READY"); Console.Out.Flush();
        var command = await Console.In.ReadLineAsync();
        if (command == "clean") await table.DeleteAsync();
    }
}

internal sealed class BrokerFactory : WebApplicationFactory<global::Program>
{
    protected override IEnumerable<System.Reflection.Assembly> GetTestAssemblies() => [typeof(Entry).Assembly];
}

internal sealed class Observation : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
        app.Use(async (context, following) => {
            var path = context.Request.Path.Value!;
            var arrivedAt = DateTimeOffset.UtcNow;
            var state = Path.Combine(Entry.DirectoryPath,"private-state");
            if (path == "/v2/enrollments" && !File.Exists(Path.Combine(state,"recovery.json")))
                throw new InvalidOperationException("Recovery not persisted before start.");
            if (path.EndsWith("/acknowledgment")) {
                var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(state,"enrollment.json")));
                var recovery = JsonDocument.Parse(File.ReadAllText(Path.Combine(state,"recovery.json")));
                if (!recovery.RootElement.GetProperty("ackAttempted").GetBoolean() ||
                    receipt.RootElement.GetProperty("deviceSecret").GetString() is not { Length: >=43 })
                    throw new InvalidOperationException("Receipt not persisted before ack.");
                if (OperatingSystem.IsLinux() && (File.GetUnixFileMode(Path.Combine(state,"enrollment.json")) != (UnixFileMode.UserRead|UnixFileMode.UserWrite)))
                    throw new InvalidOperationException("Receipt not private.");
                Entry.Record(new { path, receiptBeforeAck=true });
                var fault = Path.Combine(Entry.DirectoryPath,"ack-response-lost");
                if (!Entry.SkipAckFault && !File.Exists(fault)) { File.WriteAllText(fault,"observed"); Entry.Record(new { path, status=0, receiptBeforeAck=true }); context.Abort(); return; }
            }
            await following();
            Entry.Record(new { path, status=context.Response.StatusCode });
            if (path.EndsWith("/installation-token") || path.EndsWith("/completion") || path.EndsWith("/acknowledgment")) {
                var table = context.RequestServices.GetRequiredService<TableClient>();
                foreach (var partition in new[] { "global:issuance-v1", "repository:123" }) {
                    var quota = await table.GetEntityAsync<TableEntity>(partition, "quota");
                    Entry.Record(new { quotaObservation=true, path, status=context.Response.StatusCode, partition,
                        observedAt=DateTimeOffset.UtcNow, counters=quota.Value.Where(p=>p.Key is "Window" or "Admissions" or "Reads" or "Signs" or "Mints" or "Slots").ToDictionary(p=>p.Key,p=>p.Value) });
                }
            }
            if (context.Response.StatusCode == 429 && path.EndsWith("/completion")) {
                var pendingId = path.Split('/')[3];
                var parts = pendingId.Split('_', 2);
                var table = context.RequestServices.GetRequiredService<TableClient>();
                var row = (await table.GetEntityAsync<TableEntity>("repository:"+parts[0], "pending:"+parts[1])).Value;
                using var recovery = JsonDocument.Parse(File.ReadAllText(Path.Combine(state,"recovery.json")));
                Entry.Record(new { floorObservation=true, arrivedAt,
                    workStartedAt=row.GetDateTimeOffset("WorkStartedAt"), nextWorkAt=row.GetDateTimeOffset("NextWorkAt"),
                    clientLastWorkAt=recovery.RootElement.GetProperty("lastWorkAt").GetString() });
            }
        });
        next(app);
    };
}

internal sealed class SigningBoundary : CryptographyClient
{
    public override Task<SignResult> SignAsync(SignatureAlgorithm algorithm, byte[] digest, CancellationToken cancellationToken=default)
    {
        Entry.Record(new { signing=true });
        return Task.FromResult(CryptographyModelFactory.SignResult("https://test.vault.azure.net/keys/app/version", new byte[256], algorithm));
    }
}

internal sealed class GitHubBoundary : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        object result;
        if (request.Method == HttpMethod.Delete) { Entry.Record(new { provider=request.Method+" "+path }); return new(HttpStatusCode.NoContent); }
        if (request.Method == HttpMethod.Post) {
            var input = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            var permissions=input.GetProperty("permissions");
            var ids=input.GetProperty("repository_ids");
            if (ids.GetArrayLength()!=1 || ids[0].GetInt64()!=123) throw new InvalidOperationException("Wrong provider repository scope");
            var verifier=permissions.TryGetProperty("agent_variables",out var approval);
            var clone = permissions.TryGetProperty("contents", out var contents);
            var hooks = permissions.TryGetProperty("repository_hooks", out var hookAccess);
            if (verifier ? approval.GetString()!="read" || permissions.EnumerateObject().Count()!=1 :
                permissions.EnumerateObject().Count()!=1+(clone?1:0)+(hooks?1:0) || permissions.GetProperty("discussions").GetString()!="write" ||
                (clone && contents.GetString()!="read") || (hooks && hookAccess.GetString()!="write"))
                throw new InvalidOperationException("Wrong provider token scope");
            Entry.Record(new { provider=request.Method+" "+path, permissions });
            result=new { token=verifier?"assembled-verifier-token":"assembled-runtime-token", expires_at=DateTimeOffset.UtcNow.AddHours(1), repository_selection="selected", permissions,
                repositories=new[]{new{id=123,full_name="example/project"}} };
            return new(HttpStatusCode.Created) { Content=JsonContent.Create(result) };
        }
        if (path.EndsWith("/installation")) result=new { id=789,app_id=5387625,app_slug="repo-agent-connector",suspended_at=(string?)null,
            permissions=new { discussions="write",agent_variables="read",contents="read",repository_hooks="write" } };
        else if(path=="/installation/repositories") result=new { total_count=1,repositories=new[]{new{id=123,full_name="example/project"}} };
        else if(path=="/repos/example/project/agents/variables") {
            using var model=JsonDocument.Parse(File.ReadAllText(Path.Combine(Entry.DirectoryPath,"github.json")));
            var variables=model.RootElement.GetProperty("variables").EnumerateObject().Select(p=>new{name=p.Name,value=p.Value.GetString()}).ToArray();
            result=new {total_count=variables.Length,variables};
        } else throw new InvalidOperationException("Unexpected provider endpoint "+path);
        Entry.Record(new { provider=request.Method+" "+path });
        return new(HttpStatusCode.OK) { Content=JsonContent.Create(result) };
    }
}
