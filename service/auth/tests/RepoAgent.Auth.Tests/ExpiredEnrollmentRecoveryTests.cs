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

    [Fact]
    public async Task Unknown_setup_send_still_blocks_owner_recovery_after_known_tokens_expire()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var old = await Unacknowledged(f);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.GitHub.BeforeMint = _ => throw new GitHubUnavailable("Provider send outcome unknown");
        using var unknown = await f.Send("/v2/enrollments", Request(Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unknown.StatusCode);
        f.Clock.Advance(TimeSpan.FromDays(2));
        foreach (var ledger in await IssuanceLedgers(f)) await Reclaim(f, ledger);
        await ReclaimPending(f, old.Id);

        await ReclaimPending(f, old.Id);

        var gate = await Read(f, RevocationFixture.Partition, "gate");
        Assert.Equal(old.Device, gate.GetString("OwnerDeviceId"));
        Assert.Equal(1L, gate.GetInt64("OutstandingTokenCount"));
        Assert.False((await f.Storage.Table.GetEntityIfExistsAsync<TableEntity>(gate.PartitionKey,
            "revocation:" + old.Device, cancellationToken: Token)).HasValue);
        using var runtime = await f.Send($"/v1/devices/{old.Device}/installation-token", secret: old.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, runtime.StatusCode);
    }

    [Theory]
    [InlineData("acknowledged")]
    [InlineData("epoch")]
    [InlineData("pending-binding")]
    [InlineData("missing-device")]
    [InlineData("missing-release")]
    [InlineData("missing-acknowledgment")]
    public async Task Owner_recovery_requires_never_activated_device_and_complete_current_binding_proofs(string barrier)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var old = await Unacknowledged(f);
        if (barrier == "acknowledged")
        {
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            using var ack = await f.Send($"/v2/devices/{old.Device}/acknowledgment", new { completionId = old.Completion }, old.Secret);
            Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        }
        f.Clock.Advance(TimeSpan.FromDays(91));
        await SettleVerifiers(f);
        await ReclaimPending(f, old.Id);
        var device = await Read(f, RevocationFixture.Partition, "device:" + old.Device);
        if (barrier == "pending-binding") device["PendingId"] = "123456_" + Guid.NewGuid().ToString("N");
        if (barrier == "missing-acknowledgment") device.Remove("Acknowledged");
        if (barrier is "pending-binding" or "missing-acknowledgment")
            await f.Storage.Table.UpdateEntityAsync(device, device.ETag, TableUpdateMode.Replace, Token);
        if (barrier == "missing-device") await f.Storage.Table.DeleteEntityAsync(device.PartitionKey, device.RowKey, device.ETag, Token);
        if (barrier == "missing-release")
        {
            var release = await Read(f, RevocationFixture.Partition, "release:pending:" + old.Id);
            await f.Storage.Table.DeleteEntityAsync(release.PartitionKey, release.RowKey, release.ETag, Token);
        }
        var before = await Read(f, RevocationFixture.Partition, "gate");

        if (barrier == "epoch")
        {
            var ledger = await Read(f, "global:enrollment-v1", "pending:" + old.Id);
            await new BrokerCapacityReclamation(f.Storage.Table, Options(f) with { DeploymentEpoch = "restored-epoch" }, f.Clock)
                .ReclaimEnrollmentAsync(old.Id, ledger.GetString("Generation")!, Token);
        }
        else if (barrier is "pending-binding" or "missing-device" or "missing-release")
            await Assert.ThrowsAsync<IssuanceDenied>(() => ReclaimPending(f, old.Id));
        else await ReclaimPending(f, old.Id);

        Assert.Equal(before.ETag, (await Read(f, before.PartitionKey, before.RowKey)).ETag);
        Assert.Equal(old.Device, (await Read(f, before.PartitionKey, before.RowKey)).GetString("OwnerDeviceId"));
    }

    [Fact]
    public async Task Stale_recovery_cannot_release_a_replacement_after_another_worker_recovers_the_owner()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var old = await Unacknowledged(f);
        f.Clock.Advance(TimeSpan.FromMinutes(15));
        await ReclaimPending(f, old.Id);
        await SettleVerifiers(f);
        var pause = new CapacityPause(body => body.Contains("\"State\":\"vacant\"", StringComparison.Ordinal));
        var stale = ReclaimPending(f, old.Id, f.Storage.CreateClient(pause.Transport));
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            await ReclaimPending(f, old.Id);
            var replacement = await Active(f);
            var gate = await Read(f, RevocationFixture.Partition, "gate");
            pause.Release.TrySetResult();

            await Assert.ThrowsAsync<TableTransactionFailedException>(() => stale);

            Assert.Equal(gate.ETag, (await Read(f, gate.PartitionKey, gate.RowKey)).ETag);
            Assert.Equal(replacement.Device, (await Read(f, gate.PartitionKey, gate.RowKey)).GetString("OwnerDeviceId"));
            using var runtime = await f.Send($"/v1/devices/{replacement.Device}/installation-token", secret: replacement.Secret);
            Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        }
        finally
        {
            pause.Release.TrySetResult();
            try { await stale; } catch (TableTransactionFailedException) { }
        }
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
