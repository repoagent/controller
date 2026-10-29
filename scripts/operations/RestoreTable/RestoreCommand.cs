using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Azure.Identity;

namespace RepoAgent.RestoreTable;

public sealed record RestoreResult(string Operation, string RunId, string Table, int Rows, int Pages,
    bool TypedContentVerified, int ValidationPages = 0, bool SettlementProven = false, bool WorkerFenceProven = false);

public static class RestoreCommand
{
    // Published Azurite development credential; accepted ONLY for the literal loopback endpoint.
    // https://github.com/Azure/Azurite#default-storage-account
    public const string EmulatorKey = "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==";
    private const string Tenant = "3c356090-bfe9-459b-a2ac-064231707881";

    public static async Task<RestoreResult> RunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length < 5 || args[0] is not ("initialize" or "snapshot" or "restore" or "verify") || args.Length % 2 != 1)
            throw new InvalidDataException("Use a closed operator command and named arguments.");
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i += 2)
            if (args[i] is not ("--run-id" or "--directory" or "--local-table-endpoint") || !flags.TryAdd(args[i], args[i + 1]))
                throw new InvalidDataException("Unknown or duplicate argument.");
        var run = RestoreRun.Parse(flags["--run-id"]);
        var artifacts = new PrivateArtifacts(flags["--directory"]);
        var local = flags.TryGetValue("--local-table-endpoint", out var localEndpoint);
        var endpoint = local ? RestoreRun.LocalEndpoint(localEndpoint!) : run.Endpoint;
        var tableName = args[0] is "restore" or "verify" ? RestoreRun.TargetTable : RestoreRun.SourceTable;
        SnapshotDocument? snapshot = null;
        if (args[0] != "initialize") artifacts.CheckFence(run, endpoint);
        if (args[0] is "restore" or "verify") snapshot = SnapshotDocument.Read(artifacts.Read("snapshot.json"), run, endpoint);
        if (args[0] == "snapshot") artifacts.RequireAbsent("snapshot.json");
        var createdReceipt = args[0] == "initialize" ? "source-created.json" : "target-created.json";
        if (args[0] is "initialize" or "restore")
        {
            artifacts.RequireAbsent(createdReceipt);
            artifacts.RequireAbsent(args[0] == "initialize" ? "source-initialized.json" : "target-verified.json");
        }

        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = !local };
        using var http = new HttpClient(handler);
        using var transport = new HttpClientTransport(http);
        var options = new TableClientOptions { Transport = new ScopedTransport(transport, endpoint, tableName) };
        options.Retry.MaxRetries = 0;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(3);
        options.Diagnostics.IsLoggingEnabled = false;
        var service = local
            ? new TableServiceClient(endpoint, new TableSharedKeyCredential("devstoreaccount1", EmulatorKey), options)
            : new TableServiceClient(endpoint, new AzureCliCredential(new AzureCliCredentialOptions
            { TenantId = Tenant, ProcessTimeout = TimeSpan.FromSeconds(20) }), options);
        var table = service.GetTableClient(tableName);
        if (args[0] is "initialize" or "restore")
        {
            // CreateTable, never CreateIfNotExists. Existing/ambiguous targets stay untouched.
            var created = await service.CreateTableAsync(tableName, ct);
            if (created.GetRawResponse().Status is not (201 or 204) || created.Value.Name != tableName)
                throw new InvalidDataException("Fresh Table creation is unconfirmed.");
            artifacts.Write(createdReceipt, JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = 1,
                runId = run.Id,
                endpoint = endpoint.AbsoluteUri,
                table = tableName,
                responseStatus = created.GetRawResponse().Status,
                createdAt = DateTimeOffset.UtcNow
            }));
            var rows = snapshot?.Rows ?? InitialRows();
            foreach (var row in rows)
                await table.AddEntityAsync(Entity(row), ct);
            var observed = await CaptureAsync(table, run, endpoint, ct);
            if (observed.ContentSha256 != SnapshotDocument.Digest(rows))
                throw new InvalidDataException("Persisted typed content differs from the intended rows.");
            artifacts.Write(args[0] == "initialize" ? "source-initialized.json" : "target-verified.json",
                JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, rows = observed.Rows.Length, verified = true }));
            return new(args[0], run.Id, tableName, observed.Rows.Length, observed.Pages, true);
        }
        var current = await CaptureAsync(table, run, endpoint, ct);
        var validationPages = 0;
        if (args[0] == "verify")
        {
            if (current.ContentSha256 != snapshot!.ContentSha256) throw new InvalidDataException("Restored typed content differs.");
        }
        else
        {
            // A second complete pass detects observed drift. Only the operator's real fence can establish quiescence.
            var second = await CaptureAsync(table, run, endpoint, ct);
            validationPages = second.Pages;
            if (current.ContentSha256 != second.ContentSha256) throw new InvalidDataException("Snapshot observed changing content.");
            artifacts.Write("snapshot.json", current.Write());
        }
        return new(args[0], run.Id, tableName, current.Rows.Length, current.Pages, true, validationPages);
    }

    private static TableEntity Entity(SnapshotRow row)
    {
        row.Validate();
        var entity = new TableEntity(row.PartitionKey, row.RowKey);
        foreach (var (name, value) in row.Properties) entity.Add(name, value.Decode());
        return entity;
    }

    private static async Task<SnapshotDocument> CaptureAsync(TableClient table, RestoreRun run, Uri endpoint, CancellationToken ct)
    {
        var rows = new List<SnapshotRow>();
        var pages = 0;
        var bytes = 4096;
        await foreach (var page in table.QueryAsync<TableEntity>(maxPerPage: 32, cancellationToken: ct).AsPages(pageSizeHint: 32))
        {
            if (++pages > 1024) throw new InvalidDataException("Snapshot page budget exceeded.");
            foreach (var entity in page.Values)
            {
                if (rows.Count >= SnapshotDocument.MaxRows) throw new InvalidDataException("Snapshot row budget exceeded.");
                var row = SnapshotRow.From(entity.PartitionKey, entity.RowKey, entity
                    .Where(p => !SnapshotRow.IsSystem(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
                row.Validate();
                bytes = checked(bytes + JsonSerializer.SerializeToUtf8Bytes(row, JsonSerializerOptions.Web).Length + 1);
                if (bytes > SnapshotDocument.MaxBytes) throw new InvalidDataException("Snapshot byte budget exceeded.");
                rows.Add(row);
            }
        }
        var snapshot = SnapshotDocument.Create(run, endpoint, rows, pages);
        snapshot.Validate(run, endpoint);
        _ = snapshot.Write();
        return snapshot;
    }

    private static SnapshotRow[] InitialRows() =>
    [
        SnapshotRow.From("global:enrollment-v1", "quota", new Dictionary<string, object>
        { ["RetentionSchemaVersion"] = 1L, ["CandidateCount"] = 0L, ["RetainedEnrollmentCount"] = 0L, ["Count"] = 0L }),
        SnapshotRow.From("global:issuance-v1", "quota", new Dictionary<string, object>
        { ["RetentionSchemaVersion"] = 1L, ["RetainedIssuanceCount"] = 0L, ["Window"] = 0L,
          ["Admissions"] = 0L, ["Reads"] = 0L, ["Signs"] = 0L, ["Mints"] = 0L, ["Slots"] = 0L })
    ];

    private sealed class ScopedTransport(HttpPipelineTransport inner, Uri endpoint, string table) : HttpPipelineTransport
    {
        public override Request CreateRequest() => inner.CreateRequest();
        public override void Process(HttpMessage message)
        {
            Validate(message.Request);
            inner.Process(message);
        }
        public override ValueTask ProcessAsync(HttpMessage message)
        {
            Validate(message.Request);
            return inner.ProcessAsync(message);
        }
        private void Validate(Request request)
        {
            if (!RestoreRun.AllowsRequest(endpoint, table, request.Uri.ToUri(), request.Method.Method))
                throw new InvalidDataException("Transport refused an out-of-scope Table request.");
        }
    }
}
