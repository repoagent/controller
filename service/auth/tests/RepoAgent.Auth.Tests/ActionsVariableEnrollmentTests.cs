using System.Net;
using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

// The broker's enrollment and issuance over the in-memory broker state; GitHub is the in-memory fake, whose direct
// repository Actions and Agent variables are separate so the observed approval location is explicit.
public sealed class ActionsVariableEnrollmentTests
{

    [Fact]
    public async Task Fresh_Actions_variable_approval_completes_private_enrollment_and_observed_removal_closes_runtime()
    {
        var f = new InMemoryBroker();
        var github = Repository.Configure(f.GitHub);

        var start = await f.Start(Request(), 3);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var pending = start.Json;
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
        var early = await f.Complete(pendingId, 3);
        Assert.Equal(HttpStatusCode.OK, early.StatusCode);
        Assert.Equal("pending", early.Json.GetProperty("state").GetString());

        github.Actions["REPOAGENT_APPROVAL"] = marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var completion = await f.Complete(pendingId, 3);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var receipt = completion.Json;
        Assert.True(receipt.TryGetProperty("enrollment", out var enrollment), "Fresh Actions approval did not complete: " + receipt);
        var device = enrollment.GetProperty("deviceId").GetString()!;
        var secret = enrollment.GetProperty("deviceSecret").GetString()!;
        Assert.Equal(1, enrollment.GetProperty("protocolVersion").GetInt32());
        Assert.DoesNotContain(secret, marker);
        Assert.DoesNotContain(github.Actions.Values, value => value.Contains(secret, StringComparison.Ordinal));

        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var ack = await f.Acknowledge(device, receipt.GetProperty("completionId").GetString()!, secret, 3);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var runtime = await f.Issue(device, secret);
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        // The issuer's exact v1 body. The adapter's no-store header is untested until #204.
        Assert.Equal(9, runtime.Json.EnumerateObject().Count());
        Assert.Contains("read variable actions owner/project REPOAGENT_APPROVAL", f.GitHub.Requests);
        Assert.DoesNotContain(f.GitHub.Requests, r => r.Contains(" agents ", StringComparison.Ordinal));
        Assert.Equal(1, github.VerifierMints);
        Assert.Equal("actions_variables", github.VerifierPermission);

        github.Actions.Remove("REPOAGENT_APPROVAL");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var removed = await f.Issue(device, secret);
        Assert.Equal(HttpStatusCode.Unauthorized, removed.StatusCode);
        github.Actions["REPOAGENT_APPROVAL"] = marker;
                f.Clock.Advance(TimeSpan.FromMinutes(1));
        var restored = await f.Issue(device, secret);
        Assert.Equal(HttpStatusCode.Unauthorized, restored.StatusCode);
    }

    // CI workflows and repository settings share the direct repository Actions variables with the
    // approval marker. Their number must never decide whether a running controller keeps its approval.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unrelated_Actions_variables_never_deny_approval_and_removal_still_closes_at_any_count(bool beforeEnrollment)
    {
        var f = new InMemoryBroker();
        var github = Repository.Configure(f.GitHub);
        void Unrelated() { foreach (var i in Enumerable.Range(0, 100)) github.Actions[$"CI_SETTING_{i:000}"] = "public"; }
        if (beforeEnrollment) Unrelated();
        var active = await Active(f, github);
        if (!beforeEnrollment) Unrelated();

        var renewed = await f.Issue(active.Device, active.Secret);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);

        github.Actions.Remove("REPOAGENT_APPROVAL");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var removed = await f.Issue(active.Device, active.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, removed.StatusCode);
        github.Actions["REPOAGENT_APPROVAL"] = active.Marker;
                f.Clock.Advance(TimeSpan.FromMinutes(1));
        var restored = await f.Issue(active.Device, active.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, restored.StatusCode);
    }

    // Repository-settings tooling writes REPOAGENT_ repository variables; only the catalogue's reserved
    // approval name may share that prefix, so settings changes can never overwrite an approval marker.
    [Theory]
    [InlineData(3, "repoagent_model", HttpStatusCode.BadRequest)]
    [InlineData(3, "REPOAGENT_FUTURE_SETTING", HttpStatusCode.BadRequest)]
    [InlineData(3, "repoagent_approval", HttpStatusCode.OK)]
    [InlineData(4, "REPOAGENT_MODEL", HttpStatusCode.BadRequest)]
    [InlineData(4, "repoagent_approval", HttpStatusCode.OK)]
    [InlineData(2, "REPOAGENT_MODEL", HttpStatusCode.BadRequest)]
    [InlineData(2, "APPROVAL", HttpStatusCode.OK)]
    public async Task Repository_variable_approval_cannot_take_a_repository_settings_name(int setupVersion, string variable, HttpStatusCode expected)
    {
        var f = new InMemoryBroker();
        Repository.Configure(f.GitHub);
        var start = await f.Start(Request() with { Variable = variable }, setupVersion);
        Assert.Equal(expected, start.StatusCode);
        if (expected == HttpStatusCode.BadRequest) Assert.Empty(f.GitHub.Requests);
    }

    [Fact]
    public async Task Public_coordinates_copied_markers_or_the_wrong_variable_location_never_complete_enrollment()
    {
        var f = new InMemoryBroker();
        var github = Repository.Configure(f.GitHub);
        var actions = await Start(f, 3, Request());
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
        var stranger = await f.Complete(actions.Id, 3, new string('z', 43));
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
            await AssertPending(f, actions.Id);
        }

        github.Agent.Clear();
        github.Actions.Clear();
        github.Actions["REPOAGENT_APPROVAL"] = actions.Marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var approved = await f.Complete(actions.Id, 3);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.True(approved.Json.TryGetProperty("enrollment", out _));
    }

    // Which answers prove a change is the core's rule (ApprovalObserverTests); this is the runtime route's outcome.
    [Theory]
    [InlineData("denied", false)]
    [InlineData("changed", true)]
    [InlineData("removed", true)]
    public async Task Runtime_reads_live_Actions_approval_and_only_confirmed_change_closes(string observation, bool terminal)
    {
        var f = new InMemoryBroker();
        var github = Repository.Configure(f.GitHub);
        var active = await Active(f, github);
        if (observation == "denied") f.GitHub.VariableAnswer = _ => new GitHubRead<GitHubVariable>.Refused();
        if (observation == "changed") github.Actions["REPOAGENT_APPROVAL"] = active.Marker.Replace("\"version\":3", "\"version\":2", StringComparison.Ordinal);
        if (observation == "removed") github.Actions.Remove("REPOAGENT_APPROVAL");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var observed = await f.Issue(active.Device, active.Secret);
        Assert.Equal(terminal ? HttpStatusCode.Unauthorized : HttpStatusCode.ServiceUnavailable, observed.StatusCode);

        f.GitHub.VariableAnswer = null;
        github.Actions["REPOAGENT_APPROVAL"] = active.Marker;
                f.Clock.Advance(TimeSpan.FromMinutes(1));
        var after = await f.Issue(active.Device, active.Secret);
        Assert.Equal(terminal ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task Legacy_Agent_grant_keeps_its_own_location_and_setup_versions_never_cross()
    {
        var f = new InMemoryBroker();
        var github = Repository.Configure(f.GitHub);
        var request = Request() with { Variable = "APPROVAL" };
        var (pending, marker) = await Start(f, 2, request);
        Assert.Equal(2, JsonSerializer.Deserialize<JsonElement>(marker).GetProperty("version").GetInt32());
        github.Agent["APPROVAL"] = marker;

        // A pending Agent setup is never resumed, completed or acknowledged through v3.
        var reads = f.GitHub.Reads;
        var mints = f.GitHub.Mints;
        var crossedStart = await f.Start(request, 3);
        var crossedStatus = await f.Status(pending, 3);
        var crossedCompletion = await f.Complete(pending, 3);
        Assert.Equal(HttpStatusCode.Conflict, crossedStart.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, crossedStatus.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, crossedCompletion.StatusCode);
        Assert.Equal((reads, mints), (f.GitHub.Reads, f.GitHub.Mints));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var completion = await f.Complete(pending, 2);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var receipt = completion.Json;
        var device = receipt.GetProperty("enrollment").GetProperty("deviceId").GetString()!;
        var secret = receipt.GetProperty("enrollment").GetProperty("deviceSecret").GetString()!;
        var completionId = receipt.GetProperty("completionId").GetString()!;
        reads = f.GitHub.Reads;
        var crossedAck = await f.Acknowledge(device, completionId, secret, 3);
        Assert.Equal(HttpStatusCode.Conflict, crossedAck.StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var ack = await f.Acknowledge(device, completionId, secret, 2);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var runtime = await f.Issue(device, secret);
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        Assert.Equal("agent_variables", github.VerifierPermission);
        Assert.DoesNotContain(f.GitHub.Requests, r => r.Contains(" actions ", StringComparison.Ordinal));

        // Copying the Agent approval into Actions variables neither sustains nor moves it.
        github.Actions["APPROVAL"] = marker;
        github.Agent.Remove("APPROVAL");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var moved = await f.Issue(device, secret);
        Assert.Equal(HttpStatusCode.Unauthorized, moved.StatusCode);
        Assert.DoesNotContain(f.GitHub.Requests, r => r.Contains(" actions ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(3, "polling-clone", "repository-actions-clone-approval-v3")]
    [InlineData(3, "webhook-clone", "repository-actions-clone-approval-v3")]
    [InlineData(4, "polling-clone", "repository-agent-clone-approval-v4")]
    [InlineData(4, "webhook-clone", "repository-agent-clone-approval-v4")]
    public async Task Clone_profiles_issue_their_setups_clone_family_without_widening_runtime_permissions(int setupVersion, string profile, string policy)
    {
        var f = new InMemoryBroker();
        var github = Repository.Configure(f.GitHub);
        var active = await Active(f, github, setupVersion, profile);
        Assert.Equal(2, active.Enrollment.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(profile, active.Enrollment.GetProperty("profile").GetString());
        Assert.Equal(policy, active.Enrollment.GetProperty("policyVersion").GetString());
        var expected = profile == "webhook-clone" ? new[] { "contents", "discussions", "repository_hooks" } : ["contents", "discussions"];
        Assert.Equal(expected, active.Enrollment.GetProperty("permissions").EnumerateObject().Select(p => p.Name).Order());

        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var v1 = await f.Issue(active.Device, active.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, v1.StatusCode);
        var lease = await f.Issue(active.Device, active.Secret, 2);
        Assert.Equal(HttpStatusCode.OK, lease.StatusCode);
        var body = lease.Json;
        Assert.Equal(policy, body.GetProperty("policyVersion").GetString());
        Assert.Equal(expected, body.GetProperty("permissions").EnumerateObject().Select(p => p.Name).Order());
    }

    internal static RepositoryVariableEnrollmentRequest Request(string profile = "polling") =>
        new(Guid.NewGuid().ToString("N"), 123456, "owner/project", "repoagent_approval", profile);
    internal static async Task<(string Id, string Marker)> Start(InMemoryBroker f, int setupVersion, RepositoryVariableEnrollmentRequest request)
    {
        var start = await f.Start(request, setupVersion);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        return (start.Json.GetProperty("pendingId").GetString()!, start.Json.GetProperty("marker").GetString()!);
    }
    private static async Task AssertPending(InMemoryBroker f, string pending)
    {
        var response = await f.Complete(pending, 3);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("deviceSecret", response.Text);
        Assert.Equal("pending", response.Json.GetProperty("state").GetString());
    }

    internal sealed record ActiveGrant(RepositoryVariableEnrollmentRequest Request, string Pending, string Marker, string Device, string Secret, string Completion, JsonElement Enrollment);

    // Setup v2 and v4 use the Agent location; setup v3 the Actions location. A v2 grant keeps its
    // legacy APPROVAL variable, so it can be compared with copied v3 and v4 coordinates.
    internal static async Task<ActiveGrant> Active(InMemoryBroker f, Repository github, int version = 3, string profile = "polling")
    {
        var request = version == 2 ? Request(profile) with { Variable = "APPROVAL" } : Request(profile);
        var (pending, marker) = await Start(f, version, request);
        (version == 3 ? github.Actions : github.Agent)[request.Variable.ToUpperInvariant()] = marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var completion = await f.Complete(pending, version);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var receipt = completion.Json;
        var enrollment = receipt.GetProperty("enrollment");
        var grant = new ActiveGrant(request, pending, marker, enrollment.GetProperty("deviceId").GetString()!,
            enrollment.GetProperty("deviceSecret").GetString()!, receipt.GetProperty("completionId").GetString()!, enrollment);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await f.Acknowledge(grant.Device, grant.Completion, grant.Secret, version)).StatusCode);
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

        public static Repository Configure(InMemoryGitHub github)
        {
            var repository = new Repository(github);
            github.BeforeMint = mint =>
            {
                Assert.Equal(InMemoryGitHub.Project.Id, mint.Repository.Id);
                var permissions = mint.Authority.Permissions;
                if (permissions.Keys.ToArray() is [var permission] && permission.EndsWith("variables", StringComparison.Ordinal))
                {
                    // The broker-internal verifier reads approval with exactly one read-only grant.
                    Assert.Equal("read", permissions[permission]);
                    repository.VerifierPermission = permission;
                    repository.VerifierMints++;
                    return Task.CompletedTask;
                }
                // Runtime credentials never write variables or read other approval locations; only the explicit
                // repository-settings profiles may add one variables store's read (asserted per test).
                Assert.DoesNotContain(permissions.Keys, name => name is "actions" or "environments" or "variables" or "secrets");
                Assert.All(permissions.Where(p => p.Key.EndsWith("variables", StringComparison.Ordinal)), p => Assert.Equal("read", p.Value));
                Assert.True(permissions.Keys.Count(name => name.EndsWith("variables", StringComparison.Ordinal)) <= 1);
                Assert.Equal("write", permissions["discussions"]);
                repository.RuntimePermissions.Add(permissions.ToDictionary(p => p.Key, p => p.Value));
                return Task.CompletedTask;
            };
            return repository;
        }
    }
}
