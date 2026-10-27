using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

// Retirement of a resolved retained allocation once its 30-day audit window has passed: five bounded phases that delete
// its repository row, release proof and ledger, and refund its retained charge exactly once. Lost and failed phases,
// competing writers, stale workers and redactors converge on that one refund. An allocation whose authority is
// unresolved, whose enrollment may be published, or that is of another generation stays charged.
public sealed class RetainedRetirementConformanceTests
{
    static readonly string[] RetainedFields = [
                "global:enrollment-v1/quota", "global:enrollment-v1/repository:123456", "global:issuance-v1/quota", "repository:123456/gate"
            ];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    public async Task Retirement_lost_committed_phase_converges_with_one_refund(int boundary, bool enrollment)
    {
        var f = new InMemoryBroker();
        var allocation = await RetirementEligible(f, enrollment);
        var commits = 0;
        f.State.AfterCommit = _ => ++commits == boundary
            ? throw new BrokerStateUnavailable("Lost committed retirement phase " + boundary) : Task.CompletedTask;
        await Assert.ThrowsAsync<BrokerStateUnavailable>(() => Retirement(f, allocation));
        f.State.AfterCommit = null;
        Assert.Equal(boundary < 5 ? 1L : 0L, RetainedCount(f, allocation));
        if (boundary < 2) Assert.NotNull(f.State.Row(allocation.Target));
        else Assert.Null(f.State.Row(allocation.Target));
        var reads = f.State.Reads;
        var transactions = Transactions(f);
        await Retirement(f, allocation);
        f.State.BeforeCommit = null;
        Assert.InRange(f.State.Reads - reads, 1, 20);
        Assert.InRange(transactions.Count, 0, 5);
        Assert.All(transactions, writes => Assert.InRange(writes, 1, 4));
        await Retirement(f, allocation);
        RetirementGone(f, allocation);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Retirement_uncommitted_phase_failure_keeps_credit_until_recovery(int boundary)
    {
        var f = new InMemoryBroker();
        var allocation = await RetirementEligible(f, false);
        var commits = 0;
        f.State.BeforeCommit = _ => ++commits == boundary
            ? throw new BrokerStateUnavailable("Before retirement commit " + boundary) : Task.CompletedTask;
        await Assert.ThrowsAsync<BrokerStateUnavailable>(() => Retirement(f, allocation));
        f.State.BeforeCommit = null;
        Assert.Equal(1L, RetainedCount(f, allocation));
        await Retirement(f, allocation);
        RetirementGone(f, allocation);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("release")]
    [InlineData("gate")]
    [InlineData("reference")]
    [InlineData("quota")]
    [InlineData("ledger")]
    public async Task Retirement_real_version_conflict_rolls_back_delete_or_refund(string competing)
    {
        var f = new InMemoryBroker();
        var allocation = await RetirementEligible(f, false);
        StateKey key = competing switch
        {
            "target" => allocation.Target,
            "release" => allocation.Release,
            "gate" => RepositoryGate.Key(Retained.Repository),
            "reference" => Reference(f, allocation),
            "quota" => IssuanceQuota.Key,
            _ => allocation.Ledger
        };
        var commits = 0;
        f.State.BeforeCommit = _ =>
        {
            // A real committed competing writer: rewriting the row unchanged still issues a new version.
            if (++commits == (competing is "quota" or "ledger" ? 5 : 2)) f.State.Change(key, _ => { });
            return Task.CompletedTask;
        };
        await Assert.ThrowsAsync<StateConflict>(() => Retirement(f, allocation));
        f.State.BeforeCommit = null;
        Assert.Equal(1L, RetainedCount(f, allocation));
        if (competing is not ("quota" or "ledger"))
        {
            Assert.NotNull(f.State.Row(allocation.Target));
            var release = f.State.Get(allocation.Release);
            Assert.NotNull(release);
            Assert.NotEqual(true, release.RetirementTargetDeleted);
        }
        await Retirement(f, allocation);
        RetirementGone(f, allocation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retirement_unexplained_missing_target_or_obligation_never_refunds(bool missingRelease)
    {
        var f = new InMemoryBroker();
        var allocation = await RetirementEligible(f, false);
        Assert.True(f.State.Remove(missingRelease ? allocation.Release : allocation.Target));
        await Assert.ThrowsAsync<IssuanceDenied>(() => Retirement(f, allocation));
        Assert.Equal(1L, RetainedCount(f, allocation));
        Assert.False(LedgerRow(f, allocation).ContainsKey("RetirementState"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Retirement_redactor_cannot_recreate_rows_with_fresh_or_stale_gate(bool enrollment, bool afterDecision)
    {
        var f = new InMemoryBroker();
        var allocation = await RetirementEligible(f, enrollment);
        // The redactor is held after its first read (the ledger) or before its first transaction (its decision).
        var redactor = new StaleWriter(f.State) { HoldAfterRead = afterDecision ? 0 : 1, HoldCommit = afterDecision ? 1 : 0 };
        var stale = enrollment ? f.Redaction(redactor).RedactEnrollmentAsync(allocation.Id, allocation.Generation, Token)
            : f.Redaction(redactor).RedactIssuanceAsync(allocation.Id, allocation.Generation, Token);
        try
        {
            await redactor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            await Retirement(f, allocation);
            redactor.Release.TrySetResult();
            if (afterDecision) await Assert.ThrowsAsync<StateConflict>(() => stale);
            else await stale;
            RetirementGone(f, allocation);
        }
        finally
        {
            redactor.Release.TrySetResult();
            try { await stale; } catch (Exception ex) when (ex is StateConflict or BrokerStateUnavailable) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retirement_late_first_settlement_uses_original_audit_clock(bool enrollment)
    {
        var f = new InMemoryBroker();
        var allocation = await RetirementEligible(f, enrollment, lateSettlement: true);
        await Retirement(f, allocation);
        RetirementGone(f, allocation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retirement_request_budget_and_metadata_survive_exact_audit_boundary(bool enrollment)
    {
        var f = new InMemoryBroker();
        var allocation = await RetirementEligible(f, enrollment);
        f.Clock.Advance(TimeSpan.FromTicks(-1));
        var gateKey = RepositoryGate.Key(Retained.Repository);
        f.State.Change(gateKey, row =>
        {
            row["OwnerDeviceId"] = "unrelated-owner";
            row["VerifierAttempt"] = "unrelated-verifier";
        });
        var gate = f.State.Row(gateKey)!;
        var gateVersion = f.State.Version(gateKey);
        var quota = f.State.Row(IssuanceQuota.Key)!;
        await Retirement(f, allocation);
        Assert.Equal(gateVersion, f.State.Version(gateKey));
        Assert.Equal(1L, RetainedCount(f, allocation));
        f.Clock.Advance(TimeSpan.FromTicks(1));
        var reads = f.State.Reads;
        var transactions = Transactions(f);
        await Retirement(f, allocation);
        f.State.BeforeCommit = null;
        Assert.InRange(f.State.Reads - reads, 1, 20);
        Assert.Equal(5, transactions.Count);
        Assert.All(transactions, writes => Assert.InRange(writes, 1, 4));
        RetirementGone(f, allocation);
        var after = f.State.Row(gateKey)!;
        foreach (var field in gate.Keys.Where(k => k != "Sequence")) Assert.Equal(gate[field], after[field]);
        var currentQuota = f.State.Row(IssuanceQuota.Key)!;
        foreach (var field in new[] { "Window", "Admissions", "Reads", "Signs", "Mints" }) Assert.Equal(quota[field], currentQuota[field]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retirement_mint_work_claim_and_cleanup_have_one_real_gate_winner(bool mintWins)
    {
        var f = new InMemoryBroker();
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        var id = Guid.NewGuid().ToString("N");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = 0;
        // The writer is held at its global mint charge, after its durable claim, and then fails before the charge; or
        // it is held at the claim itself.
        f.State.BeforeCommit = async transaction =>
        {
            var matches = mintWins ? transaction.InScope(IssuanceCapacity.Scope) : transaction.Writes("MintAdmissionStarted", true);
            if (!matches || Interlocked.CompareExchange(ref paused, 1, 0) != 0) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(12), Token);
            if (mintWins) throw new BrokerStateUnavailable("Stop before global mint charge after durable claim");
        };
        var writer = f.Start(Retained.AgentRequest(id), 2);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            var ledger = Retained.EnrollmentLedger(f, id).Value;
            var allocation = new Allocation(true, id, ledger.Generation!);
            var pending = f.State.Row(allocation.Target);
            Assert.NotNull(pending);
            Assert.Equal(mintWins, pending.TryGetValue("MintAdmissionStarted", out var started) && started is true);
            Assert.False(pending.ContainsKey("Attempt"));
            Assert.Empty(Retained.IssuanceLedgers(f));
            f.Clock.Advance(ledger.Deadline!.Value - f.Clock.GetUtcNow());
            await Retirement(f, allocation);
            f.Clock.Advance(TimeSpan.FromDays(30));
            await f.Redaction().RedactEnrollmentAsync(id, allocation.Generation, Token);
            await Retirement(f, allocation);
            if (mintWins)
            {
                Assert.True(f.State.Get(PendingKey(allocation))?.MintAdmissionStarted);
                Assert.Equal(1L, RetainedCount(f, allocation));
                await Retirement(f, allocation);
            }
            else RetirementGone(f, allocation);
            release.TrySetResult();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await writer).StatusCode);
            Assert.Empty(Retained.IssuanceLedgers(f));
            Assert.Equal(0, f.GitHub.Mints);
        }
        finally
        {
            release.TrySetResult();
            await writer;
        }
    }

    [Fact]
    public async Task Retirement_new_writer_resuming_historical_global_allocation_never_adds_version()
    {
        var f = new InMemoryBroker();
        var id = Guid.NewGuid().ToString("N");
        var allocation = await UnpublishedEnrollment(f, id);
        f.State.Change(allocation.Ledger, row => Assert.True(row.Remove("ReclamationVersion")));
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        Assert.Equal(HttpStatusCode.OK, (await f.Start(Retained.AgentRequest(id), 2)).StatusCode);
        Assert.False(LedgerRow(f, allocation).ContainsKey("ReclamationVersion"));
        var pending = f.State.Row(allocation.Target);
        Assert.NotNull(pending);
        Assert.False(pending.ContainsKey("ReclamationVersion"));
        Assert.Equal(true, pending["MintAdmissionStarted"]);
        f.Clock.Advance(TimeSpan.FromDays(32));
        await Retirement(f, allocation);
        await Retirement(f, allocation);
        Assert.Equal(1L, RetainedCount(f, allocation));
    }

    [Theory]
    [InlineData("unversioned")]
    [InlineData("mint-started")]
    [InlineData("completed")]
    public async Task Retirement_manual_enrollment_proofs_remain_charged(string reason)
    {
        var f = new InMemoryBroker();
        var allocation = await RetirementEligible(f, true);
        if (reason == "unversioned") f.State.Change(allocation.Ledger, row => Assert.True(row.Remove("ReclamationVersion")));
        f.State.Change(allocation.Target, row =>
        {
            if (reason == "unversioned") Assert.True(row.Remove("ReclamationVersion"));
            else if (reason == "mint-started") row["MintAdmissionStarted"] = true;
            else row["CompletionId"] = Guid.NewGuid().ToString("N");
        });
        await Retirement(f, allocation);
        await Retirement(f, allocation);
        Assert.Equal(1L, RetainedCount(f, allocation));
        Assert.NotNull(f.State.Row(allocation.Target));
    }

    [Fact]
    public async Task Retirement_runtime_is_independent_of_completed_enrollment_publication_charge()
    {
        var f = new InMemoryBroker();
        var active = await Retained.ActiveAgent(f);
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(active.Device, active.Secret)).StatusCode);
        var runtimeLedger = Assert.Single(Retained.IssuanceLedgers(f), x => x.Value.Purpose == "runtime").Value;
        var runtime = new Allocation(false, runtimeLedger.OperationId!, runtimeLedger.Generation!);
        var enrollment = new Allocation(true, active.Id, Retained.EnrollmentLedger(f, active.Id).Value.Generation!);
        f.Clock.Advance(f.State.Get(Retained.Attempt(runtimeLedger))!.ExpiresAt!.Value.AddMinutes(5) - f.Clock.GetUtcNow());
        await Retirement(f, runtime);
        await Retirement(f, enrollment);
        var deviceKey = ControllerEnrollment.Key(Retained.Repository, active.Device);
        Assert.Equal(0L, f.State.Get(deviceKey)!.OutstandingTokenCount);
        var device = f.State.Version(deviceKey);
        f.Clock.Advance(TimeSpan.FromDays(30));
        await Retirement(f, enrollment);
        await Retirement(f, runtime);
        Assert.Null(f.State.Row(runtime.Ledger));
        Assert.Null(f.State.Row(runtime.Target));
        Assert.Null(f.State.Row(runtime.Release));
        Assert.Equal(1L, RetainedCount(f, runtime));
        Assert.Equal(1L, RetainedCount(f, enrollment));
        Assert.Equal(device, f.State.Version(deviceKey));
        Assert.NotNull(f.State.Row(EnrollmentLocator.Key(active.Device)));
        Assert.NotNull(f.State.Row(enrollment.Target));
    }

    [Fact]
    public async Task Retirement_delayed_unconditional_locator_publisher_keeps_enrollment_credit()
    {
        var f = new InMemoryBroker();
        var (id, pendingId) = await Retained.StartAgent(f);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        // Locator publication is a one-write transaction of its own scope.
        var pause = new CommitPause(f.State, transaction => transaction.Touches("locator"));
        var publisher = f.Complete(pendingId, 2);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            var ledger = Retained.EnrollmentLedger(f, id).Value;
            var allocation = new Allocation(true, id, ledger.Generation!);
            var locatorKey = EnrollmentLocator.Key(ledger.EnrollmentId!);
            Assert.Null(f.State.Row(locatorKey));
            Assert.True(f.State.Row(allocation.Target)!.ContainsKey("CompletionId"));
            Assert.NotNull(f.State.Row(ControllerEnrollment.Key(Retained.Repository, ledger.EnrollmentId!)));
            f.Clock.Advance(ledger.Deadline!.Value.AddDays(31) - f.Clock.GetUtcNow());
            await Retirement(f, allocation);
            await Retirement(f, allocation);
            Assert.Equal(1L, RetainedCount(f, allocation));
            pause.Release.TrySetResult();
            Assert.Equal(HttpStatusCode.Gone, (await publisher).StatusCode);
            var locator = f.State.Get(locatorKey);
            Assert.NotNull(locator);
            Assert.Equal(ledger.PendingId, locator.PendingId);
            Assert.Equal(ledger.EnrollmentId, locator.EnrollmentId);
            Assert.Equal(1L, RetainedCount(f, allocation));
        }
        finally
        {
            pause.Release.TrySetResult();
            await publisher;
        }
    }

    [Theory]
    [InlineData("sending")]
    [InlineData("unknown")]
    [InlineData("invalid-scope")]
    public async Task Retirement_unresolved_authority_remains_charged_after_payload_and_audit_ages(string state)
    {
        var f = new InMemoryBroker();
        _ = await Retained.StartAgent(f);
        var ledger = Assert.Single(Retained.IssuanceLedgers(f)).Value;
        var allocation = new Allocation(false, ledger.OperationId!, ledger.Generation!);
        f.State.Change(allocation.Target, row =>
        {
            if (state == "invalid-scope") row["ScopeValid"] = false;
            else
            {
                row["State"] = state;
                Assert.True(row.Remove("ExpiresAt"));
            }
        });
        f.Clock.Advance(TimeSpan.FromDays(90));
        await f.Redaction().RedactIssuanceAsync(allocation.Id, allocation.Generation, Token);
        await Retirement(f, allocation);
        await Retirement(f, allocation);
        Assert.Equal(1L, RetainedCount(f, allocation));
        Assert.Equal(1L, f.State.Get(IssuanceQuota.Key)!.Slots);
        Assert.Equal(1L, f.Gate.OutstandingTokenCount);
        Assert.False(LedgerRow(f, allocation).ContainsKey("RetirementState"));
        Assert.Null(f.State.Row(allocation.Release));
    }

    [Fact]
    public async Task Retirement_live_pending_reference_blocks_then_terminal_reference_and_exact_verifier_clear_allow_deletion()
    {
        var f = new InMemoryBroker();
        var allocation = await RetirementEligible(f, false);
        var reference = Reference(f, allocation);
        // A live reference is independently authoritative even if its date contradicts the older allocation.
        f.State.Change(reference, row => row["Deadline"] = f.Clock.GetUtcNow().AddMinutes(15));
        await Retirement(f, allocation);
        Assert.False(LedgerRow(f, allocation).ContainsKey("RetirementState"));
        Assert.Equal(1L, RetainedCount(f, allocation));
        f.State.Change(reference, row => row["State"] = "closed");
        var gateKey = RepositoryGate.Key(Retained.Repository);
        f.State.Change(gateKey, row => row["VerifierAttempt"] = allocation.Id);
        await Retirement(f, allocation);
        RetirementGone(f, allocation);
        Assert.False(f.State.Row(gateKey)!.ContainsKey("VerifierAttempt"));
        Assert.NotNull(f.State.Row(reference));
    }

    [Fact]
    public async Task Retirement_repeated_real_enrollment_cycles_leave_only_fixed_rows()
    {
        var f = new InMemoryBroker { Retained = new(128, 1, 4096) };
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var allocation = await RetirementEligible(f, true);
            await Retirement(f, allocation);
            RetirementGone(f, allocation);
            Assert.Equal(RetainedFields, f.Rows().Select(row => row.Key));
            Assert.Equal(1L, f.State.Get(EnrollmentQuota.Key)!.CandidateCount);
        }
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        Assert.Equal(HttpStatusCode.OK, (await f.Start(Retained.AgentRequest(Guid.NewGuid().ToString("N")), 2)).StatusCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Retirement_two_workers_race_real_commits_and_refund_once(int pausedBatch)
    {
        var f = new InMemoryBroker();
        var allocation = await RetirementEligible(f, false);
        var worker = new StaleWriter(f.State) { HoldCommit = pausedBatch };
        var stale = Retirement(f, allocation, worker);
        try
        {
            await worker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            await Retirement(f, allocation);
            worker.Release.TrySetResult();
            var conflict = await Assert.ThrowsAnyAsync<Exception>(() => stale);
            Assert.True(conflict is StateConflict or BrokerStateUnavailable, conflict.ToString());
            RetirementGone(f, allocation);
            await Retirement(f, allocation);
            RetirementGone(f, allocation);
        }
        finally
        {
            worker.Release.TrySetResult();
            try { await stale; } catch (Exception ex) when (ex is StateConflict or BrokerStateUnavailable) { }
        }
    }

    [Fact]
    public async Task Retirement_old_generation_cannot_touch_reallocated_identical_enrollment_identity()
    {
        var f = new InMemoryBroker { Retained = new(128, 1, 4096) };
        var old = await RetirementEligible(f, true);
        await Retirement(f, old);
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        Assert.Equal(HttpStatusCode.OK, (await f.Start(Retained.AgentRequest(old.Id), 2)).StatusCode);
        var current = Retained.EnrollmentLedger(f, old.Id);
        var pending = f.State.Version(old.Target);
        Assert.NotNull(pending);
        Assert.NotEqual(old.Generation, current.Value.Generation);
        await Assert.ThrowsAsync<IssuanceDenied>(() => Retirement(f, old));
        await Assert.ThrowsAsync<IssuanceDenied>(() => f.Redaction().RedactEnrollmentAsync(old.Id, old.Generation, Token));
        Assert.Equal(current.Version, f.State.Version(old.Ledger));
        Assert.Equal(pending, f.State.Version(old.Target));
        Assert.Equal(1L, RetainedCount(f, old));
    }

    // One retained allocation of repository 123456: an enrollment ledger named by its idempotency ID, or an issuance
    // ledger named by its operation ID, with its generation. Target is its repository row: the pending controller
    // enrollment or the issuance attempt.
    private sealed record Allocation(bool IsEnrollment, string Id, string Generation)
    {
        public StateKey Ledger => IsEnrollment ? EnrollmentLedger.Key(Id) : IssuanceLedger.Key(Id);
        public StateKey Target => IsEnrollment
            ? PendingControllerEnrollment.Key(Retained.Repository, Id) : IssuanceAttempt.Key(Retained.Repository, Id);
        public StateKey<ReleaseProof> Release => IsEnrollment
            ? ReleaseProof.Enrollment(Retained.Repository, Id) : ReleaseProof.Issuance(Retained.Repository, Id);
    }

    private static Task Retirement(InMemoryBroker f, Allocation allocation, IBrokerState? state = null) => allocation.IsEnrollment
        ? f.Reclamation(state).ReclaimEnrollmentAsync(allocation.Id, allocation.Generation, Token)
        : f.Reclamation(state).ReclaimIssuanceAsync(allocation.Id, allocation.Generation, Token);

    // The allocation family's retained count on its global quota.
    private static long? RetainedCount(InMemoryBroker f, Allocation allocation) => allocation.IsEnrollment
        ? f.State.Get(EnrollmentQuota.Key)!.RetainedEnrollmentCount : f.State.Get(IssuanceQuota.Key)!.RetainedIssuanceCount;

    private static Dictionary<string, object> LedgerRow(InMemoryBroker f, Allocation allocation) =>
        f.State.Row(allocation.Ledger) ?? throw new InvalidOperationException("No ledger " + allocation.Ledger);

    private static StateKey<PendingControllerEnrollment> PendingKey(Allocation allocation) =>
        PendingControllerEnrollment.Key(Retained.Repository, allocation.Id);

    // The pending controller enrollment an issuance allocation's verifier attempt was minted for.
    private static StateKey<PendingControllerEnrollment> Reference(InMemoryBroker f, Allocation allocation) =>
        InMemoryBroker.PendingKey(f.State.Get(IssuanceLedger.Key(allocation.Id))!.PendingId!);

    private static void RetirementGone(InMemoryBroker f, Allocation allocation)
    {
        foreach (var key in new[] { allocation.Ledger, allocation.Target, allocation.Release })
            Assert.True(f.State.Row(key) is null, $"Retirement must physically delete {key}");
        Assert.Equal(0L, RetainedCount(f, allocation));
    }

    // The write count of every transaction from now until the test clears BeforeCommit.
    private static List<int> Transactions(InMemoryBroker f)
    {
        var transactions = new List<int>();
        f.State.BeforeCommit = transaction =>
        {
            transactions.Add(transaction.Writes.Count);
            return Task.CompletedTask;
        };
        return transactions;
    }

    // An enrollment admitted globally whose admission response was lost before its repository publication.
    private static async Task<Allocation> UnpublishedEnrollment(InMemoryBroker f, string? id = null)
    {
        var lost = false;
        f.State.AfterCommit = _ =>
        {
            if (lost) return Task.CompletedTask;
            lost = true;
            throw new BrokerStateUnavailable("Lost new allocation response");
        };
        id ??= Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(Retained.AgentRequest(id), 2)).StatusCode);
        Assert.True(lost);
        f.State.AfterCommit = null;
        return new(true, id, Retained.EnrollmentLedger(f, id).Value.Generation!);
    }

    // An allocation settled at its resolution (or 31 days late), then aged to its audit time: an unpublished enrollment,
    // resolved at its deadline, or a verifier GitHub token's issuance, resolved five minutes after it expired.
    private static async Task<Allocation> RetirementEligible(InMemoryBroker f, bool enrollment, bool lateSettlement = false)
    {
        Allocation allocation;
        DateTimeOffset resolved;
        if (enrollment)
        {
            allocation = await UnpublishedEnrollment(f);
            var ledger = f.State.Get(EnrollmentLedger.Key(allocation.Id))!;
            Assert.Equal(1L, ledger.ReclamationVersion);
            resolved = ledger.Deadline!.Value;
        }
        else
        {
            _ = await Retained.StartAgent(f);
            var ledger = Assert.Single(Retained.IssuanceLedgers(f)).Value;
            allocation = new(false, ledger.OperationId!, ledger.Generation!);
            resolved = f.State.Get(Retained.Attempt(ledger))!.ExpiresAt!.Value.AddMinutes(5);
        }
        f.Clock.Advance(resolved.AddDays(lateSettlement ? 31 : 0) - f.Clock.GetUtcNow());
        await Retirement(f, allocation);
        if (!lateSettlement) f.Clock.Advance(TimeSpan.FromDays(30));
        return allocation;
    }

    // Another writer's own connection to the broker state, as a second client with its own transport was. It holds this
    // writer's n-th read after its answer (HoldAfterRead) or its n-th transaction before it reaches the state
    // (HoldCommit) until the test releases it; every other call passes.
    private sealed class StaleWriter(IBrokerState inner) : IBrokerState
    {
        private int reads, commits;
        public int HoldAfterRead { get; init; }
        public int HoldCommit { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<Versioned<T>?> ReadAsync<T>(StateKey<T> key, CancellationToken ct) where T : BrokerRecord, new()
        {
            var read = await inner.ReadAsync(key, ct);
            if (Interlocked.Increment(ref reads) == HoldAfterRead) await HoldAsync();
            return read;
        }

        public async Task<IReadOnlyList<StateVersion?>> CommitAsync(StateTransaction transaction, CancellationToken ct)
        {
            if (Interlocked.Increment(ref commits) == HoldCommit) await HoldAsync();
            return await inner.CommitAsync(transaction, ct);
        }

        public Task<StatePage<T>> ListAsync<T>(StateRange<T> range, string after, string? continuation, CancellationToken ct)
            where T : BrokerRecord, new() => inner.ListAsync(range, after, continuation, ct);

        private async Task HoldAsync()
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(12), Token);
        }
    }
}
