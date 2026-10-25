using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed partial class CapacityReclamationTests
{
    [Fact]
    public async Task Redacted_unpublished_pending_releases_only_its_global_occupancy()
    {
        var ct = TestContext.Current.CancellationToken;
        var committed = false;
        var lostResponse = new ObservedTransport { After = message =>
        {
            if (ObservedTransport.IsBatch(message) && !committed)
            {
                committed = true;
                throw new HttpRequestException("Lost global admission response before repository publication");
            }
            return Task.CompletedTask;
        }};
        await using var f = await EnrollmentFixture.CreateAsync(lostResponse);
        var id = Guid.NewGuid().ToString("N");
        using var start = await f.Send("/v2/enrollments", new
        {
            idempotencyId = id, repositoryId = 123456,
            repository = "owner/project", variable = "APPROVAL", profile = "polling"
        });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, start.StatusCode);
        Assert.True(committed);
        var table = f.Storage.Table;
        var key = "pending:" + id;
        var ledger = (await table.GetEntityAsync<TableEntity>("global:enrollment-v1", key, cancellationToken: ct)).Value;
        Assert.False((await table.GetEntityIfExistsAsync<TableEntity>(RevocationFixture.Partition, key, cancellationToken: ct)).HasValue);
        Assert.False((await table.GetEntityIfExistsAsync<TableEntity>(RevocationFixture.Partition, "gate", cancellationToken: ct)).HasValue);
        var options = new BrokerOptions(new Uri("https://example.table.core.windows.net"),
            f.Storage.TableName, RevocationFixture.Epoch, null, 120, 8, 15);
        f.Clock.Advance(ledger.GetDateTimeOffset("PayloadPurgeAt")!.Value - f.Clock.GetUtcNow());

        // This is the real #78 absent-target tombstone, not a test-authored approximation.
        await new PayloadRedaction(new TableBrokerState(table), f.Clock)
            .RedactEnrollmentAsync(id, ledger.GetString("Generation")!, ct);
        var tombstone = (await table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, key, cancellationToken: ct)).Value;
        var gate = (await table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "gate", cancellationToken: ct)).Value;
        Assert.Equal("redacted", tombstone.GetString("State"));
        Assert.True(tombstone.GetBoolean("PayloadRedacted"));
        Assert.False(tombstone.ContainsKey("RepositoryCharged"));
        Assert.Equal(0L, gate.GetInt64("PendingCount"));
        Assert.Equal(0L, gate.GetInt64("OutstandingTokenCount"));
        var cleanup = new CapacityReclamation(new TableBrokerState(table), options.DeploymentEpoch, f.Clock);

        await cleanup.ReclaimEnrollmentAsync(id, ledger.GetString("Generation")!, ct);
        await cleanup.ReclaimEnrollmentAsync(id, ledger.GetString("Generation")!, ct);

        var quota = (await table.GetEntityAsync<TableEntity>("global:enrollment-v1", "quota", cancellationToken: ct)).Value;
        var directory = (await table.GetEntityAsync<TableEntity>("global:enrollment-v1", "repository:123456", cancellationToken: ct)).Value;
        gate = (await table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "gate", cancellationToken: ct)).Value;
        Assert.Equal(0L, quota.GetInt64("Count"));
        Assert.Equal(0L, directory.GetInt64("PendingCount"));
        Assert.Equal(0L, gate.GetInt64("PendingCount"));
        Assert.Equal(0L, gate.GetInt64("OutstandingTokenCount"));
        Assert.Equal(1L, quota.GetInt64("RetainedEnrollmentCount"));
        Assert.Equal(1L, quota.GetInt64("CandidateCount"));
        Assert.Equal(0, f.GitHub.Mints);
    }

    [Fact]
    public async Task Expired_pending_releases_occupancy_without_refunding_retained_or_token_capacity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await EnrollmentFixture.CreateAsync(settings: new() { ["Broker:RetainedEnrollmentLimit"] = "1" });
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        var request = new
        {
            idempotencyId = Guid.NewGuid().ToString("N"), repositoryId = 123456,
            repository = "owner/project", variable = "APPROVAL", profile = "polling"
        };
        using var start = await f.Send("/v2/enrollments", request);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var response = await start.Content.ReadFromJsonAsync<JsonElement>(ct);
        var pendingId = response.GetProperty("pendingId").GetString()!;
        var key = "pending:" + request.idempotencyId;
        var table = f.Storage.Table;
        var ledger = (await table.GetEntityAsync<TableEntity>("global:enrollment-v1", key, cancellationToken: ct)).Value;
        var quota = (await table.GetEntityAsync<TableEntity>("global:enrollment-v1", "quota", cancellationToken: ct)).Value;
        var directory = (await table.GetEntityAsync<TableEntity>("global:enrollment-v1", "repository:123456", cancellationToken: ct)).Value;
        var gate = (await table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "gate", cancellationToken: ct)).Value;
        Assert.Equal(1L, quota.GetInt64("Count"));
        Assert.Equal(1L, directory.GetInt64("PendingCount"));
        Assert.Equal(1L, gate.GetInt64("PendingCount"));
        Assert.Equal(1L, gate.GetInt64("OutstandingTokenCount"));
        Assert.Equal(1L, quota.GetInt64("RetainedEnrollmentCount"));

        f.Clock.Advance(ledger.GetDateTimeOffset("Deadline")!.Value - f.Clock.GetUtcNow());
        using var expired = await f.Send($"/v2/enrollments/{pendingId}/completion");
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        var options = new BrokerOptions(new Uri("https://example.table.core.windows.net"),
            f.Storage.TableName, RevocationFixture.Epoch, null, 120, 8, 15,
            new Uri("https://broker.example"));
        var reclamation = new CapacityReclamation(new TableBrokerState(table), options.DeploymentEpoch, f.Clock);

        await reclamation.ReclaimEnrollmentAsync(request.idempotencyId, ledger.GetString("Generation")!, ct);

        // These fresh reads verify committed occupancy, independently of the objects used to arrange it.
        quota = (await table.GetEntityAsync<TableEntity>("global:enrollment-v1", "quota", cancellationToken: ct)).Value;
        directory = (await table.GetEntityAsync<TableEntity>("global:enrollment-v1", "repository:123456", cancellationToken: ct)).Value;
        gate = (await table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "gate", cancellationToken: ct)).Value;
        Assert.Equal(0L, quota.GetInt64("Count"));
        Assert.Equal(0L, directory.GetInt64("PendingCount"));
        Assert.Equal(0L, gate.GetInt64("PendingCount"));
        Assert.Equal(1L, quota.GetInt64("RetainedEnrollmentCount"));
        Assert.Equal(1L, gate.GetInt64("OutstandingTokenCount"));
        using var retainedFull = await f.Send("/v2/enrollments", new
        {
            idempotencyId = Guid.NewGuid().ToString("N"), repositoryId = 123456,
            repository = "owner/project", variable = "APPROVAL", profile = "polling"
        });
        Assert.Equal(HttpStatusCode.TooManyRequests, retainedFull.StatusCode);
        Assert.Equal(1, f.GitHub.Mints);
    }
}
