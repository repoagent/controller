using Xunit;

namespace RepoAgentController.Tests;

public sealed class CapacityRecoveryTests
{
    [Fact]
    public async Task Unreadable_recovery_intent_stays_busy_and_cannot_be_bypassed_by_waiting_input()
    {
        await using var world = new BridgeWorld { Files = true };
        world.GitHub.Open(1, "@test-app[bot] work");
        await world.Begin();
        await world.Poll();
        var (session, turn) = world.Running(1);
        world.Runtime.Report(session, AgentSessionStatus.SystemError);
        world.Runtime.CapacityFailure(session, turn);
        await world.Dispatch();
        world.GitHub.Comment(1, "C1", "later input");
        await world.Poll();
        await world.End();
        var state = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(world.StatePath))!;
        state["discussions"]!["D1"]!["capacityRecovery"] = "unreadable recovery";
        File.WriteAllText(world.StatePath, state.ToJsonString());
        await world.Begin();
        await world.Dispatch();
        Assert.Single(world.Turns);
        Assert.Equal(1, world.Status()["work"]!["retrying"]!.GetValue<int>());
        Assert.Equal("unreadable recovery", world.Record(1)!["capacityRecovery"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_uncorrelated_completion_cannot_consume_the_accepted_input_needed_for_credential_redelivery()
    {
        await using var world = new BridgeWorld();
        world.GitHub.Open(1, "@test-app[bot] work");
        await world.Begin();
        await world.Poll();
        var (session, turn) = world.Running(1);
        world.Runtime.CapacityFailure("another-session", turn);
        await world.Dispatch();
        world.Finish(session, turn, failed: true, rejection: "unauthorized");
        await world.Dispatch();
        Assert.Equal([1], world.Waiting());
        Assert.True(Assert.Single(world.Saved(1)!.Waiting!.Inputs).Redelivery);
        Assert.Null(world.Saved(1)!.Recovery);
    }

    [Theory]
    [InlineData(AgentTurnStatus.Interrupted, true)]
    [InlineData(AgentTurnStatus.Completed, true)]
    [InlineData(AgentTurnStatus.Unknown, true)]
    [InlineData(AgentTurnStatus.Failed, false)]
    public async Task Only_a_correlated_terminal_failed_capacity_outcome_schedules_recovery(AgentTurnStatus status, bool capacity)
    {
        await using var world = new BridgeWorld();
        world.GitHub.Open(1, "@test-app[bot] work");
        await world.Begin();
        await world.Poll();
        var (session, turn) = world.Running(1);
        world.Runtime.CapacityFailure("another-session", turn);
        world.Runtime.CapacityFailure(session, "another-turn");
        await world.Dispatch();
        Assert.NotNull(world.Saved(1)!.Running);
        Assert.Null(world.Saved(1)!.Recovery);
        world.Runtime.Report(session, AgentSessionStatus.Idle);
        world.Runtime.Completed(session, turn, status, capacity: capacity);
        await world.Dispatch();
        world.Clock.Advance(TimeSpan.FromMinutes(10));
        await world.Dispatch();
        Assert.Single(world.Turns);
        Assert.Null(world.Saved(1)!.Recovery);
        Assert.Empty(world.Logged("capacity-recovery-scheduled"));
    }

    [Fact]
    public async Task Completion_before_acceptance_and_duplicate_events_schedule_each_failed_turn_once()
    {
        await using var world = new BridgeWorld { Admission = new RecordingCheckoutAdmission() };
        world.GitHub.Open(1, "@test-app[bot] work");
        await world.Begin();
        world.Answer(request =>
        {
            world.Runtime.CapacityFailure(request.SessionId, "T1");
            world.Runtime.Report(request.SessionId, AgentSessionStatus.SystemError);
            return new("T1", true);
        });
        await world.Poll();
        Assert.Equal(TimeSpan.FromSeconds(120), await world.Dispatch());
        world.Runtime.CapacityFailure("S1", "T1");
        world.Clock.Advance(TimeSpan.FromSeconds(120));
        world.Answer(request =>
        {
            world.Runtime.CapacityFailure(request.SessionId, "T2");
            return new("T2", true);
        });
        await world.Dispatch();
        Assert.Equal(TimeSpan.FromSeconds(120), await world.Dispatch());
        Assert.Equal(2, world.Saved(1)!.Recovery!.Attempts);
        Assert.Equal(2, world.Logged("capacity-recovery-scheduled").Count);
        Assert.Equal(2, world.Turns.Count);
        Assert.Equal(2, world.Admission.Calls.Count(call => call == "confirm"));
    }

    [Fact]
    public async Task Due_recovery_respects_active_capacity_and_credentials_and_graceful_stop_preserves_it()
    {
        await using var world = new BridgeWorld();
        world.Variables.Set(Bridge.ActiveSessionSetting, "1");
        world.GitHub.Open(1, "@test-app[bot] work");
        world.GitHub.Open(2, "@test-app[bot] other work");
        await world.Begin();
        await world.Poll();
        var (session, turn) = world.Running(1);
        world.Runtime.Report(session, AgentSessionStatus.SystemError);
        world.Runtime.CapacityFailure(session, turn);
        await world.Dispatch();
        world.Clock.Advance(TimeSpan.FromSeconds(120));
        await world.Dispatch();
        Assert.Equal(2, world.Turns.Count); // Other Discussion holds the sole slot.
        var other = world.Running(2);
        world.Finish(other.Session, other.Turn, failed: true, rejection: "unauthorized");
        await world.Dispatch();
        Assert.Equal(2, world.Turns.Count); // Credential rejection globally holds recovery too.
        Assert.NotNull(world.Saved(1)!.Recovery);
        await world.Bridge.StopAndDrain(TestContext.Current.CancellationToken);
        await world.Dispatch();
        Assert.Equal(2, world.Turns.Count);
        var due = world.Saved(1)!.Recovery!.After;
        await world.Begin();
        await world.Dispatch();
        Assert.Equal(3, world.Turns.Count);
        Assert.Equal(session, world.Turns[2].SessionId);
        Assert.Equal(due, world.Saved(1)!.Recovery!.After);
    }

    [Fact]
    public async Task Restarted_recovery_cannot_bypass_managed_checkout_uncertainty()
    {
        await using var world = new BridgeWorld();
        world.GitHub.Open(1, "@test-app[bot] work");
        await world.Begin();
        await world.Poll();
        var (session, turn) = world.Running(1);
        world.Runtime.Report(session, AgentSessionStatus.SystemError);
        world.Runtime.CapacityFailure(session, turn);
        await world.Dispatch();
        var binding = new CheckoutBinding("/workspace/repository", "0123456789abcdef0123456789abcdef", 345, "refs/heads/main");
        var journal = new InMemoryRefreshJournal { Saved = new(binding.Root, binding.Owner, binding.RepositoryId) { Blocked = true } };
        await using var refresh = new CheckoutRefresh(new ScriptedCheckout(), binding, journal, true, false, world.Clock, new RecordingLog());
        world.CheckoutRefresh = refresh;
        await world.Begin();
        world.Clock.Advance(TimeSpan.FromSeconds(120));
        await world.Dispatch();
        Assert.Single(world.Turns);
        Assert.True(refresh.AdmissionBlocked);
        Assert.Null(world.Saved(1)!.Pending);
        Assert.NotNull(world.Saved(1)!.Recovery);
    }

    [Fact]
    public async Task Cancellation_while_resuming_recovery_starts_no_turn_and_retains_the_scheduled_wait()
    {
        await using var world = new BridgeWorld { Files = true };
        world.GitHub.Open(1, "@test-app[bot] work");
        await world.Begin();
        await world.Poll();
        var (session, turn) = world.Running(1);
        world.Runtime.Report(session, AgentSessionStatus.SystemError);
        world.Runtime.CapacityFailure(session, turn);
        await world.Dispatch();
        var due = world.Saved(1)!.Recovery!.After;
        await world.Begin();
        world.Clock.Advance(TimeSpan.FromSeconds(120));
        world.Runtime.Resuming = _ => world.Interrupt();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => world.Dispatch());
        Assert.Single(world.Turns);
        Assert.Null(world.Saved(1)!.Pending);
        Assert.Equal(due, world.Saved(1)!.Recovery!.After);
        Assert.Null(world.Saved(1)!.Recovery!.Suspended);
    }

    [Fact]
    public async Task An_unrelated_recovery_failure_is_never_retried_but_explicit_later_input_can_finish_the_work()
    {
        await using var world = new BridgeWorld();
        world.GitHub.Open(1, "@test-app[bot] finish the work");
        await world.Begin();
        await world.Poll();
        var (session, turn) = world.Running(1);
        world.Runtime.Report(session, AgentSessionStatus.SystemError);
        world.Runtime.CapacityFailure(session, turn);
        await world.Dispatch();
        world.Clock.Advance(TimeSpan.FromSeconds(120));
        await world.Dispatch();
        world.Finish(1, failed: true); // Unrelated failure is outside automatic capacity recovery.
        await world.Dispatch();
        world.Clock.Advance(TimeSpan.FromHours(3));
        await world.Dispatch();
        Assert.Equal(2, world.Turns.Count);
        Assert.NotNull(world.Saved(1)!.Recovery!.Suspended);
        world.GitHub.Comment(1, "C1", "I fixed the unrelated problem; finish the remaining work.");
        await world.Poll();
        Assert.Equal(3, world.Turns.Count);
        Assert.Equal(["C1"], BridgeWorld.Subjects(world.Envelope(2)));
        world.Finish(1);
        await world.Dispatch();
        Assert.Null(world.Saved(1)!.Recovery);
    }

    [Fact]
    public async Task Recovery_survives_restart_holds_later_input_and_repeated_failures_without_holding_other_Discussions()
    {
        await using var world = new BridgeWorld { Files = true };
        world.Variables.Set(Bridge.ActiveSessionSetting, "1");
        world.GitHub.Open(1, "@test-app[bot] finish a change");
        world.GitHub.Open(2, "@test-app[bot] independent work");
        await world.Begin();
        await world.Poll();
        var selection = world.Turns[0].Model;
        var (session, turn) = world.Running(1);
        world.Runtime.Report(session, AgentSessionStatus.SystemError);
        world.Runtime.CapacityFailure(session, turn);
        await world.Dispatch();
        Assert.Equal(2, world.Turns.Count); // The released slot admits the other Discussion immediately.
        world.GitHub.Comment(1, "C1", "later ordered input");
        await world.Poll();
        world.Finish(2);
        await world.Dispatch();
        Assert.Equal("recovering", world.Status()["sessions"]![0]!["state"]!.GetValue<string>());
        Assert.Equal(1, world.Status()["work"]!["retrying"]!.GetValue<int>());
        Assert.Equal(0, world.Status()["work"]!["running"]!.GetValue<int>());
        Assert.Equal("model-capacity", world.Status()["sessions"]![0]!["reason"]!.GetValue<string>());

        world.Clock.Advance(TimeSpan.FromSeconds(45));
        await world.Begin();
        Assert.Equal(TimeSpan.FromSeconds(75), await world.Dispatch());
        Assert.Equal([1], world.Waiting());
        // Changed defaults and labels cannot change the retained exact selection.
        world.Variables.Set(ModelSelection.ReasoningSetting, "xhigh");
        await world.Refresh();
        world.Clock.Advance(TimeSpan.FromSeconds(75));
        await world.Dispatch();
        Assert.Equal(3, world.Turns.Count);
        Assert.Equal((session, selection), (world.Turns[2].SessionId, world.Turns[2].Model));
        var recoveryTurn = world.Running(1).Turn;
        world.Runtime.Report(session, AgentSessionStatus.SystemError);
        world.Runtime.CapacityFailure(session, recoveryTurn);
        world.Runtime.CapacityFailure(session, recoveryTurn); // Duplicate terminal notification schedules only once.
        Assert.Equal(TimeSpan.FromSeconds(120), await world.Dispatch());
        Assert.Equal(2, world.Status()["sessions"]![0]!["attempts"]!.GetValue<int>());
        world.Clock.Advance(TimeSpan.FromSeconds(120));
        await world.Dispatch();
        Assert.Equal(4, world.Turns.Count);
        world.Finish(1);
        await world.Dispatch();
        Assert.Equal(5, world.Turns.Count);
        Assert.Equal(["C1"], BridgeWorld.Subjects(world.Envelope(4)));
        Assert.Null(world.Record(1)!["capacityRecovery"]);
        Assert.Equal(0, world.Status()["work"]!["retrying"]!.GetValue<int>());
        Assert.Equal(2, world.Logged("capacity-recovery-scheduled").Count);
        Assert.Single(world.Logged("capacity-recovery-completed"));
        Assert.Equal(1, world.MostActive);
    }

    [Fact]
    public async Task Terminal_capacity_failure_waits_two_minutes_then_continues_the_same_session_and_selection()
    {
        await using var world = new BridgeWorld();
        world.GitHub.Open(1, "@test-app[bot] implement the requested work");
        await world.Begin();
        await world.Poll();
        var original = Assert.Single(world.Turns);
        var (session, turn) = world.Running(1);
        world.Runtime.Report(session, AgentSessionStatus.SystemError);
        world.Runtime.CapacityFailure(session, turn);

        Assert.Equal(TimeSpan.FromSeconds(120), await world.Dispatch());
        world.Clock.Advance(TimeSpan.FromSeconds(119));
        await world.Dispatch();
        Assert.Single(world.Turns);
        world.Clock.Advance(TimeSpan.FromSeconds(1));
        await world.Dispatch();

        Assert.Equal(2, world.Turns.Count);
        var continuation = world.Turns[1];
        Assert.Equal(original.SessionId, continuation.SessionId);
        Assert.Equal(original.Model, continuation.Model);
        Assert.Contains("inspect", continuation.Input, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("external effects", continuation.Input, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("finish", continuation.Input, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("implement the requested work", continuation.Input);
    }
}
