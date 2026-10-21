using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed partial class CapacityReclamationTests
{
    [Theory]
    [InlineData("polling")]
    [InlineData("polling-clone")]
    public async Task Revoked_acknowledged_owner_releases_only_after_all_capabilities_settle(string profile)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var old = await AcknowledgedOwner(f, profile);
        var version = profile == "polling-clone" ? 2 : 1;
        using var revoke = await f.Send($"/v{version}/devices/{old.Device}", secret: old.Secret, method: HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        var device = await Read(f, RevocationFixture.Partition, "device:" + old.Device);
        var receipt = await Read(f, RevocationFixture.Partition, "revocation:" + old.Device);
        await ReclaimPending(f, old.Id);
        await ReclaimPending(f, old.Id);
        Assert.Equal(old.Device, (await Read(f, RevocationFixture.Partition, "gate")).GetString("OwnerDeviceId"));
        var blocked = await Start(f);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var tooEarly = await f.Send($"/v2/enrollments/{blocked.Pending}/completion");
        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);
        await SettleOwnerCapabilities(f);
        await f.RestartAsync();

        await ReclaimPending(f, old.Id);

        var replacement = await Active(f);
        Assert.NotEqual(old.Device, replacement.Device);
        using var current = await f.Send($"/v1/devices/{replacement.Device}/installation-token", secret: replacement.Secret);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        using var denied = await f.Send($"/v{version}/devices/{old.Device}/installation-token", secret: old.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        var gate = await Read(f, RevocationFixture.Partition, "gate");
        await ReclaimPending(f, old.Id);
        Assert.Equal(gate.ETag, (await Read(f, gate.PartitionKey, gate.RowKey)).ETag);
        var released = await Read(f, device.PartitionKey, device.RowKey);
        foreach (var field in new[] { "ExpiresAt", "LastActivityAt", "FirstTerminalAt", "Acknowledged", "State" })
            Assert.Equal(device[field], released[field]);
        var audit = await Read(f, receipt.PartitionKey, receipt.RowKey);
        Assert.Equal(receipt["RevokedAt"], audit["RevokedAt"]);
        Assert.Equal("revoked-acknowledged", audit.GetString("Reason"));
        Assert.True(audit.ContainsKey("OwnerReleasedAt"));
        Assert.Equal(3L, (await Read(f, "global:enrollment-v1", "quota")).GetInt64("RetainedEnrollmentCount"));
    }

    private static async Task<ActiveJourney> AcknowledgedOwner(EnrollmentFixture f, string profile = "polling")
    {
        CloneEnrollmentContractTests.ConfigureGitHub(f, profile);
        var id = Guid.NewGuid().ToString("N");
        using var start = await f.Send("/v2/enrollments", new { idempotencyId = id, repositoryId = 123456,
            repository = "owner/project", variable = "APPROVAL", profile });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var status = await start.Content.ReadFromJsonAsync<JsonElement>(Token);
        f.GitHub.Marker = status.GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var complete = await f.Send($"/v2/enrollments/{status.GetProperty("pendingId").GetString()}/completion");
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var receipt = await complete.Content.ReadFromJsonAsync<JsonElement>(Token);
        var device = receipt.GetProperty("enrollment");
        var owner = new ActiveJourney(id, device.GetProperty("deviceId").GetString()!, device.GetProperty("deviceSecret").GetString()!,
            receipt.GetProperty("completionId").GetString()!);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var ack = await f.Send($"/v2/devices/{owner.Device}/acknowledgment", new { completionId = owner.Completion }, owner.Secret);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var runtime = await f.Send($"/v{(profile == "polling-clone" ? 2 : 1)}/devices/{owner.Device}/installation-token", secret: owner.Secret);
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        return owner;
    }

    private static async Task SettleOwnerCapabilities(EnrollmentFixture f)
    {
        f.Clock.Advance(TimeSpan.FromMinutes(66));
        foreach (var ledger in await IssuanceLedgers(f)) await Reclaim(f, ledger);
    }
}
