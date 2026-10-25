using System.Net;
using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

// The `cleanup --once` run, as the broker core runs it over the in-memory broker state: what one run redacts, how its
// 60-second deadline stops it, the summary monitoring reads, and what it does with rows no record can read. Every run
// uses the broker's manual clock.
public sealed partial class BrokerCleanupTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Cleanup_removes_due_enrollment_payload_and_keeps_its_deadlines()
    {
        var f = new InMemoryBroker();
        var (id, _) = await Retained.StartAgent(f);
        var ledgerKey = EnrollmentLedger.Key(id);
        var pendingKey = PendingControllerEnrollment.Key(Retained.Repository, id);
        var before = f.State.Get(ledgerKey)!;
        Assert.True(Stores(f, ledgerKey, "RecoveryHash"));
        Assert.True(Stores(f, pendingKey, "RecoveryHash"));
        f.Clock.Advance(TimeSpan.FromDays(2));
        Assert.True(before.PayloadPurgeAt < f.Clock.GetUtcNow());

        var run = await Run(f);

        Assert.Equal(0, run.Int("failures"));
        Assert.Equal(0, run.Exit);
        var ledger = f.State.Get(ledgerKey)!;
        Assert.Equal(before.Deadline, ledger.Deadline);
        Assert.Equal(before.PayloadPurgeAt, ledger.PayloadPurgeAt);
        Assert.False(Stores(f, ledgerKey, "RecoveryHash"), "Cleanup must remove the overdue global setup secret hash.");
        Assert.False(Stores(f, pendingKey, "RecoveryHash"), "Cleanup must remove the overdue repository setup secret hash.");
    }

    [Fact]
    public async Task Deadline_cancelled_cycle_checkpoint_reports_unknown_selected_lane_ages()
    {
        var f = new InMemoryBroker();
        Assert.Equal(0, (await Run(f)).Exit);
        var old = f.Clock.GetUtcNow().AddDays(-2);
        foreach (var key in new[] { CleanupCursor.Payload, CleanupCursor.Audit })
            f.State.Change(key, row =>
            {
                row["NextFamily"] = 0L;
                if (key == CleanupCursor.Payload) row["NextLane"] = 0L;
                foreach (var family in new[] { "Enrollment", "Issuance" })
                    row[family + "StartedAt"] = row[family + "ProgressAt"] = old;
            });
        var durableBefore = f.State.Get(CleanupCursor.Audit)!;
        var cycle = durableBefore.Issuance.Cycle!.Value;
        // The audit lane's issuance cycle checkpoint, the run's last transaction, is held while the deadline fires; the
        // deadline then cancels it before it reaches the store, as it cancels a Table request in flight.
        bool Checkpoint(StateTransaction transaction) =>
            transaction.Touches(CleanupCursor.Audit.Name) && transaction.Writes("IssuanceCycle", cycle + 1);
        var held = new CommitPause(f.State, Checkpoint);
        var pause = f.State.BeforeCommit!;
        f.State.BeforeCommit = async transaction =>
        {
            await pause(transaction);
            if (Checkpoint(transaction)) throw new OperationCanceledException("The run's deadline cancelled the checkpoint.");
        };
        var clock = new TimerClock(f.Clock);
        var running = f.CleanupAsync(clock);
        await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var requests = f.State.Reads + f.State.Lists + f.State.Commits;
        Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(clock.Due));
        clock.Fire();
        held.Release.TrySetResult();
        var result = await running;

        Assert.Equal(requests, f.State.Reads + f.State.Lists + f.State.Commits);
        Assert.Equal(0, result.Exit);
        Assert.Equal("time-budget", result.StopReason);
        Assert.Equal(0, result.Int("failures"));
        var durableAfter = f.State.Get(CleanupCursor.Audit)!;
        Assert.Equal(cycle, durableAfter.Issuance.Cycle);
        Assert.Equal(durableBefore.Issuance.StartedAt, durableAfter.Issuance.StartedAt);
        Assert.Equal(durableBefore.Issuance.ProgressAt, durableAfter.Issuance.ProgressAt);
        var selected = Assert.Single(result.Summary.GetProperty("cursorAndCycleAges").EnumerateArray(),
            row => row.GetProperty("lane").GetString() == "audit" && row.GetProperty("family").GetString() == "issuance");
        Assert.Equal(JsonValueKind.Null, selected.GetProperty("cursorAgeSeconds").ValueKind);
        Assert.Equal(JsonValueKind.Null, selected.GetProperty("cycleAgeSeconds").ValueKind);
    }

    [Fact]
    public async Task Summary_keeps_its_monitored_properties_when_the_run_succeeds_or_its_store_fails()
    {
        // Production dashboards compute metrics from these names.
        string[] schema = [.. new[] { "processedAllocations", "deletedRowsObserved", "payloadFieldsRemovedObserved", "unsettledAllocationsObserved",
            "failures", "conflicts", "incompleteCoverage", "stopReason", "logicalQueryPages", "sdkTableDispatches", "sdkBatchDispatches",
            "reviewedHttp11Envelope", "elapsedMilliseconds", "oldestObservedOverduePayloadAt", "oldestObservedOverduePayloadAgeSeconds",
            "unknownDeadlineCount", "cursorAndCycleAges" }.Order()];
        var succeeding = await Run(new InMemoryBroker());
        Assert.Equal(0, succeeding.Exit);
        Assert.Equal(schema, succeeding.Summary.EnumerateObject().Select(p => p.Name).Order());

        var f = new InMemoryBroker();
        f.State.BeforeRead = _ => throw new BrokerStateUnavailable("The store does not answer");
        f.State.BeforeList = _ => throw new BrokerStateUnavailable("The store does not answer");
        f.State.BeforeCommit = _ => throw new BrokerStateUnavailable("The store does not answer");
        var failing = await Run(f);
        Assert.Equal(1, failing.Exit);
        Assert.Equal("storage-or-state-failed", failing.StopReason);
        Assert.Equal(schema, failing.Summary.EnumerateObject().Select(p => p.Name).Order());
    }

    // A cleanup cursor no record can read fails the run as it reads the cursors, before any turn: nothing is written, the
    // due payload stays, and only a cursor read before it reports ages.
    [Theory]
    [InlineData("payload")]
    [InlineData("audit")]
    public async Task Unreadable_cleanup_cursor_fails_the_run_before_any_turn_and_writes_nothing(string lane)
    {
        var f = new InMemoryBroker();
        Assert.Equal(0, (await Run(f)).Exit);
        var (id, _) = await Retained.StartAgent(f);
        f.Clock.Advance(TimeSpan.FromDays(2));
        var cursor = lane == "payload" ? CleanupCursor.Payload : CleanupCursor.Audit;
        f.State.Change(cursor, row => row["Unrecognized"] = "x");
        var before = f.Rows();
        var commits = f.State.Commits;

        var run = await Run(f);

        Assert.Equal(1, run.Exit);
        Assert.Equal(1, run.Int("failures"));
        Assert.Equal(0, run.Int("conflicts"));
        Assert.Equal("storage-or-state-failed", run.StopReason);
        Assert.True(Incomplete(run));
        Assert.Equal(0, run.Int("processedAllocations"));
        // Both quotas, then the cursors in lane order up to the unreadable one.
        Assert.Equal(lane == "payload" ? 3 : 4, run.Int("sdkTableDispatches"));
        Assert.Equal(0, run.Int("sdkBatchDispatches"));
        Assert.Equal(commits, f.State.Commits);
        Assert.Equal(before.Select(row => (row.Key, row.Version)), f.Rows().Select(row => (row.Key, row.Version)));
        Assert.True(Stores(f, EnrollmentLedger.Key(id), "RecoveryHash"));
        foreach (var ages in run.Summary.GetProperty("cursorAndCycleAges").EnumerateArray())
        {
            // The payload cursor, read first, last progressed in the first run, two days ago.
            var read = lane == "audit" && ages.GetProperty("lane").GetString() == "payload";
            foreach (var age in new[] { ages.GetProperty("cursorAgeSeconds"), ages.GetProperty("cycleAgeSeconds") })
                if (read) Assert.Equal(172_800, age.GetDouble());
                else Assert.Equal(JsonValueKind.Null, age.ValueKind);
        }
    }

    // An enrollment ledger no record can read fails every turn that lists it first. In both lanes the enrollment family
    // stays on the ledger before it, run after run: it never reaches the ledgers after it or completes a cycle, and its
    // cursor ages grow. The issuance family redacts every due GitHub token, even the unreadable enrollment's own, and
    // completes its cycles. The request budget spreads the runs' work.
    [Fact]
    public async Task Unreadable_enrollment_ledger_holds_both_lanes_on_the_ledger_before_it_while_issuance_is_redacted()
    {
        var f = new InMemoryBroker();
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        string[] ids = [1.ToString("x32"), 2.ToString("x32"), 3.ToString("x32")];
        foreach (var id in ids)
        {
            Assert.Equal(HttpStatusCode.OK, (await f.Start(Retained.AgentRequest(id), 2)).StatusCode);
            f.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        var operations = Retained.IssuanceLedgers(f).Select(ledger => ledger.Value).ToList();
        Assert.Equal(3, operations.Count);
        Assert.All(operations, operation => Assert.True(Stores(f, Retained.Attempt(operation), "ProtectedToken")));
        f.Clock.Advance(TimeSpan.FromDays(2));
        var unreadable = EnrollmentLedger.Key(ids[1]);
        f.State.Change(unreadable, row => row["Unrecognized"] = "x");
        var previous = EnrollmentLedger.Key(ids[0]).Name;
        bool SetupSecretHash(string id) => Stores(f, EnrollmentLedger.Key(id), "RecoveryHash")
            || Stores(f, PendingControllerEnrollment.Key(Retained.Repository, id), "RecoveryHash");
        bool[] Tokens() => [.. operations.Select(operation => Stores(f, Retained.Attempt(operation), "ProtectedToken"))];
        void AssertCursors(string enrollmentLastKey, long enrollmentCycle, string issuanceLastKey, long issuanceCycle)
        {
            foreach (var cursor in new[] { f.State.Get(CleanupCursor.Payload)!, f.State.Get(CleanupCursor.Audit)! })
            {
                Assert.Equal(enrollmentLastKey, cursor.Enrollment.LastKey);
                Assert.Equal(enrollmentCycle, cursor.Enrollment.Cycle);
                Assert.Equal(issuanceLastKey, cursor.Issuance.LastKey);
                Assert.Equal(issuanceCycle, cursor.Issuance.Cycle);
            }
        }
        void AssertAges(CleanupRun run, double enrollment, double issuanceCursor, double issuanceCycle)
        {
            foreach (var ages in run.Summary.GetProperty("cursorAndCycleAges").EnumerateArray())
            {
                var issuance = ages.GetProperty("family").GetString() == "issuance";
                Assert.Equal(issuance ? issuanceCursor : enrollment, ages.GetProperty("cursorAgeSeconds").GetDouble());
                Assert.Equal(issuance ? issuanceCycle : enrollment, ages.GetProperty("cycleAgeSeconds").GetDouble());
            }
        }

        // The first run finishes the ledger before it and the first operation in both lanes, then stops for its budget.
        var first = await Run(f);
        Assert.Equal(0, first.Exit);
        Assert.Equal(0, first.Int("failures"));
        Assert.Equal("request-budget", first.StopReason);
        Assert.Equal(4, first.Int("processedAllocations"));
        Assert.True(Incomplete(first));
        AssertCursors(previous, 0, IssuanceLedger.Key(operations[0].OperationId!).Name, 0);
        Assert.Equal([false, true, true], ids.Select(SetupSecretHash));
        Assert.Equal([false, true, true], Tokens());
        AssertAges(first, 0, 0, 0);

        // The second run meets it in both lanes; the issuance family goes on to the last operation.
        f.Clock.Advance(TimeSpan.FromHours(1));
        var second = await Run(f);
        Assert.Equal(1, second.Exit);
        Assert.Equal(2, second.Int("failures"));
        Assert.Equal(0, second.Int("conflicts"));
        Assert.Equal("request-budget", second.StopReason);
        Assert.Equal(4, second.Int("processedAllocations"));
        Assert.True(Incomplete(second));
        AssertCursors(previous, 0, IssuanceLedger.Key(operations[2].OperationId!).Name, 0);
        Assert.Equal([false, true, true], ids.Select(SetupSecretHash));
        Assert.Equal([false, false, false], Tokens());
        AssertAges(second, 3600, 0, 3600);

        // The third meets it again in both lanes, and the issuance family completes its cycle.
        f.Clock.Advance(TimeSpan.FromHours(1));
        var third = await Run(f);
        Assert.Equal(1, third.Exit);
        Assert.Equal(2, third.Int("failures"));
        Assert.Equal(0, third.Int("conflicts"));
        Assert.Equal("stream-failed", third.StopReason);
        Assert.Equal(0, third.Int("processedAllocations"));
        Assert.True(Incomplete(third));
        AssertCursors(previous, 0, "", 1);
        Assert.Equal([false, true, true], ids.Select(SetupSecretHash));
        AssertAges(third, 7200, 0, 0);
        Assert.Equal("x", f.State.Row(unreadable)!["Unrecognized"]);
    }

    // A page the listing query could not have answered proves nothing. The turn that got it fails and writes nothing for
    // it, both lanes' enrollment family stays where it was, and the issuance family completes its cycle.
    [Theory]
    [InlineData("before-range")]
    [InlineData("past-range")]
    [InlineData("other-scope")]
    [InlineData("not-after-the-position")]
    [InlineData("oversized-page")]
    [InlineData("oversized-continuation")]
    public async Task Page_the_query_could_not_have_answered_fails_its_turn_and_holds_the_family(string answer)
    {
        var f = new InMemoryBroker();
        var (id, _) = await Retained.StartAgent(f);
        var key = EnrollmentLedger.Key(id);
        // Each lane's first enrollment page, or the page after the ledger the lane then processed.
        var position = answer == "not-after-the-position" ? key.Name : "";
        ListedRow Ledger(string scope, string name) => new(scope, name, f.State.Row(key), f.State.Version(key));
        f.State.Answer = page => page.Scope != IssuanceCapacity.EnrollmentScope || page.After != position ? page : answer switch
        {
            "before-range" => page with { Rows = [Ledger(page.Scope, "pending!" + id)] },
            "past-range" => page with { Rows = [Ledger(page.Scope, "pending;" + id)] },
            "other-scope" => page with { Rows = [Ledger(IssuanceCapacity.Scope, key.Name)] },
            "not-after-the-position" => page with { Rows = [Ledger(page.Scope, key.Name)] },
            "oversized-page" => page with
            {
                Rows = [Ledger(page.Scope, key.Name), .. Enumerable.Range(0, 32).Select(i => Ledger(page.Scope, key.Name + i.ToString("D2")))]
            },
            _ => page with { Rows = [], Continuation = "after:" + new string('~', 8187) },
        };

        var run = await f.CleanupAsync(dispatches: 10_000, batches: 10_000);

        Assert.Equal(1, run.Exit);
        Assert.Equal(2, run.Int("failures"));
        Assert.Equal("stream-failed", run.StopReason);
        foreach (var cursor in new[] { f.State.Get(CleanupCursor.Payload)!, f.State.Get(CleanupCursor.Audit)! })
        {
            Assert.Equal(position, cursor.Enrollment.LastKey);
            Assert.Null(cursor.Enrollment.Continuation);
            Assert.Equal(0L, cursor.Enrollment.Cycle);
            Assert.Equal(1L, cursor.Issuance.Cycle);
        }
    }

    // A listed ledger the store could not bound, or answered without a version, fails only the turns that read it: the
    // ledgers before it on the same page are redacted and reclaimed, and both lanes' enrollment family then stays on the
    // ledger before it.
    [Theory]
    [InlineData("unbounded")]
    [InlineData("no-version")]
    public async Task Listed_ledger_without_its_row_or_version_fails_only_the_turns_that_read_it(string answer)
    {
        var f = new InMemoryBroker();
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        string[] ids = [1.ToString("x32"), 2.ToString("x32"), 3.ToString("x32")];
        foreach (var id in ids)
        {
            Assert.Equal(HttpStatusCode.OK, (await f.Start(Retained.AgentRequest(id), 2)).StatusCode);
            f.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        f.Clock.Advance(TimeSpan.FromDays(2));
        var bad = EnrollmentLedger.Key(ids[2]).Name;
        var listed = 0;
        f.State.Answer = page =>
        {
            if (page.Scope != IssuanceCapacity.EnrollmentScope) return page;
            if (page.Rows.Any(row => row.Name == bad)) listed++;
            return page with
            {
                Rows = [.. page.Rows.Select(row => row.Name != bad ? row : answer == "unbounded" ? row with { Row = null } : row with { Version = null })]
            };
        };

        var run = await f.CleanupAsync(dispatches: 10_000, batches: 10_000);

        Assert.Equal(1, run.Exit);
        Assert.Equal(2, run.Int("failures"));
        Assert.Equal("stream-failed", run.StopReason);
        Assert.Equal(6, listed);
        Assert.Equal([false, false, true], ids.Select(id => Stores(f, EnrollmentLedger.Key(id), "RecoveryHash")));
        Assert.Equal([false, false, true], ids.Select(id => Stores(f, PendingControllerEnrollment.Key(Retained.Repository, id), "RecoveryHash")));
        Assert.Equal([true, true, false], ids.Select(id => f.State.Get(EnrollmentLedger.Key(id))!.PendingCapacityReleased == true));
        foreach (var cursor in new[] { f.State.Get(CleanupCursor.Payload)!, f.State.Get(CleanupCursor.Audit)! })
        {
            Assert.Equal(EnrollmentLedger.Key(ids[1]).Name, cursor.Enrollment.LastKey);
            Assert.Equal(0L, cursor.Enrollment.Cycle);
            Assert.Equal(1L, cursor.Issuance.Cycle);
        }
    }

    // A clock whose time is the broker's manual clock, whose timestamps follow that time, so a run's elapsed time is the
    // manual time that passed, and whose timers are the TimerClock's.
    private sealed class ElapsedClock(IssuanceClock clock, TimerClock timers) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();
        public override long GetTimestamp() => clock.GetUtcNow().UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            timers.CreateTimer(callback, state, dueTime, period);
    }
}
