using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed partial class CapacityReclamationTests
{
    // Deliberate migration: an Agent-variable (setup v2) owner keeps working and blocks
    // Actions-variable (setup v3) completion until it is revoked, its capabilities settle
    // and cleanup releases ownership. Nothing converts or overwrites the old grant.
    [Fact]
    public async Task Agent_owner_migrates_to_Actions_approval_only_after_revocation_settlement_and_release()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f);
        var old = await ActionsVariableEnrollmentTests.Active(f, github, version: 2);
        using (var runtime = await f.Send($"/v1/devices/{old.Device}/installation-token", secret: old.Secret))
            Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);

        using (var start = await f.Send("/v3/enrollments", ActionsVariableEnrollmentTests.Request()))
        {
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
            var pending = await start.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Token);
            github.Actions["REPOAGENT_APPROVAL"] = pending.GetProperty("marker").GetString()!;
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            using var blocked = await f.Send($"/v3/enrollments/{pending.GetProperty("pendingId").GetString()}/completion");
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            Assert.DoesNotContain("deviceSecret", await blocked.Content.ReadAsStringAsync(Token));
        }
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using (var stillActive = await f.Send($"/v1/devices/{old.Device}/installation-token", secret: old.Secret))
            Assert.Equal(HttpStatusCode.OK, stillActive.StatusCode);

        // Operator revocation: host removes the Agent marker; the device self-revokes.
        github.Agent.Remove("APPROVAL");
        github.Actions.Clear();
        using (var revoke = await f.Send($"/v1/devices/{old.Device}", secret: old.Secret, method: HttpMethod.Delete))
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        await ReclaimPending(f, old.Request.IdempotencyId);
        await SettleOwnerCapabilities(f);
        await f.RestartAsync();
        await ReclaimPending(f, old.Request.IdempotencyId);

        var replacement = await ActionsVariableEnrollmentTests.Active(f, github);
        Assert.NotEqual(old.Device, replacement.Device);
        using var current = await f.Send($"/v1/devices/{replacement.Device}/installation-token", secret: replacement.Secret);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        github.Agent["APPROVAL"] = old.Marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var restored = await f.Send($"/v1/devices/{old.Device}/installation-token", secret: old.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, restored.StatusCode);
        Assert.Equal(replacement.Device, (await Read(f, RevocationFixture.Partition, "gate")).GetString("OwnerDeviceId"));
    }
}
