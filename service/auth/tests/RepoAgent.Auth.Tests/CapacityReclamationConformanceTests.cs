using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

// Capacity reclamation against the writers it races, over the in-memory broker state. A reclamation that overtakes an
// enrollment or issuance writer fences it, and the winner of a send claim and an unsent reclamation decides the GitHub
// mint and the exact charge. A stale global release loses once its slot is reused, and occupancy stays charged while an
// attempt's outcome is unknown or untrusted. A lost commit response converges on retry without a second refund.
public sealed class CapacityReclamationConformanceTests
{
    static readonly string[] ExpectedSlots = ["slot:000", "slot:001"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("pending")]
    [InlineData("enrollment-mint")]
    [InlineData("runtime-mint")]
    [InlineData("runtime-verifier")]
    public async Task Reclaimed_global_only_admission_cannot_publish_after_its_repository_fence(string stage)
    {
        var f = new InMemoryBroker();
        var active = stage.StartsWith("runtime", StringComparison.Ordinal) ? await Retained.ActiveAgent(f) : null;
        if (stage == "runtime-verifier") f.Clock.Advance(TimeSpan.FromMinutes(56));
        if (active is null) AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        var old = Retained.IssuanceLedgers(f);
        // Hold the repository publication that follows the committed global admission.
        var pause = new CommitPause(f.State, t => t.Writes("State", stage == "pending" ? "discovering" : "reserved"));
        var id = NewId();
        var writer = active is null ? f.Start(Retained.AgentRequest(id), 2) : f.Issue(active.Device, active.Secret);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            StateKey target;
            if (stage == "pending")
            {
                var ledger = Retained.EnrollmentLedger(f, id).Value;
                target = PendingControllerEnrollment.Key(Retained.Repository, id);
                Assert.Null(f.State.Row(target));
                f.Clock.Advance(ledger.Deadline!.Value - f.Clock.GetUtcNow());
                await f.Reclamation().ReclaimEnrollmentAsync(id, ledger.Generation!, Token);
            }
            else
            {
                var ledger = Assert.Single(Retained.IssuanceLedgers(f), x => old.All(b => b.Value.OperationId != x.Value.OperationId)).Value;
                target = Retained.Attempt(ledger);
                Assert.Null(f.State.Row(target));
                await Retained.ReclaimIssuance(f, ledger);
            }
            var fenced = f.State.Version(target);
            Assert.NotNull(fenced);
            var tokens = f.Gate.OutstandingTokenCount;
            pause.Release.TrySetResult();
            Assert.NotEqual(HttpStatusCode.OK, (await writer).StatusCode);
            Assert.Equal(fenced, f.State.Version(target));
            Assert.Equal(tokens, f.Gate.OutstandingTokenCount);
            Assert.Equal(active is null ? 0 : 1, f.GitHub.Mints);
            Assert.Equal(old.Count, Slots(f).Count);
        }
        finally { pause.Release.TrySetResult(); await writer; }
    }

    [Fact]
    public async Task Stale_global_cleanup_commit_loses_after_another_worker_releases_and_reuses_its_slot()
    {
        var f = new InMemoryBroker { Limits = new(GlobalAttempts: 1) };
        _ = await Retained.StartAgent(f);
        var old = Assert.Single(Retained.IssuanceLedgers(f)).Value;
        f.Clock.Advance(f.State.Get(Retained.Attempt(old))!.ExpiresAt!.Value.AddMinutes(5) - f.Clock.GetUtcNow());
        // The first global release is the stale worker's; the other worker's passes.
        var pause = new CommitPause(f.State, t => t.Writes("CapacityState", "released"));
        var stale = Retained.ReclaimIssuance(f, old);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            var proof = ReleaseProof.Issuance(Retained.Repository, old.OperationId!);
            var obligation = f.State.Version(proof);
            Assert.NotNull(obligation);
            await Retained.ReclaimIssuance(f, old);
            _ = await Retained.StartAgent(f);
            var slot = IssuanceSlot.Key(0);
            var replacement = f.State.Read(slot)!;
            Assert.NotEqual(old.Generation, replacement.Value.Generation);
            pause.Release.TrySetResult();
            await Assert.ThrowsAsync<StateConflict>(() => stale);
            Assert.Equal(replacement.Version, f.State.Version(slot));
            Assert.Equal(1L, Quota(f).Slots);
            Assert.Equal(2L, Quota(f).RetainedIssuanceCount);
            Assert.Equal(obligation, f.State.Version(proof));
        }
        finally
        {
            pause.Release.TrySetResult();
            try { await stale; } catch (Exception ex) when (ex is StateConflict or BrokerStateUnavailable) { }
        }
    }

    [Fact]
    public async Task Positive_provider_revocation_releases_invalid_scope_token_without_waiting_for_expiry()
    {
        var f = new InMemoryBroker();
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        f.GitHub.MintAnswer = (_, token) => GitHubRead.Found(token with { Value = "invalid-scope-token", OnlyListedRepositories = false });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(Retained.AgentRequest(NewId()), 2)).StatusCode);
        Assert.Equal(1, f.GitHub.Revokes);
        var ledger = Assert.Single(Retained.IssuanceLedgers(f)).Value;
        var settled = f.State.Row(Retained.Attempt(ledger))!;
        Assert.Equal("settled", settled["State"]);
        Assert.True(settled.ContainsKey("SettledAt"));
        await Retained.ReclaimIssuance(f, ledger);
        await Retained.ReclaimIssuance(f, ledger);
        Assert.Equal(0L, Quota(f).Slots);
        Assert.Equal(0L, f.Gate.OutstandingTokenCount);
        Assert.Equal(1L, Quota(f).RetainedIssuanceCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Send_claim_or_unsent_reclamation_winner_controls_provider_post_and_exact_charge(bool runtime, bool sendWins)
    {
        var f = new InMemoryBroker { Limits = new(GlobalAttempts: 2) };
        var active = runtime ? await Retained.ActiveAgent(f) : null;
        if (!runtime) AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        var before = Retained.IssuanceLedgers(f);
        var mints = f.GitHub.Mints;
        var send = new CommitPause(f.State, t => t.Writes("State", "sending"));
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishPost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = f.GitHub.BeforeMint;
        f.GitHub.BeforeMint = async mint =>
        {
            if (sendWins)
            {
                posted.TrySetResult();
                await finishPost.Task.WaitAsync(TimeSpan.FromSeconds(12), Token);
            }
            if (previous is not null) await previous(mint);
        };
        var writer = runtime ? f.Issue(active!.Device, active.Secret) : f.Start(Retained.AgentRequest(NewId()), 2);
        try
        {
            await send.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            var ledger = Assert.Single(Retained.IssuanceLedgers(f), x => before.All(b => b.Value.OperationId != x.Value.OperationId)).Value;
            if (sendWins)
            {
                send.Release.TrySetResult();
                await posted.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            }
            await Retained.ReclaimIssuance(f, ledger);
            Assert.Equal(sendWins ? "sending" : "settled", f.State.Get(Retained.Attempt(ledger))!.State);
            Assert.Equal(sendWins ? before.Count + 1L : before.Count, Quota(f).Slots);
            send.Release.TrySetResult();
            finishPost.TrySetResult();
            Assert.Equal(sendWins ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, (await writer).StatusCode);
            Assert.Equal(mints + (sendWins ? 1 : 0), f.GitHub.Mints);
            if (runtime && !sendWins)
            {
                f.Clock.Advance(TimeSpan.FromMinutes(1));
                Assert.Equal(HttpStatusCode.OK, (await f.Issue(active!.Device, active.Secret)).StatusCode);
                var slot = IssuanceSlot.Key(1);
                var replacement = f.State.Read(slot)!;
                Assert.NotEqual(ledger.Generation, replacement.Value.Generation);
                await Retained.ReclaimIssuance(f, ledger);
                Assert.Equal(replacement.Version, f.State.Version(slot));
                Assert.Equal(2L, Quota(f).Slots);
            }
        }
        finally { send.Release.TrySetResult(); finishPost.TrySetResult(); await writer; }
    }

    [Theory]
    [InlineData("before-repository")]
    [InlineData("after-repository")]
    [InlineData("after-global")]
    public async Task Lost_cleanup_response_converges_once_and_preserves_active_ack_identity(string boundary)
    {
        var f = new InMemoryBroker();
        var active = await Retained.ActiveAgent(f);
        var generation = Retained.EnrollmentLedger(f, active.Id).Value.Generation!;
        var deviceKey = ControllerEnrollment.Key(Retained.Repository, active.Device);
        var gateKey = RepositoryGate.Key(Retained.Repository);
        var device = f.State.Row(deviceKey)!;
        var gate = f.State.Row(gateKey)!;
        // The repository settlement is the first commit and the global release the second.
        var commits = 0;
        if (boundary == "before-repository")
            f.State.BeforeCommit = _ => throw new BrokerStateUnavailable("Before repository cleanup commit");
        else
            f.State.AfterCommit = _ => ++commits == (boundary == "after-global" ? 2 : 1)
                ? throw new BrokerStateUnavailable("Lost committed cleanup response") : Task.CompletedTask;
        await Assert.ThrowsAsync<BrokerStateUnavailable>(() => f.Reclamation().ReclaimEnrollmentAsync(active.Id, generation, Token));
        f.State.BeforeCommit = null;
        f.State.AfterCommit = null;
        await f.Reclamation().ReclaimEnrollmentAsync(active.Id, generation, Token);
        var pendingKey = PendingControllerEnrollment.Key(Retained.Repository, active.Id);
        var proofKey = ReleaseProof.Enrollment(Retained.Repository, active.Id);
        var released = f.State.Version(pendingKey);
        var release = f.State.Version(proofKey);
        Assert.NotNull(released);
        Assert.NotNull(release);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await f.Reclamation().ReclaimEnrollmentAsync(active.Id, generation, Token);
        Assert.Equal(HttpStatusCode.OK, (await f.Acknowledge(active.Device, active.Completion, active.Secret, 2)).StatusCode);
        Assert.Equal(released, f.State.Version(pendingKey));
        Assert.Equal(release, f.State.Version(proofKey));
        var currentDevice = f.State.Row(deviceKey)!;
        foreach (var field in new[] { "LastActivityAt", "ExpiresAt", "CompletionId", "Acknowledged" })
            Assert.Equal(device[field], currentDevice[field]);
        var currentGate = f.State.Row(gateKey)!;
        foreach (var field in new[] { "OwnerDeviceId", "Epoch", "State", "OutstandingTokenCount" })
            Assert.Equal(gate[field], currentGate[field]);
        Assert.Equal(0L, f.Gate.PendingCount);
        var quota = f.State.Get(EnrollmentQuota.Key)!;
        Assert.Equal(0L, quota.Count);
        Assert.Equal(0L, f.State.Get(CandidateRepository.Key(Retained.Repository))!.PendingCount);
        Assert.Equal(1L, quota.RetainedEnrollmentCount);
    }

    [Fact]
    public async Task Enrollment_reuses_lowest_hole_at_original_expiry_plus_five_minutes_but_retained_backlog_still_denies()
    {
        var f = new InMemoryBroker { Retained = new(128, 256, 3), Limits = new(GlobalAttempts: 2) };
        var first = await Retained.StartAgent(f);
        var a = Assert.Single(Retained.IssuanceLedgers(f)).Value;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        _ = await Retained.StartAgent(f);
        var second = Retained.IssuanceLedgers(f).Single(x => x.Value.OperationId != a.OperationId).Value;
        Assert.Equal(0L, a.GlobalSlot);
        Assert.Equal(1L, second.GlobalSlot);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Start(Retained.AgentRequest(NewId()), 2)).StatusCode);
        var due = f.State.Get(Retained.Attempt(a))!.ExpiresAt!.Value.AddMinutes(5);
        f.Clock.Advance(due.AddTicks(-1) - f.Clock.GetUtcNow());
        await Retained.ReclaimIssuance(f, a);
        Assert.Equal(2L, Quota(f).Slots);
        f.Clock.Advance(TimeSpan.FromTicks(1));
        var before = f.State.Row(IssuanceQuota.Key)!;
        await Retained.ReclaimIssuance(f, a);
        await Retained.ReclaimIssuance(f, a);
        var freed = f.State.Row(IssuanceQuota.Key)!;
        Assert.Equal(1L, freed["Slots"]);
        foreach (var field in new[] { "RetainedIssuanceCount", "Admissions", "Reads", "Signs", "Mints" })
            Assert.Equal(before[field], freed[field]);
        await Retained.ReclaimEnrollment(f, first.Id);
        _ = await Retained.StartAgent(f);
        var replacement = Assert.Single(Retained.IssuanceLedgers(f),
            x => x.Value.OperationId != a.OperationId && x.Value.OperationId != second.OperationId).Value;
        Assert.Equal(0L, replacement.GlobalSlot);
        Assert.NotEqual(a.Generation, replacement.Generation);
        await Retained.ReclaimIssuance(f, a); // Old identity must never touch the new occupant of slot zero.
        Assert.Equal(replacement.Generation, f.State.Get(IssuanceSlot.Key(0))!.Generation);
        Assert.Equal(ExpectedSlots, Slots(f));
        Assert.Equal(3L, Quota(f).RetainedIssuanceCount);
        f.Clock.Advance(f.State.Get(Retained.Attempt(replacement))!.ExpiresAt!.Value.AddMinutes(5) - f.Clock.GetUtcNow());
        await Retained.ReclaimIssuance(f, replacement);
        await Retained.ReclaimEnrollment(f, replacement.PendingId!.Split('_')[1]);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Start(Retained.AgentRequest(NewId()), 2)).StatusCode);
        Assert.Equal(3, f.GitHub.VerifierMints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_absent_issuance_redaction_tombstone_releases_but_contrary_charge_evidence_quarantines(bool ambiguous)
    {
        var f = new InMemoryBroker();
        // Hold the enrollment's verifier reservation in the repository after its global charge committed.
        var pause = new CommitPause(f.State, t => t.Writes("State", "reserved"));
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        var writer = f.Start(Retained.AgentRequest(NewId()), 2);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            var a = Assert.Single(Retained.IssuanceLedgers(f)).Value;
            var attempt = Retained.Attempt(a);
            Assert.Null(f.State.Row(attempt));
            f.Clock.Advance(a.PayloadPurgeAt!.Value - f.Clock.GetUtcNow());
            await f.Redaction().RedactIssuanceAsync(a.OperationId!, a.Generation!, Token);
            Assert.Equal("redacted", f.State.Get(attempt)!.State);
            if (ambiguous) f.State.Change(attempt, row => row["RepositoryCharged"] = false);
            await Retained.ReclaimIssuance(f, a);
            Assert.Equal(ambiguous ? 1L : 0L, Quota(f).Slots);
            Assert.Equal(0L, f.Gate.OutstandingTokenCount);
            Assert.Equal(1L, Quota(f).RetainedIssuanceCount);
        }
        finally { pause.Release.TrySetResult(); await writer; }
        Assert.Equal(0, f.GitHub.Mints);
    }

    [Theory]
    [InlineData("sending")]
    [InlineData("scope")]
    [InlineData("missing-expiry")]
    [InlineData("sentinel-expiry")]
    [InlineData("overflow-expiry")]
    [InlineData("before-reservation-expiry")]
    public async Task Unresolved_or_untrusted_actual_attempt_retains_occupancy(string invalid)
    {
        var f = new InMemoryBroker();
        _ = await Retained.StartAgent(f);
        var a = Assert.Single(Retained.IssuanceLedgers(f)).Value;
        var attempt = Retained.Attempt(a);
        var reservedAt = f.State.Get(attempt)!.ReservedAt!.Value;
        f.State.Change(attempt, row =>
        {
            if (invalid == "sending") row["State"] = "sending";
            if (invalid == "scope") row["ScopeValid"] = false;
            if (invalid == "missing-expiry") Assert.True(row.Remove("ExpiresAt"));
            if (invalid == "sentinel-expiry") row["ExpiresAt"] = DateTimeOffset.MaxValue;
            if (invalid == "overflow-expiry") row["ExpiresAt"] = DateTimeOffset.MaxValue.AddMinutes(-1);
            if (invalid == "before-reservation-expiry") row["ExpiresAt"] = reservedAt.AddMinutes(-1);
        });
        f.Clock.Advance(TimeSpan.FromDays(2));
        var gate = f.State.Version(RepositoryGate.Key(Retained.Repository));
        Assert.NotNull(gate);
        await Retained.ReclaimIssuance(f, a);
        Assert.Equal(1L, Quota(f).Slots);
        Assert.Equal(gate, f.State.Version(RepositoryGate.Key(Retained.Repository)));
        Assert.Equal(1, f.GitHub.Mints);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Released_runtime_cleanup_recovers_each_lost_commit_without_refunding_device_twice(int lostCommit)
    {
        var f = new InMemoryBroker();
        var active = await Retained.ActiveAgent(f);
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(active.Device, active.Secret)).StatusCode);
        var runtime = Assert.Single(Retained.IssuanceLedgers(f), x => x.Value.Purpose == "runtime").Value;
        var attempt = f.State.Get(Retained.Attempt(runtime))!;
        Assert.Equal("released", attempt.State);
        var deviceKey = ControllerEnrollment.Key(Retained.Repository, active.Device);
        var device = f.State.Row(deviceKey)!;
        Assert.Equal(1L, device["OutstandingTokenCount"]);
        f.Clock.Advance(attempt.ExpiresAt!.Value.AddMinutes(5).AddTicks(-1) - f.Clock.GetUtcNow());
        await Retained.ReclaimIssuance(f, runtime);
        Assert.Equal(2L, Quota(f).Slots);
        f.Clock.Advance(TimeSpan.FromTicks(1));
        // The repository settlement is the first commit and the global release the second.
        var commits = 0;
        f.State.AfterCommit = _ => ++commits == lostCommit
            ? throw new BrokerStateUnavailable("Lost runtime settlement response") : Task.CompletedTask;
        await Assert.ThrowsAsync<BrokerStateUnavailable>(() => Retained.ReclaimIssuance(f, runtime));
        f.State.AfterCommit = null;
        await Retained.ReclaimIssuance(f, runtime);
        var proof = ReleaseProof.Issuance(Retained.Repository, runtime.OperationId!);
        var obligation = f.State.Version(proof);
        Assert.NotNull(obligation);
        await Retained.ReclaimIssuance(f, runtime);
        Assert.Equal(obligation, f.State.Version(proof));
        // #71's duplicate settlement remains inert. After #81's audit horizon,
        // the resolved runtime allocation retires independently of its enrollment.
        f.Clock.Advance(TimeSpan.FromDays(31));
        await f.Redaction().RedactIssuanceAsync(runtime.OperationId!, runtime.Generation!, Token);
        await Retained.ReclaimIssuance(f, runtime);
        Assert.Null(f.State.Row(proof));
        Assert.Equal(1L, Quota(f).Slots);
        Assert.Equal(1L, Quota(f).RetainedIssuanceCount);
        Assert.Equal(1L, f.Gate.OutstandingTokenCount);
        var current = f.State.Row(deviceKey)!;
        Assert.Equal(0L, current["OutstandingTokenCount"]);
        foreach (var field in new[] { "LastActivityAt", "ExpiresAt", "State" }) Assert.Equal(device[field], current[field]);
    }

    [Fact]
    public async Task Provider_unknown_send_stays_charged_after_deadline_and_device_receipt()
    {
        var f = new InMemoryBroker();
        var active = await Retained.ActiveAgent(f);
        f.GitHub.BeforeMint = _ => throw new GitHubUnavailable("Provider send outcome unknown");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Issue(active.Device, active.Secret)).StatusCode);
        var runtime = Assert.Single(Retained.IssuanceLedgers(f), x => x.Value.Purpose == "runtime").Value;
        var attemptKey = Retained.Attempt(runtime);
        var attempt = f.State.Row(attemptKey)!;
        Assert.Equal("sending", attempt["State"]);
        Assert.False(attempt.ContainsKey("ExpiresAt"));
        var version = f.State.Version(attemptKey);
        Assert.Equal(HttpStatusCode.NoContent, (await f.Revoke(active.Device, active.Secret)).StatusCode);
        Assert.Equal(active.Device, f.State.Get(RevocationReceipt.Key(Retained.Repository, active.Device))!.EnrollmentId);
        f.Clock.Advance(TimeSpan.FromDays(2));
        await Retained.ReclaimIssuance(f, runtime);
        Assert.Equal(version, f.State.Version(attemptKey));
        Assert.Equal(2L, Quota(f).Slots);
        Assert.Equal(1L, f.Enrollment(active.Device).OutstandingTokenCount);
    }

    [Fact]
    public async Task Wrong_generation_and_underflow_cannot_refund_a_real_charged_attempt()
    {
        var f = new InMemoryBroker();
        _ = await Retained.StartAgent(f);
        var ledger = Assert.Single(Retained.IssuanceLedgers(f)).Value;
        var attemptKey = Retained.Attempt(ledger);
        var attempt = f.State.Read(attemptKey)!;
        f.Clock.Advance(attempt.Value.ExpiresAt!.Value.AddMinutes(5) - f.Clock.GetUtcNow());
        await Assert.ThrowsAsync<IssuanceDenied>(() => f.Reclamation().ReclaimIssuanceAsync(ledger.OperationId!, NewId(), Token));
        f.State.Change(RepositoryGate.Key(Retained.Repository), row => row["OutstandingTokenCount"] = 0L);
        await Assert.ThrowsAsync<IssuanceDenied>(() => Retained.ReclaimIssuance(f, ledger));
        Assert.Equal(attempt.Version, f.State.Version(attemptKey));
        Assert.Equal(1L, Quota(f).Slots);
        Assert.Null(f.State.Row(ReleaseProof.Issuance(Retained.Repository, ledger.OperationId!)));
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static IssuanceQuota Quota(InMemoryBroker f) => f.State.Get(IssuanceQuota.Key)!;

    // The global slot rows ("slot:NNN"), in ordinal name order.
    private static List<string> Slots(InMemoryBroker f) =>
        [.. f.State.Rows(IssuanceCapacity.Scope).Select(row => row.Name).Where(name => name.StartsWith("slot:", StringComparison.Ordinal))];
}
