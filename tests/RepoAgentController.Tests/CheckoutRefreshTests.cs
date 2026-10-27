using Xunit;

namespace RepoAgentController.Tests;

// The managed checkout's refresh in the core: when a refresh may run, what the journal records before, during and after it,
// and how admitted agent work keeps the checkout excluded until every turn that may hold its input has settled.
public sealed class CheckoutRefreshTests : IAsyncDisposable
{
    static readonly CheckoutBinding Binding = new("/workspace/repository", "0123456789abcdef0123456789abcdef", 345, "refs/heads/main");
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    const string Uncertain = "Checkout/native state uncertain; manual recovery required";
    readonly InMemoryRefreshJournal journal = new();
    readonly ScriptedCheckout checkout = new();
    readonly SteppedClock clock = new(new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
    readonly RecordingLog log = new();
    readonly FakeAgentRuntime runtime = new();
    CheckoutRefresh? refresh;

    static CancellationToken Token => TestContext.Current.CancellationToken;

    RefreshJournal Saved => journal.Saved!;

    /// <summary>The refresh as a start opens it. Until it is started no worker runs and no refresh is requested, so only
    /// admissions and agent events change its journal; a running worker wakes on each and may record a block as its own reason.</summary>
    CheckoutRefresh Open(bool priorSessions = false, bool pendingDiscussion = false) =>
        refresh = new(checkout, Binding, journal, priorSessions, pendingDiscussion, clock, log);

    Task Start(CheckoutRefresh opened, CancellationToken ct) => opened.Start(new AgentSessions(runtime, log, opened), Interval, ct);

    /// <summary>A started refresh, its worker running.</summary>
    async Task<CheckoutRefresh> Started()
    {
        var started = Open();
        await Start(started, Token);
        return started;
    }

    static RefreshJournal Recorded(Action<RefreshJournal>? change = null)
    {
        var recorded = new RefreshJournal(Binding.Root, Binding.Owner, Binding.RepositoryId) { Requested = 2, Completed = 2 };
        change?.Invoke(recorded);
        return recorded;
    }

    [Fact]
    public async Task Startup_uncertainty_keeps_the_controller_available_without_admitting_or_restarting_agent_work()
    {
        journal.Saved = Recorded();
        var opened = Open(pendingDiscussion: true);

        await Start(opened, Token);

        Assert.True(Saved.Blocked);
        Assert.Contains(log.Entries, entry => entry.Kind == "refresh-blocked");
        Assert.Equal(Uncertain, (await Assert.ThrowsAsync<IOException>(() => opened.Admit(Token))).Message);
        var committed = false;
        Assert.False(await opened.CommitAtIdle(_ => { committed = true; return Task.CompletedTask; }, Token));
        Assert.False(committed);
        Assert.Equal(0, checkout.Refreshes);
        Assert.Equal(1, runtime.Epoch);
    }

    [Fact]
    public void A_failed_startup_evidence_snapshot_does_not_rewrite_the_original_journal()
    {
        journal.Saved = Recorded();
        journal.PreserveFailure = new IOException("Evidence storage unavailable");

        Assert.Throws<IOException>(() => Open(pendingDiscussion: true));

        Assert.False(Saved.Blocked);
    }

    [Fact]
    public async Task A_later_runtime_fault_keeps_admission_closed_until_explicit_recovery()
    {
        journal.Saved = Recorded();
        var opened = Open(pendingDiscussion: true);
        await Start(opened, Token);

        runtime.Disconnect();

        Assert.True(Saved.Blocked);
        Assert.True(opened.AdmissionBlocked);
    }

    static async Task Idle(CheckoutRefresh refresh)
    {
        using var turn = await refresh.Admit(Token);
        turn.Abandon();
    }

    static AgentTurnCompleted Completed(string session, string turn) => new(1, session, turn, AgentTurnStatus.Completed, "completed", null);

    static async Task Delivered(CheckoutRefresh refresh, string session, string turn)
    {
        using (var admitted = await refresh.Admit(Token))
        {
            admitted.Bind(session);
            admitted.Accepted(session, turn);
            admitted.Confirm();
        }
        refresh.Observe(Completed(session, turn));
    }

    [Fact]
    public async Task A_first_start_records_a_journal_bound_to_the_checkout_and_applies_the_refresh_it_requests()
    {
        RefreshJournal? applying = null;
        checkout.Then((beforeApply, _) =>
        {
            beforeApply("C1", "C2");
            applying = journal.Saved;
            return Task.FromResult(true);
        });

        await Started();

        // The refresh is recorded before the checkout changes, then completed and cleared.
        Assert.Equal((1L, 0L, new RefreshApplying("C1", "C2", 1)), (applying!.Requested, applying.Completed, applying.Applying));
        Assert.Equal((Binding.Root, Binding.Owner, Binding.RepositoryId), (Saved.Root, Saved.Owner, Saved.RepositoryId));
        Assert.Equal((1L, 1L, false, 0), (Saved.Requested, Saved.Completed, Saved.Blocked, Saved.Running));
        Assert.Null(Saved.Applying);
        Assert.Null(Saved.Reason);
        Assert.Equal(2, runtime.Epoch); // A changed checkout reloads the agent runtime.
        Assert.Equal(1, checkout.Refreshes);
    }

    [Fact]
    public async Task An_unchanged_checkout_completes_the_refresh_without_reloading_the_agent()
    {
        journal.Saved = Recorded(recorded => recorded.Reason = "authoritative default-branch lookup unavailable; retry pending");

        await Started();

        Assert.Equal((3L, 3L), (Saved.Requested, Saved.Completed));
        Assert.Null(Saved.Reason);
        Assert.Equal(1, runtime.Epoch);
    }

    public static TheoryData<string> Uncertainties() =>
    [
        "earlier sessions without a journal", "a pending Discussion delivery", "a recorded block", "a recorded admission",
        "a recorded running turn", "a recorded refresh applying"
    ];

    [Theory]
    [MemberData(nameof(Uncertainties))]
    public async Task A_start_that_cannot_rule_out_agent_work_or_a_checkout_change_blocks_admission(string form)
    {
        if (form != "earlier sessions without a journal") journal.Saved = Recorded(saved =>
        {
            if (form == "a recorded block") saved.Blocked = true;
            if (form == "a recorded admission") saved.Active["a"] = new(1) { Thread = "S1" };
            if (form == "a recorded running turn") saved.Running = 1;
            if (form == "a recorded refresh applying") saved.Applying = new("C1", "C2", 2);
        });

        var opened = Open(priorSessions: form == "earlier sessions without a journal", pendingDiscussion: form == "a pending Discussion delivery");

        Assert.True(opened.AdmissionBlocked);
        if (form == "earlier sessions without a journal") Assert.Null(journal.Saved);
        else Assert.Equal((true, "Native activity or checkout update is uncertain; inspect state before recovery"), (Saved.Blocked, Saved.Reason));
        Assert.Equal(Uncertain, (await Assert.ThrowsAsync<IOException>(() => opened.Admit(Token))).Message);
        Assert.False(await opened.CommitAtIdle(_ => throw new InvalidOperationException("A blocked checkout must not commit"), Token));
    }

    public static TheoryData<string> BlocksBeforeTheRunningCount() => ["a recorded block", "a recorded admission", "a recorded refresh applying"];

    // As before the move, the start reads the running count only when nothing before it blocks.
    [Theory]
    [MemberData(nameof(BlocksBeforeTheRunningCount))]
    public void A_running_count_no_writer_leaves_beside_an_earlier_block_is_retained_for_recovery_refusal(string form)
    {
        journal.Saved = Recorded(saved =>
        {
            saved.RunningUnreadable = true;
            if (form == "a recorded block") saved.Blocked = true;
            if (form == "a recorded admission") saved.Active["a"] = new(1) { Thread = "S1" };
            if (form == "a recorded refresh applying") saved.Applying = new("C1", "C2", 2);
        });

        Open();

        Assert.Equal((true, 0, true), (Saved.Blocked, Saved.Running, Saved.RunningUnreadable));
        Assert.Equal("Native activity or checkout update is uncertain; inspect state before recovery", Saved.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_running_count_no_writer_leaves_with_no_earlier_block_stops_the_start_before_it_writes(bool pendingDiscussion)
    {
        journal.Saved = Recorded(saved => saved.RunningUnreadable = true);
        journal.WriteFailure = new IOException("written");

        Assert.Equal(RefreshJournal.Unusable, Assert.Throws<IOException>(() => Open(pendingDiscussion: pendingDiscussion)).Message);
    }

    [Fact]
    public async Task Earlier_sessions_with_a_settled_journal_start_unblocked()
    {
        journal.Saved = Recorded();
        var opened = Open(priorSessions: true);

        Assert.False(Saved.Blocked);
        Assert.Null(Saved.Reason);
        await Idle(opened);
    }

    public static TheoryData<string> Unusable() => ["root", "owner", "repository", "completed below zero", "requested below completed"];

    [Theory]
    [MemberData(nameof(Unusable))]
    public void A_journal_bound_to_another_checkout_or_with_impossible_generations_stops_the_start_before_it_writes(string form)
    {
        journal.Saved = form switch
        {
            "root" => new RefreshJournal("/workspace/other", Binding.Owner, Binding.RepositoryId),
            "owner" => new RefreshJournal(Binding.Root, "fedcba9876543210fedcba9876543210", Binding.RepositoryId),
            "repository" => new RefreshJournal(Binding.Root, Binding.Owner, 346),
            "completed below zero" => Recorded(saved => saved.Completed = -1),
            _ => Recorded(saved => saved.Requested = 1)
        };
        journal.WriteFailure = new IOException("written");

        Assert.Equal(RefreshJournal.Unusable, Assert.Throws<IOException>(() => Open()).Message);
    }

    [Fact]
    public async Task A_deferred_refresh_keeps_its_reason_and_retries_only_after_five_seconds_or_a_new_request()
    {
        checkout.Defers("default branch changed; inspect the recorded binding");
        var started = await Started();
        Assert.Equal((1L, 0L, false, "default branch changed; inspect the recorded binding"), (Saved.Requested, Saved.Completed, Saved.Blocked, Saved.Reason));
        Assert.Equal(("refresh-pending", "default branch changed; inspect the recorded binding"), Assert.Single(log.Entries));

        // Admission takes the idle boundary, but a deferred refresh waits five seconds on the clock.
        checkout.Defers("still deferred");
        await Idle(started);
        clock.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1));
        await Idle(started);
        Assert.Equal(1, checkout.Refreshes);
        clock.Advance(TimeSpan.FromTicks(1));
        await Idle(started);
        Assert.Equal((2, "still deferred"), (checkout.Refreshes, Saved.Reason));

        // A new request, as a push to the default branch makes, retries at once.
        started.Request();
        await Idle(started);
        Assert.Equal((3, 2L, 2L), (checkout.Refreshes, Saved.Requested, Saved.Completed));
        Assert.Null(Saved.Reason);
    }

    [Fact]
    public async Task A_refresh_deferred_after_it_began_applying_blocks_admission_with_its_reason()
    {
        checkout.Then((beforeApply, _) =>
        {
            beforeApply("C1", "C2");
            return Task.FromException<bool>(new CheckoutDeferred("fetch refused: changed"));
        });
        var opened = Open();
        opened.Request();

        // Admission takes the idle boundary, where the refresh begins applying and then defers.
        Assert.Equal(Uncertain, (await Assert.ThrowsAsync<IOException>(() => opened.Admit(Token))).Message);

        Assert.Equal(("refresh-pending", "fetch refused: changed"), Assert.Single(log.Entries));
        Assert.Equal((true, "fetch refused: changed", new RefreshApplying("C1", "C2", 1)), (Saved.Blocked, Saved.Reason, Saved.Applying));
    }

    [Fact]
    public async Task A_running_worker_records_a_block_it_finds_as_its_own_reason()
    {
        checkout.Then((beforeApply, _) =>
        {
            beforeApply("C1", "C2");
            return Task.FromException<bool>(new CheckoutDeferred("fetch refused: changed"));
        });

        var opened = Open();
        await Start(opened, Token);
        await log.Written(2);

        Assert.Equal([("refresh-pending", "fetch refused: changed"), ("refresh-blocked", Uncertain)], log.Entries);
        Assert.Equal((true, Uncertain, new RefreshApplying("C1", "C2", 1)), (Saved.Blocked, Saved.Reason, Saved.Applying));
    }

    [Fact]
    public async Task A_refresh_an_earlier_lifetime_requested_waits_for_this_lifetimes_request()
    {
        journal.Saved = Recorded(saved => saved.Requested = 3);
        var opened = Open();

        await Idle(opened);
        Assert.Equal(0, checkout.Refreshes);

        opened.Request();
        await Idle(opened);
        Assert.Equal((1, 4L, 4L), (checkout.Refreshes, Saved.Requested, Saved.Completed));
    }

    [Fact]
    public async Task A_refresh_that_fails_unsafely_blocks_admission_with_its_reason()
    {
        checkout.Then((_, _) => Task.FromException<bool>(new IOException("Managed checkout blocked: ownership record changed")));
        var opened = Open();

        Assert.Equal("Managed checkout blocked: ownership record changed", (await Assert.ThrowsAsync<IOException>(() => Start(opened, Token))).Message);
        Assert.Equal((true, "Managed checkout blocked: ownership record changed", 0L), (Saved.Blocked, Saved.Reason, Saved.Completed));
        Assert.Equal(Uncertain, (await Assert.ThrowsAsync<IOException>(() => opened.Admit(Token))).Message);
    }

    [Fact]
    public async Task Agent_work_that_becomes_uncertain_while_a_refresh_runs_leaves_it_incomplete_and_blocked()
    {
        checkout.Then((beforeApply, _) =>
        {
            beforeApply("C1", "C2");
            refresh!.Observe(new AgentRuntimeDisconnected(1));
            return Task.FromResult(false);
        });
        var opened = Open();

        Assert.Equal("Native refresh became uncertain", (await Assert.ThrowsAsync<IOException>(() => Start(opened, Token))).Message);
        Assert.Equal((true, "Native refresh became uncertain", 0L), (Saved.Blocked, Saved.Reason, Saved.Completed));
    }

    [Fact]
    public async Task Stopping_before_the_refresh_applies_leaves_it_requested_and_stopping_while_it_applies_blocks()
    {
        using var before = CancellationTokenSource.CreateLinkedTokenSource(Token);
        checkout.Then((_, ct) => { before.Cancel(); return Task.FromCanceled<bool>(ct); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Start(Open(), before.Token));
        Assert.Equal((1L, 0L, false), (Saved.Requested, Saved.Completed, Saved.Blocked));
        Assert.Null(Saved.Applying);

        using var applying = CancellationTokenSource.CreateLinkedTokenSource(Token);
        checkout.Then((beforeApply, ct) => { beforeApply("C1", "C2"); applying.Cancel(); return Task.FromCanceled<bool>(ct); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Start(Open(), applying.Token));
        Assert.Equal((2L, 0L, true, "Refresh interrupted while applying"), (Saved.Requested, Saved.Completed, Saved.Blocked, Saved.Reason));
        Assert.Equal(new RefreshApplying("C1", "C2", 2), Saved.Applying);
    }

    [Fact]
    public async Task The_worker_refreshes_at_once_when_requested_and_requests_a_refresh_each_interval_on_the_clock()
    {
        var started = await Started();

        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        checkout.Then((_, _) => { requested.SetResult(); return Task.FromResult(false); });
        started.Request();
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await journal.Reached(saved => saved.Completed == 2);
        Assert.Equal((2, 2L), (checkout.Refreshes, Saved.Requested));

        // Each wait the worker arms on the clock lasts the interval; the first to pass requests a refresh. However the worker
        // arms its waits, it has one pending while it waits, and moving time past it wakes the worker.
        var elapsed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        checkout.Then((_, _) => { elapsed.SetResult(); return Task.FromException<bool>(new IOException("unsafe")); });
        for (var waits = 0; waits < 5; waits++)
        {
            await Task.WhenAny(clock.Waiting(), elapsed.Task);
            if (elapsed.Task.IsCompleted) break;
            clock.Advance(Interval);
        }
        await elapsed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await log.Written(1);

        Assert.All(clock.Delays, delay => Assert.Equal(Interval, delay));
        // A failure in the worker blocks the checkout and ends the worker.
        Assert.Equal(("refresh-blocked", "unsafe"), Assert.Single(log.Entries));
        Assert.Equal((3, 3L, 2L, true, "unsafe"), (checkout.Refreshes, Saved.Requested, Saved.Completed, Saved.Blocked, Saved.Reason));
    }

    [Fact]
    public async Task An_admission_is_recorded_until_its_acknowledged_turn_completes()
    {
        var started = Open();
        string id;
        using (var turn = await started.Admit(Token))
        {
            (id, var admitted) = Assert.Single(Saved.Active);
            Assert.Equal(("", "", false, false), (admitted.Thread, admitted.Turn, admitted.Confirmed, admitted.Unconfirmed));
            turn.Bind("S1");
            Assert.Equal("S1", Saved.Active[id].Thread);
            turn.Accepted("S1", "T1");
            Assert.Equal(("S1", "T1", 1), (Saved.Active[id].Thread, Saved.Active[id].Turn, Saved.Running));
            turn.Confirm();
            Assert.True(Saved.Active[id].Confirmed);
        }
        Assert.False(await started.CommitAtIdle(_ => throw new InvalidOperationException("Committed while a turn runs"), Token));

        started.Observe(Completed("S1", "T1"));

        Assert.Equal((0, 0, false), (Saved.Active.Count, Saved.Running, Saved.Blocked));
        Assert.True(await started.CommitAtIdle(_ => Task.CompletedTask, Token));
    }

    [Fact]
    public async Task A_turn_that_completes_before_its_acknowledgement_keeps_the_admission_until_it_is_acknowledged_and_confirmed()
    {
        var started = Open();
        using var turn = await started.Admit(Token);
        turn.Bind("S1");

        started.Observe(Completed("S1", "T1"));
        var early = Assert.Single(Saved.Active).Value;
        Assert.Equal(("S1", "", false, false, 0), (early.Thread, early.Turn, early.Confirmed, Saved.Blocked, Saved.Running));

        turn.Accepted("S1", "T1");
        Assert.Single(Saved.Active);
        turn.Confirm();
        Assert.Empty(Saved.Active);
        Assert.False(Saved.Blocked);
    }

    [Fact]
    public async Task A_turn_that_completes_before_its_delivery_is_confirmed_keeps_the_admission_until_it_is_confirmed()
    {
        var started = Open();
        using var turn = await started.Admit(Token);
        turn.Bind("S1");
        turn.Accepted("S1", "T1");

        started.Observe(Completed("S1", "T1"));
        Assert.Equal((1, 0), (Saved.Active.Count, Saved.Running));

        turn.Confirm();
        Assert.Empty(Saved.Active);
    }

    [Fact]
    public async Task An_admission_without_input_is_abandoned_but_not_once_acknowledged()
    {
        var started = Open();
        await Idle(started);
        Assert.Empty(Saved.Active);

        using var acknowledged = await started.Admit(Token);
        acknowledged.Bind("S1");
        acknowledged.Accepted("S1", "T1");
        Assert.Equal("Accepted native admission cannot be abandoned", Assert.Throws<IOException>(acknowledged.Abandon).Message);
        Assert.Single(Saved.Active);
    }

    [Fact]
    public async Task Unacknowledged_input_keeps_the_checkout_excluded_until_a_later_turn_on_its_session_completes()
    {
        var started = Open();
        using (var unbound = await started.Admit(Token)) unbound.Unconfirmed();
        Assert.Empty(Saved.Active); // No session, so no turn can hold the input.

        using (var turn = await started.Admit(Token)) { turn.Bind("S1"); turn.Unconfirmed(); }
        var first = Assert.Single(Saved.Active).Key;
        using (var other = await started.Admit(Token)) { other.Bind("S2"); other.Unconfirmed(); }
        using (var turn = await started.Admit(Token)) { turn.Bind("S1"); turn.Unconfirmed(); }

        // Only the newest unacknowledged input per session is kept.
        Assert.Equal(new[] { ("S2", true), ("S1", true) }, Saved.Active.Values.Select(item => (item.Thread, item.Unconfirmed)));
        Assert.DoesNotContain(first, Saved.Active.Keys);
        Assert.False(await started.CommitAtIdle(_ => throw new InvalidOperationException("Committed with input unsettled"), Token));

        await Delivered(started, "S1", "T3");

        // The acknowledged turn settles S1's earlier input; S2's stays.
        Assert.Equal(["S2"], Saved.Active.Values.Select(item => item.Thread));
    }

    [Fact]
    public async Task A_requested_refresh_waits_until_no_admitted_work_may_hold_the_checkout()
    {
        var started = Open();
        using (var turn = await started.Admit(Token)) { turn.Bind("S1"); turn.Unconfirmed(); }
        started.Request();
        using (var turn = await started.Admit(Token)) { turn.Bind("S1"); turn.Accepted("S1", "T1"); turn.Confirm(); }
        Assert.Equal(0, checkout.Refreshes);

        started.Observe(Completed("S1", "T1"));
        await Idle(started);

        Assert.Equal((1, 1L, 1L, false), (checkout.Refreshes, Saved.Requested, Saved.Completed, Saved.Blocked));
    }

    [Fact]
    public async Task Input_sent_after_a_turn_was_acknowledged_is_not_settled_by_its_completion()
    {
        var started = Open();
        using (var turn = await started.Admit(Token)) { turn.Bind("S1"); turn.Accepted("S1", "T1"); turn.Confirm(); }
        using (var turn = await started.Admit(Token)) { turn.Bind("S1"); turn.Unconfirmed(); }

        started.Observe(Completed("S1", "T1"));

        Assert.Equal(new[] { ("S1", true) }, Saved.Active.Values.Select(item => (item.Thread, item.Unconfirmed)));
    }

    [Fact]
    public async Task A_turn_on_a_session_whose_admission_is_already_acknowledged_is_unmatched()
    {
        var started = Open();
        using (var turn = await started.Admit(Token)) { turn.Bind("S1"); turn.Accepted("S1", "T1"); turn.Confirm(); }

        started.Observe(new AgentTurnStarted(1, "S1", "T2"));

        Assert.Equal((true, "Unmatched native lifecycle event"), (Saved.Blocked, Saved.Reason));
    }

    [Fact]
    public async Task An_admission_left_unresolved_blocks_the_checkout_with_its_ID()
    {
        var started = Open();
        using (await started.Admit(Token)) { }

        Assert.Equal((true, "Native admission interrupted; inspect pending operation " + Saved.Active.Keys.Single()), (Saved.Blocked, Saved.Reason));
        Assert.Equal(Uncertain, (await Assert.ThrowsAsync<IOException>(() => started.Admit(Token))).Message);
    }

    [Fact]
    public async Task At_most_64_admissions_stay_unresolved()
    {
        var started = Open();
        for (var number = 1; number <= 64; number++)
            using (var turn = await started.Admit(Token)) { turn.Bind("S" + number); turn.Unconfirmed(); }

        Assert.Equal("Too many unresolved native admissions", (await Assert.ThrowsAsync<IOException>(() => started.Admit(Token))).Message);
        Assert.Equal((64, false), (Saved.Active.Count, Saved.Blocked));
    }

    [Fact]
    public async Task Identities_the_agent_could_not_have_given_are_refused()
    {
        var started = Open();
        using (var turn = await started.Admit(Token))
        {
            Assert.Equal("Malformed native thread identity", Assert.Throws<IOException>(() => turn.Bind("S\n1")).Message);
            Assert.Equal("Malformed native thread identity", Assert.Throws<IOException>(() => turn.Bind("")).Message);
            Assert.Equal("Malformed native thread identity", Assert.Throws<IOException>(() => turn.Bind(new string('S', 257))).Message);
            turn.Bind(new string('S', 256));
            turn.Bind("S1");
            Assert.Equal("Native acknowledgement identity differs", Assert.Throws<IOException>(() => turn.Accepted("S2", "T1")).Message);
            Assert.Equal("Native acknowledgement identity differs", Assert.Throws<IOException>(() => turn.Accepted("S1", "")).Message);
            Assert.Equal("Native admission has no acknowledgement", Assert.Throws<IOException>(turn.Confirm).Message);
            turn.Accepted("S1", "T1");
            turn.Confirm();
        }
        started.Observe(Completed("S1", "T1"));

        // A later admission acknowledged with a turn that completed before it was admitted, even if the completion repeats.
        using (var reused = await started.Admit(Token))
        {
            reused.Bind("S1");
            started.Observe(Completed("S1", "T1"));
            Assert.Equal("Native reused a completed turn identity", Assert.Throws<IOException>(() => reused.Accepted("S1", "T1")).Message);
            reused.Abandon();
        }
        Assert.False(Saved.Blocked);
    }

    public static TheoryData<string, string> Faults() => new()
    {
        { "a turn no admission can hold", "Unmatched native lifecycle event" },
        { "a lost transport", "Native transport disconnected; completion unknown" },
        { "a completed turn restarted", "Native restarted a completed turn identity" },
        { "a turn without an identity", "Malformed native lifecycle identity" },
        { "a completion without an identity", "Malformed native lifecycle identity" },
        { "a turn without a session", "Malformed native lifecycle identity" },
        { "a completion with an identity over 256 characters", "Malformed native lifecycle identity" },
    };

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task An_agent_event_the_journal_cannot_account_for_blocks_the_checkout(string form, string reason)
    {
        var started = Open();
        await Delivered(started, "S1", "T1");
        started.Observe(Completed("S1", "T1")); // A repeated completion is harmless.
        started.Observe(new AgentSessionStatusChanged(1, "S9", AgentSessionStatus.Idle)); // So is any other event.
        Assert.False(Saved.Blocked);

        started.Observe(form switch
        {
            "a turn no admission can hold" => new AgentTurnStarted(1, "S9", "T9"),
            "a lost transport" => new AgentRuntimeDisconnected(1),
            "a completed turn restarted" => new AgentTurnStarted(1, "S1", "T1"),
            "a turn without an identity" => new AgentTurnStarted(1, "S1", null),
            "a completion without an identity" => new AgentTurnCompleted(1, "S1", null, AgentTurnStatus.Completed, "completed", null),
            "a turn without a session" => new AgentTurnStarted(1, "", "T1"),
            _ => Completed("S1", new string('T', 257))
        });

        Assert.Equal((true, reason), (Saved.Blocked, Saved.Reason));
    }

    [Fact]
    public async Task A_new_runtime_process_needs_settled_work_ignores_the_earlier_process_and_forgets_its_turns()
    {
        var started = Open();
        started.Observe(new AgentTurnStarted(2, "S9", "T9")); // Not yet the current process.
        using (var turn = await started.Admit(Token)) { turn.Bind("S1"); turn.Accepted("S1", "T1"); turn.Confirm(); }
        Assert.Equal("Cannot replace native transport with unresolved work", Assert.Throws<IOException>(() => started.NewEpoch(2)).Message);

        started.Observe(Completed("S1", "T1"));
        started.NewEpoch(2);
        started.Observe(new AgentRuntimeDisconnected(1)); // The earlier process.
        Assert.False(Saved.Blocked);

        // The earlier process's completed T1 is forgotten, so the new one's T1 is unmatched rather than restarted.
        started.Observe(new AgentTurnStarted(2, "S1", "T1"));
        Assert.Equal((true, "Unmatched native lifecycle event"), (Saved.Blocked, Saved.Reason));
        Assert.Equal("Cannot replace native transport with unresolved work", Assert.Throws<IOException>(() => started.NewEpoch(3)).Message);
    }

    [Fact]
    public async Task Committing_at_idle_waits_for_a_completed_refresh()
    {
        var started = await Started();
        var commits = 0;
        Assert.True(await started.CommitAtIdle(_ => { commits++; return Task.CompletedTask; }, Token));

        checkout.Defers("retry pending");
        started.Request();
        Assert.False(await started.CommitAtIdle(_ => { commits++; return Task.CompletedTask; }, Token));
        Assert.Equal(1, commits);

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(await started.CommitAtIdle(_ => { commits++; return Task.CompletedTask; }, Token));
        Assert.Equal((2, 2L, 2L), (commits, Saved.Requested, Saved.Completed));
    }

    [Fact]
    public async Task Completed_turns_are_remembered_up_to_256_forgetting_the_oldest_no_admission_can_still_name()
    {
        var started = Open();
        for (var number = 1; number <= 257; number++) await Delivered(started, "S" + number, "T" + number);

        using (var remembered = await started.Admit(Token))
        {
            remembered.Bind("S2");
            Assert.Equal("Native reused a completed turn identity", Assert.Throws<IOException>(() => remembered.Accepted("S2", "T2")).Message);
            remembered.Abandon();
        }
        using (var forgotten = await started.Admit(Token))
        {
            forgotten.Bind("S1");
            forgotten.Accepted("S1", "T1");
            forgotten.Confirm();
        }
        Assert.False(Saved.Blocked);
    }

    [Fact]
    public async Task Turns_an_unacknowledged_admission_may_still_be_acknowledged_with_are_never_forgotten()
    {
        var started = Open();
        using var turn = await started.Admit(Token);
        turn.Bind("S1");
        for (var number = 1; number <= 256; number++) started.Observe(Completed("S1", "T" + number));
        Assert.Equal((0, false), (Saved.Running, Saved.Blocked));

        started.Observe(Completed("S1", "T257"));

        Assert.Equal((true, "Native correlation capacity exhausted; manual recovery required"), (Saved.Blocked, Saved.Reason));
    }

    [Fact]
    public async Task A_turn_still_running_is_never_forgotten()
    {
        var started = Open();
        using (var turn = await started.Admit(Token)) { turn.Bind("S1"); turn.Unconfirmed(); }
        started.Observe(new AgentTurnStarted(1, "S1", "T1")); // The unacknowledged input's turn, never acknowledged.
        await Delivered(started, "S1", "T2"); // Settles that input, so no admission names T1 any more.
        for (var number = 3; number <= 257; number++) await Delivered(started, "S" + number, "T" + number);

        Assert.Equal((0, 1, false), (Saved.Active.Count, Saved.Running, Saved.Blocked));
    }

    [Fact]
    public async Task A_journal_that_cannot_be_written_blocks_admission()
    {
        var started = await Started();
        journal.WriteFailure = new IOException("disk full");

        Assert.Equal("disk full", Assert.Throws<IOException>(started.Request).Message);
        journal.WriteFailure = null;
        Assert.Equal(Uncertain, (await Assert.ThrowsAsync<IOException>(() => started.Admit(Token))).Message);
    }

    public async ValueTask DisposeAsync()
    {
        if (refresh is not null) await refresh.DisposeAsync();
    }
}
