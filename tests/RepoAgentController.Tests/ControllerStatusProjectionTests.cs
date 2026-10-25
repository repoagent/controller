using Microsoft.Extensions.Time.Testing;
using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

// Operator status as the core projects it from a lifetime's log and composes it from the saved Discussions, the recorded
// settings and the checkout's turns, over in-memory stores. Its JSON is ControllerStatusGoldenTests'.
public sealed class ControllerStatusProjectionTests
{
    const string Repository = "owner/repository";
    static readonly DateTimeOffset Start = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    readonly FakeTimeProvider clock = new(Start);
    readonly InMemoryStatusRecord record = new();
    readonly InMemoryDiscussionStore discussions = new();
    readonly InMemoryControllerState settings = new();
    readonly InMemoryCheckoutWork checkout = new();

    ControllerStatus Open() => new(Repository, "polling", record, discussions, settings, checkout, clock);

    OperatorStatus Stopped() => ControllerStatus.Read(record, discussions, settings, checkout, clock)!;

    static LogEntry Entry(int seconds, string kind, string message) => new(Start.AddSeconds(seconds), kind, message);

    static string At(int seconds) => Start.AddSeconds(seconds).ToString("O");

    [Fact]
    public async Task A_failed_delivery_of_a_running_controller_reports_its_session_retrying_and_the_failure_before_and_after_it_stops()
    {
        await using var world = new BridgeWorld();
        world.GitHub.Open(1, "@test-app[bot] please look at fixture 1");
        world.Answer(_ => throw new AgentRequestRejected("turn rejected"));
        await world.Begin();
        Log.Write("ready", "mode=polling"); // As the controller logs once its loops start.
        await world.Poll();

        foreach (var (status, state) in new[] { (world.Status(), "running"), (await Ended(world), "stopped") })
        {
            Assert.Equal(state, status["controller"]!["state"]!.GetValue<string>());
            var session = Assert.Single(status["sessions"]!.AsArray())!;
            Assert.Equal((BridgeWorld.Url(1), "S1", "retrying", 1), (session["discussion"]!.GetValue<string>(), session["session"]!.GetValue<string>(),
                session["state"]!.GetValue<string>(), session["attempts"]!.GetValue<int>()));
            Assert.NotNull(session["retryAt"]);
            Assert.Equal(1, status["work"]!["retrying"]!.GetValue<int>());
            var failure = Assert.Single(status["failures"]!.AsArray())!;
            Assert.Equal("delivery-retry", failure["kind"]!.GetValue<string>());
            Assert.Equal(Assert.Single(world.Logged("delivery-retry")), failure["message"]!.GetValue<string>());
        }

        static async Task<System.Text.Json.Nodes.JsonObject> Ended(BridgeWorld world)
        {
            await world.End();
            return world.Status();
        }
    }

    [Fact]
    public void A_denied_app_token_renewal_reports_the_app_credential_and_then_the_controller_failed_and_a_stop_keeps_it_failed()
    {
        using (var status = Open())
        {
            status.Record(Entry(1, "ready", "mode=polling"));
            status.Record(Entry(2, "app-token-refreshed", "expires=later"));
            status.Record(Entry(3, "app-refresh-error", "GitHub App installation token refresh failed: HTTP 401"));
            Assert.Equal(new CredentialStatus("failed") { UpdatedAt = At(3), Error = "GitHub App installation token refresh failed: HTTP 401" },
                status.Snapshot().Authentication);
            Assert.Equal("running", status.Snapshot().Controller.State);
            status.Record(Entry(4, "fatal", "the GitHub App credential was denied"));
            Assert.Equal("failed", status.Snapshot().Controller.State);
            clock.Advance(TimeSpan.FromSeconds(10));
        }

        var stopped = Stopped();
        Assert.Equal(new ControllerRun("failed") { Mode = "polling", StartedAt = At(0), UpdatedAt = At(10), StoppedAt = At(10) }, stopped.Controller);
        Assert.Equal(["app-refresh-error", "fatal"], stopped.Failures.Select(failure => failure.Kind));
        Assert.Equal("failed", stopped.Authentication.State);
    }

    [Fact]
    public void A_lifetime_without_a_fatal_failure_stops_stopped()
    {
        using (var status = Open()) status.Record(Entry(1, "ready", "mode=polling"));

        Assert.Equal("stopped", Stopped().Controller.State);
    }

    [Theory]
    [InlineData("codex-auth-supplied", "supplied", null)]
    [InlineData("codex-auth-applied", "supplied", null)]
    [InlineData("codex-auth-verified", "healthy", null)]
    [InlineData("codex-auth-failed", "failed", "agent credentials rejected")]
    public void The_agent_credential_reports_each_event_of_its_state_at_that_time(string kind, string state, string? error)
    {
        using var status = Open();
        status.Record(Entry(5, kind, "agent credentials rejected"));

        Assert.Equal(new CredentialStatus(state) { UpdatedAt = At(5), Error = error }, status.Snapshot().CodexAuthentication);
        Assert.Equal(new CredentialStatus("unknown"), status.Snapshot().Authentication);
    }

    [Fact]
    public void Failures_keep_the_latest_twenty_and_a_message_over_2000_characters_is_cut_there()
    {
        using var status = Open();
        for (var failure = 0; failure < 22; failure++) status.Record(Entry(failure, "poll-error", $"failure {failure}"));
        status.Record(Entry(22, "dispatch-error", new string('x', 2000)));
        status.Record(Entry(23, "dispatch-error", new string('y', 2001)));

        var failures = status.Snapshot().Failures;
        Assert.Equal([.. Enumerable.Range(4, 18).Select(failure => $"failure {failure}"), new string('x', 2000), new string('y', 2000) + " <truncated>"],
            failures.Select(failure => failure.Message));
        Assert.Equal(At(4), failures[0].At);
    }

    [Theory]
    [InlineData("forwarded", "https://github.com/owner/repository/discussions/1 session=S1 latest=https://github.com/owner/repository/discussions/1#discussioncomment-7 waiting=0",
        "https://github.com/owner/repository/discussions/1#discussioncomment-7")]
    [InlineData("session-created", "https://github.com/owner/repository/discussions/1 session=S1", "https://github.com/owner/repository/discussions/1")]
    [InlineData("forwarded", "session=S1 for https://github.com/owner/repository/discussions/1", null)]
    [InlineData("forwarded", "https://example.org/owner/repository session=S1 latest=https://example.org/1", null)]
    public void The_latest_activity_links_its_latest_message_else_its_leading_GitHub_URL(string kind, string message, string? url)
    {
        using var status = Open();
        status.Record(Entry(3, kind, message));

        Assert.Equal(new StatusEvent(At(3), kind, message) { Url = url }, status.Snapshot().LatestActivity);
        Assert.Empty(status.Snapshot().Failures);
    }

    [Theory]
    [InlineData("input-waiting", true, 0)]
    [InlineData("input-requeued", true, 0)]
    [InlineData("turn-ended", true, 0)]
    [InlineData("discovered", false, 0)]
    [InlineData("ready", true, 1)]
    [InlineData("poll-error", true, 1)]
    [InlineData("forwarded", true, 1)]
    public void A_work_event_only_notifies_consumers_and_an_event_status_reports_is_recorded(string kind, bool notified, int writes)
    {
        using var status = Open();
        var changes = 0;
        status.Changed += () => changes++;
        var before = (record.Writes, record.Recorded);

        status.Record(Entry(1, kind, "https://github.com/owner/repository/discussions/1"));

        Assert.Equal(notified ? 1 : 0, changes);
        Assert.Equal(before.Writes + writes, record.Writes);
        if (writes == 0) Assert.Same(before.Recorded, record.Recorded);
        else Assert.Equal(At(1), record.Recorded!.Controller!.UpdatedAt);
    }

    [Fact]
    public void A_lifetime_starts_with_the_earlier_lifetimes_failures_and_latest_activity_and_none_of_its_other_status()
    {
        using (var earlier = Open())
        {
            earlier.Record(Entry(1, "ready", "mode=polling"));
            earlier.Record(Entry(2, "app-refresh-error", "HTTP 401"));
            earlier.Record(Entry(3, "forwarded", "https://github.com/owner/repository/discussions/1 session=S1"));
            earlier.Running(["S1"]);
        }
        clock.Advance(TimeSpan.FromMinutes(1));

        using var status = Open();

        var started = status.Snapshot();
        Assert.Equal(new ControllerRun("starting") { Mode = "polling", StartedAt = At(60), UpdatedAt = At(60) }, started.Controller);
        Assert.Equal(new CredentialStatus("unknown"), started.Authentication);
        Assert.Equal(new StatusEvent(At(2), "app-refresh-error", "HTTP 401"), Assert.Single(started.Failures));
        Assert.Equal("forwarded", started.LatestActivity!.Kind);
        Assert.Empty(record.Recorded!.RunningSessions);
    }

    [Fact]
    public void A_running_turn_whose_Discussion_record_was_forgotten_holds_its_slot_while_the_controller_runs()
    {
        discussions.WriteDiscussions(State(("D1", Record(1, "S1"))));
        using (var status = Open())
        {
            status.Running(["S2", "S1"]);
            Assert.Equal(["S1", "S2"], record.Recorded!.RunningSessions);
            Assert.Equal((1, 1), (status.Snapshot().Work.Sessions, status.Snapshot().Work.Running)); // S1's record holds no turn.
            status.Running(["S1"]);
            Assert.Equal(0, status.Snapshot().Work.Running);
            status.Running(["S1", "S3"]);
        }

        Assert.Equal(0, Stopped().Work.Running); // Turns end with their lifetime.
    }

    [Fact]
    public void A_Discussion_reports_its_pending_delivery_else_its_retry_else_its_running_turn_with_their_times()
    {
        var since = Start.AddMinutes(1);
        discussions.WriteDiscussions(State(
            ("D1", Record(1, "S1", pending: new("send", since, ["C1"], 1), retry: new(2, since), running: new("T1", since))),
            ("D2", Record(2, "S2", retry: new(2, since.AddMinutes(4)), running: new("T2", since))),
            ("D3", Record(3, "S3", running: new("T3", since.AddMinutes(1)))),
            ("D4", Record(4, "S4")),
            ("D5", Record(5, "S5")),
            ("D6", Record(6, null, inputs: 2))));
        checkout.Work = new(new HashSet<string> { "S5", "S6" }, true);
        using var status = Open();

        var snapshot = status.Snapshot();

        Assert.Equal([
            new(Url(1), "S1", "pending") { Since = since.ToString("O"), Operation = "send", Waiting = 1 },
            new(Url(2), "S2", "retrying") { Attempts = 2, RetryAt = since.AddMinutes(4).ToString("O"), Waiting = 1 },
            new(Url(3), "S3", "running") { Since = since.AddMinutes(1).ToString("O") },
            new(Url(4), "S4", "idle"),
            new(Url(5), "S5", "running"), // The checkout's turn.
            new(Url(6), null, "idle") { Waiting = 2 }], snapshot.Sessions);
        Assert.Equal(new WorkCounts(5, 1, 1, 4, 2, Bridge.DefaultActiveSessions, true), snapshot.Work);
        Assert.Equal((Repository, "test-app[bot]"), (snapshot.Repository, snapshot.Actor));
    }

    [Fact]
    public void A_delivery_or_retry_kept_as_read_reports_none_of_its_times_or_counts()
    {
        discussions.WriteDiscussions(State(
            ("D1", Record(1, "S1", pending: new("send", Start, ["C1"], 1) { AsRead = """{"operation":5}""" })),
            ("D2", Record(2, "S2", retry: new(2, Start) { AsRead = """{"attempts":"x"}""" }))));
        using var status = Open();

        Assert.Equal([new(Url(1), "S1", "pending") { Waiting = 1 }, new(Url(2), "S2", "retrying") { Waiting = 1 }], status.Snapshot().Sessions);
    }

    [Fact]
    public void A_running_turn_kept_as_read_runs_while_the_controller_runs_and_an_unreadable_record_is_not_reported()
    {
        var state = State(("D1", Record(1, "S1")), ("D2", Record(2, "S2")), ("D3", new(Url(3)) { Unreadable = new("5", "not an object", false) }));
        state.Discussions["D1"].UnreadableKeys[RecordKey.Running] = new("""{"turn":5}""", "turn is not text", true);
        state.Discussions["D2"].UnreadableKeys[RecordKey.Running] = new("5", "not an object", false);
        discussions.WriteDiscussions(state);
        using var status = Open();

        Assert.Equal([new(Url(1), "S1", "running"), new(Url(2), "S2", "idle")], status.Snapshot().Sessions);
    }

    [Fact]
    public void Status_reports_no_work_of_a_state_that_is_unreadable_as_a_whole()
    {
        discussions.WriteDiscussions(new DiscussionState(Repository) { Unreadable = """{"discussions":5}""" });
        checkout.Work = new(new HashSet<string> { "S1" }, false);
        using var status = Open();
        status.Running(["S1"]);

        var snapshot = status.Snapshot();

        Assert.Empty(snapshot.Sessions);
        Assert.Equal(new WorkCounts(0, 0, 0, 0, 1, Bridge.DefaultActiveSessions, false), snapshot.Work);
    }

    [Theory]
    [InlineData(SettingState.Configured, "2", 2)]
    [InlineData(SettingState.ReadFailed, "6", 6)] // A retained value.
    [InlineData(SettingState.Configured, "two", Bridge.DefaultActiveSessions)]
    [InlineData(SettingState.Invalid, null, Bridge.DefaultActiveSessions)]
    public void The_active_session_limit_is_the_recorded_effective_value_else_the_default(SettingState state, string? value, int limit)
    {
        settings.WriteSettings(new(new Dictionary<string, SettingValue> { [Bridge.ActiveSessionSetting] = new(state, value) { Since = Start } }));
        using var status = Open();

        Assert.Equal(limit, status.Snapshot().Work.Limit);
        Assert.Same(settings.ReadSettings()!.Settings[Bridge.ActiveSessionSetting], status.Snapshot().Settings!.Settings[Bridge.ActiveSessionSetting]);
    }

    [Fact]
    public void A_stopped_controller_without_a_status_record_or_saved_state_has_no_status()
    {
        Assert.Null(ControllerStatus.Read(record, discussions, settings, checkout, clock));

        discussions.WriteDiscussions(State(("D1", Record(1, "S1"))));
        var status = Stopped();

        Assert.Equal((new ControllerRun("unknown"), Repository, 1), (status.Controller, status.Repository, status.Sessions.Count));
    }

    [Fact]
    public void A_failure_logged_with_a_known_secret_is_reported_redacted()
    {
        var secret = "status-secret-" + Guid.NewGuid().ToString("N");
        Log.Protect(secret);
        using var status = Open();
        using (Log.Observe(status.Record)) Log.Write("poll-error", "GitHub refused " + secret);

        Assert.Equal("GitHub refused <redacted>", Assert.Single(status.Snapshot().Failures).Message);
    }

    [Fact]
    public void A_closed_lifetime_records_nothing_more()
    {
        var status = Open();
        status.Dispose();
        var closed = record.Recorded;

        status.Record(Entry(5, "fatal", "late"));
        status.Running(["S1"]);
        status.Dispose();

        Assert.Same(closed, record.Recorded);
    }

    static string Url(int number) => BridgeWorld.Url(number);

    static DiscussionState State(params (string Id, DiscussionRecord Record)[] records)
    {
        var state = new DiscussionState(Repository) { AgentLogin = "test-app[bot]" };
        foreach (var (id, discussion) in records) state.Discussions[id] = discussion;
        return state;
    }

    static DiscussionRecord Record(int number, string? session, PendingDelivery? pending = null, DeliveryRetry? retry = null, RunningTurn? running = null,
        int? inputs = null)
    {
        var discussion = new DiscussionRecord(Url(number)) { SessionId = session, Pending = pending, Running = running };
        if (retry is not null || pending is not null || inputs is not null)
        {
            discussion.Waiting = new("{}") { Retry = retry };
            for (var input = 0; input < (inputs ?? 1); input++) discussion.Waiting.Inputs.Add(new(Start, "{}", null));
        }
        return discussion;
    }
}
