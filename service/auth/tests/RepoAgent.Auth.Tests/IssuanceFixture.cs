using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace RepoAgent.Auth.Tests;

internal sealed class IssuanceFixture : IAsyncDisposable
{
    public RevocationFixture Storage { get; } = new();
    public EnrolledDevice Device { get; private set; } = null!;
    public IssuanceClock Clock { get; } = new();
    public SigningTransport Signing { get; } = new();
    public GitHubTransport GitHub { get; } = new();
    private readonly DirectoryInfo keyDirectory = new(Path.Combine(Path.GetTempPath(), "issuer-keys-" + Guid.NewGuid().ToString("N")));
    public BrokerOptions Options => new(new Uri("https://example.table.core.windows.net"), Storage.TableName, RevocationFixture.Epoch, null, 120, 8, 15);
    public IssuanceStore Store(HttpPipelineTransport? transport = null, IssuanceLimits? limits = null) => new(Storage.CreateClient(transport), Options,
        DataProtectionProvider.Create(keyDirectory), Clock, limits);
    public InstallationTokenIssuer Issuer(HttpPipelineTransport? transport = null, IssuanceLimits? limits = null) => new(Store(transport, limits),
        new GitHubIssuance(new HttpClient(GitHub), Clock), new AppJwtSigner(Signing, Clock), Clock);

    public static async Task<IssuanceFixture> CreateAsync()
    {
        var f = new IssuanceFixture();
        await f.Storage.InitializeAsync();
        f.Device = await f.Storage.EnrollAsync();
        var d = (await f.Storage.Table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "device:" + f.Device.DeviceId)).Value;
        d["Acknowledged"] = true;
        d["AppId"] = IssuancePolicy.AppId;
        d["AppSlug"] = IssuancePolicy.AppSlug;
        d["GitHubHost"] = "github.com";
        d["PolicyVersion"] = IssuancePolicy.Version;
        d["Repository"] = "owner/project";
        d["InstallationId"] = 789L;
        d["Profile"] = "polling";
        d["Environment"] = "approval/environment";
        d["EnvironmentId"] = 987L;
        d["Variable"] = "APPROVAL";
        d["Challenge"] = new string('a', 43);
        var gate = await f.GateAsync();
        d["Marker"] = ApprovalMarker.Create(new(gate, d));
        await f.Storage.Table.UpdateEntityAsync(d, d.ETag, TableUpdateMode.Replace);
        f.GitHub.Marker = (string)d["Marker"];
        return f;
    }
    public Task<IssuanceResult> IssueAsync(CancellationToken caller = default, HttpPipelineTransport? transport = null) =>
        Issuer(transport).IssueAsync(Device.DeviceId, Device.Secret, caller);
    public async Task<TableEntity> GateAsync() => (await Storage.Table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "gate")).Value;
    public async Task<TableEntity> DeviceAsync() => (await Storage.Table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "device:" + Device.DeviceId)).Value;
    public async Task<List<TableEntity>> AttemptsAsync()
    {
        var rows = new List<TableEntity>();
        await foreach (var row in Storage.Table.QueryAsync<TableEntity>(e => e.PartitionKey == RevocationFixture.Partition && e.RowKey.CompareTo("attempt:") >= 0 && e.RowKey.CompareTo("attempt;") < 0))
            // Return attempts produced by the tested issuer; the offline unknown attempt remains charged separately.
            if (row.RowKey != "attempt:one") rows.Add(row);
        return rows;
    }
    public async ValueTask DisposeAsync()
    {
        await Storage.DisposeAsync();
        if (keyDirectory.Exists) keyDirectory.Delete(true);
    }
}
internal sealed class IssuanceClock : TimeProvider
{
    private DateTimeOffset current = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => current;
    public void Advance(TimeSpan amount) => current += amount;
}
internal sealed class SigningTransport : CryptographyClient
{
    public int Calls;
    public Func<Task>? Before;
    public override async Task<SignResult> SignAsync(SignatureAlgorithm algorithm, byte[] digest, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Calls);
        if (Before is not null) await Before();
        return CryptographyModelFactory.SignResult("https://example.vault.azure.net/keys/app/version", new byte[256], algorithm);
    }
}
internal sealed class GitHubTransport : HttpMessageHandler
{
    public string Marker = "";
    public int Reads, Mints, Revokes, VerifierMints;
    public Func<HttpRequestMessage, Task<HttpResponseMessage?>>? Before;
    public List<string> Requests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
        if (request.Method == HttpMethod.Get) Reads++;
        if (request.Method == HttpMethod.Post) Mints++;
        if (request.Method == HttpMethod.Delete) Revokes++;
        if (Before is not null && await Before(request) is { } replacement) return replacement;
        if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.NoContent);
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post)
        {
            var input = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
            var ids = input.GetProperty("repository_ids");
            if (ids.GetArrayLength() != 1 || ids[0].GetInt64() != 123456) throw new InvalidOperationException("Unexpected requested repository scope.");
            var permissions = input.GetProperty("permissions");
            var verifier = permissions.TryGetProperty("environments", out _);
            if (verifier) VerifierMints++;
            return Json(new { token = verifier ? "verifier-token" : "runtime-token-" + Mints,
                expires_at = DateTimeOffset.UtcNow.AddHours(1), repository_selection = "selected", permissions,
                repositories = new[] { new { id = 123456, full_name = "owner/project" } } }, HttpStatusCode.Created);
        }
        if (path.EndsWith("/installation", StringComparison.Ordinal)) return Json(new
        {
            id = 789, app_id = IssuancePolicy.AppId, app_slug = IssuancePolicy.AppSlug, suspended_at = (string?)null,
            permissions = new { discussions = "write", environments = "read", actions = "read" }
        });
        if (path == "/installation/repositories") return Json(new { total_count = 1, repositories = new[] { new { id = 123456, full_name = "owner/project" } } });
        if (path.EndsWith("/variables/APPROVAL", StringComparison.Ordinal)) return Json(new { name = "APPROVAL", value = Marker });
        return Json(new { id = 987, name = "approval/environment" });
    }
    public static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(value) };
}
