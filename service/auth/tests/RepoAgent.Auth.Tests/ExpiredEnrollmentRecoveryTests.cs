using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed partial class CapacityReclamationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_unacknowledged_owner_allows_fresh_enrollment_after_positive_settlement(bool pendingAlreadyReleased)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var old = await Unacknowledged(f);
        f.Clock.Advance(TimeSpan.FromMinutes(15));
        if (pendingAlreadyReleased) await ReclaimPending(f, old.Id);
        await SettleVerifiers(f);
        await f.RestartAsync();

        await ReclaimPending(f, old.Id);
        await ReclaimPending(f, old.Id);

        // Exercise the consumer's actual recovery outcome, not just a vacant-row projection.
        var replacement = await Active(f);
        Assert.NotEqual(old.Device, replacement.Device);
        Assert.NotEqual(old.Secret, replacement.Secret);
        using var runtime = await f.Send($"/v1/devices/{replacement.Device}/installation-token", secret: replacement.Secret);
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        using var oldRuntime = await f.Send($"/v1/devices/{old.Device}/installation-token", secret: old.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, oldRuntime.StatusCode);
        using var oldAck = await f.Send($"/v2/devices/{old.Device}/acknowledgment", new { completionId = old.Completion }, old.Secret);
        Assert.NotEqual(HttpStatusCode.OK, oldAck.StatusCode);

        // A delayed old cleanup visit must not close or release the replacement.
        var gate = await Read(f, RevocationFixture.Partition, "gate");
        await ReclaimPending(f, old.Id);
        Assert.Equal(gate.ETag, (await Read(f, gate.PartitionKey, gate.RowKey)).ETag);
        var retired = await Read(f, RevocationFixture.Partition, "device:" + old.Device);
        Assert.Equal("revoked", retired.GetString("State"));
        Assert.False(retired.GetBoolean("Acknowledged"));
        Assert.Equal(old.Device, (await Read(f, RevocationFixture.Partition, "revocation:" + old.Device)).GetString("DeviceId"));
        var quota = await Read(f, "global:enrollment-v1", "quota");
        Assert.Equal(2L, quota.GetInt64("RetainedEnrollmentCount"));
        Assert.Equal(1L, quota.GetInt64("Count"));
    }

    private static async Task<(string Id, string Device, string Secret, string Completion)> Unacknowledged(EnrollmentFixture f)
    {
        var start = await Start(f);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var response = await f.Send($"/v2/enrollments/{start.Pending}/completion");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        var enrollment = receipt.GetProperty("enrollment");
        return (start.Id, enrollment.GetProperty("deviceId").GetString()!, enrollment.GetProperty("deviceSecret").GetString()!,
            receipt.GetProperty("completionId").GetString()!);
    }

    private static async Task SettleVerifiers(EnrollmentFixture f)
    {
        var ledgers = await IssuanceLedgers(f);
        var expiry = DateTimeOffset.MinValue;
        foreach (var ledger in ledgers)
        {
            var attempt = await Read(f, RevocationFixture.Partition, ledger.GetString("AttemptRowKey")!);
            expiry = new[] { expiry, attempt.GetDateTimeOffset("ExpiresAt")!.Value.AddMinutes(5) }.Max();
        }
        if (expiry > f.Clock.GetUtcNow()) f.Clock.Advance(expiry - f.Clock.GetUtcNow());
        foreach (var ledger in ledgers) await Reclaim(f, ledger);
    }

    private static async Task ReclaimPending(EnrollmentFixture f, string id, TableClient? table = null)
    {
        var ledger = await Read(f, "global:enrollment-v1", "pending:" + id);
        await Cleanup(f, table).ReclaimEnrollmentAsync(id, ledger.GetString("Generation")!, Token);
    }
}
