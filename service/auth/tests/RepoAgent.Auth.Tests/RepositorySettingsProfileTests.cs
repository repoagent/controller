using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

// Public broker HTTP, real pinned Azurite Table and Data Protection; GitHub is the controlled
// Actions/Agent-variable transport. The repository-settings profiles are a separate, explicitly
// versioned runtime authority: each grants its frozen profile's permissions plus Variables read.
public sealed class RepositorySettingsProfileTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
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
        await using var f = await EnrollmentFixture.CreateAsync();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f);
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
            using var refused = await f.Send($"/v{frozen}/devices/{active.Device}/installation-token", secret: active.Secret);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
        Assert.Equal(mints, f.GitHub.Mints);

        using var runtime = await f.Send($"/v3/devices/{active.Device}/installation-token", secret: active.Secret);
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        var lease = await Json(runtime);
        Assert.Equal((3, profile, Policy), (lease.GetProperty("protocolVersion").GetInt32(),
            lease.GetProperty("profile").GetString(), lease.GetProperty("policyVersion").GetString()));
        Assert.Equal(expected, Map(lease.GetProperty("permissions")));
        Assert.Equal(expected, Assert.Single(github.RuntimePermissions));
        // Approval verification stays a separate broker-internal capability.
        Assert.Equal((1, "actions_variables"), (github.VerifierMints, github.VerifierPermission));

        // Observed approval withdrawal closes a repository-settings profile like any other grant.
        github.Actions.Remove("REPOAGENT_APPROVAL");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var removed = await f.Send($"/v3/devices/{active.Device}/installation-token", secret: active.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, removed.StatusCode);
    }

    [Theory]
    [InlineData(1, "polling-settings")]
    [InlineData(2, "polling-settings")]
    [InlineData(2, "webhook-clone-settings")]
    public async Task Settings_profiles_are_refused_without_a_v3_Actions_approval(int setupVersion, string profile)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        ActionsVariableEnrollmentTests.Repository.Configure(f);
        object request = setupVersion == 1
            ? f.Request() with { Profile = profile }
            : ActionsVariableEnrollmentTests.Request(profile) with { Variable = "APPROVAL" };

        using var start = await f.Send($"/v{setupVersion}/enrollments", request);

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
        await using var f = await EnrollmentFixture.CreateAsync();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f);
        var active = await ActionsVariableEnrollmentTests.Active(f, github, setupVersion, profile);
        var expected = new Dictionary<string, string> { ["discussions"] = "write" };
        if (profile.StartsWith("webhook", StringComparison.Ordinal)) expected["repository_hooks"] = "write";
        if (profile.EndsWith("-clone", StringComparison.Ordinal)) expected["contents"] = "read";
        Assert.Equal(runtimeVersion, active.Enrollment.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(expected, Map(active.Enrollment.GetProperty("permissions")));

        var mints = f.GitHub.Mints;
        using var settingsRoute = await f.Send($"/v3/devices/{active.Device}/installation-token", secret: active.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, settingsRoute.StatusCode);
        using var settingsRevoke = await f.Send($"/v3/devices/{active.Device}", secret: active.Secret, method: HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.Unauthorized, settingsRevoke.StatusCode);
        Assert.Equal(mints, f.GitHub.Mints);

        using var runtime = await f.Send($"/v{runtimeVersion}/devices/{active.Device}/installation-token", secret: active.Secret);
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        Assert.Equal(expected, Map((await Json(runtime)).GetProperty("permissions")));
        Assert.Equal(expected, Assert.Single(github.RuntimePermissions));
    }

    [Fact]
    public async Task GitHub_returning_Variables_for_a_frozen_profile_is_an_invalid_scope_that_is_never_served()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f);
        var active = await ActionsVariableEnrollmentTests.Active(f, github);
        var configured = f.GitHub.Before!;
        f.GitHub.Before = async request =>
        {
            if (request.Method != HttpMethod.Post || request.RequestUri!.AbsolutePath.EndsWith("/installation/token", StringComparison.Ordinal))
                return await configured(request);
            var input = await request.Content!.ReadFromJsonAsync<JsonElement>(Token);
            if (!input.GetProperty("permissions").TryGetProperty("discussions", out _)) return await configured(request);
            return GitHubTransport.Json(new
            {
                token = "widened-runtime-token", expires_at = f.Clock.GetUtcNow().AddHours(1), repository_selection = "selected",
                permissions = new { discussions = "write", actions_variables = "read" },
                repositories = new[] { new { id = 123456, full_name = "owner/project" } }
            }, HttpStatusCode.Created);
        };

        using var runtime = await f.Send($"/v1/devices/{active.Device}/installation-token", secret: active.Secret);

        Assert.NotEqual(HttpStatusCode.OK, runtime.StatusCode);
        Assert.DoesNotContain("widened-runtime-token", await runtime.Content.ReadAsStringAsync(Token));
        Assert.Contains(f.GitHub.Requests, r => r == "DELETE /installation/token");
    }

    [Theory]
    [InlineData("Profile", "polling")]
    [InlineData("Profile", "webhook-settings")]
    [InlineData("PolicyVersion", "repository-actions-approval-v3")]
    [InlineData("PolicyVersion", "repository-actions-clone-approval-v3")]
    public async Task Durable_repository_settings_profile_is_never_reinterpreted_and_denies_before_reservation_or_provider(string field, string value)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f);
        var active = await ActionsVariableEnrollmentTests.Active(f, github, profile: "polling-settings");
        var row = (await f.Storage.Table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "device:" + active.Device, cancellationToken: Token)).Value;
        row[field] = value;
        await f.Storage.Table.UpdateEntityAsync(row, row.ETag, TableUpdateMode.Replace, Token);
        await f.RestartAsync();
        var requests = f.GitHub.Requests.Count;

        foreach (var route in new[] { 1, 2, 3 })
        {
            using var denied = await f.Send($"/v{route}/devices/{active.Device}/installation-token", secret: active.Secret);
            Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
            Assert.Equal("", await denied.Content.ReadAsStringAsync(Token));
        }
        Assert.Equal(requests, f.GitHub.Requests.Count);
        Assert.Empty(github.RuntimePermissions);
    }

    [Fact]
    public async Task Settings_device_self_revokes_only_through_its_own_runtime_route()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f);
        var active = await ActionsVariableEnrollmentTests.Active(f, github, profile: "webhook-settings");

        foreach (var frozen in new[] { 1, 2 })
        {
            using var refused = await f.Send($"/v{frozen}/devices/{active.Device}", secret: active.Secret, method: HttpMethod.Delete);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }
        using var revoke = await f.Send($"/v3/devices/{active.Device}", secret: active.Secret, method: HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var after = await f.Send($"/v3/devices/{active.Device}/installation-token", secret: active.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    private static Dictionary<string, string> Map(JsonElement permissions) =>
        permissions.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
    private static Task<JsonElement> Json(HttpResponseMessage response) => response.Content.ReadFromJsonAsync<JsonElement>(Token);
}
