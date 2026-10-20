using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class CapacityReclamationTests
{
    [Fact]
    public async Task Expired_pending_releases_occupancy_without_refunding_retained_or_token_capacity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var f = await EnrollmentFixture.CreateAsync();
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
        var reclamation = new BrokerCapacityReclamation(table, options, f.Clock);

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
    }
}
