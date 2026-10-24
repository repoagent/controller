using Xunit;
using static RepoAgentController.Tests.ControllerHarness;

namespace RepoAgentController.Tests;

// Runs the real controller entrypoint against a controlled GitHub world and native peer (ControllerHarness).
[Collection("Managed checkout environment")]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")] // The App gh wrapper and native relay use Unix tools.
public sealed class ActiveSessionLimitTests
{
    [Fact]
    public async Task Concurrent_Discussions_never_exceed_the_configured_limit_and_finished_turns_admit_waiting_ones()
    {
        await using var controller = new Controller(discussions: 4);
        controller.Variables[Setting] = "2";
        await controller.Start("--repository-settings"); // All four Discussions arrive in one observation.

        Assert.Equal(2, controller.Native.TurnCount);
        var held = await controller.Waiting();
        Assert.Equal(2, held.Length);
        Assert.All(held, discussion => Assert.Contains(controller.Log,
            line => line.Contains($" input-waiting {Url(discussion)} ") && line.EndsWith(" active=2 limit=2")));

        // A completed turn releases its slot to one waiting Discussion; the other keeps waiting.
        await controller.Finish(await controller.Turn(1));
        var third = await controller.Turn(3);
        Assert.Contains(third.Discussion, held);
        await controller.Poll();
        Assert.Equal(3, controller.Native.TurnCount);
        Assert.Equal(held.Except([third.Discussion]), await controller.Waiting());

        // A failed turn releases its slot too, even though its session is not idle afterwards.
        await controller.Finish(await controller.Turn(2), failed: true);
        var fourth = await controller.Turn(4);
        Assert.Equal(held.Order(), new[] { third.Discussion, fourth.Discussion }.Order());
        Assert.Equal(2, controller.Native.MostActive);
        Assert.Empty(await controller.Waiting());
    }

    [Fact]
    public async Task A_changed_limit_applies_to_admission_without_interrupting_running_turns_and_idle_sessions_take_no_slot()
    {
        await using var controller = new Controller(discussions: 3);
        controller.Variables[Setting] = "1";
        await controller.Start("--repository-settings");
        var running = await controller.Turn(1);
        Assert.Equal(1, controller.Native.TurnCount);

        // Raising the limit admits waiting Discussions at the next settings refresh, while the first turn still runs.
        await controller.ChangeSetting("3");
        var raised = new[] { await controller.Turn(2), await controller.Turn(3) };
        Assert.Equal(3, controller.Native.MostActive);

        // Lowering it stops no running turn; new input waits until running turns fall below the new limit.
        await controller.ChangeSetting("1");
        await controller.Finish(running);
        controller.Comment(running.Discussion, "C1", "A follow-up while two other turns run.");
        await controller.Poll();
        Assert.Contains(controller.Log, line => line.Contains($" input-waiting {Url(running.Discussion)} ") && line.EndsWith(" active=2 limit=1"));
        await controller.Finish(raised[0]);
        await controller.Poll();
        Assert.Equal(3, controller.Native.TurnCount);
        Assert.Equal(new[] { running.Discussion }, await controller.Waiting());

        // The two idle stored sessions outnumber the limit, yet the follow-up runs once no turn is running.
        await controller.Finish(raised[1]);
        var followUp = await controller.Turn(4);
        Assert.Equal(running.Discussion, followUp.Discussion);
        Assert.Equal("C1", followUp.Envelope["changes"]![0]!["subject"]!["id"]!.GetValue<string>());
        // No turn was interrupted or steered: the native only ever received new turns.
        Assert.DoesNotContain(controller.Native.Methods, method => method.StartsWith("turn/", StringComparison.Ordinal) && method != "turn/start");
    }

    [Theory]
    [InlineData(null, null)] // Disabled, as in broker mode.
    [InlineData("--repository-settings", null)] // Confirmed absent.
    [InlineData("--repository-settings", 503)] // Unreadable at startup.
    public async Task Without_an_effective_setting_four_native_turns_run_and_further_Discussions_wait(string? option, int? status)
    {
        await using var controller = new Controller(discussions: 5);
        if (status is not null) controller.Statuses[Setting] = status.Value;
        await controller.Start(option is null ? [] : [option]);

        Assert.Equal(4, controller.Native.TurnCount);
        var held = Assert.Single(await controller.Waiting());
        Assert.Contains(controller.Log, line => line.Contains($" input-waiting {Url(held)} ") && line.EndsWith(" active=4 limit=4"));

        await controller.Finish(await controller.Turn(1));
        Assert.Equal(held, (await controller.Turn(5)).Discussion);
        Assert.Equal(4, controller.Native.MostActive);
        Assert.Empty(await controller.Waiting());
    }

    [Fact]
    public async Task A_failed_turn_leaves_its_session_ready_for_the_next_input_without_a_restart()
    {
        await using var controller = new Controller(discussions: 1);
        await controller.Start();
        var failed = await controller.Turn(1);
        // Pinned native order for a failed turn: the thread reports systemError, then turn/completed with status failed.
        await controller.Finish(failed, failed: true);

        controller.Comment(1, "C1", "Please try that again.");
        await controller.Poll();

        var next = await controller.Turn(2);
        Assert.Equal((failed.Thread, failed.Discussion), (next.Thread, next.Discussion));
        Assert.Equal("C1", Assert.Single(next.Envelope["changes"]!.AsArray())!["subject"]!["id"]!.GetValue<string>());
        Assert.Empty(await controller.Waiting());
    }

    [Fact]
    public async Task A_retried_delivery_holds_no_slot_while_it_backs_off_and_then_waits_for_a_free_one()
    {
        await using var controller = new Controller(discussions: 2);
        controller.Variables[Setting] = "1";
        controller.Native.Reject(1); // The native refuses the first Discussion's first turn.
        await controller.Start("--repository-settings");

        // The refused delivery backs off without a slot, so the second Discussion takes the only one.
        var second = await controller.Turn(1);
        Assert.Equal(2, second.Discussion);
        Assert.Contains(controller.Log, line => line.Contains($" delivery-retry {Url(1)} ") && line.Contains(" redelivery=true "));

        // Once its backoff ends the retry still respects the limit: it waits while the slot is taken.
        controller.Elapse(TimeSpan.FromSeconds(10));
        await controller.Poll();
        Assert.Equal(1, controller.Native.TurnCount);
        Assert.Equal(new[] { 1 }, await controller.Waiting());

        // The freed slot goes to the retry, still marked as a redelivery, and the limit was never exceeded.
        await controller.Finish(second);
        var retried = await controller.Turn(2);
        Assert.Equal(1, retried.Discussion);
        Assert.True(retried.Envelope["_bridge"]!["redelivery"]!.GetValue<bool>());
        Assert.Equal(1, controller.Native.MostActive);
        Assert.Empty(await controller.Waiting());
    }

    [Fact]
    public async Task Input_held_by_a_selection_error_holds_no_slot_and_then_waits_for_a_free_one()
    {
        await using var controller = new Controller(discussions: 2);
        controller.Variables[Setting] = "1";
        controller.Labels(1, "repoagent:gpt-unoffered:low"); // A model the Codex account does not offer.
        await controller.Start("--repository-settings");

        // The unusable selection holds the first Discussion's input without a turn or a slot; the second one runs.
        var second = await controller.Turn(1);
        Assert.Equal(2, second.Discussion);
        Assert.Contains(controller.Log, line => line.Contains($" selection-error {Url(1)}: "));
        Assert.Equal(new[] { 1 }, await controller.Waiting());

        // Removing the label makes the input dispatchable, but only once the limit has a free slot.
        controller.Labels(1);
        await controller.Poll();
        Assert.Equal(1, controller.Native.TurnCount);
        Assert.Equal(new[] { 1 }, await controller.Waiting());
        await controller.Finish(second);
        var first = await controller.Turn(2);
        Assert.Equal(1, first.Discussion);
        Assert.Equal(1, controller.Native.MostActive);
        Assert.Empty(await controller.Waiting());
    }
}
