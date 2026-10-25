using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

// Moving a repository to another approval contract or profile, over the in-memory broker state. It is always a fresh
// enrollment: the old owner keeps working and blocks the new completion until it is revoked, its GitHub tokens settle
// and reclamation releases its ownership. Nothing converts or upgrades the old grant.
public sealed class ActionsVariableMigrationTests
{
    // Deliberate migration: an Agent-variable (setup v2) owner keeps working and blocks
    // Actions-variable (setup v3) completion until it is revoked, its capabilities settle
    // and cleanup releases ownership. Nothing converts or overwrites the old grant.
    [Fact]
    public async Task Agent_owner_migrates_to_Actions_approval_only_after_revocation_settlement_and_release()
    {
        var f = new InMemoryBroker();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f.GitHub);
        var old = await ActionsVariableEnrollmentTests.Active(f, github, version: 2);
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(old.Device, old.Secret)).StatusCode);

        var start = await f.Start(ActionsVariableEnrollmentTests.Request(), 3);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        github.Actions["REPOAGENT_APPROVAL"] = start.Json.GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var blocked = await f.Complete(start.Json.GetProperty("pendingId").GetString()!, 3);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.DoesNotContain("deviceSecret", blocked.Text);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(old.Device, old.Secret)).StatusCode);

        // Operator revocation: host removes the Agent marker; the controller self-revokes.
        github.Agent.Remove("APPROVAL");
        github.Actions.Clear();
        Assert.Equal(HttpStatusCode.NoContent, (await f.Revoke(old.Device, old.Secret)).StatusCode);
        await Retained.ReclaimEnrollment(f, old.Request.IdempotencyId);
        await SettleOwnerCapabilities(f);
        await Retained.ReclaimEnrollment(f, old.Request.IdempotencyId);

        var replacement = await ActionsVariableEnrollmentTests.Active(f, github);
        Assert.NotEqual(old.Device, replacement.Device);
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(replacement.Device, replacement.Secret)).StatusCode);
        github.Agent["APPROVAL"] = old.Marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Issue(old.Device, old.Secret)).StatusCode);
        Assert.Equal(replacement.Device, f.Gate.OwnerEnrollmentId);
    }

    // Opting in to repository settings is a fresh enrollment. The frozen v3 owner keeps its exact
    // grants, is never upgraded to a repository-settings profile and blocks it until revoked and settled.
    [Fact]
    public async Task Frozen_Actions_owner_moves_to_the_settings_profile_only_through_revocation_and_fresh_enrollment()
    {
        var f = new InMemoryBroker();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f.GitHub);
        var old = await ActionsVariableEnrollmentTests.Active(f, github);
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(old.Device, old.Secret)).StatusCode);

        var start = await f.Start(ActionsVariableEnrollmentTests.Request("polling-settings") with { Variable = "SETTINGS_APPROVAL" }, 3);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        github.Actions["SETTINGS_APPROVAL"] = start.Json.GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var blocked = await f.Complete(start.Json.GetProperty("pendingId").GetString()!, 3);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.DoesNotContain("deviceSecret", blocked.Text);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Issue(old.Device, old.Secret, 3)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(old.Device, old.Secret)).StatusCode);
        Assert.All(github.RuntimePermissions, grants => Assert.DoesNotContain("actions_variables", grants.Keys));

        github.Actions.Clear();
        Assert.Equal(HttpStatusCode.NoContent, (await f.Revoke(old.Device, old.Secret)).StatusCode);
        await Retained.ReclaimEnrollment(f, old.Request.IdempotencyId);
        await SettleOwnerCapabilities(f);
        await Retained.ReclaimEnrollment(f, old.Request.IdempotencyId);

        var replacement = await ActionsVariableEnrollmentTests.Active(f, github, profile: "polling-settings");
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(replacement.Device, replacement.Secret, 3)).StatusCode);
        Assert.Equal("read", github.RuntimePermissions[^1]["actions_variables"]);
        github.Actions["REPOAGENT_APPROVAL"] = old.Marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Issue(old.Device, old.Secret)).StatusCode);
    }

    // Past the owner's GitHub token expiry plus the five-minute margin, reclamation settles every issuance attempt.
    private static async Task SettleOwnerCapabilities(InMemoryBroker f)
    {
        f.Clock.Advance(TimeSpan.FromMinutes(66));
        foreach (var ledger in Retained.IssuanceLedgers(f)) await Retained.ReclaimIssuance(f, ledger.Value);
    }
}
