using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
        var old = await f.ActiveAsync(github, version: 2);
        using (var runtime = await f.Send($"/v1/devices/{old.Device}/installation-token", secret: old.Secret))
            Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);

        using (var start = await f.Send("/v3/enrollments", ActionsVariableEnrollmentTests.Request()))
        {
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
            var pending = await start.Content.ReadFromJsonAsync<JsonElement>(Token);
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

        var replacement = await f.ActiveAsync(github);
        Assert.NotEqual(old.Device, replacement.Device);
        using var current = await f.Send($"/v1/devices/{replacement.Device}/installation-token", secret: replacement.Secret);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        github.Agent["APPROVAL"] = old.Marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var restored = await f.Send($"/v1/devices/{old.Device}/installation-token", secret: old.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, restored.StatusCode);
        Assert.Equal(replacement.Device, (await Read(f, RevocationFixture.Partition, "gate")).GetString("OwnerDeviceId"));
    }

    // Opting in to repository settings is a fresh enrollment. The frozen v3 owner keeps its exact
    // grants, is never upgraded to a repository-settings profile and blocks it until revoked and settled.
    [Fact]
    public async Task Frozen_Actions_owner_moves_to_the_settings_profile_only_through_revocation_and_fresh_enrollment()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f);
        var old = await f.ActiveAsync(github);
        using (var runtime = await f.Send($"/v1/devices/{old.Device}/installation-token", secret: old.Secret))
            Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);

        using (var start = await f.Send("/v3/enrollments", ActionsVariableEnrollmentTests.Request("polling-settings") with { Variable = "SETTINGS_APPROVAL" }))
        {
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
            var pending = await start.Content.ReadFromJsonAsync<JsonElement>(Token);
            github.Actions["SETTINGS_APPROVAL"] = pending.GetProperty("marker").GetString()!;
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            using var blocked = await f.Send($"/v3/enrollments/{pending.GetProperty("pendingId").GetString()}/completion");
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            Assert.DoesNotContain("deviceSecret", await blocked.Content.ReadAsStringAsync(Token));
        }
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using (var upgraded = await f.Send($"/v3/devices/{old.Device}/installation-token", secret: old.Secret))
            Assert.Equal(HttpStatusCode.Unauthorized, upgraded.StatusCode);
        using (var stillActive = await f.Send($"/v1/devices/{old.Device}/installation-token", secret: old.Secret))
            Assert.Equal(HttpStatusCode.OK, stillActive.StatusCode);
        Assert.All(github.RuntimePermissions, grants => Assert.DoesNotContain("actions_variables", grants.Keys));

        github.Actions.Clear();
        using (var revoke = await f.Send($"/v1/devices/{old.Device}", secret: old.Secret, method: HttpMethod.Delete))
            Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        await ReclaimPending(f, old.Request.IdempotencyId);
        await SettleOwnerCapabilities(f);
        await f.RestartAsync();
        await ReclaimPending(f, old.Request.IdempotencyId);

        var replacement = await f.ActiveAsync(github, profile: "polling-settings");
        using var current = await f.Send($"/v3/devices/{replacement.Device}/installation-token", secret: replacement.Secret);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal("read", github.RuntimePermissions[^1]["actions_variables"]);
        github.Actions["REPOAGENT_APPROVAL"] = old.Marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var restored = await f.Send($"/v1/devices/{old.Device}/installation-token", secret: old.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, restored.StatusCode);
    }
}
