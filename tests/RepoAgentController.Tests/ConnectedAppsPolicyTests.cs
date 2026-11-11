using System.Text.Json.Nodes;
using Xunit;

namespace RepoAgentController.Tests;

public sealed class ConnectedAppsPolicyTests
{
    [Theory]
    [InlineData("inherit", "")]
    [InlineData("selected", "")]
    [InlineData("disabled", "gmail")]
    [InlineData("all", "gmail")]
    [InlineData("selected", "_default")]
    [InlineData("selected", "gmail.enabled")]
    [InlineData("selected", "gmail\n")]
    public void Invalid_authority_choices_are_rejected(string mode, string id) =>
        Assert.Throws<ArgumentException>(() => new ConnectedAppsPolicy(mode, id.Length == 0 ? [] : [id]));

    [Fact]
    public void Selected_policy_deduplicates_the_allowlist_and_excludes_unlisted_apps()
    {
        var policy = new ConnectedAppsPolicy("selected", ["github", "github"]);
        Assert.Equal(["github"], policy.AllowedApps);
        Assert.True(policy.AllowsApp("github"));
        Assert.False(policy.AllowsApp("gmail"));
        Assert.False(policy.AllowsApp("future_app"));
        Assert.False(policy.UnlistedAppsEnabled);
    }

    [Theory]
    [InlineData("disabled", false, false, true)]
    [InlineData("selected", true, false, false)]
    [InlineData("all", true, true, true)]
    public void Effective_policy_requires_matching_authority_and_rejects_unlisted_enables(string mode, bool feature, bool defaults, bool gmail)
    {
        var policy = new ConnectedAppsPolicy(mode, mode == "selected" ? ["github"] : []);
        var states = new Dictionary<string, bool?> { ["github"] = true, ["gmail"] = gmail };
        Assert.True(policy.Matches(feature, defaults, states));
        if (mode == "selected")
        {
            states["future_app"] = true;
            Assert.False(policy.Matches(feature, defaults, states));
        }
        Assert.False(policy.Matches(!feature, defaults, states));
        Assert.False(policy.Matches(feature, !defaults, states));
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("selected")]
    [InlineData("all")]
    public void Missing_observations_cannot_establish_policy_enforcement(string mode)
    {
        var policy = new ConnectedAppsPolicy(mode, mode == "selected" ? ["github"] : []);
        Assert.False(policy.Matches(null, null, null));
        Assert.False(policy.Matches(policy.AppsEnabled, null, new Dictionary<string, bool?>()));
        if (policy.AppsEnabled) Assert.False(policy.Matches(true, policy.UnlistedAppsEnabled, null));
        if (mode == "selected")
        {
            Assert.False(policy.Matches(true, false, new Dictionary<string, bool?>()));
            Assert.False(policy.Matches(true, false, new Dictionary<string, bool?> { ["github"] = null }));
        }
    }

    [Fact]
    public void All_mode_deliberately_includes_future_apps()
    {
        var policy = new ConnectedAppsPolicy("all", []);
        Assert.True(policy.UnlistedAppsEnabled);
        Assert.True(policy.AllowsApp("future_app"));
        Assert.False(ConnectedAppsPolicy.Disabled.AllowsApp("gmail"));
    }

    [Fact]
    public void Private_status_roundtrips_policy_and_unavailable_ids()
    {
        var record = new LifetimeStatus("owner/repository", new("running"), new("healthy"), new("supplied"))
        { ConnectedApps = new("selected", ["gmail"], "verified") { UnavailableApps = ["gmail"] } };
        var read = OperatorStatusJson.ReadRecord(OperatorStatusJson.Record(record));
        Assert.Equal("selected", read.ConnectedApps!.Mode);
        Assert.Equal("verified", read.ConnectedApps.Verification);
        Assert.Equal(["gmail"], read.ConnectedApps.AllowedApps);
        Assert.Equal(["gmail"], read.ConnectedApps.UnavailableApps);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{}")]
    public void Malformed_recorded_allowlist_never_reports_verified(string invalidId)
    {
        var record = JsonNode.Parse("{\"connectedApps\":{\"mode\":\"selected\",\"verification\":\"verified\",\"allowedApps\":[" + invalidId + "]}}")!.AsObject();
        Assert.Equal("unknown", OperatorStatusJson.ReadRecord(record).ConnectedApps!.Verification);
    }

    [Fact]
    public void Stable_id_limits_are_strict()
    {
        var longest = new string('a', 200);
        Assert.Equal([longest], new ConnectedAppsPolicy("selected", [longest]).AllowedApps);
        Assert.Throws<ArgumentException>(() => new ConnectedAppsPolicy("selected", [longest + "a"]));
        Assert.False(ConnectedAppsPolicy.IsValidAppId("app.with.dots"));
    }
}
