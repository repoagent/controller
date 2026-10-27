using System.Net;
using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class AgentVariableConformanceTests
{
    // Which answers prove a withdrawal is the core's rule (ApprovalObserverTests); this is the completion route's outcome.
    [Fact]
    public async Task Unverifiable_approval_read_denies_completion_without_closing_and_a_fresh_positive_read_recovers()
    {
        var f = AgentFixture();
        var pending = await Start(f);
        var receipt = await Complete(f, pending);
        // GitHub answers the named read with another variable, whose value the response must not disclose.
        f.GitHub.VariableAnswer = _ => GitHubRead.Found(new GitHubVariable("UNRELATED", InMemoryGitHub.PrivateValue));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var denied = await f.Complete(pending, 2);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        var body = denied.Text;
        Assert.DoesNotContain("deviceSecret", body);
        Assert.DoesNotContain(InMemoryGitHub.PrivateValue, body);

        f.GitHub.VariableAnswer = null;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await f.Complete(pending, 2);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(receipt.ToString(), recovered.Json.ToString());
        await Ack(f, receipt);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var runtime = await f.Issue(Device(receipt), Secret(receipt));
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        var result = runtime.Json;
        Assert.Equal(1, result.GetProperty("protocolVersion").GetInt32());
        Assert.False(result.GetProperty("permissions").TryGetProperty("agent_variables", out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Versions_share_idempotency_but_cannot_replay_setup_completion_or_ack(int version)
    {
        var f = AgentFixture();
        var agent = Request();
        var legacy = InMemoryBroker.Request() with { IdempotencyId = agent.IdempotencyId };
        // Setup v1 mints its own verifier; the Agent mint handler would refuse its permissions.
        if (version == 1) f.GitHub.BeforeMint = null;
        var start = version == 1 ? await f.Start(legacy) : await f.Start(agent, 2);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var body = start.Json;
        var pending = body.GetProperty("pendingId").GetString()!;
        f.GitHub.Marker = body.GetProperty("marker").GetString()!;
        var mints = f.GitHub.Mints;
        var crossed = version == 1 ? await f.Start(agent, 2) : await f.Start(legacy);
        Assert.Equal(HttpStatusCode.Conflict, crossed.StatusCode);
        var reads = f.GitHub.Reads;
        var status = await f.Status(pending, 3 - version);
        var completion = await f.Complete(pending, 3 - version);
        Assert.Equal(HttpStatusCode.Conflict, status.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, completion.StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
        Assert.Equal(mints, f.GitHub.Mints);
        Assert.Single(f.Rows(), r => r.Scope == InMemoryBroker.Partition && r.Name.StartsWith("pending:", StringComparison.Ordinal));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var receipt = await Complete(f, pending, version);
        reads = f.GitHub.Reads;
        var ack = await f.Acknowledge(Device(receipt), receipt.GetProperty("completionId").GetString()!, Secret(receipt), 3 - version);
        Assert.Equal(HttpStatusCode.Conflict, ack.StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await f.Complete(pending, version);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(receipt.ToString(), recovered.Json.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_incumbent_or_unknown_authority_blocks_Agent_completion(bool unknown)
    {
        var f = AgentFixture();
        var agentHandler = f.GitHub.BeforeMint;
        f.GitHub.BeforeMint = _ => unknown ? throw new GitHubUnavailable("Unknown legacy mint outcome") : Task.CompletedTask;
        var legacy = await f.Start(InMemoryBroker.Request());
        Assert.Equal(unknown ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, legacy.StatusCode);
        string? incumbent = null;
        if (!unknown)
        {
            var body = legacy.Json;
            f.GitHub.Marker = body.GetProperty("marker").GetString()!;
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            incumbent = Device(await Complete(f, body.GetProperty("pendingId").GetString()!, 1));
        }
        var before = f.Rows().Single(r => r.Name.StartsWith("attempt:", StringComparison.Ordinal));
        f.GitHub.BeforeMint = agentHandler;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var historical = await Retained.HistoricalDiscovery(f, () => f.Start(Request(), 2));
        Assert.Equal(HttpStatusCode.OK, historical.StatusCode);
        var pending = historical.Json.GetProperty("pendingId").GetString()!;
        f.GitHub.Marker = historical.Json.GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var reads = f.GitHub.Reads;
        var completion = await f.Complete(pending, 2);
        Assert.Equal(HttpStatusCode.Conflict, completion.StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
        Assert.Equal(before.Version, f.State.Version(before.Scope, before.Name));
        if (unknown) Assert.DoesNotContain(f.Rows(), r => r.Name.StartsWith("device:", StringComparison.Ordinal));
        else Assert.Equal(incumbent, f.Gate.OwnerEnrollmentId);
    }

    // Which reported scopes are out of scope is the core's rule (GitHubTokenMinterTests); "extra" is the service's outcome.
    [Theory]
    [InlineData("extra")]
    [InlineData("independent-name")]
    public async Task Invalid_Agent_verifier_binding_never_discloses_marker_and_known_tokens_stay_accounted(string fault)
    {
        var f = AgentFixture();
        var accountedBeforeRevoke = false;
        if (fault == "independent-name")
            f.GitHub.Repositories = GitHubRead.Found(new InstallationRepositories(1, [new RepositoryIdentity(123456, "other/project")]));
        else f.GitHub.MintAnswer = (_, token) => GitHubRead.Found(token with
        {
            Value = "invalid-binding-token",
            Permissions = new Dictionary<string, string?> { ["agent_variables"] = "read", ["actions"] = "read" }
        });
        f.GitHub.BeforeRevoke = _ =>
        {
            var known = Assert.Single(f.Rows(), r => r.Name.StartsWith("attempt:", StringComparison.Ordinal)).Read<IssuanceAttempt>();
            accountedBeforeRevoke = known.State == "known" && known.ProtectedToken is not null && known.ScopeValid == false;
            return Task.CompletedTask;
        };
        var start = await f.Start(Request(), 2);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, start.StatusCode);
        Assert.Equal("enrollment_settlement_unconfirmed", start.Json.GetProperty("code").GetString());
        Assert.Equal(["code"], start.Json.EnumerateObject().Select(field => field.Name));
        var attempt = Assert.Single(f.Rows(), r => r.Name.StartsWith("attempt:", StringComparison.Ordinal)).Read<IssuanceAttempt>();
        Assert.DoesNotContain(f.Rows(), r => r.Name.StartsWith("device:", StringComparison.Ordinal));
        if (fault != "independent-name")
        {
            Assert.True(accountedBeforeRevoke);
            Assert.Equal(1, f.GitHub.Revokes);
            Assert.Equal("settled", attempt.State);
        }
        else Assert.NotNull(attempt.ProtectedToken);
    }

    [Theory]
    [InlineData("missing-both")]
    [InlineData("missing-kind")]
    [InlineData("missing-version")]
    [InlineData("unknown-kind")]
    [InlineData("wrong-policy")]
    public async Task Incomplete_or_conflicting_durable_discriminators_cannot_select_Agent_runtime(string fault)
    {
        var f = AgentFixture();
        var receipt = await Complete(f, await Start(f));
        await Ack(f, receipt);
        f.State.Change(ControllerEnrollment.Key(InMemoryBroker.RepositoryId, Device(receipt)), row =>
        {
            if (fault == "missing-both") { row.Remove("ApprovalKind"); row.Remove("SetupVersion"); }
            if (fault == "missing-kind") row.Remove("ApprovalKind");
            if (fault == "missing-version") row.Remove("SetupVersion");
            if (fault == "unknown-kind") row["ApprovalKind"] = "unknown";
            if (fault == "wrong-policy") row["PolicyVersion"] = IssuancePolicy.Version;
        });
        var reads = f.GitHub.Reads;
        var denied = await f.Issue(Device(receipt), Secret(receipt));
        Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
        Assert.Equal("", denied.Text);
        Assert.Equal(reads, f.GitHub.Reads);
    }

    [Theory]
    [InlineData("polling")]
    [InlineData("webhook")]
    public async Task V1_runtime_and_self_revoke_dispatch_from_authenticated_Agent_device(string profile)
    {
        var f = AgentFixture();
        var receipt = await Complete(f, await Start(f, profile));
        await Ack(f, receipt);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var issued = await f.Issue(Device(receipt), Secret(receipt));
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var permissions = issued.Json.GetProperty("permissions");
        Assert.Equal("write", permissions.GetProperty("discussions").GetString());
        Assert.Equal(profile == "webhook", permissions.TryGetProperty("repository_hooks", out _));
        Assert.Equal(profile == "webhook" ? 2 : 1, permissions.EnumerateObject().Count());
        var revoked = await f.Revoke(Device(receipt), Secret(receipt));
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var runtime = await f.Issue(Device(receipt), Secret(receipt));
        Assert.Equal(HttpStatusCode.Unauthorized, runtime.StatusCode);
        Assert.Equal("closed", f.Gate.State);
        Assert.Equal("revoked", f.Enrollment(Device(receipt)).State);
    }

    [Fact]
    public async Task Legacy_protected_completion_cannot_recover_Agent_receipt_even_with_same_keys_and_binding()
    {
        var f = AgentFixture();
        var pending = await Start(f);
        var receipt = await Complete(f, pending);
        var key = InMemoryBroker.PendingKey(pending);
        var original = f.State.Get(key)!.ProtectedCompletion!;
        // Authentic legacy purpose with the same secret protection, repository, epoch,
        // pending ID and plaintext. Only the approval protocol domain differs.
        var legacy = ApprovalContract.Legacy.CompletionPurpose(InMemoryBroker.RepositoryId, InMemoryBroker.Epoch, pending);
        f.State.Change(key, row => row["ProtectedCompletion"] = f.Secrets.Protect(legacy, receipt.GetProperty("enrollment").GetRawText()));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var denied = await f.Complete(pending, 2);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        Assert.DoesNotContain(Secret(receipt), denied.Text);
        f.State.Change(key, row => row["ProtectedCompletion"] = original);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await f.Complete(pending, 2);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(receipt.ToString(), recovered.Json.ToString());
    }

    [Theory]
    [InlineData("installation")]
    [InlineData("legacy-marker")]
    public async Task Confirmed_changed_binding_or_copied_legacy_marker_closes_Agent_approval(string change)
    {
        var f = AgentFixture();
        var pending = await Start(f);
        await Complete(f, pending);
        var marker = f.GitHub.Marker;
        if (change == "installation") f.GitHub.Installation = Moved;
        else
        {
            f.GitHub.Agent["APPROVAL"] = marker.Replace("\"version\":2", "\"version\":1", StringComparison.Ordinal);
            Assert.NotEqual(marker, f.GitHub.Agent["APPROVAL"]);
        }
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var changed = await f.Complete(pending, 2);
        Assert.DoesNotContain("deviceSecret", changed.Text);
        f.GitHub.Installation = GitHubRead.Found(InMemoryGitHub.Installed);
        f.GitHub.Marker = marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var restored = await f.Complete(pending, 2);
        Assert.Equal(HttpStatusCode.Gone, restored.StatusCode);
    }

    [Fact]
    public async Task Stale_changed_runtime_observation_denies_without_closure_and_fresh_approval_recovers()
    {
        var f = AgentFixture();
        var receipt = await Complete(f, await Start(f));
        await Ack(f, receipt);
        var marker = f.GitHub.Marker;
        f.GitHub.Agent["APPROVAL"] = InMemoryGitHub.PrivateValue;
        var variableReads = 0;
        f.GitHub.BeforeRead = request =>
        {
            if (!request.StartsWith("variable ", StringComparison.Ordinal)) return Task.CompletedTask;
            variableReads++;
            // The changed answer is well formed but its oldest contributing read
            // is now 11 s old: outside freshness (10 s), inside the operation lifetime (15 s).
            f.Clock.Advance(TimeSpan.FromSeconds(11));
            return Task.CompletedTask;
        };
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var stale = await f.Issue(Device(receipt), Secret(receipt));
        Assert.NotEqual(HttpStatusCode.OK, stale.StatusCode);
        Assert.Equal(1, variableReads);
        var body = stale.Text;
        Assert.DoesNotContain("token", body);
        Assert.DoesNotContain(InMemoryGitHub.PrivateValue, body);

        f.GitHub.BeforeRead = null;
        f.GitHub.Marker = marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await f.Issue(Device(receipt), Secret(receipt));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(Device(receipt), f.Gate.OwnerEnrollmentId);
    }

    // Which installation and repository answers prove a binding change is the core's rule (ApprovalObserverTests);
    // these are the runtime and continuation routes' outcomes.
    [Theory]
    [InlineData("installation", true)]
    [InlineData("installation-stale", false)]
    [InlineData("denied", false)]
    public async Task Runtime_binding_change_is_terminal_only_when_well_formed_and_fresh(string observation, bool terminal)
    {
        var f = AgentFixture();
        var receipt = await Complete(f, await Start(f));
        await Ack(f, receipt);
        var observed = ObserveInstallationReads(f, stale: observation.EndsWith("-stale", StringComparison.Ordinal));
        f.GitHub.Installation = observation == "denied" ? new GitHubRead<AppInstallation>.Refused() : Moved;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var changed = await f.Issue(Device(receipt), Secret(receipt));
        Assert.True(observed());
        Assert.NotEqual(HttpStatusCode.OK, changed.StatusCode);
        Assert.DoesNotContain("token", changed.Text);

        f.GitHub.BeforeRead = null;
        f.GitHub.Installation = GitHubRead.Found(InMemoryGitHub.Installed);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var restored = await f.Issue(Device(receipt), Secret(receipt));
        Assert.Equal(terminal ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, restored.StatusCode);
        if (terminal) Assert.Equal("closed", f.Gate.State);
    }

    [Theory]
    [InlineData("empty", false, false)]
    [InlineData("installation-stale", true, false)]
    [InlineData("installation-fresh", true, true)]
    [InlineData("repository-name-fresh", true, true)]
    public async Task Completion_and_ack_binding_uncertainty_is_recoverable_but_fresh_changes_are_terminal(string observation, bool acknowledge, bool terminal)
    {
        var f = AgentFixture();
        var pending = await Start(f);
        var receipt = await Complete(f, pending);
        var installation = observation.StartsWith("installation-", StringComparison.Ordinal);
        var observed = installation ? ObserveInstallationReads(f, stale: observation.EndsWith("-stale", StringComparison.Ordinal))
            : ObserveRepositoryReads(f);
        if (installation) f.GitHub.Installation = Moved;
        else f.GitHub.Repositories = GitHubRead.Found(new InstallationRepositories(1,
            observation == "empty" ? [] : [new RepositoryIdentity(123456, "other/project")]));
        Task<Answer> Continue() => acknowledge
            ? f.Acknowledge(Device(receipt), receipt.GetProperty("completionId").GetString()!, Secret(receipt), 2)
            : f.Complete(pending, 2);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var denied = await Continue();
        Assert.True(observed());
        Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
        Assert.DoesNotContain(Secret(receipt), denied.Text);

        f.GitHub.BeforeRead = null;
        f.GitHub.Installation = GitHubRead.Found(InMemoryGitHub.Installed);
        f.GitHub.Repositories = GitHubRead.Found(new InstallationRepositories(1, [InMemoryGitHub.Project]));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var restored = await Continue();
        Assert.Equal(terminal ? HttpStatusCode.Gone : HttpStatusCode.OK, restored.StatusCode);
        if (terminal) return;
        if (!acknowledge)
        {
            Assert.Equal(receipt.ToString(), restored.Json.ToString());
            await Ack(f, receipt);
        }
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var runtime = await f.Issue(Device(receipt), Secret(receipt));
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
    }

    // The same App now installed under another installation id.
    private static GitHubRead<AppInstallation> Moved => GitHubRead.Found(InMemoryGitHub.Installed with { Id = 790 });

    // Records whether the installation was read; a stale read lands 11 s late, outside freshness (10 s).
    private static Func<bool> ObserveInstallationReads(InMemoryBroker f, bool stale)
    {
        var observed = false;
        f.GitHub.BeforeRead = request =>
        {
            if (!request.StartsWith("installation ", StringComparison.Ordinal)) return Task.CompletedTask;
            observed = true;
            if (stale) f.Clock.Advance(TimeSpan.FromSeconds(11));
            return Task.CompletedTask;
        };
        return () => observed;
    }
    private static Func<bool> ObserveRepositoryReads(InMemoryBroker f)
    {
        var observed = false;
        f.GitHub.BeforeRead = request =>
        {
            observed |= request == "repositories";
            return Task.CompletedTask;
        };
        return () => observed;
    }

    private static InMemoryBroker AgentFixture()
    {
        var f = new InMemoryBroker();
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        return f;
    }
    private static RepositoryVariableEnrollmentRequest Request(string profile = "polling") => new(Guid.NewGuid().ToString("N"), 123456, "owner/project", "APPROVAL", profile);
    private static string Device(JsonElement receipt) => receipt.GetProperty("enrollment").GetProperty("deviceId").GetString()!;
    private static string Secret(JsonElement receipt) => receipt.GetProperty("enrollment").GetProperty("deviceSecret").GetString()!;
    private static async Task<string> Start(InMemoryBroker f, string profile = "polling")
    {
        var start = await f.Start(Request(profile), 2);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var body = start.Json;
        f.GitHub.Marker = body.GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        return body.GetProperty("pendingId").GetString()!;
    }
    private static async Task<JsonElement> Complete(InMemoryBroker f, string pending, int version = 2)
    {
        var response = await f.Complete(pending, version);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = response.Json;
        Assert.Equal(1, receipt.GetProperty("enrollment").GetProperty("protocolVersion").GetInt32());
        return receipt;
    }
    private static async Task Ack(InMemoryBroker f, JsonElement receipt)
    {
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var response = await f.Acknowledge(Device(receipt), receipt.GetProperty("completionId").GetString()!, Secret(receipt), 2);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
