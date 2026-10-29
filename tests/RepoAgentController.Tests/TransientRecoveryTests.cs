using Xunit;

namespace RepoAgentController.Tests;

public sealed class TransientRecoveryTests
{
    [Theory]
    [InlineData(AgentTurnStatus.Interrupted, null)]
    [InlineData(AgentTurnStatus.Completed, null)]
    [InlineData(AgentTurnStatus.Unknown, null)]
    [InlineData(AgentTurnStatus.Failed, "unauthorized")]
    public async Task Failure_facts_do_not_override_terminal_status_credentials_or_correlation(AgentTurnStatus status, string? rejection)
    {
        await using var world = new BridgeWorld();
        world.GitHub.Open(1, "@test-app[bot] work");
        await world.Begin();
        await world.Poll();
        var (session, turn) = world.Running(1);
        var failure = new AgentFailure(AgentFailureKind.HttpConnection, 503);
        world.Runtime.TransientFailure("another-session", turn, failure);
        world.Runtime.TransientFailure(session, "another-turn", failure);
        world.Runtime.Completed(session, turn, AgentTurnStatus.Failed, epoch: 0, failure: failure);
        await world.Dispatch();
        Assert.NotNull(world.Saved(1)!.Running);
        Assert.Null(world.Saved(1)!.Recovery);
        world.Runtime.Report(session, AgentSessionStatus.Idle);
        world.Runtime.Completed(session, turn, status, rejection, failure: failure);
        await world.Dispatch();
        world.Clock.Advance(TimeSpan.FromMinutes(10));
        await world.Dispatch();
        Assert.Single(world.Turns);
        Assert.Null(world.Saved(1)!.Recovery);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"kind\":\"FutureFailure\",\"httpStatus\":503}")]
    [InlineData("{\"kind\":\"HttpConnection\",\"httpStatus\":429}")]
    [InlineData("{\"kind\":\"StreamConnection\"}")]
    [InlineData("{\"kind\":\"HttpConnection\",\"httpStatus\":\"503\"}")]
    public async Task Unreadable_or_unconfirmed_saved_failure_stays_busy_without_admitting_later_input(string damaged)
    {
        await using var world = new BridgeWorld { Files = true };
        world.GitHub.Open(1, "@test-app[bot] work");
        await world.Begin();
        await world.Poll();
        var (session, turn) = world.Running(1);
        world.Runtime.Report(session, AgentSessionStatus.SystemError);
        world.Runtime.TransientFailure(session, turn, new(AgentFailureKind.HttpConnection, 503));
        await world.Dispatch();
        world.GitHub.Comment(1, "C1", "later input");
        await world.Poll();
        await world.End();
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(world.StatePath))!;
        document["discussions"]!["D1"]!["capacityRecovery"]!["failure"] = System.Text.Json.Nodes.JsonNode.Parse(damaged);
        File.WriteAllText(world.StatePath, document.ToJsonString());
        await world.Begin();
        world.Clock.Advance(TimeSpan.FromMinutes(10));
        await world.Dispatch();
        Assert.Single(world.Turns);
        Assert.Equal(1, world.Status()["work"]!["retrying"]!.GetValue<int>());
    }

    [Fact]
    public async Task Existing_capacity_records_without_failure_details_keep_their_original_due_time_and_reason()
    {
        await using var world = new BridgeWorld { Files = true };
        world.GitHub.Open(1, "@test-app[bot] work");
        await world.Begin();
        await world.Poll();
        var (session, turn) = world.Running(1);
        world.Runtime.Report(session, AgentSessionStatus.SystemError);
        world.Runtime.CapacityFailure(session, turn);
        await world.Dispatch();
        Assert.Null(world.Record(1)!["capacityRecovery"]!["failure"]);
        var due = world.Saved(1)!.Recovery!.After;
        world.Clock.Advance(TimeSpan.FromSeconds(40));
        await world.Begin();
        Assert.Equal(TimeSpan.FromSeconds(80), await world.Dispatch());
        Assert.Equal(due, world.Saved(1)!.Recovery!.After);
        Assert.Equal("model-capacity", world.Status()["sessions"]![0]!["reason"]!.GetValue<string>());
        world.Clock.Advance(TimeSpan.FromSeconds(80));
        await world.Dispatch();
        Assert.Equal(session, world.Turns[1].SessionId);
        Assert.Contains("model capacity was unavailable", world.Turns[1].Input);
    }

    [Fact]
    public async Task Mixed_transient_failures_retain_reason_selection_order_and_partial_work_across_restart()
    {
        await using var world = new BridgeWorld { Files = true };
        world.Variables.Set(Bridge.ActiveSessionSetting, "1");
        world.GitHub.Open(1, "@test-app[bot] finish partial work");
        world.GitHub.Open(2, "@test-app[bot] independent work");
        await world.Begin();
        await world.Poll();
        var original = world.Turns[0];
        var (session, turn) = world.Running(1);
        world.Runtime.Report(session, AgentSessionStatus.SystemError);
        world.Runtime.TransientFailure(session, turn, new(AgentFailureKind.StreamConnection, 503));
        await world.Dispatch();
        Assert.Equal(2, world.Turns.Count);
        world.GitHub.Comment(1, "C1", "later ordered input");
        await world.Poll();
        world.Finish(2);
        await world.Dispatch();
        world.Clock.Advance(TimeSpan.FromSeconds(45));
        await world.Begin();
        Assert.Equal(TimeSpan.FromSeconds(75), await world.Dispatch());
        Assert.Equal(new(AgentFailureKind.StreamConnection, 503), world.Saved(1)!.Recovery!.Failure);
        Assert.Equal("stream-connection", world.Status()["sessions"]![0]!["reason"]!.GetValue<string>());
        Assert.Equal(503, world.Status()["sessions"]![0]!["httpStatus"]!.GetValue<int>());
        world.Variables.Set(ModelSelection.ReasoningSetting, "xhigh");
        await world.Refresh();
        world.Clock.Advance(TimeSpan.FromSeconds(75));
        await world.Dispatch();
        Assert.Equal((original.SessionId, original.Model), (world.Turns[2].SessionId, world.Turns[2].Model));
        Assert.Contains("stream-connection (HTTP 503)", world.Turns[2].Input);
        Assert.Contains("external effects", world.Turns[2].Input);
        Assert.DoesNotContain("finish partial work", world.Turns[2].Input);
        var retry = world.Running(1).Turn;
        world.Runtime.Report(session, AgentSessionStatus.SystemError);
        world.Runtime.TransientFailure(session, retry, new(AgentFailureKind.RequestThrottled));
        world.Runtime.TransientFailure(session, retry, new(AgentFailureKind.RequestThrottled));
        Assert.Equal(TimeSpan.FromSeconds(120), await world.Dispatch());
        Assert.Equal(2, world.Saved(1)!.Recovery!.Attempts);
        Assert.Equal("request-throttled", world.Status()["sessions"]![0]!["reason"]!.GetValue<string>());
        Assert.Null(world.Status()["sessions"]![0]!["httpStatus"]);
        Assert.Equal(2, world.Logged("transient-recovery-scheduled").Count);
        world.Clock.Advance(TimeSpan.FromSeconds(120));
        await world.Dispatch();
        world.Finish(1);
        await world.Dispatch();
        Assert.Null(world.Saved(1)!.Recovery);
        Assert.Equal(["C1"], BridgeWorld.Subjects(world.Envelope(4)));
        Assert.Single(world.Logged("transient-recovery-completed"));
        Assert.Equal(1, world.MostActive);
    }

    public static TheoryData<AgentFailureKind, int?, bool> HttpBoundaries
    {
        get
        {
            var cases = new TheoryData<AgentFailureKind, int?, bool>();
            foreach (var kind in new[] { AgentFailureKind.HttpConnection, AgentFailureKind.StreamConnection,
                AgentFailureKind.StreamDisconnected, AgentFailureKind.ResponseAttemptsExhausted })
            {
                foreach (var status in new[] { 408, 500, 502, 503, 504 }) cases.Add(kind, status, true);
                foreach (var status in new int?[] { null, 400, 401, 403, 404, 429, 501, 505 }) cases.Add(kind, status, false);
            }
            cases.Add((AgentFailureKind)999, 503, false);
            cases.Add(AgentFailureKind.RequestThrottled, 429, false);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(HttpBoundaries))]
    public async Task Only_confirmed_http_failures_with_allowlisted_statuses_schedule_continuation(AgentFailureKind kind, int? status, bool recoverable)
    {
        await using var world = new BridgeWorld();
        world.GitHub.Open(1, "@test-app[bot] work");
        await world.Begin();
        await world.Poll();
        var (session, turn) = world.Running(1);
        world.Runtime.Report(session, AgentSessionStatus.SystemError);
        world.Runtime.TransientFailure(session, turn, new(kind, status));
        await world.Dispatch();
        Assert.Equal(recoverable, world.Saved(1)!.Recovery is not null);
        world.Clock.Advance(TimeSpan.FromSeconds(120));
        await world.Dispatch();
        Assert.Equal(recoverable ? 2 : 1, world.Turns.Count);
    }

    [Fact]
    public async Task Request_throttling_continues_partial_work_in_the_original_session_after_two_minutes()
    {
        await using var world = new BridgeWorld();
        world.GitHub.Open(1, "@test-app[bot] implement the remaining feature");
        await world.Begin();
        await world.Poll();
        var original = Assert.Single(world.Turns);
        var (session, turn) = world.Running(1);
        world.Runtime.Report(session, AgentSessionStatus.SystemError);
        world.Runtime.TransientFailure(session, turn, new(AgentFailureKind.RequestThrottled));
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
        Assert.Contains("request throttling", continuation.Input, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("external effects", continuation.Input, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("implement the remaining feature", continuation.Input);
    }
}
