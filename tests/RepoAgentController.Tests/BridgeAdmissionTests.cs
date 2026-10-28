using System.Text.Json.Nodes;
using Xunit;

namespace RepoAgentController.Tests;

// When waiting input is admitted to an agent turn, in-process (BridgeWorld): the active-session limit, the session's own
// status, rejected Codex account credentials, and what wakes the dispatch loop. Turns run until the test ends them.
public sealed class BridgeAdmissionTests
{
    const string Setting = Bridge.ActiveSessionSetting;

    // Discussions 1 to count, each mentioning the App, listed in that order.
    static BridgeWorld Mentioned(int count)
    {
        var world = new BridgeWorld();
        for (var number = 1; number <= count; number++) world.GitHub.Open(number, $"@test-app[bot] please look at fixture {number}");
        return world;
    }

    static int Discussion(AgentTurnRequest turn) => System.Text.Json.Nodes.JsonNode.Parse(turn.Input)!["discussion"]!["number"]!.GetValue<int>();

    static void HeldAt(BridgeWorld world, int number, int active, int limit) => Assert.Contains(world.Logged("input-waiting"),
        line => line.StartsWith(BridgeWorld.Url(number) + " ", StringComparison.Ordinal) && line.EndsWith($" active={active} limit={limit}", StringComparison.Ordinal));

    [Fact]
    public async Task Concurrent_Discussions_never_exceed_the_configured_limit_and_finished_turns_admit_waiting_ones()
    {
        await using var world = Mentioned(3);
        world.Variables.Set(Setting, "2");
        await world.Begin();
        await world.Poll(); // Three Discussions arrive in one observation, and a fourth a minute later.
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        world.GitHub.Open(4, "@test-app[bot] please look at fixture 4");
        await world.Poll();

        Assert.Equal([1, 2], world.Turns.Select(Discussion));
        Assert.Equal([3, 4], world.Waiting());
        HeldAt(world, 3, 2, 2);
        HeldAt(world, 4, 2, 2);

        // A completed turn releases its slot to the Discussion waiting longest; the other keeps waiting.
        world.Finish(1);
        await world.Dispatch();
        await world.Poll();
        Assert.Equal([1, 2, 3], world.Turns.Select(Discussion));
        Assert.Equal([4], world.Waiting());

        // A failed turn releases its slot too, even though its session is not idle afterwards.
        world.Finish(2, failed: true);
        await world.Dispatch();
        Assert.Equal([1, 2, 3, 4], world.Turns.Select(Discussion));
        Assert.Equal(2, world.MostActive);
        Assert.Empty(world.Waiting());
    }

    [Fact]
    public async Task A_changed_limit_applies_to_admission_without_interrupting_running_turns_and_idle_sessions_take_no_slot()
    {
        await using var world = Mentioned(3);
        world.Variables.Set(Setting, "1");
        await world.Begin();
        await world.Poll();
        Assert.Single(world.Turns);

        // Raising the limit admits waiting Discussions at the next settings refresh, while the first turn still runs.
        world.Variables.Set(Setting, "3");
        await world.Refresh();
        await world.Dispatch();
        Assert.Equal([1, 2, 3], world.Turns.Select(Discussion));
        Assert.Equal(3, world.MostActive);

        // Lowering it ends no running turn; new input waits until running turns fall below the new limit.
        world.Variables.Set(Setting, "1");
        await world.Refresh();
        world.Finish(1);
        await world.Dispatch();
        world.GitHub.Comment(1, "C1", "A follow-up while two other turns run.");
        await world.Poll();
        HeldAt(world, 1, 2, 1);
        world.Finish(2);
        await world.Dispatch();
        Assert.Equal(3, world.Turns.Count);
        Assert.Equal([1], world.Waiting());

        // The two idle stored sessions outnumber the limit, yet the follow-up runs once no turn is running.
        world.Finish(3);
        await world.Dispatch();
        Assert.Equal(1, Discussion(world.Turns[3]));
        Assert.Equal(["C1"], BridgeWorld.Subjects(world.Envelope(3)));
    }

    [Theory]
    [InlineData(false, false)] // Disabled, as in broker mode.
    [InlineData(true, false)] // Confirmed absent.
    [InlineData(true, true)] // Unreadable at startup.
    public async Task Without_an_effective_setting_four_agent_turns_run_and_further_Discussions_wait(bool repositorySettings, bool unreadable)
    {
        await using var world = Mentioned(5);
        world.RepositorySettings = repositorySettings;
        if (unreadable) world.Variables.Fail(Setting, new RepositorySettingRejectedException(503));
        await world.Begin();
        await world.Poll();

        Assert.Equal(4, world.Turns.Count);
        Assert.Equal([5], world.Waiting());
        HeldAt(world, 5, 4, 4);

        world.Finish(1);
        await world.Dispatch();
        Assert.Equal(5, Discussion(world.Turns[4]));
        Assert.Equal(4, world.MostActive);
        Assert.Empty(world.Waiting());
    }

    [Theory]
    [InlineData(AgentTurnStatus.Completed, "completed")]
    [InlineData(AgentTurnStatus.Interrupted, "interrupted")]
    [InlineData(AgentTurnStatus.Failed, "failed")]
    [InlineData(AgentTurnStatus.Unknown, "")] // Reported without a status.
    [InlineData(AgentTurnStatus.Unknown, "cancelled")] // Pinned Codex never sends it; a later agent tool might.
    public async Task An_ended_turn_is_logged_with_the_status_its_runtime_reported(AgentTurnStatus status, string reported)
    {
        await using var world = Mentioned(1);
        await world.Begin();
        await world.Poll();
        world.Runtime.Report("S1", AgentSessionStatus.Idle);
        world.Runtime.Completed("S1", "T1", status, reported: reported);

        await world.Dispatch();

        Assert.Equal($"{BridgeWorld.Url(1)} session=S1 turn=T1 status={reported}", Assert.Single(world.Logged("turn-ended")));
    }

    [Fact]
    public async Task A_failed_turn_leaves_its_session_ready_for_the_next_input_without_a_restart()
    {
        await using var world = Mentioned(1);
        await world.Begin();
        await world.Poll();
        // Pinned Codex's order for a failed turn: the session reports systemError, then the turn completes as failed.
        world.Finish(1, failed: true);

        world.GitHub.Comment(1, "C1", "Please try that again.");
        world.GitHub.Comment(1, "C2", "And this as well.");
        await world.Poll();

        Assert.Equal(["S1", "S1"], world.Turns.Select(turn => turn.SessionId));
        Assert.Equal("continue", world.Envelope(1)["_bridge"]!["mode"]!.GetValue<string>());
        Assert.Equal(["C1", "C2"], BridgeWorld.Subjects(world.Envelope(1)));
        Assert.Empty(world.Waiting());
        await world.Deliver();
        Assert.True(world.GitHub.HasEyes("C1") && world.GitHub.HasEyes("C2"));
    }

    [Fact]
    public async Task A_retried_delivery_holds_no_slot_while_it_backs_off_and_then_waits_for_a_free_one()
    {
        await using var world = Mentioned(2);
        world.Variables.Set(Setting, "1");
        world.Answer(_ => throw new AgentRequestRejected("fixture rejection")); // The agent refuses the first Discussion's first turn.
        await world.Begin();
        await world.Poll();

        // The refused delivery backs off without a slot, so the second Discussion takes the only one.
        Assert.Equal([1, 2], world.Turns.Select(Discussion));
        Assert.Contains(world.Logged("delivery-retry"), line => line.StartsWith(BridgeWorld.Url(1) + " ", StringComparison.Ordinal) && line.Contains(" redelivery=true "));

        // Once its backoff ends the retry still respects the limit: it waits while the slot is taken.
        world.Clock.Advance(TimeSpan.FromSeconds(10));
        await world.Poll();
        await world.Dispatch();
        Assert.Equal(2, world.Turns.Count);
        Assert.Equal([1], world.Waiting());

        // The freed slot goes to the retry, still marked as a redelivery, and the limit was never exceeded.
        world.Finish(2);
        await world.Dispatch();
        Assert.Equal(1, Discussion(world.Turns[2]));
        Assert.True(world.Envelope(2)["_bridge"]!["redelivery"]!.GetValue<bool>());
        Assert.Equal(1, world.MostActive);
        Assert.Empty(world.Waiting());
    }

    [Fact]
    public async Task Input_held_by_a_selection_error_holds_no_slot_and_then_waits_for_a_free_one()
    {
        await using var world = Mentioned(2);
        world.Variables.Set(Setting, "1");
        world.GitHub.Labels(1, "repoagent:gpt-unoffered:low"); // A model the Codex account does not offer.
        await world.Begin();
        await world.Poll();

        // The unusable selection holds the first Discussion's input without a turn or a slot; the second one runs.
        Assert.Equal([2], world.Turns.Select(Discussion));
        Assert.Contains(world.Logged("selection-error"), line => line.StartsWith(BridgeWorld.Url(1) + ": ", StringComparison.Ordinal));
        Assert.Equal([1], world.Waiting());

        // Removing the label makes the input dispatchable, but only once the limit has a free slot.
        world.GitHub.Labels(1);
        await world.Poll();
        Assert.Single(world.Turns);
        Assert.Equal([1], world.Waiting());
        world.Finish(2);
        await world.Dispatch();
        Assert.Equal([2, 1], world.Turns.Select(Discussion));
        Assert.Equal(1, world.MostActive);
        Assert.Empty(world.Waiting());
    }

    [Theory]
    [InlineData(AgentSessionStatus.Active)]
    [InlineData(AgentSessionStatus.Unknown)]
    public async Task Unknown_and_active_status_hold_input_in_order_across_a_restart_until_the_session_reports_idle(AgentSessionStatus resumed)
    {
        await using var world = Mentioned(1);
        await world.Begin();
        await world.Poll();
        world.Runtime.Report("S1", AgentSessionStatus.Unknown); // A status this controller does not recognize.
        world.Runtime.Completed("S1", "T1");
        world.GitHub.Comment(1, "C1", "first after the unknown status");
        world.GitHub.Comment(1, "C2", "second after the unknown status");
        await world.Poll();
        await world.Dispatch();
        Assert.Contains(world.Logged("input-waiting"), line => line.Contains(" session=S1 status=Unknown "));
        await world.End();

        Assert.Null(world.Saved(1)!.Pending);
        Assert.Equal(["C1", "C2"], world.Saved(1)!.Waiting!.Inputs.Select(input => JsonNode.Parse(input.ChangeJson)!["subject"]!["id"]!.GetValue<string>()));
        Assert.Equal((2, 2), (world.Status()["work"]!["waiting"]!.GetValue<int>(), world.Status()["sessions"]![0]!["waiting"]!.GetValue<int>()));

        // A restarted runtime has not loaded S1: the bridge loads it and uses only the status it reports.
        world.PrepareRuntime = runtime => runtime.Resumes = resumed;
        await world.Begin();
        for (var pass = 0; pass < 2; pass++)
        {
            await world.Poll();
            await world.Dispatch();
        }
        Assert.Single(world.Turns);
        world.Runtime.Report("S1", AgentSessionStatus.Idle);
        await world.Dispatch();
        Assert.Equal(["C1", "C2"], BridgeWorld.Subjects(world.Envelope(1)));
        Assert.Equal("continue", world.Envelope(1)["_bridge"]!["mode"]!.GetValue<string>());
        Assert.False(world.GitHub.HasEyes("C1")); // EYES follow acceptance, through the outbox.
        await world.Deliver();
        Assert.True(world.GitHub.HasEyes("C1") && world.GitHub.HasEyes("C2"));
    }

    // The dashboard renders this status (ControllerDashboardPublicationTests); here the bridge's records produce it.
    [Fact]
    public async Task Status_counts_a_running_turn_against_the_limit_with_input_waiting_for_that_turn_or_for_a_free_slot()
    {
        await using var world = Mentioned(1);
        world.Variables.Set(Setting, "1");
        await world.Begin();
        await world.Poll();
        world.GitHub.Comment(1, "C1", "One more detail for it."); // Waits in the controller instead of steering the running turn.
        world.GitHub.Open(2, "@test-app[bot] please look at this as well.");
        await world.Poll();
        await world.Deliver();

        var status = world.Status();
        Assert.Equal((1, 1, 2, 1), (status["work"]!["running"]!.GetValue<int>(), status["work"]!["limit"]!.GetValue<int>(),
            status["work"]!["waiting"]!.GetValue<int>(), status["work"]!["sessions"]!.GetValue<int>()));
        var sessions = status["sessions"]!.AsArray();
        Assert.Equal((BridgeWorld.Url(1), "running", 1), (sessions[0]!["discussion"]!.GetValue<string>(), sessions[0]!["state"]!.GetValue<string>(), sessions[0]!["waiting"]!.GetValue<int>()));
        Assert.NotNull(sessions[0]!["since"]);
        Assert.Equal((BridgeWorld.Url(2), "idle", 1), (sessions[1]!["discussion"]!.GetValue<string>(), sessions[1]!["state"]!.GetValue<string>(), sessions[1]!["waiting"]!.GetValue<int>()));
        Assert.Single(world.Turns);
        Assert.False(world.GitHub.HasEyes("C1"));
    }

    // DispatchWhenIdle subscribes and starts waiting before it returns, and only a wake ends its wait. A step that follows one
    // wake can pass on a wake left over from earlier, so the raised limit runs in a lifetime whose loop has had none.
    [Fact]
    public async Task The_dispatch_loop_wakes_for_a_due_retry_a_scheduled_retry_a_finished_turn_and_a_raised_limit()
    {
        await using var world = Mentioned(1);
        world.Answer(_ => throw new AgentRequestRejected("fixture rejection"));
        Assert.True(await world.RunOnce()); // Leaves a retry scheduled five seconds on, and nothing else to wake the next loop.

        await world.Begin();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(world.Token);
        var loop = world.Bridge.DispatchWhenIdle(stop.Token, world.Token);
        await world.Clock.Armed();
        world.Clock.Advance(TimeSpan.FromSeconds(5));
        await world.WhenLogged("forwarded", 1);

        // A delivery that fails during an observation wakes the waiting loop, which then waits for that retry.
        world.Answer(_ => throw new AgentRequestRejected("fixture rejection"));
        world.GitHub.Open(2, "@test-app[bot] retried by the loop.");
        await world.Poll();
        await world.Clock.Armed();
        world.Clock.Advance(TimeSpan.FromSeconds(5));
        await world.WhenLogged("forwarded", 2);
        Assert.Equal(2, Discussion(world.Turns[^1]));

        world.GitHub.Comment(1, "C1", "Waits for the running turn.");
        await world.Poll();
        world.Finish(1);
        await world.WhenLogged("forwarded", 3);
        Assert.Equal(["C1"], BridgeWorld.Subjects(world.Envelope(world.Turns.Count - 1)));
        await stop.CancelAsync();
        await loop;

        // The next lifetime's turns end with its start. Input set up before its loop starts takes the only slot or waits for it.
        world.Variables.Set(Setting, "1");
        await world.Begin();
        Assert.Equal(0, world.Status()["work"]!["running"]!.GetValue<int>());
        world.GitHub.Comment(1, "C2", "Takes the only slot.");
        world.GitHub.Comment(2, "C3", "Waits for a free slot.");
        await world.Poll();
        Assert.Equal([2], world.Waiting());
        using var next = CancellationTokenSource.CreateLinkedTokenSource(world.Token);
        loop = world.Bridge.DispatchWhenIdle(next.Token, world.Token);
        world.Variables.Set(Setting, "2");
        await world.Refresh();
        await world.WhenLogged("forwarded", 5);
        Assert.Equal(["C3"], BridgeWorld.Subjects(world.Envelope(world.Turns.Count - 1)));
        await next.CancelAsync();
        await loop;
    }

    [Fact]
    public async Task Input_after_a_credential_rejection_waits_and_replacement_credentials_redeliver_the_rejected_input_first_in_the_same_session()
    {
        await using var world = Mentioned(1);
        await world.Begin();
        await world.Poll();
        world.Finish(1);
        world.GitHub.Comment(1, "C1", "first comment after expiry");
        await world.Poll();
        world.GitHub.Comment(1, "C2", "second comment while that turn runs"); // Waits behind the turn, not yet known rejected.
        await world.Poll();
        world.Finish("S1", "T2", failed: true, rejection: "unexpected status 401 Unauthorized");
        await world.Dispatch(); // Settles the rejected turn: its input goes back first in line, ahead of C2, and is held.
        Assert.Contains(world.Logged("input-requeued"), line => line.Contains(" turn=T2 inputs=1 sources=C1:"));
        Assert.Equal("failed", world.Status()["codexAuthentication"]!["state"]!.GetValue<string>());
        Assert.Contains(world.Status()["failures"]!.AsArray(), failure => failure!["kind"]!.GetValue<string>() == "codex-auth-failed");

        // Later input is held instead of being accepted into a turn that cannot authenticate: no turn, no EYES.
        world.GitHub.Comment(1, "C3", "third comment while credentials are rejected");
        await world.Poll();
        await world.Dispatch();
        await world.Deliver();
        Assert.Equal(2, world.Turns.Count);
        Assert.StartsWith(BridgeWorld.Url(1) + " waiting=2: Codex account credentials were rejected", Assert.Single(world.Logged("input-held")));
        Assert.False(world.GitHub.HasEyes("C2") || world.GitHub.HasEyes("C3"));

        // Replacement credentials apply at an idle restart, between dispatches. The same session resumes and receives the
        // rejected turn's input first, as a redelivery, then the input that waited meanwhile.
        var prepared = false;
        Assert.True(await world.Bridge.ExcludingDispatch(() => world.Sessions.RestartWhenIdle(() => prepared = true, world.Token), world.Token));
        Assert.True(prepared);
        await world.Dispatch();
        Assert.Equal(["S1"], world.Runtime.Resumed);
        Assert.True(world.Envelope(2)["_bridge"]!["redelivery"]!.GetValue<bool>());
        Assert.Equal(["C1"], BridgeWorld.Subjects(world.Envelope(2)));
        world.Finish(1);
        await world.Dispatch();
        Assert.Null(world.Envelope(3)["_bridge"]!["redelivery"]);
        Assert.Equal(["C2", "C3"], BridgeWorld.Subjects(world.Envelope(3)));
        Assert.Equal(["S1", "S1", "S1", "S1"], world.Turns.Select(turn => turn.SessionId));
        Assert.Equal("healthy", world.Status()["codexAuthentication"]!["state"]!.GetValue<string>());
        await world.Deliver();
        Assert.True(world.GitHub.HasEyes("C2") && world.GitHub.HasEyes("C3"));
    }

    [Fact]
    public async Task A_bootstrap_whose_credentials_were_rejected_is_redelivered_as_the_bootstrap_with_its_context()
    {
        await using var world = Mentioned(1);
        await world.Begin();
        await world.Poll();
        world.Finish("S1", "T1", failed: true, rejection: "unexpected status 401 Unauthorized");
        await world.Dispatch();

        Assert.True(await world.Bridge.ExcludingDispatch(() => world.Sessions.RestartWhenIdle(() => { }, world.Token), world.Token));
        await world.Dispatch();

        var redelivered = world.Envelope(1);
        Assert.True(redelivered["_bridge"]!["redelivery"]!.GetValue<bool>());
        Assert.Equal("bootstrap", redelivered["_bridge"]!["mode"]!.GetValue<string>());
        Assert.Equal(world.Envelope(0)["context"]!.ToJsonString(), redelivered["context"]!.ToJsonString());
    }

    [Fact]
    public async Task A_credential_replacement_waits_for_the_running_turn_and_later_input_reaches_the_restarted_runtime()
    {
        await using var world = Mentioned(1);
        await world.Begin();
        await world.Poll();
        Task<bool> Replace() => world.Bridge.ExcludingDispatch(() => world.Sessions.RestartWhenIdle(() => { }, world.Token), world.Token);

        Assert.False(await Replace()); // The bootstrap turn still runs on the credentials it started with.
        Assert.Equal(1, world.Runtime.Epoch);
        world.Finish(1);
        Assert.True(await Replace());
        Assert.Equal(2, world.Runtime.Epoch);

        world.GitHub.Comment(1, "C1", "first comment after replacement");
        await world.Poll();
        Assert.Equal(2, world.Turns.Count);
        Assert.Equal(["S1"], world.Runtime.Resumed); // Loaded again into the restarted runtime before its turn.
    }

    // With a managed checkout, each delivery is admitted before it reaches a session and resolved once by what the native did:
    // confirmed with its acknowledged turn, abandoned when no turn can hold its input, or left unconfirmed when one may.
    [Fact]
    public async Task An_acknowledged_delivery_binds_its_session_and_confirms_its_turn()
    {
        await using var world = Mentioned(1);
        world.Admission = new();
        await world.Begin();
        Assert.True(await world.Poll());
        world.Finish(1);
        world.GitHub.Comment(1, "C1", "More, please.");
        Assert.True(await world.Poll());

        string[] delivery(string turn) => ["admit", "bind S1", $"accepted S1 {turn}", "confirm", "dispose"];
        Assert.Equal([.. delivery("T1"), .. delivery("T2")], world.Admission.Calls);
    }

    [Theory]
    [InlineData(true, "abandon")] // The native refused the input, so no turn holds it.
    [InlineData(false, "unconfirmed")] // No acknowledgement: the native may hold the input.
    public async Task A_delivery_the_native_refused_is_abandoned_and_one_it_may_hold_stays_unconfirmed(bool refused, string resolution)
    {
        await using var world = Mentioned(1);
        world.Admission = new();
        world.Answer(_ => throw (refused ? new AgentRequestRejected("refused") : new TimeoutException("no acknowledgement")));
        await world.Begin();
        await world.Poll();

        Assert.Equal(["admit", "bind S1", resolution, "dispose"], world.Admission.Calls);
        Assert.Single(world.Logged("delivery-retry"));
    }

    // The reader can deliver a turn's completion before the response that acknowledges it. The checkout stays excluded until
    // the delivery is acknowledged, saved and confirmed; test_checkout_refresh.py guards the same order out of process.
    [Fact]
    public async Task A_turn_that_completes_before_its_acknowledgement_keeps_the_checkout_excluded_until_the_delivery_is_confirmed()
    {
        await using var world = Mentioned(1);
        var journal = new InMemoryRefreshJournal();
        await using var refresh = new CheckoutRefresh(new ScriptedCheckout(),
            new("/workspace/repository", "0123456789abcdef0123456789abcdef", 345, "refs/heads/main"), journal, false, false, world.Clock, new RecordingLog());
        world.CheckoutRefresh = refresh;
        RefreshJournal? early = null;
        world.Answer(_ =>
        {
            world.Runtime.Completed("S1", "T1");
            early = journal.Saved;
            return new("T1", false);
        });
        await world.Begin();
        Assert.True(await world.Poll());

        var admitted = Assert.Single(early!.Active).Value;
        Assert.Equal(("S1", "", false, false), (admitted.Thread, admitted.Turn, admitted.Confirmed, early.Blocked));
        Assert.Equal((0, 0, false), (journal.Saved!.Active.Count, journal.Saved.Running, journal.Saved.Blocked));
        Assert.True(await refresh.CommitAtIdle(_ => Task.CompletedTask, world.Token));
    }

    [Fact]
    public async Task A_delivery_that_offers_no_input_is_abandoned()
    {
        // The session cannot start.
        await using (var world = Mentioned(1))
        {
            world.Admission = new();
            world.PrepareRuntime = runtime => runtime.SessionStartFailure = new IOException("no session");
            await world.Begin();
            await world.Poll();
            Assert.Equal(["admit", "abandon", "dispose"], world.Admission.Calls);
        }
        // The selection is unusable, so the input waits.
        await using (var world = Mentioned(1))
        {
            world.Admission = new();
            world.GitHub.Labels(1, "repoagent:luna6");
            await world.Begin();
            await world.Poll();
            Assert.Equal(["admit", "abandon", "dispose"], world.Admission.Calls);
        }
        // The model catalogue cannot be read, so no selection is made.
        await using (var world = Mentioned(1))
        {
            world.Admission = new();
            world.PrepareRuntime = runtime => runtime.ModelsFailure = new IOException("no catalogue");
            await world.Begin();
            await world.Poll();
            Assert.Equal(["admit", "abandon", "dispose"], world.Admission.Calls);
        }
        // A restarted runtime reports the session active once it loads it.
        await using (var world = Mentioned(1))
        {
            await world.Begin();
            await world.Poll();
            world.Finish(1);
            world.GitHub.Comment(1, "C1", "More, please.");
            world.Admission = new();
            world.PrepareRuntime = runtime => runtime.Resumes = AgentSessionStatus.Active;
            await world.Begin();
            await world.Poll();
            Assert.Equal(["admit", "bind S1", "abandon", "dispose"], world.Admission.Calls);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_waits_for_entered_acknowledgement_and_settles_correlated_completion(bool completesBeforeAcknowledgement)
    {
        var clock = new SteppedClock(BridgeWorld.Start);
        var github = new InMemoryDiscussions(clock);
        github.Open(1, "@test-app[bot] complete this accepted work");
        var saved = new InMemoryDiscussionStore();
        var state = new InMemoryControllerState();
        var log = new RecordingLog();
        var runtime = new HeldAcknowledgement();
        var sessions = new AgentSessions(runtime, log);
        var settings = new RepositorySettings(RepositorySettingsCatalogue.Settings, null, state, clock, log);
        await using var outbox = new GitHubOutbox(BridgeWorld.Repository, github, InMemoryDiscussions.App, state, clock, log);
        var bridge = new Bridge(BridgeWorld.Repository, github, InMemoryDiscussions.App, sessions,
            new StateStore(saved, BridgeWorld.Repository), outbox, [], settings, new AgentInputJson(), log, clock);
        using var activity = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var observing = bridge.Poll(activity.Token);
        await runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var drain = bridge.StopAndDrain(abort.Token);
        try
        {
            Assert.False(drain.IsCompleted);
            Assert.False(runtime.Activity.IsCancellationRequested);
            Assert.NotNull(saved.Saved!.Discussions["D1"].Pending);
            if (completesBeforeAcknowledgement) runtime.Inner.Completed("S1", "T1");
            runtime.Acknowledge.TrySetResult();
            Assert.True(await observing);
            if (!completesBeforeAcknowledgement)
            {
                // No completion is available yet: this public exclusion only lets the queued stop enter behind admission.
                await bridge.ExcludingDispatch(() => Task.FromResult(true), activity.Token)
                    .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
            Assert.False(runtime.Activity.IsCancellationRequested);
            Assert.Null(saved.Saved!.Discussions["D1"].Pending);
            if (!completesBeforeAcknowledgement)
            {
                Assert.False(drain.IsCompleted, "Native acknowledgement is not correlated turn completion.");
                Assert.NotNull(saved.Saved.Discussions["D1"].Running);
                runtime.Inner.Report("S1", AgentSessionStatus.Idle);
                Assert.False(drain.IsCompleted, "An early native idle report cannot finish accepted work.");
                runtime.Inner.Completed("S1", "T1");
            }
            await drain.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Null(saved.Saved!.Discussions["D1"].Pending);
            Assert.Null(saved.Saved.Discussions["D1"].Running);
            Assert.Single(runtime.Inner.Requests);
            Assert.Contains(log.Entries, entry => entry.Kind == "turn-ended" && entry.Message.EndsWith("status=completed", StringComparison.Ordinal));
        }
        finally
        {
            runtime.Acknowledge.TrySetResult();
            await abort.CancelAsync();
            await observing;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_never_admits_queued_or_new_work_through_either_dispatch_path(bool observeQueued)
    {
        await using var world = Mentioned(2);
        world.Variables.Set(Setting, "1");
        await world.Begin();
        await world.Poll();
        Assert.Single(world.Turns);
        Assert.Equal([2], world.Waiting());
        var drain = world.Bridge.StopAndDrain(world.Token);
        world.Finish(1);
        await drain.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        if (observeQueued) Assert.True(await world.Observe(2));
        else await world.Dispatch();
        world.GitHub.Open(3, "@test-app[bot] retain this for the next lifetime");
        Assert.True(await world.Observe(3));
        await world.Dispatch();

        Assert.Single(world.Turns);
        Assert.Contains(2, world.Waiting()); // Already-checkpointed input survives; retaining a new post-fence observation is optional.
        Assert.Equal(["D1"], world.WithSessions);
        Assert.Null(world.Saved(1)!.Running);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_retains_an_accepted_turn_after_early_idle_even_when_its_Discussion_is_forgotten(bool forgotten)
    {
        await using var world = Mentioned(1);
        await world.Begin();
        await world.Poll();
        var (session, turn) = world.Running(1);
        if (forgotten)
        {
            world.GitHub.Remove(1);
            Assert.True(await world.Observe(1));
            Assert.Null(world.Saved(1));
        }
        world.Runtime.Report(session, AgentSessionStatus.Idle);
        Assert.Equal(1, world.Sessions.ActiveTurns);
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var drain = world.Bridge.StopAndDrain(abort.Token);
        try
        {
            Assert.False(drain.IsCompleted, "Only a correlated end can retire the accepted turn, even without its Discussion record.");
            world.Runtime.Completed(session, turn);
            await drain.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(0, world.Sessions.ActiveTurns);
            if (!forgotten) Assert.Null(world.Saved(1)!.Running);
        }
        finally { await abort.CancelAsync(); }
    }

    [Fact]
    public async Task Shutdown_waits_fail_closed_for_native_active_work_without_a_known_accepted_turn()
    {
        await using var world = new BridgeWorld();
        await world.Begin();
        world.Runtime.Report("native-active", AgentSessionStatus.Active);
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var drain = world.Bridge.StopAndDrain(abort.Token);
        try
        {
            Assert.False(drain.IsCompleted);
            world.Runtime.Report("native-active", AgentSessionStatus.Idle);
            await drain.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Empty(world.Turns);
        }
        finally { await abort.CancelAsync(); }
    }

    [Fact]
    public async Task Shutdown_of_an_idle_bridge_is_immediate_and_repeated_stop_never_reopens_admission()
    {
        await using var world = new BridgeWorld();
        await world.Begin();
        await world.Bridge.StopAndDrain(world.Token).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await world.Bridge.StopAndDrain(world.Token).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        world.GitHub.Open(1, "@test-app[bot] wait for a later controller lifetime");
        Assert.True(await world.Observe(1));
        await world.Dispatch();

        Assert.Empty(world.Turns);
        Assert.Empty(world.WithSessions);
    }

    [Fact]
    public async Task Shutdown_abort_keeps_admission_fenced_and_does_not_forge_a_turn_completion()
    {
        await using var world = Mentioned(2);
        world.Variables.Set(Setting, "1");
        await world.Begin();
        await world.Poll();
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var drain = world.Bridge.StopAndDrain(abort.Token);
        await abort.CancelAsync();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.NotNull(world.Saved(1)!.Running);
            Assert.Empty(world.Logged("turn-ended"));
            Assert.Equal([2], world.Waiting());
        }
        finally
        {
            world.Finish(1);
        }
        await world.Observe(2);
        await world.Dispatch();
        Assert.Single(world.Turns);
        Assert.Equal([2], world.Waiting());
    }

    [Fact]
    public async Task Shutdown_a_precanceled_repeat_cannot_report_clean_completion_or_undo_the_idle_fence()
    {
        await using var world = new BridgeWorld();
        await world.Begin();
        await world.Bridge.StopAndDrain(world.Token);
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await abort.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => world.Bridge.StopAndDrain(abort.Token));
        world.GitHub.Open(1, "@test-app[bot] remain durably waiting");
        await world.Observe(1);
        await world.Dispatch();
        Assert.Empty(world.Turns);
        Assert.Empty(world.WithSessions);
    }

    [Fact]
    public async Task Shutdown_settles_a_real_native_disconnect_as_lost_without_inventing_success()
    {
        await using var world = Mentioned(1);
        await world.Begin();
        await world.Poll();
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var drain = world.Bridge.StopAndDrain(abort.Token);
        try
        {
            world.Runtime.Disconnect();
            await drain.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Null(world.Saved(1)!.Running);
            Assert.EndsWith("status=lost", Assert.Single(world.Logged("turn-ended")), StringComparison.Ordinal);
        }
        finally { await abort.CancelAsync(); }
    }

    [Fact]
    public async Task Shutdown_settles_a_completion_published_while_an_earlier_outcome_is_saved()
    {
        var clock = new SteppedClock(BridgeWorld.Start);
        var github = new InMemoryDiscussions(clock);
        github.Open(1, "@test-app[bot] first accepted turn");
        github.Open(2, "@test-app[bot] second accepted turn");
        var durable = new InMemoryDiscussionStore();
        var persisting = new AfterDiscussionSave(durable);
        var state = new InMemoryControllerState();
        var log = new RecordingLog();
        var runtime = new FakeAgentRuntime();
        var sessions = new AgentSessions(runtime, log);
        var settings = new RepositorySettings(RepositorySettingsCatalogue.Settings, null, state, clock, log);
        await using var outbox = new GitHubOutbox(BridgeWorld.Repository, github, InMemoryDiscussions.App, state, clock, log);
        var bridge = new Bridge(BridgeWorld.Repository, github, InMemoryDiscussions.App, sessions,
            new StateStore(persisting, BridgeWorld.Repository), outbox, [], settings, new AgentInputJson(), log, clock);
        Assert.True(await bridge.Poll(TestContext.Current.CancellationToken));
        Assert.Equal(2, sessions.ActiveTurns);
        var first = durable.Saved!.Discussions["D1"];
        var second = durable.Saved.Discussions["D2"];
        runtime.Report(first.SessionId!, AgentSessionStatus.Idle);
        runtime.Completed(first.SessionId!, first.Running!.Turn);
        var completedDuringSave = false;
        persisting.AfterWrite = () =>
        {
            // The port first copies the write: S1 is settled while its atomic snapshot still contains active S2.
            Assert.Null(durable.Saved!.Discussions["D1"].Running);
            Assert.NotNull(durable.Saved.Discussions["D2"].Running);
            completedDuringSave = true;
            runtime.Report(second.SessionId!, AgentSessionStatus.Idle);
            runtime.Completed(second.SessionId!, second.Running!.Turn);
        };

        await bridge.StopAndDrain(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(completedDuringSave);
        Assert.Null(durable.Saved!.Discussions["D1"].Running);
        Assert.Null(durable.Saved.Discussions["D2"].Pending);
        Assert.Null(durable.Saved.Discussions["D2"].Running);
        Assert.Equal(2, log.Entries.Count(entry => entry.Kind == "turn-ended" && entry.Message.EndsWith("status=completed", StringComparison.Ordinal)));
        Assert.Equal(2, runtime.Requests.Count);
    }

    // A faithful storage-port decorator: the existing fake owns copy-on-write durability; the callback follows its save.
    sealed class AfterDiscussionSave(IDiscussionStore inner) : IDiscussionStore
    {
        public Action? AfterWrite { get; set; }
        public DiscussionState? ReadDiscussions() => inner.ReadDiscussions();
        public void WriteDiscussions(DiscussionState state)
        {
            inner.WriteDiscussions(state);
            var after = AfterWrite;
            AfterWrite = null;
            after?.Invoke();
        }
    }

    // Delays only the project-owned runtime port's acknowledgement; all session and delivery decisions remain real.
    sealed class HeldAcknowledgement : IAgentRuntime
    {
        public FakeAgentRuntime Inner { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Acknowledge { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Activity { get; private set; }
        public event Action<AgentRuntimeEvent>? Changed { add => Inner.Changed += value; remove => Inner.Changed -= value; }
        public Task Restart(Action<int> whileStopped, CancellationToken ct) => Inner.Restart(whileStopped, ct);
        public Task<(string SessionId, AgentSessionStatus Status)> StartSession(CancellationToken ct) => Inner.StartSession(ct);
        public Task<AgentSessionStatus> ResumeSession(string sessionId, CancellationToken ct) => Inner.ResumeSession(sessionId, ct);
        public Task<(string? Model, string? Effort)> ConfiguredModel(CancellationToken ct) => Inner.ConfiguredModel(ct);
        public Task<IReadOnlyList<NativeModel>> Models(CancellationToken ct) => Inner.Models(ct);
        public async Task<AgentTurnAccepted> StartTurn(AgentTurnRequest request, CancellationToken ct)
        {
            Activity = ct;
            Entered.TrySetResult();
            await Acknowledge.Task.WaitAsync(ct);
            return await Inner.StartTurn(request, ct);
        }
    }

}
