using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed partial class CapacityReclamationTests
{
    [Theory]
    [InlineData("polling")]
    [InlineData("polling-clone")]
    public async Task Same_enrollment_recovers_after_cleanup_fences_an_unsent_verifier(string profile)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var (id, request, ledger) = await FencedEnrollment(f, profile);
        var before = await Read(f, RevocationFixture.Partition, "pending:" + id);
        var old = await Read(f, RevocationFixture.Partition, ledger.GetString("AttemptRowKey")!);
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));

        using var retry = await f.Send("/v2/enrollments", request);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        var status = await retry.Content.ReadFromJsonAsync<JsonElement>(Token);
        f.GitHub.Marker = status.GetProperty("marker").GetString()!;
        var pending = status.GetProperty("pendingId").GetString()!;
        Assert.Equal(before.GetString("PendingId"), pending);
        var after = await Read(f, before.PartitionKey, before.RowKey);
        foreach (var field in new[] { "DeviceId", "Challenge", "Deadline", "CreatedAt", "Epoch", "Generation" })
            Assert.Equal(before[field], after[field]);
        Assert.NotEqual(old.GetString("OperationId"), after.GetString("Attempt"));
        var audit = await Read(f, old.PartitionKey, old.RowKey);
        Assert.Equal("unsent", audit.GetString("SettlementReason"));
        Assert.Equal(old.GetDateTimeOffset("JwtExpiresAt"), audit.GetDateTimeOffset("JwtExpiresAt"));
        Assert.True(audit.GetBoolean("CapacityReleased"));
        Assert.Equal(2L, (await Read(f, "global:issuance-v1", "quota")).GetInt64("RetainedIssuanceCount"));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var completion = await f.Send($"/v2/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var receipt = await completion.Content.ReadFromJsonAsync<JsonElement>(Token);
        var device = receipt.GetProperty("enrollment").GetProperty("deviceId").GetString()!;
        var secret = receipt.GetProperty("enrollment").GetProperty("deviceSecret").GetString()!;
        Assert.Equal(before.GetString("DeviceId"), device);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var ack = await f.Send($"/v2/devices/{device}/acknowledgment",
            new { completionId = receipt.GetProperty("completionId").GetString() }, secret);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var version = profile == "polling-clone" ? 2 : 1;
        using var runtime = await f.Send($"/v{version}/devices/{device}/installation-token", secret: secret);
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
    }

    [Theory]
    [InlineData("capacity")]
    [InlineData("reason")]
    [InlineData("send")]
    [InlineData("clock")]
    [InlineData("expired")]
    public async Task Retrying_settled_verifier_requires_positive_unsent_proof_and_live_enrollment(string barrier)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var (id, request, ledger) = await FencedEnrollment(f);
        var old = await Read(f, RevocationFixture.Partition, ledger.GetString("AttemptRowKey")!);
        if (barrier == "capacity") old.Remove("CapacityReleased");
        if (barrier == "reason") old["SettlementReason"] = "revoked";
        if (barrier == "send") old["SendClaimedAt"] = old["ReservedAt"];
        if (barrier == "clock") old.Remove("SettledAt");
        if (barrier != "expired") await f.Storage.Table.UpdateEntityAsync(old, old.ETag, TableUpdateMode.Replace, Token);
        var pending = await Read(f, RevocationFixture.Partition, "pending:" + id);
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(barrier == "expired" ? 15 : 1));

        using var retry = await f.Send("/v2/enrollments", request);

        Assert.Equal(barrier == "expired" ? HttpStatusCode.Gone : HttpStatusCode.ServiceUnavailable, retry.StatusCode);
        Assert.Equal(0, f.GitHub.Mints);
        Assert.Single(await IssuanceLedgers(f));
        Assert.Equal(pending.ETag, (await Read(f, pending.PartitionKey, pending.RowKey)).ETag);
    }

    [Fact]
    public async Task Concurrent_unsent_retries_cannot_publish_two_replacement_verifiers()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var (_, request, _) = await FencedEnrollment(f);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var pause = new CapacityPause(body => body.Contains("\"SettlementReason\":\"unsent\"", StringComparison.Ordinal));
        await f.RestartAsync(pause.Transport);
        var stale = f.Send("/v2/enrollments", request);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            using var winner = await f.Send("/v2/enrollments", request);
            Assert.Equal(HttpStatusCode.OK, winner.StatusCode);
            pause.Release.TrySetResult();
            using var loser = await stale;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, loser.StatusCode);
            Assert.Equal(1, f.GitHub.Mints);
            Assert.Equal(2, (await IssuanceLedgers(f)).Count);
            using var again = await f.Send("/v2/enrollments", request);
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(1, f.GitHub.Mints);
        }
        finally { pause.Release.TrySetResult(); using var response = await stale; }
    }

    private static async Task<(string Id, object Request, TableEntity Ledger)> FencedEnrollment(EnrollmentFixture f, string profile = "polling")
    {
        CloneEnrollmentContractTests.ConfigureGitHub(f, profile);
        var id = Guid.NewGuid().ToString("N");
        var request = new { idempotencyId = id, repositoryId = 123456,
            repository = "owner/project", variable = "APPROVAL", profile };
        var pause = new CapacityPause(body => body.Contains("\"State\":\"sending\"", StringComparison.Ordinal));
        await f.RestartAsync(pause.Transport);
        var writer = f.Send("/v2/enrollments", request);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            var ledger = Assert.Single(await IssuanceLedgers(f));
            await Reclaim(f, ledger);
            pause.Release.TrySetResult();
            using var interrupted = await writer;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, interrupted.StatusCode);
            Assert.Equal(0, f.GitHub.Mints);
            return (id, request, ledger);
        }
        finally { pause.Release.TrySetResult(); using var response = await writer; }
    }
}
