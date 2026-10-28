using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

// Enrollment under lost commit responses, competing writers and restarts. A broker state commit is one Table
// transaction; an AfterCommit hook that throws BrokerStateUnavailable loses the response of a transaction that did
// commit. Each broker call builds its services afresh, as a restarted process would.
public sealed class EnrollmentFaultTests
{
    private static Task Lost(string what) => throw new BrokerStateUnavailable(what);

    [Fact]
    public async Task Lost_global_admissions_remain_bounded_and_duplicate_restart_resumes_one_reservation()
    {
        var f = new InMemoryBroker();
        f.State.AfterCommit = _ => Lost("Lost committed admission");
        var original = InMemoryBroker.Request();
        for (var i = 0; i < 64; i++)
        {
            var request = i == 0 ? original : InMemoryBroker.Request() with { RepositoryId = 123456 + i / 4 };
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(request)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Start(InMemoryBroker.Request() with { RepositoryId = 999999 })).StatusCode);
        var rows = f.Rows();
        Assert.Equal(82, rows.Count); // 64 ledgers, 16 directories, two initialized quotas.
        Assert.Single(rows, row => row.Key == "global:issuance-v1/quota");
        Assert.All(rows.Where(row => row.Scope != "global:issuance-v1"), row => Assert.Equal("global:enrollment-v1", row.Scope));
        Assert.Equal(64, f.State.Get(EnrollmentQuota.Key)!.Count);
        Assert.Equal(0, f.GitHub.Reads);
        Assert.Equal(0, f.GitHub.Mints);
        var deadline = f.State.Get(EnrollmentLedger.Key(original.IdempotencyId))!.Deadline;
        f.State.AfterCommit = null;
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.Start(original)));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await f.Start(original);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(deadline, recovered.Json.GetProperty("deadline").GetDateTimeOffset());
        Assert.Equal(1, f.GitHub.Mints);
        rows = f.Rows();
        Assert.Equal(64, rows.Count(r => r.Scope == "global:enrollment-v1" && r.Name.StartsWith("pending:", StringComparison.Ordinal)));
        Assert.Single(rows, r => r.Scope == InMemoryBroker.Partition && r.Name.StartsWith("pending:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_completion_commit_or_locator_response_recovers_one_encrypted_controller_enrollment_after_restart(bool locator)
    {
        var f = new InMemoryBroker();
        var pending = await Start(f);
        var commits = 0;
        var injected = false;
        f.State.AfterCommit = _ =>
        {
            // Work claim, global charge, repository charge, the completion commit, then the locator's own transaction.
            if (++commits == (locator ? 5 : 4) && !injected)
            {
                injected = true;
                return Lost("Lost completion persistence response");
            }
            return Task.CompletedTask;
        };
        var lost = await f.Complete(pending);
        Assert.True(injected);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, lost.StatusCode);
        var enrollmentId = Assert.Single(f.Rows(), r => r.Name.StartsWith("device:", StringComparison.Ordinal)).Read<ControllerEnrollment>();
        var hash = enrollmentId.ControllerSecretHash;
        Assert.False(enrollmentId.Acknowledged);
        f.State.AfterCommit = null;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await f.Complete(pending);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(recovered.Text, (await f.Complete(pending)).Text);
        enrollmentId = Assert.Single(f.Rows(), r => r.Name.StartsWith("device:", StringComparison.Ordinal)).Read<ControllerEnrollment>();
        Assert.Equal(hash, enrollmentId.ControllerSecretHash);
        Assert.Equal(1, f.GitHub.Mints);
    }

    // Which answers prove a withdrawal or a binding change is the core's rule (ApprovalObserverTests);
    // this is the setup v1 completion's outcome for one of each.
    [Theory]
    [InlineData("absent", true)]
    [InlineData("404", false)]
    [InlineData("environment", true)]
    public async Task Only_verified_withdrawal_or_binding_change_closes_observed_approval(string observation, bool closed)
    {
        var f = new InMemoryBroker();
        var pending = await Start(f);
        var completion = await f.Complete(pending);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var marker = f.GitHub.Marker;
        var environment = f.GitHub.Environment;
        if (observation == "absent") f.GitHub.Marker = "";
        if (observation == "404") f.GitHub.PageAnswer = (_, _) => new GitHubRead<GitHubVariablePage>.NotFound();
        if (observation == "environment") f.GitHub.Environment = GitHubRead.Found(new ApprovalEnvironment(988, "approval/environment"));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.DoesNotContain("deviceSecret", (await f.Complete(pending)).Text);
        f.GitHub.Marker = marker;
        f.GitHub.PageAnswer = null;
        f.GitHub.Environment = environment;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var retry = await f.Complete(pending);
        Assert.Equal(closed ? HttpStatusCode.Gone : HttpStatusCode.OK, retry.StatusCode);
        if (!closed) Assert.Equal(completion.Text, retry.Text);
        else Assert.Equal("closed", f.Pending(pending).State);
    }

    [Fact]
    public async Task Repeated_observations_share_durable_floor_and_do_not_grow_rows_or_extend_deadline()
    {
        var f = new InMemoryBroker();
        var pending = await Start(f, approve: false);
        var initial = f.Pending(pending);
        var rows = f.Rows().Count;
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await f.Complete(pending)).StatusCode);
            var reads = f.GitHub.Reads;
            for (var j = 0; j < 3; j++) Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Complete(pending)).StatusCode);
            Assert.Equal(reads, f.GitHub.Reads);
            Assert.Equal(rows, f.Rows().Count);
            f.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.Equal(initial.Deadline, f.Pending(pending).Deadline);
        Assert.Equal(1, f.GitHub.Mints);
        f.Clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(HttpStatusCode.Gone, (await f.Complete(pending)).StatusCode);
    }

    [Fact]
    public async Task Revoke_during_ack_observation_wins_ETag_conflict_and_never_reactivates()
    {
        var f = new InMemoryBroker();
        var pending = await Start(f);
        var delivered = (await f.Complete(pending)).Json;
        var envelope = delivered.GetProperty("enrollment");
        var enrollmentId = envelope.GetProperty("deviceId").GetString()!;
        var secret = envelope.GetProperty("deviceSecret").GetString()!;
        var revoked = false;
        f.GitHub.BeforeRead = async request =>
        {
            if (!revoked && request.StartsWith("variables ", StringComparison.Ordinal))
            {
                revoked = true;
                Assert.Equal(HttpStatusCode.NoContent, (await f.Revoke(enrollmentId, secret)).StatusCode);
            }
        };
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var completionId = delivered.GetProperty("completionId").GetString()!;
        var ack = await f.Acknowledge(enrollmentId, completionId, secret);
        Assert.True(revoked);
        Assert.NotEqual(HttpStatusCode.OK, ack.StatusCode);
        f.GitHub.BeforeRead = null;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotEqual(HttpStatusCode.OK, (await f.Acknowledge(enrollmentId, completionId, secret)).StatusCode);
        Assert.Equal("revoked", f.Enrollment(enrollmentId).State);
    }

    [Fact]
    public async Task Lost_repository_admissions_count_toward_four_pending_limit_before_any_provider_work()
    {
        var f = new InMemoryBroker();
        var commits = 0;
        // The ledger commits; the gate and pending enrollment commit and their response is lost.
        f.State.AfterCommit = _ => ++commits % 2 == 0 ? Lost("Lost committed repository admission") : Task.CompletedTask;
        var original = InMemoryBroker.Request();
        for (var i = 0; i < 4; i++)
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(i == 0 ? original : InMemoryBroker.Request())).StatusCode);
        var before = f.Rows();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Start(InMemoryBroker.Request())).StatusCode);
        Assert.Equal(before.Count, f.Rows().Count);
        Assert.Equal(4, before.Count(r => r.Scope == InMemoryBroker.Partition && r.Name.StartsWith("pending:", StringComparison.Ordinal)));
        Assert.Equal(4, f.Gate.PendingCount);
        Assert.Equal(0, f.GitHub.Reads);
        Assert.Equal(0, f.GitHub.Mints);
        f.State.AfterCommit = null;
        Assert.Equal(HttpStatusCode.OK, (await f.Start(original)).StatusCode);
        Assert.Equal(1, f.GitHub.Mints);
    }

    [Fact]
    public async Task Unknown_other_pending_authority_is_never_replayed_or_borrowed_for_completion()
    {
        var f = new InMemoryBroker();
        var original = InMemoryBroker.Request();
        var normal = f.GitHub.BeforeMint;
        f.GitHub.BeforeMint = _ => throw new GitHubUnavailable("Unknown GitHub mint outcome");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(original)).StatusCode);
        var attempt = Assert.Single(f.Rows(), r => r.Name.StartsWith("attempt:", StringComparison.Ordinal));
        Assert.Equal("sending", attempt.Read<IssuanceAttempt>().State);
        f.GitHub.BeforeMint = normal;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(original)).StatusCode);
        Assert.Equal(1, f.GitHub.Mints);
        var historical = await Retained.HistoricalDiscovery(f, () => f.Start(InMemoryBroker.Request()));
        Assert.Equal(HttpStatusCode.OK, historical.StatusCode);
        var next = historical.Json.GetProperty("pendingId").GetString()!;
        f.GitHub.Marker = historical.Json.GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var reads = f.GitHub.Reads;
        Assert.Equal(HttpStatusCode.Conflict, (await f.Complete(next)).StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
        Assert.DoesNotContain(f.Rows(), r => r.Name.StartsWith("device:", StringComparison.Ordinal));
        Assert.Equal(2, f.Gate.OutstandingTokenCount);
        Assert.Equal(attempt.Version, f.State.Version(attempt.Scope, attempt.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Original_deadline_or_changed_epoch_prevents_recovery_and_acknowledgment(bool epoch)
    {
        var f = new InMemoryBroker();
        var pending = await Start(f);
        var completion = await f.Complete(pending);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var delivered = completion.Json;
        var enrollmentId = delivered.GetProperty("enrollment").GetProperty("deviceId").GetString()!;
        var secret = delivered.GetProperty("enrollment").GetProperty("deviceSecret").GetString()!;
        if (epoch) f.DeploymentEpoch = "restored-epoch";
        else f.Clock.Advance(f.Pending(pending).Deadline!.Value - f.Clock.GetUtcNow());
        var reads = f.GitHub.Reads;
        Assert.Equal(epoch ? HttpStatusCode.Unauthorized : HttpStatusCode.Gone, (await f.Complete(pending)).StatusCode);
        var ack = await f.Acknowledge(enrollmentId, delivered.GetProperty("completionId").GetString()!, secret);
        Assert.Equal(epoch ? HttpStatusCode.Unauthorized : HttpStatusCode.Gone, ack.StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
        Assert.False(f.Enrollment(enrollmentId).Acknowledged);
    }

    [Fact]
    public async Task Duplicate_active_ack_after_restart_does_not_extend_inactivity_or_retain_recovery_authority()
    {
        var f = new InMemoryBroker();
        var pending = await Start(f);
        var delivered = (await f.Complete(pending)).Json;
        var enrollmentId = delivered.GetProperty("enrollment").GetProperty("deviceId").GetString()!;
        var secret = delivered.GetProperty("enrollment").GetProperty("deviceSecret").GetString()!;
        var completionId = delivered.GetProperty("completionId").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await f.Acknowledge(enrollmentId, completionId, secret)).StatusCode);
        var key = ControllerEnrollment.Key(InMemoryBroker.RepositoryId, enrollmentId);
        var (initial, version) = (f.State.Get(key)!, f.State.Version(key));
        f.Clock.Advance(TimeSpan.FromDays(1));
        var reads = f.GitHub.Reads;
        Assert.Equal(HttpStatusCode.OK, (await f.Acknowledge(enrollmentId, completionId, secret)).StatusCode);
        var current = f.State.Get(key)!;
        Assert.Equal(version, f.State.Version(key));
        Assert.Equal(initial.LastActivityAt, current.LastActivityAt);
        Assert.Equal(initial.ExpiresAt, current.ExpiresAt);
        Assert.Equal(reads, f.GitHub.Reads);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Complete(pending)).StatusCode);
        var stored = f.State.Row(InMemoryBroker.PendingKey(pending))!;
        Assert.False(stored.ContainsKey("ProtectedCompletion"));
        Assert.False(stored.ContainsKey("RecoveryHash"));
    }

    [Fact]
    public async Task New_discovery_preserves_incumbent_and_cannot_transfer_its_verifier_authority()
    {
        var f = new InMemoryBroker();
        var first = await Start(f);
        Assert.Equal(HttpStatusCode.OK, (await f.Complete(first)).StatusCode);
        var incumbent = f.Gate;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var historical = await Retained.HistoricalDiscovery(f, () => f.Start(InMemoryBroker.Request()));
        Assert.Equal(HttpStatusCode.OK, historical.StatusCode);
        var second = historical.Json.GetProperty("pendingId").GetString()!;
        f.GitHub.Marker = historical.Json.GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var gate = f.Gate;
        Assert.Equal(incumbent.OwnerEnrollmentId, gate.OwnerEnrollmentId);
        Assert.Equal(incumbent.VerifierAttempt, gate.VerifierAttempt);
        Assert.Equal("unacknowledged", gate.State);
        var reads = f.GitHub.Reads;
        Assert.Equal(HttpStatusCode.Conflict, (await f.Complete(second)).StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
        Assert.Single(f.Rows(), r => r.Name.StartsWith("device:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    public async Task Resumed_unsent_verifier_uses_original_JWT_claims_and_cannot_refresh_original_expiry(int delaySeconds)
    {
        var f = new InMemoryBroker();
        var commits = 0;
        var interrupted = false;
        f.State.AfterCommit = _ =>
        {
            // Admission, repository state, work claim, global work, then the persisted mint attempt.
            // Lose that last response before signing/discovery/send claim can run.
            if (++commits != 5) return Task.CompletedTask;
            interrupted = true;
            return Lost("Lost reserved verifier response before signing");
        };
        var request = InMemoryBroker.Request();
        var initial = await f.Start(request);
        Assert.True(interrupted);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, initial.StatusCode);
        var original = Assert.Single(f.Rows(), row => row.Name.StartsWith("attempt:", StringComparison.Ordinal)).Read<IssuanceAttempt>();
        Assert.Equal("reserved", original.State);
        Assert.Equal(0, f.GitHub.Reads);
        Assert.Equal(0, f.GitHub.Mints);
        Assert.Equal(0, f.GitHub.Authorizations);
        int? sentAuthorization = null;
        f.GitHub.BeforeMint = mint =>
        {
            sentAuthorization = ((InMemoryGitHub.Authorization)mint.App).Number;
            return Task.CompletedTask;
        };
        f.State.AfterCommit = null;
        f.Clock.Advance(TimeSpan.FromSeconds(delaySeconds));
        var resumed = await f.Start(request);
        var persisted = Assert.Single(f.Rows(), row => row.Name.StartsWith("attempt:", StringComparison.Ordinal)).Read<IssuanceAttempt>();
        Assert.Equal(original.ReservedAt, persisted.ReservedAt);
        Assert.Equal(original.ReservedAt!.Value.AddHours(24), persisted.PayloadPurgeAt);
        var ledger = f.State.Get(IssuanceLedger.Key(original.OperationId!))!;
        Assert.Equal(original.Generation, ledger.Generation);
        Assert.Equal(persisted.PayloadPurgeAt, ledger.PayloadPurgeAt);
        Assert.Equal(original.JwtIssuedAt, persisted.JwtIssuedAt);
        Assert.Equal(original.JwtExpiresAt, persisted.JwtExpiresAt);
        if (delaySeconds == 120)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resumed.StatusCode);
            Assert.Empty(f.GitHub.Windows);
            Assert.Null(sentAuthorization);
            Assert.Equal(0, f.GitHub.Reads);
            Assert.Equal(0, f.GitHub.Mints);
            Assert.Equal("reserved", persisted.State);
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
            Assert.Equal(1, f.GitHub.Mints);
            // One authorization, over the original reserved window, signs the resumed mint.
            var window = Assert.Single(f.GitHub.Windows);
            Assert.Equal(original.JwtIssuedAt, window.IssuedAt);
            Assert.Equal(original.JwtExpiresAt, window.ExpiresAt);
            Assert.Equal(1, sentAuthorization);
        }
    }

    private static async Task<string> Start(InMemoryBroker f, bool approve = true)
    {
        var start = await f.Start(InMemoryBroker.Request());
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        if (approve) f.GitHub.Marker = start.Json.GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        return start.Json.GetProperty("pendingId").GetString()!;
    }
}
