using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

// Moving a repository to another approval contract or profile, over the in-memory broker state. It is always a fresh
// enrollment: the old owner keeps working and blocks the new start until it is revoked, its GitHub tokens settle
// and reclamation releases its ownership. Nothing converts or upgrades the old grant.
public sealed class ActionsVariableMigrationTests
{
    // Deliberate migration: an Agent-variable (setup v2) owner keeps working and blocks
    // Actions-variable (setup v3) admission until it is revoked, its capabilities settle
    // and cleanup releases ownership. Nothing converts or overwrites the old grant.
    [Fact]
    public async Task Agent_owner_migrates_to_Actions_approval_only_after_revocation_settlement_and_release()
    {
        var f = new InMemoryBroker();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f.GitHub);
        var old = await ActionsVariableEnrollmentTests.Active(f, github, version: 2);
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(old.EnrollmentId, old.Secret)).StatusCode);

        var before = f.Rows().Select(row => (row.Key, row.Version)).ToList();
        var mints = f.GitHub.Mints;
        var blocked = await f.Start(ActionsVariableEnrollmentTests.Request(), 3);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.DoesNotContain("deviceSecret", blocked.Text);
        Assert.Equal(before, f.Rows().Select(row => (row.Key, row.Version)));
        Assert.Equal(mints, f.GitHub.Mints);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(old.EnrollmentId, old.Secret)).StatusCode);

        // Operator revocation: host removes the Agent marker; the controller self-revokes.
        github.Agent.Remove("APPROVAL");
        github.Actions.Clear();
        Assert.Equal(HttpStatusCode.NoContent, (await f.Revoke(old.EnrollmentId, old.Secret)).StatusCode);
        await Retained.ReclaimEnrollment(f, old.Request.IdempotencyId);
        await SettleOwnerCapabilities(f);
        await Retained.ReclaimEnrollment(f, old.Request.IdempotencyId);

        var replacement = await ActionsVariableEnrollmentTests.Active(f, github);
        Assert.NotEqual(old.EnrollmentId, replacement.EnrollmentId);
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(replacement.EnrollmentId, replacement.Secret)).StatusCode);
        github.Agent["APPROVAL"] = old.Marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Issue(old.EnrollmentId, old.Secret)).StatusCode);
        Assert.Equal(replacement.EnrollmentId, f.Gate.OwnerEnrollmentId);
    }

    // Opting in to repository settings is a fresh enrollment. The frozen v3 owner keeps its exact
    // grants, is never upgraded to a repository-settings profile and blocks it until revoked and settled.
    [Fact]
    public async Task Frozen_Actions_owner_moves_to_the_settings_profile_only_through_revocation_and_fresh_enrollment()
    {
        var f = new InMemoryBroker();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f.GitHub);
        var old = await ActionsVariableEnrollmentTests.Active(f, github);
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(old.EnrollmentId, old.Secret)).StatusCode);

        var before = f.Rows().Select(row => (row.Key, row.Version)).ToList();
        var mints = f.GitHub.Mints;
        var blocked = await f.Start(ActionsVariableEnrollmentTests.Request("polling-settings") with { Variable = "SETTINGS_APPROVAL" }, 3);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.DoesNotContain("deviceSecret", blocked.Text);
        Assert.Equal(before, f.Rows().Select(row => (row.Key, row.Version)));
        Assert.Equal(mints, f.GitHub.Mints);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Issue(old.EnrollmentId, old.Secret, 3)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(old.EnrollmentId, old.Secret)).StatusCode);
        Assert.All(github.RuntimePermissions, grants => Assert.DoesNotContain("actions_variables", grants.Keys));

        github.Actions.Clear();
        Assert.Equal(HttpStatusCode.NoContent, (await f.Revoke(old.EnrollmentId, old.Secret)).StatusCode);
        await Retained.ReclaimEnrollment(f, old.Request.IdempotencyId);
        await SettleOwnerCapabilities(f);
        await Retained.ReclaimEnrollment(f, old.Request.IdempotencyId);

        var replacement = await ActionsVariableEnrollmentTests.Active(f, github, profile: "polling-settings");
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(replacement.EnrollmentId, replacement.Secret, 3)).StatusCode);
        Assert.Equal("read", github.RuntimePermissions[^1]["actions_variables"]);
        github.Actions["REPOAGENT_APPROVAL"] = old.Marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Issue(old.EnrollmentId, old.Secret)).StatusCode);
    }

    // StoryBeam's shape: a setup v2 webhook-clone owner keeps renewing exactly as before setup v4 existed, and blocks
    // v4 admission until it is revoked, its GitHub tokens settle and reclamation releases its ownership.
    [Fact]
    public async Task Agent_v2_clone_owner_keeps_renewing_unchanged_and_moves_to_v4_only_after_revocation_settlement_and_release()
    {
        var f = new InMemoryBroker();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f.GitHub);
        var old = await ActionsVariableEnrollmentTests.Active(f, github, 2, "webhook-clone");
        var frozen = new Dictionary<string, string> { ["contents"] = "read", ["discussions"] = "write", ["repository_hooks"] = "write" };
        void AssertV2Lease(Answer lease)
        {
            Assert.Equal(HttpStatusCode.OK, lease.StatusCode);
            Assert.Equal((2, "webhook-clone", "repository-agent-clone-approval-v2"), (lease.Json.GetProperty("protocolVersion").GetInt32(),
                lease.Json.GetProperty("profile").GetString(), lease.Json.GetProperty("policyVersion").GetString()));
            Assert.Equal(frozen, lease.Json.GetProperty("permissions").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!));
            Assert.Equal(frozen, github.RuntimePermissions[^1]);
            Assert.Equal("agent_variables", github.VerifierPermission);
        }
        AssertV2Lease(await f.Issue(old.EnrollmentId, old.Secret, 2));

        var before = f.Rows().Select(row => (row.Key, row.Version)).ToList();
        var mints = f.GitHub.Mints;
        var blocked = await f.Start(ActionsVariableEnrollmentTests.Request("webhook-clone-settings"), 4);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.DoesNotContain("deviceSecret", blocked.Text);
        Assert.Equal(before, f.Rows().Select(row => (row.Key, row.Version)));
        Assert.Equal(mints, f.GitHub.Mints);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        AssertV2Lease(await f.Issue(old.EnrollmentId, old.Secret, 2));

        github.Agent.Remove("APPROVAL");
        Assert.Equal(HttpStatusCode.NoContent, (await f.Revoke(old.EnrollmentId, old.Secret, 2)).StatusCode);
        await Retained.ReclaimEnrollment(f, old.Request.IdempotencyId);
        await SettleOwnerCapabilities(f);
        await Retained.ReclaimEnrollment(f, old.Request.IdempotencyId);

        var replacement = await ActionsVariableEnrollmentTests.Active(f, github, 4, "webhook-clone-settings");
        var lease = await f.Issue(replacement.EnrollmentId, replacement.Secret, 3);
        Assert.Equal(HttpStatusCode.OK, lease.StatusCode);
        Assert.Equal("repository-agent-settings-approval-v4", lease.Json.GetProperty("policyVersion").GetString());
        Assert.Equal(new Dictionary<string, string>(frozen) { ["agent_variables"] = "read" }, github.RuntimePermissions[^1]);
        Assert.Equal(replacement.EnrollmentId, f.Gate.OwnerEnrollmentId);
    }

    // Past the owner's GitHub token expiry plus the five-minute margin, reclamation settles every issuance attempt.
    private static async Task SettleOwnerCapabilities(InMemoryBroker f)
    {
        f.Clock.Advance(TimeSpan.FromMinutes(66));
        foreach (var ledger in Retained.IssuanceLedgers(f)) await Retained.ReclaimIssuance(f, ledger.Value);
    }
}
