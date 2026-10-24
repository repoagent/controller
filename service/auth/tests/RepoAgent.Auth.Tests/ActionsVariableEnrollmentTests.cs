using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

// Public broker HTTP and a real pinned Azurite Table; GitHub is the in-memory fake, whose
// direct repository Actions and Agent variables are separate so the observed approval
// location is explicit.
public sealed class ActionsVariableEnrollmentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fresh_Actions_variable_approval_completes_private_enrollment_and_observed_removal_closes_runtime()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var github = Repository.Configure(f);

        using var start = await f.Send("/v3/enrollments", Request());
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var pending = await Json(start);
        var pendingId = pending.GetProperty("pendingId").GetString()!;
        var marker = pending.GetProperty("marker").GetString()!;
        var approval = JsonSerializer.Deserialize<JsonElement>(marker);
        Assert.Equal(3, approval.GetProperty("version").GetInt32());
        Assert.Equal("repository-actions-variable", approval.GetProperty("approvalKind").GetString());
        Assert.Equal("repository-actions-approval-v3", approval.GetProperty("policyVersion").GetString());
        Assert.Equal("REPOAGENT_APPROVAL", approval.GetProperty("variable").GetString());
        Assert.False(approval.TryGetProperty("environment", out _));

        // Before the installer publishes, the fresh challenge is not approved.
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var early = await f.Send($"/v3/enrollments/{pendingId}/completion");
        Assert.Equal(HttpStatusCode.OK, early.StatusCode);
        Assert.Equal("pending", (await Json(early)).GetProperty("state").GetString());

        github.Actions["REPOAGENT_APPROVAL"] = marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var completion = await f.Send($"/v3/enrollments/{pendingId}/completion");
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var receipt = await Json(completion);
        Assert.True(receipt.TryGetProperty("enrollment", out var enrollment), "Fresh Actions approval did not complete: " + receipt);
        var device = enrollment.GetProperty("deviceId").GetString()!;
        var secret = enrollment.GetProperty("deviceSecret").GetString()!;
        Assert.Equal(1, enrollment.GetProperty("protocolVersion").GetInt32());
        Assert.DoesNotContain(secret, marker);
        Assert.DoesNotContain(github.Actions.Values, value => value.Contains(secret, StringComparison.Ordinal));

        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var ack = await f.Send($"/v3/devices/{device}/acknowledgment", new { completionId = receipt.GetProperty("completionId").GetString() }, secret);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var runtime = await f.Send($"/v1/devices/{device}/installation-token", secret: secret);
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        Assert.Contains("read variable actions owner/project REPOAGENT_APPROVAL", f.GitHub.Requests);
        Assert.DoesNotContain(f.GitHub.Requests, r => r.Contains(" agents ", StringComparison.Ordinal));
        Assert.Equal(1, github.VerifierMints);
        Assert.Equal("actions_variables", github.VerifierPermission);

        github.Actions.Remove("REPOAGENT_APPROVAL");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var removed = await f.Send($"/v1/devices/{device}/installation-token", secret: secret);
        Assert.Equal(HttpStatusCode.Unauthorized, removed.StatusCode);
        github.Actions["REPOAGENT_APPROVAL"] = marker;
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var restored = await f.Send($"/v1/devices/{device}/installation-token", secret: secret);
        Assert.Equal(HttpStatusCode.Unauthorized, restored.StatusCode);
    }

    // CI workflows and repository settings share the direct repository Actions variables with the
    // approval marker. Their number must never decide whether a running controller keeps its approval.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unrelated_Actions_variables_never_deny_approval_and_removal_still_closes_at_any_count(bool beforeEnrollment)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var github = Repository.Configure(f);
        void Unrelated() { foreach (var i in Enumerable.Range(0, 100)) github.Actions[$"CI_SETTING_{i:000}"] = "public"; }
        if (beforeEnrollment) Unrelated();
        var active = await Active(f, github);
        if (!beforeEnrollment) Unrelated();

        using var renewed = await f.Send($"/v1/devices/{active.Device}/installation-token", secret: active.Secret);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);

        github.Actions.Remove("REPOAGENT_APPROVAL");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var removed = await f.Send($"/v1/devices/{active.Device}/installation-token", secret: active.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, removed.StatusCode);
        github.Actions["REPOAGENT_APPROVAL"] = active.Marker;
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var restored = await f.Send($"/v1/devices/{active.Device}/installation-token", secret: active.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, restored.StatusCode);
    }

    // Repository-settings tooling writes REPOAGENT_ Actions variables; only the catalogue's reserved
    // approval name may share that prefix, so settings changes can never overwrite an approval marker.
    // Legacy Agent variables are a separate store and keep their name rules.
    [Theory]
    [InlineData(3, "repoagent_model", HttpStatusCode.BadRequest)]
    [InlineData(3, "REPOAGENT_FUTURE_SETTING", HttpStatusCode.BadRequest)]
    [InlineData(3, "repoagent_approval", HttpStatusCode.OK)]
    [InlineData(2, "REPOAGENT_MODEL", HttpStatusCode.OK)]
    public async Task Actions_variable_approval_cannot_take_a_repository_settings_name(int setupVersion, string variable, HttpStatusCode expected)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        Repository.Configure(f);
        using var start = await f.Send($"/v{setupVersion}/enrollments", Request() with { Variable = variable });
        Assert.Equal(expected, start.StatusCode);
        if (expected == HttpStatusCode.BadRequest) Assert.Empty(f.GitHub.Requests);
    }

    [Fact]
    public async Task Public_coordinates_copied_markers_or_the_wrong_variable_location_never_complete_enrollment()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var github = Repository.Configure(f);
        var actions = await Start(f, "/v3/enrollments", Request());
        // Public renderings an observer could copy: the same binding in the legacy Agent
        // shape, and an earlier enrollment's marker that differs only by its challenge.
        var approval = JsonSerializer.Deserialize<JsonElement>(actions.Marker);
        var agentEra = actions.Marker.Replace("\"version\":3,\"approvalKind\":\"repository-actions-variable\"", "\"version\":2,\"approvalKind\":\"repository-agent-variable\"", StringComparison.Ordinal)
            .Replace("repository-actions-approval-v3", "repository-agent-approval-v2", StringComparison.Ordinal);
        var earlier = actions.Marker.Replace(approval.GetProperty("challenge").GetString()!, new string('c', 43), StringComparison.Ordinal);
        Assert.NotEqual(actions.Marker, agentEra);
        Assert.NotEqual(actions.Marker, earlier);

        // The pending URL and public marker are not possession of the recovery credential.
        github.Actions["REPOAGENT_APPROVAL"] = actions.Marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var stranger = await f.Send($"/v3/enrollments/{actions.Id}/completion", secret: new string('z', 43));
        Assert.Equal(HttpStatusCode.Unauthorized, stranger.StatusCode);
        github.Actions.Clear();

        // The fresh marker published as an Agent variable is the wrong location, and
        // copied markers in the selected Actions variable do not prove this challenge.
        foreach (var (location, value) in new[] { ("agent", actions.Marker), ("actions", agentEra), ("actions", earlier) })
        {
            github.Agent.Clear();
            github.Actions.Clear();
            (location == "agent" ? github.Agent : github.Actions)["REPOAGENT_APPROVAL"] = value;
            github.Actions["OTHER_APPROVAL"] = actions.Marker;
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            await AssertPending(f, $"/v3/enrollments/{actions.Id}/completion");
        }

        github.Agent.Clear();
        github.Actions.Clear();
        github.Actions["REPOAGENT_APPROVAL"] = actions.Marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var approved = await f.Send($"/v3/enrollments/{actions.Id}/completion");
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.True((await Json(approved)).TryGetProperty("enrollment", out _));
    }

    // Which answers prove a change is the core's rule (ApprovalObserverTests); this is the runtime route's outcome.
    [Theory]
    [InlineData("denied", false)]
    [InlineData("changed", true)]
    [InlineData("removed", true)]
    public async Task Runtime_reads_live_Actions_approval_and_only_confirmed_change_closes(string observation, bool terminal)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var github = Repository.Configure(f);
        var active = await Active(f, github);
        if (observation == "denied") f.GitHub.VariableAnswer = _ => new GitHubRead<GitHubVariable>.Refused();
        if (observation == "changed") github.Actions["REPOAGENT_APPROVAL"] = active.Marker.Replace("\"version\":3", "\"version\":2", StringComparison.Ordinal);
        if (observation == "removed") github.Actions.Remove("REPOAGENT_APPROVAL");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var observed = await f.Send($"/v1/devices/{active.Device}/installation-token", secret: active.Secret);
        Assert.Equal(terminal ? HttpStatusCode.Unauthorized : HttpStatusCode.ServiceUnavailable, observed.StatusCode);

        f.GitHub.VariableAnswer = null;
        github.Actions["REPOAGENT_APPROVAL"] = active.Marker;
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var after = await f.Send($"/v1/devices/{active.Device}/installation-token", secret: active.Secret);
        Assert.Equal(terminal ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Legacy_Agent_grant_keeps_its_own_location_and_setup_versions_never_cross()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var github = Repository.Configure(f);
        var request = Request() with { Variable = "APPROVAL" };
        var (pending, marker) = await Start(f, "/v2/enrollments", request);
        Assert.Equal(2, JsonSerializer.Deserialize<JsonElement>(marker).GetProperty("version").GetInt32());
        github.Agent["APPROVAL"] = marker;

        // A pending Agent setup is never resumed, completed or acknowledged through v3.
        var reads = f.GitHub.Reads;
        var mints = f.GitHub.Mints;
        using var crossedStart = await f.Send("/v3/enrollments", request);
        using var crossedStatus = await f.Send($"/v3/enrollments/{pending}", method: HttpMethod.Get);
        using var crossedCompletion = await f.Send($"/v3/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.Conflict, crossedStart.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, crossedStatus.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, crossedCompletion.StatusCode);
        Assert.Equal((reads, mints), (f.GitHub.Reads, f.GitHub.Mints));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var completion = await f.Send($"/v2/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var receipt = await Json(completion);
        var device = receipt.GetProperty("enrollment").GetProperty("deviceId").GetString()!;
        var secret = receipt.GetProperty("enrollment").GetProperty("deviceSecret").GetString()!;
        var completionId = new { completionId = receipt.GetProperty("completionId").GetString() };
        reads = f.GitHub.Reads;
        using var crossedAck = await f.Send($"/v3/devices/{device}/acknowledgment", completionId, secret);
        Assert.Equal(HttpStatusCode.Conflict, crossedAck.StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var ack = await f.Send($"/v2/devices/{device}/acknowledgment", completionId, secret);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var runtime = await f.Send($"/v1/devices/{device}/installation-token", secret: secret);
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        Assert.Equal("agent_variables", github.VerifierPermission);
        Assert.DoesNotContain(f.GitHub.Requests, r => r.Contains(" actions ", StringComparison.Ordinal));

        // Copying the Agent approval into Actions variables neither sustains nor moves it.
        github.Actions["APPROVAL"] = marker;
        github.Agent.Remove("APPROVAL");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var moved = await f.Send($"/v1/devices/{device}/installation-token", secret: secret);
        Assert.Equal(HttpStatusCode.Unauthorized, moved.StatusCode);
        Assert.DoesNotContain(f.GitHub.Requests, r => r.Contains(" actions ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("polling-clone")]
    [InlineData("webhook-clone")]
    public async Task Clone_profiles_issue_the_explicit_v3_clone_family_without_widening_runtime_permissions(string profile)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var github = Repository.Configure(f);
        var active = await Active(f, github, profile: profile);
        Assert.Equal(2, active.Enrollment.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(profile, active.Enrollment.GetProperty("profile").GetString());
        Assert.Equal("repository-actions-clone-approval-v3", active.Enrollment.GetProperty("policyVersion").GetString());
        var expected = profile == "webhook-clone" ? new[] { "contents", "discussions", "repository_hooks" } : ["contents", "discussions"];
        Assert.Equal(expected, active.Enrollment.GetProperty("permissions").EnumerateObject().Select(p => p.Name).Order());

        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var v1 = await f.Send($"/v1/devices/{active.Device}/installation-token", secret: active.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, v1.StatusCode);
        using var lease = await f.Send($"/v2/devices/{active.Device}/installation-token", secret: active.Secret);
        Assert.Equal(HttpStatusCode.OK, lease.StatusCode);
        var body = await Json(lease);
        Assert.Equal("repository-actions-clone-approval-v3", body.GetProperty("policyVersion").GetString());
        Assert.Equal(expected, body.GetProperty("permissions").EnumerateObject().Select(p => p.Name).Order());
    }

    internal static RepositoryVariableEnrollmentRequest Request(string profile = "polling") =>
        new(Guid.NewGuid().ToString("N"), 123456, "owner/project", "repoagent_approval", profile);
    private static Task<JsonElement> Json(HttpResponseMessage response) => response.Content.ReadFromJsonAsync<JsonElement>(Token);
    private static async Task<(string Id, string Marker)> Start(EnrollmentFixture f, string route, RepositoryVariableEnrollmentRequest request, string? secret = null)
    {
        using var start = await f.Send(route, request, secret);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var body = await Json(start);
        return (body.GetProperty("pendingId").GetString()!, body.GetProperty("marker").GetString()!);
    }
    private static async Task AssertPending(EnrollmentFixture f, string completion, string? secret = null)
    {
        using var response = await f.Send(completion, secret: secret);
        var text = await response.Content.ReadAsStringAsync(Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("deviceSecret", text);
        Assert.Equal("pending", JsonSerializer.Deserialize<JsonElement>(text).GetProperty("state").GetString());
    }

    internal sealed record ActiveGrant(RepositoryVariableEnrollmentRequest Request, string Pending, string Marker, string Device, string Secret, string Completion, JsonElement Enrollment);

    // Setup v2 uses the legacy Agent location; setup v3 the Actions location. The request
    // variable is preserved so a legacy grant can be compared with copied v3 coordinates.
    internal static async Task<ActiveGrant> Active(EnrollmentFixture f, Repository github, int version = 3, string profile = "polling")
    {
        var request = version == 3 ? Request(profile) : Request(profile) with { Variable = "APPROVAL" };
        var (pending, marker) = await Start(f, $"/v{version}/enrollments", request);
        (version == 3 ? github.Actions : github.Agent)[request.Variable.ToUpperInvariant()] = marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var completion = await f.Send($"/v{version}/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var receipt = await Json(completion);
        var enrollment = receipt.GetProperty("enrollment");
        var grant = new ActiveGrant(request, pending, marker, enrollment.GetProperty("deviceId").GetString()!,
            enrollment.GetProperty("deviceSecret").GetString()!, receipt.GetProperty("completionId").GetString()!, enrollment);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var ack = await f.Send($"/v{version}/devices/{grant.Device}/acknowledgment", new { completionId = grant.Completion }, grant.Secret);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        return grant;
    }

    // The repository's Actions and Agent variables, and the tokens GitHub mints for it. No broker policy lives here.
    internal sealed class Repository(InMemoryGitHub github)
    {
        public Dictionary<string, string> Actions => github.Actions;
        public Dictionary<string, string> Agent => github.Agent;
        public int VerifierMints;
        public string? VerifierPermission;
        public List<Dictionary<string, string>> RuntimePermissions { get; } = [];

        public static Repository Configure(EnrollmentFixture f)
        {
            var repository = new Repository(f.GitHub);
            f.GitHub.Before = async request =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    var input = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(Token));
                    var permissions = input.GetProperty("permissions");
                    Assert.Equal(123456, Assert.Single(input.GetProperty("repository_ids").EnumerateArray()).GetInt64());
                    var names = permissions.EnumerateObject().Select(p => p.Name).ToArray();
                    if (names is [var permission] && permission.EndsWith("variables", StringComparison.Ordinal))
                    {
                        // The broker-internal verifier reads approval with exactly one read-only grant.
                        Assert.Equal("read", permissions.GetProperty(permission).GetString());
                        repository.VerifierPermission = permission;
                        repository.VerifierMints++;
                        return InMemoryGitHub.Json(new
                        {
                            token = "actions-verifier-token", expires_at = f.Clock.GetUtcNow().AddHours(1),
                            repository_selection = "selected", permissions,
                            repositories = new[] { new { id = 123456, full_name = "owner/project" } }
                        }, HttpStatusCode.Created);
                    }
                    // Runtime credentials never write variables or read other approval locations; only
                    // the explicit repository-settings profiles may add Actions variables read (asserted per test).
                    Assert.DoesNotContain(names, name => name is "actions" or "environments" or "variables" or "secrets" or "agent_variables");
                    if (permissions.TryGetProperty("actions_variables", out var variables)) Assert.Equal("read", variables.GetString());
                    Assert.Equal("write", permissions.GetProperty("discussions").GetString());
                    repository.RuntimePermissions.Add(permissions.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!));
                    return InMemoryGitHub.Json(new
                    {
                        token = "runtime-token-" + f.GitHub.Mints, expires_at = f.Clock.GetUtcNow().Add(f.GitHub.RuntimeLifetime),
                        repository_selection = "selected", permissions,
                        repositories = new[] { new { id = 123456, full_name = "owner/project" } }
                    }, HttpStatusCode.Created);
                }
                return null;
            };
            return repository;
        }
    }
}
