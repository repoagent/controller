using System.Net;
using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

// The broker's enrollment, issuance and revocation over the in-memory broker state; GitHub is the in-memory
// Actions/Agent-variable fake. The repository-settings profiles are a separate, explicitly versioned runtime
// authority: each grants its frozen profile's permissions plus Variables read.
public sealed class RepositorySettingsProfileTests
{
    private const string Policy = "repository-actions-settings-approval-v3";

    public static TheoryData<string, string[]> SettingsProfiles => new()
    {
        { "polling-settings", ["discussions:write", "actions_variables:read"] },
        { "webhook-settings", ["discussions:write", "repository_hooks:write", "actions_variables:read"] },
        { "polling-clone-settings", ["discussions:write", "contents:read", "actions_variables:read"] },
        { "webhook-clone-settings", ["discussions:write", "contents:read", "repository_hooks:write", "actions_variables:read"] },
    };

    [Theory]
    [MemberData(nameof(SettingsProfiles))]
    public async Task Settings_profile_enrolled_under_v3_Actions_approval_issues_exactly_the_frozen_grants_plus_Variables_read(string profile, string[] grants)
    {
        var f = new InMemoryBroker();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f.GitHub);
        var active = await ActionsVariableEnrollmentTests.Active(f, github, profile: profile);
        var expected = grants.Select(g => g.Split(':')).ToDictionary(g => g[0], g => g[1]);

        var marker = JsonSerializer.Deserialize<JsonElement>(active.Marker);
        Assert.Equal((3, "repository-actions-variable", profile, Policy),
            (marker.GetProperty("version").GetInt32(), marker.GetProperty("approvalKind").GetString(),
             marker.GetProperty("profile").GetString(), marker.GetProperty("policyVersion").GetString()));
        Assert.Equal((3, profile, Policy), (active.Enrollment.GetProperty("protocolVersion").GetInt32(),
            active.Enrollment.GetProperty("profile").GetString(), active.Enrollment.GetProperty("policyVersion").GetString()));
        Assert.Equal(expected, Map(active.Enrollment.GetProperty("permissions")));

        // Frozen runtime routes never serve these profiles, and refusing them costs no mint.
        var mints = f.GitHub.Mints;
        foreach (var frozen in new[] { 1, 2 })
        {
            var refused = await f.Issue(active.Device, active.Secret, frozen);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
        Assert.Equal(mints, f.GitHub.Mints);

        var runtime = await f.Issue(active.Device, active.Secret, 3);
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        var lease = runtime.Json;
        Assert.Equal((3, profile, Policy), (lease.GetProperty("protocolVersion").GetInt32(),
            lease.GetProperty("profile").GetString(), lease.GetProperty("policyVersion").GetString()));
        Assert.Equal(expected, Map(lease.GetProperty("permissions")));
        Assert.Equal(expected, Assert.Single(github.RuntimePermissions));
        // Approval verification stays a separate broker-internal capability.
        Assert.Equal((1, "actions_variables"), (github.VerifierMints, github.VerifierPermission));

        // Observed approval withdrawal closes a repository-settings profile like any other grant.
        github.Actions.Remove("REPOAGENT_APPROVAL");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var removed = await f.Issue(active.Device, active.Secret, 3);
        Assert.Equal(HttpStatusCode.Unauthorized, removed.StatusCode);
    }

    [Theory]
    [InlineData(1, "polling-settings")]
    [InlineData(2, "polling-settings")]
    [InlineData(2, "webhook-clone-settings")]
    public async Task Settings_profiles_are_refused_without_a_v3_Actions_approval(int setupVersion, string profile)
    {
        var f = new InMemoryBroker();
        ActionsVariableEnrollmentTests.Repository.Configure(f.GitHub);
        var start = setupVersion == 1
            ? await f.Start(InMemoryBroker.Request() with { Profile = profile })
            : await f.Start(ActionsVariableEnrollmentTests.Request(profile) with { Variable = "APPROVAL" }, setupVersion);

        Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
        Assert.Empty(f.GitHub.Requests);
    }

    [Theory]
    [InlineData(3, "polling", 1)]
    [InlineData(3, "webhook", 1)]
    [InlineData(3, "polling-clone", 2)]
    [InlineData(3, "webhook-clone", 2)]
    [InlineData(2, "polling", 1)]
    [InlineData(2, "webhook-clone", 2)]
    public async Task Frozen_profiles_keep_their_exact_grants_and_never_reach_Variables_through_the_settings_route(int setupVersion, string profile, int runtimeVersion)
    {
        var f = new InMemoryBroker();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f.GitHub);
        var active = await ActionsVariableEnrollmentTests.Active(f, github, setupVersion, profile);
        var expected = new Dictionary<string, string> { ["discussions"] = "write" };
        if (profile.StartsWith("webhook", StringComparison.Ordinal)) expected["repository_hooks"] = "write";
        if (profile.EndsWith("-clone", StringComparison.Ordinal)) expected["contents"] = "read";
        Assert.Equal(runtimeVersion, active.Enrollment.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(expected, Map(active.Enrollment.GetProperty("permissions")));

        var mints = f.GitHub.Mints;
        var settingsRoute = await f.Issue(active.Device, active.Secret, 3);
        Assert.Equal(HttpStatusCode.Unauthorized, settingsRoute.StatusCode);
        var settingsRevoke = await f.Revoke(active.Device, active.Secret, 3);
        Assert.Equal(HttpStatusCode.Unauthorized, settingsRevoke.StatusCode);
        Assert.Equal(mints, f.GitHub.Mints);

        var runtime = await f.Issue(active.Device, active.Secret, runtimeVersion);
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        Assert.Equal(expected, Map(runtime.Json.GetProperty("permissions")));
        Assert.Equal(expected, Assert.Single(github.RuntimePermissions));
    }

    [Theory]
    [InlineData("Profile", "polling")]
    [InlineData("Profile", "webhook-settings")]
    [InlineData("PolicyVersion", "repository-actions-approval-v3")]
    [InlineData("PolicyVersion", "repository-actions-clone-approval-v3")]
    public async Task Durable_repository_settings_profile_is_never_reinterpreted_and_denies_before_reservation_or_provider(string field, string value)
    {
        var f = new InMemoryBroker();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f.GitHub);
        var active = await ActionsVariableEnrollmentTests.Active(f, github, profile: "polling-settings");
        f.State.Change(ControllerEnrollment.Key(InMemoryBroker.RepositoryId, active.Device), row => row[field] = value);
        var requests = f.GitHub.Requests.Count;

        foreach (var route in new[] { 1, 2, 3 })
        {
            var denied = await f.Issue(active.Device, active.Secret, route);
            Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
            Assert.Equal("", denied.Text);
        }
        Assert.Equal(requests, f.GitHub.Requests.Count);
        Assert.Empty(github.RuntimePermissions);
    }

    [Fact]
    public async Task Settings_device_self_revokes_only_through_its_own_runtime_route()
    {
        var f = new InMemoryBroker();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f.GitHub);
        var active = await ActionsVariableEnrollmentTests.Active(f, github, profile: "webhook-settings");

        foreach (var frozen in new[] { 1, 2 })
        {
            var refused = await f.Revoke(active.Device, active.Secret, frozen);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
        var revoke = await f.Revoke(active.Device, active.Secret, 3);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var after = await f.Issue(active.Device, active.Secret, 3);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    private static Dictionary<string, string> Map(JsonElement permissions) =>
        permissions.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
}
