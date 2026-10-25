using System.Net;
using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

// The cleanup, reclamation and redaction rules the moved cases did not pin: what each one must leave alone, the clocks
// at their exact boundaries, the fences, and the bounds of a cleanup run. Each case also checks that nothing else was
// written where that matters.
public sealed class RetainedDecisionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string NewId() => Guid.NewGuid().ToString("N");
    private static StateKey<PendingControllerEnrollment> PendingKey(string id) => PendingControllerEnrollment.Key(Retained.Repository, id);
    private static IEnumerable<(string, StateVersion)> Versions(InMemoryBroker f) => f.Rows().Select(row => (row.Key, row.Version));

    [Fact]
    public async Task Reclamation_leaves_a_live_pending_enrollment_charged_before_its_deadline()
    {
        var f = new InMemoryBroker();
        var (id, _) = await Retained.StartAgent(f);
        f.Clock.Advance(Retained.EnrollmentLedger(f, id).Value.Deadline!.Value - f.Clock.GetUtcNow() - TimeSpan.FromTicks(1));
        var before = Versions(f).ToList();

        await Retained.ReclaimEnrollment(f, id);

        Assert.Equal(before, Versions(f));
        Assert.Equal(1L, f.Gate.PendingCount);
    }

    // A pending enrollment's own row must carry its ledger's approval contract, and an active one must have an
    // acknowledged controller enrollment. Neither is written otherwise, so either is another writer's row.
    [Theory]
    [InlineData("approval")]
    [InlineData("unacknowledged")]
    public async Task Reclamation_denies_a_pending_enrollment_that_does_not_match_its_allocation(string barrier)
    {
        var f = new InMemoryBroker();
        string id;
        if (barrier == "approval")
        {
            (id, _) = await Retained.StartAgent(f);
            f.State.Change(PendingKey(id), row => row["PolicyVersion"] = ApprovalContract.AgentClone.Policy);
        }
        else
        {
            var journey = await Retained.ActiveAgent(f);
            id = journey.Id;
            Assert.Equal("active", f.State.Get(PendingKey(id))!.State);
            f.State.Change(ControllerEnrollment.Key(Retained.Repository, journey.Device), row => row["Acknowledged"] = false);
        }
        var before = Versions(f).ToList();

        await Assert.ThrowsAsync<IssuanceDenied>(() => Retained.ReclaimEnrollment(f, id));

        Assert.Equal(before, Versions(f));
    }

    // The repository settlement committed and the global release did not, so the attempt is released and the ledger
    // still charged. Without the release proof that settlement wrote, nothing proves the release: the attempt is never
    // settled again, so another outstanding GitHub token keeps its place on the gate.
    [Fact]
    public async Task Released_attempt_without_its_release_proof_is_denied_and_never_settled_twice()
    {
        var f = new InMemoryBroker();
        var journey = await Retained.ActiveAgent(f);
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(journey.Device, journey.Secret)).StatusCode);
        var verifier = Assert.Single(Retained.IssuanceLedgers(f), ledger => ledger.Value.Purpose == "verification").Value;
        var attemptKey = Retained.Attempt(verifier);
        f.Clock.Advance(f.State.Get(attemptKey)!.ExpiresAt!.Value.AddMinutes(5) - f.Clock.GetUtcNow());
        var lost = false;
        f.State.BeforeCommit = transaction =>
        {
            if (lost || !transaction.InScope(IssuanceCapacity.Scope)) return Task.CompletedTask;
            lost = true;
            throw new BrokerStateUnavailable("The global release did not commit.");
        };
        await Assert.ThrowsAsync<BrokerStateUnavailable>(() => Retained.ReclaimIssuance(f, verifier));
        f.State.BeforeCommit = null;
        Assert.True(f.State.Get(attemptKey)!.CapacityReleased);
        Assert.Null(f.State.Get(IssuanceLedger.Key(verifier.OperationId!))!.CapacityState);
        Assert.NotNull(f.State.Row(IssuanceSlot.Key(verifier.GlobalSlot!.Value)));
        Assert.Equal(1L, f.Gate.OutstandingTokenCount);
        Assert.True(f.State.Remove(ReleaseProof.Issuance(Retained.Repository, verifier.OperationId!)));
        var before = Versions(f).ToList();

        await Assert.ThrowsAsync<IssuanceDenied>(() => Retained.ReclaimIssuance(f, verifier));

        Assert.Equal(before, Versions(f));
        Assert.Equal(1L, f.Gate.OutstandingTokenCount);
    }

    // An unpublished enrollment whose admission response was lost, released at its deadline and past its audit window.
    // Retirement refunds its retained charge only when no controller enrollment, locator or receipt was published for
    // its Enrollment ID: a delayed publisher may still route to it.
    [Theory]
    [InlineData("none")]
    [InlineData("enrollment")]
    [InlineData("locator")]
    [InlineData("receipt")]
    public async Task Retirement_refunds_only_an_enrollment_nothing_was_published_for(string published)
    {
        var (f, id, ledger) = await RetirementEligibleAsync();
        var enrollmentId = ledger.EnrollmentId!;
        if (published == "enrollment")
            f.State.Seed(ControllerEnrollment.Key(Retained.Repository, enrollmentId), new() { RepositoryId = Retained.Repository, EnrollmentId = enrollmentId, Epoch = InMemoryBroker.Epoch });
        if (published == "locator")
            f.State.Seed(EnrollmentLocator.Key(enrollmentId), new() { RepositoryId = Retained.Repository, EnrollmentId = enrollmentId, PendingId = ledger.PendingId });
        if (published == "receipt")
            f.State.Seed(RevocationReceipt.Key(Retained.Repository, enrollmentId), new()
            {
                RepositoryId = Retained.Repository, EnrollmentId = enrollmentId, Epoch = InMemoryBroker.Epoch, RevokedAt = f.Clock.GetUtcNow()
            });
        var before = Versions(f).ToList();

        await Retained.ReclaimEnrollment(f, id);

        if (published == "none")
        {
            Assert.Null(f.State.Row(EnrollmentLedger.Key(id)));
            Assert.Null(f.State.Row(PendingKey(id)));
            Assert.Null(f.State.Row(ReleaseProof.Enrollment(Retained.Repository, id)));
            Assert.Equal(0L, f.State.Get(EnrollmentQuota.Key)!.RetainedEnrollmentCount);
        }
        else
        {
            Assert.Equal(before, Versions(f));
            Assert.Equal(1L, f.State.Get(EnrollmentQuota.Key)!.RetainedEnrollmentCount);
        }
    }

    // After the target is deleted and the ledger marked, the final phase rereads everything. A target that is back, or
    // a ledger another worker wrote since, stops it before the release proof is deleted and the charge refunded. A
    // changed ledger converges on the next call; a target that is back keeps the charge.
    [Theory]
    [InlineData("target")]
    [InlineData("ledger")]
    public async Task Retirement_refunds_only_when_nothing_changed_after_the_target_was_deleted(string change)
    {
        var (f, id, _) = await RetirementEligibleAsync();
        var target = f.State.Row(PendingKey(id))!;
        var changed = false;
        f.State.AfterCommit = transaction =>
        {
            if (changed || !transaction.Writes("RetirementState", "repository-deleted")) return Task.CompletedTask;
            changed = true;
            if (change == "target") f.State.Put(PendingKey(id).Scope, PendingKey(id).Name, target);
            else f.State.Change(EnrollmentLedger.Key(id), _ => { });
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<IssuanceDenied>(() => Retained.ReclaimEnrollment(f, id));

        Assert.True(changed);
        f.State.AfterCommit = null;
        Assert.NotNull(f.State.Row(ReleaseProof.Enrollment(Retained.Repository, id)));
        Assert.Equal(1L, f.State.Get(EnrollmentQuota.Key)!.RetainedEnrollmentCount);
        if (change == "target") await Assert.ThrowsAsync<IssuanceDenied>(() => Retained.ReclaimEnrollment(f, id));
        else await Retained.ReclaimEnrollment(f, id);
        Assert.Equal(change == "target" ? 1L : 0L, f.State.Get(EnrollmentQuota.Key)!.RetainedEnrollmentCount);
    }

    // Reclamation and redaction trust the gate only when it is the repository's and carries a sequence, even when the
    // call would then write nothing: redaction before the payload deadline, and reclamation at the enrollment deadline,
    // which would release it.
    [Theory]
    [InlineData("other-repository")]
    [InlineData("no-sequence")]
    public async Task Reclamation_and_redaction_deny_a_gate_that_is_not_the_repositorys_or_has_no_sequence(string gate)
    {
        var f = new InMemoryBroker();
        var (id, _) = await Retained.StartAgent(f);
        var ledger = Retained.EnrollmentLedger(f, id).Value;
        f.State.Change(RepositoryGate.Key(Retained.Repository), row =>
        {
            if (gate == "other-repository") row["RepositoryId"] = "654321";
            else Assert.True(row.Remove("Sequence"));
        });
        var before = Versions(f).ToList();

        await Assert.ThrowsAsync<IssuanceDenied>(() => f.Redaction().RedactEnrollmentAsync(id, ledger.Generation!, Token));
        f.Clock.Advance(ledger.Deadline!.Value - f.Clock.GetUtcNow());
        await Assert.ThrowsAsync<IssuanceDenied>(() => Retained.ReclaimEnrollment(f, id));

        Assert.Equal(before, Versions(f));
    }

    [Fact]
    public async Task Every_repository_transaction_of_reclamation_and_redaction_writes_the_next_gate_sequence()
    {
        var f = new InMemoryBroker();
        var (id, _) = await Retained.StartAgent(f);
        var ledger = Retained.EnrollmentLedger(f, id).Value;
        f.Clock.Advance(ledger.Deadline!.Value - f.Clock.GetUtcNow());
        var sequence = f.Gate.Sequence;

        await Retained.ReclaimEnrollment(f, id);
        Assert.Equal(sequence + 1, f.Gate.Sequence);

        f.Clock.Advance(ledger.PayloadPurgeAt!.Value - f.Clock.GetUtcNow());
        await f.Redaction().RedactEnrollmentAsync(id, ledger.Generation!, Token);
        Assert.Equal(sequence + 2, f.Gate.Sequence);
        Assert.False(f.State.Row(PendingKey(id))!.ContainsKey("RecoveryHash"));
    }

    // Neither the payload deadline nor the original deadline can be read, so the payload is due now.
    [Fact]
    public async Task Payload_whose_deadline_cannot_be_read_is_redacted_at_once()
    {
        var f = new InMemoryBroker();
        var (id, _) = await Retained.StartAgent(f);
        f.State.Change(EnrollmentLedger.Key(id), row => row["PayloadPurgeAt"] = row["Deadline"] = DateTimeOffset.MaxValue);
        Assert.True(f.State.Row(PendingKey(id))!.ContainsKey("RecoveryHash"));

        await f.Redaction().RedactEnrollmentAsync(id, Retained.EnrollmentLedger(f, id).Value.Generation!, Token);

        Assert.False(f.State.Row(PendingKey(id))!.ContainsKey("RecoveryHash"));
        Assert.False(f.State.Row(EnrollmentLedger.Key(id))!.ContainsKey("RecoveryHash"));
    }

    // The work metadata of a pending enrollment goes 30 days after it was resolved, and not a tick earlier.
    [Fact]
    public async Task Pending_work_metadata_is_removed_exactly_30_days_after_resolution()
    {
        var f = new InMemoryBroker();
        var (id, _) = await Retained.StartAgent(f);
        var ledger = Retained.EnrollmentLedger(f, id).Value;
        var deadline = ledger.Deadline!.Value;
        async Task RedactAt(DateTimeOffset at)
        {
            f.Clock.Advance(at - f.Clock.GetUtcNow());
            await f.Redaction().RedactEnrollmentAsync(id, ledger.Generation!, Token);
        }

        await RedactAt(ledger.PayloadPurgeAt!.Value);
        Assert.Equal(deadline, f.State.Get(PendingKey(id))!.FirstTerminalAt);
        await RedactAt(deadline.AddDays(30).AddTicks(-1));
        Assert.NotNull(f.State.Get(PendingKey(id))!.NextWorkAt);
        await RedactAt(deadline.AddDays(30));
        Assert.Null(f.State.Get(PendingKey(id))!.NextWorkAt);
    }

    // A settled GitHub token's send claim and release times go 30 days after settlement, and not a tick earlier.
    [Fact]
    public async Task Settled_attempt_release_metadata_is_removed_exactly_30_days_after_settlement()
    {
        var f = new InMemoryBroker();
        var journey = await Retained.ActiveAgent(f);
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(journey.Device, journey.Secret)).StatusCode);
        var runtime = Assert.Single(Retained.IssuanceLedgers(f), ledger => ledger.Value.Purpose == "runtime").Value;
        var attemptKey = Retained.Attempt(runtime);
        Assert.NotNull(f.State.Get(attemptKey)!.SendClaimedAt);
        Assert.NotNull(f.State.Get(attemptKey)!.ReleasedAt);
        f.Clock.Advance(f.State.Get(attemptKey)!.ExpiresAt!.Value.AddMinutes(5) - f.Clock.GetUtcNow());
        await Retained.ReclaimIssuance(f, runtime);
        var settled = f.State.Get(attemptKey)!;
        Assert.Equal("settled", settled.State);
        async Task RedactAt(DateTimeOffset at)
        {
            f.Clock.Advance(at - f.Clock.GetUtcNow());
            await f.Redaction().RedactIssuanceAsync(runtime.OperationId!, runtime.Generation!, Token);
        }

        await RedactAt(settled.SettledAt!.Value.AddDays(30).AddTicks(-1));
        Assert.NotNull(f.State.Get(attemptKey)!.SendClaimedAt);
        Assert.NotNull(f.State.Get(attemptKey)!.ReleasedAt);
        await RedactAt(settled.SettledAt!.Value.AddDays(30));
        Assert.Null(f.State.Get(attemptKey)!.SendClaimedAt);
        Assert.Null(f.State.Get(attemptKey)!.ReleasedAt);
    }

    // Redaction writes only the allocation's own rows: a repository with no gate, a pending enrollment of another
    // generation, and a ledger whose pending ID is not its own are another writer's state.
    [Theory]
    [InlineData("no-gate")]
    [InlineData("generation")]
    [InlineData("pending-id")]
    public async Task Redaction_denies_rows_that_are_not_its_allocations(string barrier)
    {
        var f = new InMemoryBroker();
        var (id, _) = await Retained.StartAgent(f);
        var ledger = Retained.EnrollmentLedger(f, id).Value;
        if (barrier == "no-gate") Assert.True(f.State.Remove(RepositoryGate.Key(Retained.Repository)));
        if (barrier == "generation") f.State.Change(PendingKey(id), row => row["Generation"] = NewId());
        if (barrier == "pending-id") f.State.Change(EnrollmentLedger.Key(id), row => row["PendingId"] = Retained.Repository + "_" + NewId());
        f.Clock.Advance(ledger.PayloadPurgeAt!.Value - f.Clock.GetUtcNow());
        var before = Versions(f).ToList();

        await Assert.ThrowsAsync<IssuanceDenied>(() => f.Redaction().RedactEnrollmentAsync(id, ledger.Generation!, Token));

        Assert.Equal(before, Versions(f));
    }

    [Fact]
    public async Task Cleanup_run_stops_after_16_listing_pages()
    {
        var f = new InMemoryBroker();
        f.State.EmptyPages = 100;

        var run = await f.CleanupAsync(dispatches: 10_000, batches: 10_000);

        Assert.Equal(0, run.Exit);
        Assert.Equal("page-budget", run.StopReason);
        Assert.Equal(16, run.Int("logicalQueryPages"));
        Assert.Equal(16, f.State.Lists);
    }

    // An empty page that still carries a continuation is checkpointed with it, kept until the family's next turn resumes
    // from it, and the cycle then completes.
    [Fact]
    public async Task Empty_page_continuation_is_checkpointed_and_resumed()
    {
        var f = new InMemoryBroker();
        f.State.EmptyPages = 1;
        var stored = new List<object>();
        f.State.BeforeCommit = transaction =>
        {
            foreach (var write in transaction.Writes)
                if (write.Row?.TryGetValue("EnrollmentContinuation", out var continuation) == true) stored.Add(continuation);
            return Task.CompletedTask;
        };

        var run = await f.CleanupAsync();

        Assert.Equal(0, run.Exit);
        Assert.NotEmpty(stored);
        Assert.All(stored, continuation => Assert.Equal("after:", continuation));
        Assert.Equal(0, f.State.EmptyPages);
        Assert.Null(f.State.Get(CleanupCursor.Payload)!.Enrollment.Continuation);
        Assert.Equal(1L, f.State.Get(CleanupCursor.Payload)!.Enrollment.Cycle);
    }

    // When the run's last turn failed, that turn's lane reports no cursor ages: the failed checkpoint is not progress.
    // Each turn rereads both cursors, so a lane whose failed turn came earlier reports its stored ages.
    [Fact]
    public async Task Lane_of_the_failed_last_turn_reports_unknown_cursor_and_cycle_ages()
    {
        var f = new InMemoryBroker();
        Assert.Equal(0, (await f.CleanupAsync()).Exit);
        f.State.BeforeCommit = transaction => transaction.Touches(CleanupCursor.Payload.Name) || transaction.Touches(CleanupCursor.Audit.Name)
            ? throw new BrokerStateUnavailable("The cursor checkpoint did not commit.") : Task.CompletedTask;

        var run = await f.CleanupAsync();

        Assert.Equal(1, run.Exit);
        Assert.Equal(4, run.Int("failures"));
        Assert.Equal("stream-failed", run.StopReason);
        var ages = run.Summary.GetProperty("cursorAndCycleAges").EnumerateArray()
            .GroupBy(age => age.GetProperty("lane").GetString()!)
            .ToDictionary(lane => lane.Key, lane => lane.SelectMany(age => new[] { age.GetProperty("cursorAgeSeconds"), age.GetProperty("cycleAgeSeconds") })
                .Select(age => age.ValueKind).Distinct().Single());
        Assert.Equal(2, ages.Count);
        Assert.Single(ages, lane => lane.Value == JsonValueKind.Null);
        Assert.Single(ages, lane => lane.Value == JsonValueKind.Number);
    }

    // A cursor outside its schema fails the run before any turn, as an unreadable one does, and nothing is written.
    [Theory]
    [InlineData("SchemaVersion", 2L)]
    [InlineData("NextFamily", 2L)]
    [InlineData("NextLane", 2L)]
    [InlineData("EnrollmentLastKey", "pending:not-an-id")]
    [InlineData("IssuanceCycle", -1L)]
    public async Task Cleanup_cursor_outside_its_schema_fails_the_run_before_any_turn(string property, object value)
    {
        var f = new InMemoryBroker();
        Assert.Equal(0, (await f.CleanupAsync()).Exit);
        f.State.Change(CleanupCursor.Payload, row => row[property] = value);
        var before = Versions(f).ToList();

        var run = await f.CleanupAsync();

        Assert.Equal(1, run.Exit);
        Assert.Equal(1, run.Int("failures"));
        Assert.Equal("storage-or-state-failed", run.StopReason);
        Assert.Equal(0, run.Int("processedAllocations"));
        Assert.Equal(before, Versions(f));
    }

    // An unpublished enrollment, released at its deadline and 30 days past it, so that retirement may start.
    private static async Task<(InMemoryBroker F, string Id, EnrollmentLedger Ledger)> RetirementEligibleAsync()
    {
        var f = new InMemoryBroker();
        var lost = false;
        f.State.AfterCommit = _ =>
        {
            if (lost) return Task.CompletedTask;
            lost = true;
            throw new BrokerStateUnavailable("Lost admission response before repository publication.");
        };
        var id = NewId();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(Retained.AgentRequest(id), 2)).StatusCode);
        f.State.AfterCommit = null;
        var ledger = Retained.EnrollmentLedger(f, id).Value;
        Assert.Null(f.State.Row(PendingKey(id)));
        f.Clock.Advance(ledger.Deadline!.Value - f.Clock.GetUtcNow());
        await Retained.ReclaimEnrollment(f, id);
        Assert.Equal("closed", f.State.Get(PendingKey(id))!.State);
        f.Clock.Advance(TimeSpan.FromDays(30));
        return (f, id, Retained.EnrollmentLedger(f, id).Value);
    }
}
