using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class CurrentBrokerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static JsonElement Body(IssuanceResult answer) => JsonSerializer.SerializeToElement(answer.Body, JsonSerializerOptions.Web);

    public static TheoryData<int, string> Profiles
    {
        get
        {
            var result = new TheoryData<int, string>();
            foreach (var version in Enumerable.Range(1, 5))
                foreach (var profile in new[] { "polling", "webhook", "polling-clone", "webhook-clone", "polling-settings", "webhook-settings", "polling-clone-settings", "webhook-clone-settings" })
                    if (version == 1 ? profile is "polling" or "webhook" : version != 2 || !profile.EndsWith("-settings", StringComparison.Ordinal))
                        result.Add(version, profile);
            return result;
        }
    }
    [Theory]
    [MemberData(nameof(Profiles))]
    public async Task Setup_and_runtime_preserve_each_wire_profile_without_storing_tokens(int setup, string profile)
    {
        var (f, id, secret) = await Active(setup, profile);
        var runtime = (setup == 1 ? ApprovalContract.Legacy : ApprovalContract.ForSetup(setup, profile)).RuntimeVersion;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var answer = await f.Issuer().IssueAsync(id, secret, Token, runtime);
        Assert.Equal(200, answer.Status);
        Assert.Equal(runtime, Body(answer).GetProperty("protocolVersion").GetInt32());
        Assert.Contains("runtime-token-", Body(answer).GetProperty("token").GetString());
        AssertNoTokens(f);
        Assert.Equal(2, f.State.All().Count);
        Assert.Null(f.Row.SetupSecretHash);
        Assert.Null(f.Row.ProtectedCompletion);
        Assert.Null(f.Row.PendingId);
    }

    [Fact]
    public async Task Hundreds_of_renewals_mint_fresh_tokens_and_never_grow_storage()
    {
        var (f, id, secret) = await Active();
        var previous = "";
        for (var i = 0; i < 250; i++)
        {
            f.Clock.Advance(TimeSpan.FromHours(1));
            var answer = await f.Issuer().IssueAsync(id, secret, Token);
            Assert.Equal(200, answer.Status);
            var token = Body(answer).GetProperty("token").GetString();
            Assert.NotEqual(previous, token);
            previous = token!;
            AssertNoTokens(f);
            Assert.Equal(2, f.State.All().Count);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Lost_mint_result_leaves_enrollment_usable_and_next_request_mints_again(bool verifier)
    {
        var (f, id, secret) = await Active();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.GitHub.BeforeMint = m => m.Verifier == verifier ? throw new GitHubUnavailable("lost response") : Task.CompletedTask;
        Assert.Equal(503, (await f.Issuer().IssueAsync(id, secret, Token)).Status);
        Assert.Equal("active", f.Row.State);
        Assert.Equal(2, f.State.All().Count);
        AssertNoTokens(f);
        f.GitHub.BeforeMint = null;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(200, (await f.Issuer().IssueAsync(id, secret, Token)).Status);
    }

    [Fact]
    public async Task Revocation_during_mint_cannot_publish_a_stale_token_or_leave_a_secret()
    {
        var (f, id, secret) = await Active();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.GitHub.BeforeMint = async m =>
        {
            if (!m.Verifier) Assert.Equal(204, await new ControllerSelfRevocation(f.State, InMemoryBroker.Epoch, f.Clock).RevokeAsync(id, secret, Token));
        };
        Assert.Equal(503, (await f.Issuer().IssueAsync(id, secret, Token)).Status);
        Assert.Equal("closed", f.Row.State);
        Assert.Null(f.Row.ControllerSecretHash);
        Assert.Contains(f.GitHub.Revoked, t => t.Value.StartsWith("runtime-token-", StringComparison.Ordinal));
        Assert.Equal(401, (await f.Issuer().IssueAsync(id, secret, Token)).Status);
    }

    [Fact]
    public async Task Approval_withdrawal_during_mint_closes_the_grant_and_restoring_marker_cannot_reopen_it()
    {
        var (f, id, secret) = await Active();
        var marker = f.GitHub.Marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.GitHub.BeforeMint = m => { if (!m.Verifier) f.GitHub.Marker = ""; return Task.CompletedTask; };
        Assert.Equal(401, (await f.Issuer().IssueAsync(id, secret, Token)).Status);
        f.GitHub.Marker = marker;
        Assert.Equal(401, (await f.Issuer().IssueAsync(id, secret, Token)).Status);
        Assert.Equal("closed", f.Row.State);
    }

    [Fact]
    public async Task Lost_activity_commit_response_does_not_require_reenrollment_or_reuse_a_token()
    {
        var (f, id, secret) = await Active();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.State.AfterCommit = tx => tx.Writes.Count == 1 ? throw new BrokerStateUnavailable("lost reply") : Task.CompletedTask;
        Assert.Equal(503, (await f.Issuer().IssueAsync(id, secret, Token)).Status);
        Assert.Equal("active", f.Row.State);
        f.State.AfterCommit = null;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(200, (await f.Issuer().IssueAsync(id, secret, Token)).Status);
        AssertNoTokens(f);
    }

    [Fact]
    public async Task Wrong_secret_runtime_version_and_restore_epoch_deny_before_provider_work()
    {
        var (f, id, secret) = await Active();
        var mints = f.GitHub.Mints;
        Assert.Equal(401, (await f.Issuer().IssueAsync(id, new string('z', 43), Token)).Status);
        Assert.Equal(401, (await f.Issuer().IssueAsync(id, secret, Token, 2)).Status);
        f.DeploymentEpoch = "new-restore-epoch";
        Assert.Equal(401, (await f.Issuer().IssueAsync(id, secret, Token)).Status);
        Assert.Equal(mints, f.GitHub.Mints);
    }

    [Fact]
    public async Task Missing_schema_metadata_is_not_initialized_or_inferred_from_legacy_rows()
    {
        var f = new InMemoryBroker();
        f.State.Remove(CurrentBrokerQuota.Key);
        var answer = await f.Enrollment().StartRepositoryVariableAsync(Request(), InMemoryBroker.SetupSecret, Token, 5);
        Assert.Equal(503, answer.Status);
        Assert.Empty(f.State.All());
        Assert.Equal(0, f.GitHub.Mints);
    }

    [Fact]
    public async Task Quota_failure_performs_no_provider_work_and_no_partial_counter_write()
    {
        var (f, id, secret) = await Active();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.Limits = new(RepositoryMints: 1);
        var before = f.State.All();
        var mints = f.GitHub.Mints;
        Assert.Equal(429, (await f.Issuer().IssueAsync(id, secret, Token)).Status);
        Assert.Equal(mints, f.GitHub.Mints);
        Assert.Equal(before.Select(r => r.Version), f.State.All().Select(r => r.Version));
    }

    [Fact]
    public async Task Completed_setup_can_be_recovered_then_acknowledgment_erases_recovery_secrets()
    {
        var f = new InMemoryBroker();
        var start = await f.Enrollment().StartRepositoryVariableAsync(Request(), InMemoryBroker.SetupSecret, Token, 5);
        Assert.Equal(200, start.Status);
        var id = Body(start).GetProperty("pendingId").GetString()!;
        f.GitHub.Marker = Body(start).GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.State.AfterCommit = tx => tx.Writes.Count == 1 ? throw new BrokerStateUnavailable("lost completion reply") : Task.CompletedTask;
        Assert.Equal(503, (await f.Enrollment().CompleteAsync(id, InMemoryBroker.SetupSecret, Token, 5)).Status);
        var completion = f.Row.CompletionId;
        f.State.AfterCommit = null;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await f.Enrollment().CompleteAsync(id, InMemoryBroker.SetupSecret, Token, 5);
        Assert.Equal(200, recovered.Status);
        Assert.Equal(completion, Body(recovered).GetProperty("completionId").GetString());
        var envelope = Body(recovered).GetProperty("enrollment");
        var enrollmentId = envelope.GetProperty("enrollmentId").GetString()!;
        var secret = envelope.GetProperty("controllerSecret").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(200, (await f.Enrollment().AcknowledgeAsync(enrollmentId, completion!, secret, Token, 5)).Status);
        var version = f.State.Version(CurrentRepository.Key(123456));
        var expiry = f.Row.ExpiresAt;
        Assert.Equal(200, (await f.Enrollment().AcknowledgeAsync(enrollmentId, completion!, secret, Token, 5)).Status);
        Assert.Equal(version, f.State.Version(CurrentRepository.Key(123456)));
        Assert.Equal(expiry, f.Row.ExpiresAt);
        Assert.Null(f.Row.ProtectedCompletion);
        Assert.Null(f.Row.SetupSecretHash);
        AssertNoTokens(f);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Malformed_counters_are_denied_even_after_the_minute_changes(bool global)
    {
        var (f, id, secret) = await Active();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        StateKey key = global ? CurrentBrokerQuota.Key : CurrentRepository.Key(123456);
        f.State.Change(key, row => row.Remove("Mints"));
        var mints = f.GitHub.Mints;
        var commits = f.State.Commits;
        Assert.Equal(503, (await f.Issuer().IssueAsync(id, secret, Token)).Status);
        Assert.Equal(mints, f.GitHub.Mints);
        Assert.Equal(commits, f.State.Commits);
    }

    [Fact]
    public async Task Fresh_setup_after_restore_requires_a_new_epoch_challenge_and_never_reopens_old_enrollment()
    {
        var (f, oldId, oldSecret) = await Active();
        var oldMarker = f.Row.Marker;
        f.DeploymentEpoch = "new-restore-epoch";
        Assert.Equal(401, (await f.Issuer().IssueAsync(oldId, oldSecret, Token)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var setup = await f.Enrollment().StartRepositoryVariableAsync(Request(), InMemoryBroker.SetupSecret, Token, 5);
        Assert.Equal(200, setup.Status);
        Assert.NotEqual(oldId, f.Row.EnrollmentId);
        Assert.NotEqual(oldMarker, f.Row.Marker);
        Assert.Equal(401, (await f.Issuer().IssueAsync(oldId, oldSecret, Token)).Status);
        Assert.Equal(2, f.State.All().Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Delayed_final_storage_or_verifier_disposal_cannot_publish_a_stale_runtime_token(bool disposal)
    {
        var (f, id, secret) = await Active();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        if (disposal) f.GitHub.BeforeRevoke = _ => { f.Clock.Advance(TimeSpan.FromSeconds(11)); return Task.CompletedTask; };
        else f.State.AfterCommit = tx =>
        {
            if (tx.Writes.Count == 1) f.Clock.Advance(TimeSpan.FromSeconds(11));
            return Task.CompletedTask;
        };
        Assert.Equal(503, (await f.Issuer().IssueAsync(id, secret, Token)).Status);
        Assert.Equal("active", f.Row.State);
        AssertNoTokens(f);
    }

    [Fact]
    public async Task Delayed_verifier_revocation_cannot_publish_a_stale_setup_approval()
    {
        var f = new InMemoryBroker();
        var setup = await f.Enrollment().StartRepositoryVariableAsync(Request(), InMemoryBroker.SetupSecret, Token, 5);
        Assert.Equal(200, setup.Status);
        f.GitHub.Marker = f.Row.Marker!;
        var id = f.Row.PendingId!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.GitHub.BeforeRevoke = _ => { f.Clock.Advance(TimeSpan.FromSeconds(11)); return Task.CompletedTask; };
        Assert.Equal(503, (await f.Enrollment().CompleteAsync(id, InMemoryBroker.SetupSecret, Token, 5)).Status);
        Assert.Equal("pending", f.Row.State);
        Assert.Null(f.Row.ControllerSecretHash);
        Assert.Null(f.Row.ProtectedCompletion);
    }

    [Fact]
    public async Task Invalid_runtime_scope_is_never_returned_or_persisted()
    {
        var (f, id, secret) = await Active();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.GitHub.MintAnswer = (mint, token) => GitHubRead.Found(mint.Verifier ? token : token with { Repositories = [new(999, "other/repository")] });
        Assert.Equal(503, (await f.Issuer().IssueAsync(id, secret, Token)).Status);
        AssertNoTokens(f);
        Assert.Contains(f.GitHub.Revoked, t => t.Value.StartsWith("runtime-token-", StringComparison.Ordinal));
    }

    internal static RepositoryVariableEnrollmentRequest Request() => new(Guid.NewGuid().ToString("N"), 123456, "owner/project", "APPROVAL", "polling");
    internal static async Task<(InMemoryBroker Broker, string Id, string Secret)> Active(int setup = 5, string profile = "polling")
    {
        var f = new InMemoryBroker();
        var input = Request() with { Profile = profile };
        var start = setup == 1
            ? await f.Enrollment().StartAsync(new(input.IdempotencyId, input.RepositoryId, input.Repository, "approval/environment", input.Variable, profile), InMemoryBroker.SetupSecret, Token)
            : await f.Enrollment().StartRepositoryVariableAsync(input, InMemoryBroker.SetupSecret, Token, setup);
        Assert.Equal(200, start.Status);
        var pending = Body(start).GetProperty("pendingId").GetString()!;
        var marker = Body(start).GetProperty("marker").GetString()!;
        f.GitHub.Marker = marker;
        f.GitHub.Actions["APPROVAL"] = marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var complete = await f.Enrollment().CompleteAsync(pending, InMemoryBroker.SetupSecret, Token, setup);
        Assert.Equal(200, complete.Status);
        var envelope = Body(complete).GetProperty("enrollment");
        var canonical = setup >= 5;
        var id = envelope.GetProperty(canonical ? "enrollmentId" : "deviceId").GetString()!;
        var secret = envelope.GetProperty(canonical ? "controllerSecret" : "deviceSecret").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(200, (await f.Enrollment().AcknowledgeAsync(id, Body(complete).GetProperty("completionId").GetString()!, secret, Token, setup)).Status);
        return (f, id, secret);
    }
    internal static void AssertNoTokens(InMemoryBroker f)
    {
        foreach (var row in f.State.All())
        {
            Assert.DoesNotContain(row.Row.Keys, k => k.Contains("Token", StringComparison.Ordinal));
            Assert.DoesNotContain(row.Row.Values.OfType<string>(), v => v.Contains("runtime-token-", StringComparison.Ordinal) || v.Contains("verifier-token", StringComparison.Ordinal));
        }
    }
}
