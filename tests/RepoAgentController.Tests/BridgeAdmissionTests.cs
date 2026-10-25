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

        Assert.Null(world.Record(1)!["pending"]);
        Assert.Equal(["C1", "C2"], world.Record(1)!["waiting"]!["inputs"]!.AsArray().Select(input => input!["change"]!["subject"]!["id"]!.GetValue<string>()));
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
}
