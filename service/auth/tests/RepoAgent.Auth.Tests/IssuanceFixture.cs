using Azure.Core.Pipeline;
using Azure.Data.Tables;

namespace RepoAgent.Auth.Tests;

internal sealed class IssuanceFixture : IAsyncDisposable
{
    public RevocationFixture Storage { get; } = new();
    public EnrolledDevice Device { get; private set; } = null!;
    public IssuanceClock Clock { get; } = new();
    public InMemoryGitHub GitHub { get; }
    public IssuanceFixture() => GitHub = new(Clock);
    public InMemorySecretProtection Secrets { get; } = new();
    public BrokerOptions Options => new(new Uri("https://example.table.core.windows.net"), Storage.TableName, RevocationFixture.Epoch, null, 120, 8, 15);
    public IssuanceStore Store(HttpPipelineTransport? transport = null, IssuanceLimits? limits = null) => new(Storage.CreateClient(transport), Options,
        Secrets, Clock, limits);
    public InstallationTokenIssuer Issuer(HttpPipelineTransport? transport = null, IssuanceLimits? limits = null) =>
        new(Store(transport, limits), new GitHubTokens(new GitHubRest(new HttpClient(GitHub))), GitHub, GitHub, Clock);

    public static async Task<IssuanceFixture> CreateAsync(TimeSpan? runtimeLifetime = null)
    {
        var f = new IssuanceFixture();
        if (runtimeLifetime is { } lifetime) f.GitHub.RuntimeLifetime = lifetime;
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
    public async ValueTask DisposeAsync() => await Storage.DisposeAsync();
}
internal sealed class IssuanceClock : TimeProvider
{
    private DateTimeOffset current = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => current;
    public void Advance(TimeSpan amount) => current += amount;
}
