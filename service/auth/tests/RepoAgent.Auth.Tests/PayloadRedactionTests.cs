using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed partial class PayloadRedactionTests(ITestOutputHelper output)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Abandoned_enrollment_loses_both_recovery_hashes_after_original_payload_deadline()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        var idempotencyId = Guid.NewGuid().ToString("N");
        using var start = await f.Send("/v2/enrollments", new
        {
            idempotencyId, repositoryId = 123456, repository = "owner/project",
            variable = "APPROVAL", profile = "polling"
        });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var response = await start.Content.ReadFromJsonAsync<JsonElement>(Token);
        var key = "pending:" + idempotencyId;
        var table = f.Storage.Table;
        var reservation = (await table.GetEntityAsync<TableEntity>("global:enrollment-v1", key, cancellationToken: Token)).Value;
        var pending = (await table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, key, cancellationToken: Token)).Value;
        var generation = reservation.GetString("Generation")!;
        var deadline = reservation.GetDateTimeOffset("Deadline")!.Value;
        var purgeAt = deadline.AddHours(24);
        Assert.Equal(response.GetProperty("pendingId").GetString(), reservation.GetString("PendingId"));
        Assert.False(string.IsNullOrEmpty(generation));
        Assert.Equal(generation, pending.GetString("Generation"));
        Assert.Equal(purgeAt, reservation.GetDateTimeOffset("PayloadPurgeAt"));
        Assert.Equal(purgeAt, pending.GetDateTimeOffset("PayloadPurgeAt"));
        Assert.True(reservation.ContainsKey("RecoveryHash"));
        Assert.True(pending.ContainsKey("RecoveryHash"));
        Assert.Equal(8, reservation.GetInt64("RowCredits"));
        var quota = (await table.GetEntityAsync<TableEntity>("global:enrollment-v1", "quota", cancellationToken: Token)).Value;
        Assert.Equal(1, quota.GetInt64("RetainedEnrollmentCount"));
        Assert.Equal(1, quota.GetInt64("CandidateCount"));
        output.WriteLine("Real v2 enrollment returned 200; both charged rows contain RecoveryHash. Original deadline: {0:O}; payload deadline: {1:O}.", deadline, purgeAt);

        f.Clock.Advance(purgeAt.AddTicks(1) - f.Clock.GetUtcNow());
        // Cleanup is constructed without any signing, provider or Data Protection dependency.
        await new BrokerPayloadRedaction(table, f.Clock)
            .RedactEnrollmentAsync(idempotencyId, generation, Token);

        var fresh = f.Storage.CreateClient();
        var redactedReservation = (await fresh.GetEntityAsync<TableEntity>(reservation.PartitionKey, key, cancellationToken: Token)).Value;
        var redactedPending = (await fresh.GetEntityAsync<TableEntity>(pending.PartitionKey, key, cancellationToken: Token)).Value;
        var retainedQuota = (await fresh.GetEntityAsync<TableEntity>(quota.PartitionKey, quota.RowKey, cancellationToken: Token)).Value;
        Assert.Equal(generation, redactedReservation.GetString("Generation"));
        Assert.Equal(generation, redactedPending.GetString("Generation"));
        Assert.Equal(deadline, redactedReservation.GetDateTimeOffset("Deadline"));
        Assert.Equal(deadline, redactedPending.GetDateTimeOffset("Deadline"));
        Assert.Equal(purgeAt, redactedReservation.GetDateTimeOffset("PayloadPurgeAt"));
        Assert.Equal(purgeAt, redactedPending.GetDateTimeOffset("PayloadPurgeAt"));
        Assert.Equal(8, redactedReservation.GetInt64("RowCredits"));
        Assert.Equal(quota.GetInt64("RetainedEnrollmentCount"), retainedQuota.GetInt64("RetainedEnrollmentCount"));
        Assert.Equal(quota.GetInt64("CandidateCount"), retainedQuota.GetInt64("CandidateCount"));
        output.WriteLine("Cleanup returned normally. Fresh persisted RecoveryHash presence: global={0}, repository={1}.",
            redactedReservation.ContainsKey("RecoveryHash"), redactedPending.ContainsKey("RecoveryHash"));
        Assert.Multiple(
            () => Assert.False(redactedReservation.ContainsKey("RecoveryHash"), "Global recovery hash must be physically absent after its original payload deadline."),
            () => Assert.False(redactedPending.ContainsKey("RecoveryHash"), "Repository recovery hash must be physically absent after its original payload deadline."));
    }
}
