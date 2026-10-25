using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace RepoAgentController.Tests;

// The dashboard in-process: what it publishes through the core outbox, and when, from a real ControllerStatus over state files
// in a temporary directory. GitHub is the Discussions fake. The baseline release check never completes here;
// ControllerDashboardUpdatesTests owns that section.
public sealed class ControllerDashboardPublicationTests : IDisposable
{
    const string Category = "Controller status", Title = "RepoAgent controller status", Attention = "Attention: RepoAgent controller authentication failed";
    const string First = "https://github.com/owner/repository/discussions/1";
    static readonly DateTimeOffset Start = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
    readonly string directory = Path.Combine(Path.GetTempPath(), "controller-dashboard-" + Guid.NewGuid().ToString("N"));
    readonly string statePath;
    readonly SteppedClock clock = new(Start);
    readonly InMemoryDiscussions github = new();
    readonly InMemoryControllerState state = new();
    readonly ControllerStatus status;
    readonly GitHubOutbox outbox;

    public ControllerDashboardPublicationTests()
    {
        Directory.CreateDirectory(directory);
        statePath = Path.Combine(directory, "bridge.json");
        status = new(statePath, "owner/repository", "polling");
        outbox = new("owner/repository", github, state, clock, new ControllerLog());
        github.Category(Category);
    }

    ControllerDashboard Dashboard() => new(Category, status, outbox,
        new BaselineReleases(new InMemoryBaselineReleases(), async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return null; }, false, clock, new RecordingLog(), text => text),
        clock);

    // What the dashboard last handed the outbox; delivery is the outbox's.
    OutboxOperation Published() => state.Outbox!.Operations.Single(operation => operation.Kind == "dashboard");

    string Started => Time(DateTimeOffset.Parse(status.Snapshot()["controller"]!["startedAt"]!.GetValue<string>(), CultureInfo.InvariantCulture));

    static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    [Fact]
    public async Task Current_status_is_published_at_start_then_every_30_seconds_while_idle_and_on_each_change_and_a_graceful_stop_last()
    {
        status.Record(new(Start, "ready", "repository=owner/repository actor=test-app[bot] mode=polling"));
        status.Record(new(Start, "app-token-refreshed", "installation=234"));
        status.Record(new(Start, "forwarded", $"{First} session=s1 changes=1 sources=DC_7 waiting=0 latest={First}#discussioncomment-7"));
        await using (var dashboard = Dashboard())
        {
            dashboard.Start(TestContext.Current.CancellationToken);
            await clock.Armed();
            Assert.Equal((Category, Title), (Published().Category, Published().Title));
            Assert.Equal($"""
                <!-- repoagent:controller-dashboard -->
                Operational status of this repository's RepoAgent controller. The controller replaces this text on every update; comments and reactions here are never delivered to the agent.

                - **Controller:** running (polling) since {Started}
                - **Heartbeat:** 2026-10-24T10:00:00Z. Refreshed about every 30 seconds while the controller runs, even when idle. An older heartbeat means this status is unconfirmed, not that a particular failure occurred.
                - **Authentication:** healthy
                - **Codex authentication:** unknown
                - **Repository settings:** not read; the controller runs without --repository-settings
                - **Sessions:** 0 stored; 0 of 4 turns running (active-session limit); 0 awaiting native acknowledgement; 0 inputs waiting
                - **Latest activity:** input delivered from [a comment on Discussion #1]({First}#discussioncomment-7) at 2026-10-24T10:00:00Z

                ### Updates
                The baseline release check has not completed yet.

                ### Active and queued sessions
                None.

                ### Recent failures
                None recorded.

                """, Published().Body);

            // Nothing changes; the heartbeat alone republishes, once.
            var writes = state.OutboxWrites;
            clock.Advance(TimeSpan.FromSeconds(29));
            clock.Advance(TimeSpan.FromSeconds(1));
            await clock.Armed();
            Assert.Equal(writes + 1, state.OutboxWrites);
            Assert.Contains("\n- **Heartbeat:** 2026-10-24T10:00:30Z. Refreshed about every 30 seconds", Published().Body);

            // A status change republishes at once, between heartbeats.
            clock.Advance(TimeSpan.FromSeconds(10));
            status.Record(new(Start.AddSeconds(40), "session-created", $"{First.Replace("/1", "/2")} session=s2"));
            await clock.Armed();
            Assert.Contains($"\n- **Latest activity:** session started for [Discussion #2]({First.Replace("/1", "/2")}) at 2026-10-24T10:00:40Z\n", Published().Body);
            Assert.Contains("\n- **Heartbeat:** 2026-10-24T10:00:40Z.", Published().Body);
            Assert.Empty(github.Dashboards); // Delivery is the outbox's, which this test does not start.
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        // An operator stop publishes the stop with its time, claims no heartbeat and is delivered before disposal completes.
        var stopped = Assert.Single(github.Dashboards);
        Assert.Equal(Title, stopped.Title);
        Assert.Contains($"\n- **Controller:** stopped at 2026-10-24T10:00:45Z after a graceful shutdown; it had started at {Started}\n- **Heartbeat:** none while stopped.\n", stopped.Body);
        Assert.Contains("\n- **Sessions:** 0 stored; no turns run while the controller is stopped; 0 awaiting native acknowledgement; 0 inputs waiting\n", stopped.Body);
    }

    [Fact]
    public async Task A_failure_that_ends_the_lifetime_is_published_redacted_as_the_stop_and_a_dashboard_that_never_started_publishes_nothing()
    {
        await using (Dashboard()) { }
        Assert.Null(state.Outbox);

        var secret = "ghs_" + Guid.NewGuid().ToString("N");
        Log.Protect(secret);
        await using (var dashboard = Dashboard())
        {
            dashboard.Start(TestContext.Current.CancellationToken);
            await clock.Armed();
            dashboard.Failed($"Repository webhook registration failed for {secret}:\n `hooks` returned HTTP 422");
        }
        Assert.Contains($"\n- **Controller:** stopped at 2026-10-24T10:00:00Z after a failure: `Repository webhook registration failed for <redacted>: 'hooks' returned HTTP 422`\n",
            Assert.Single(github.Dashboards).Body);
    }

    [Fact]
    public async Task Either_failed_credential_turns_the_title_to_attention_until_it_recovers_while_the_redacted_failure_stays_listed()
    {
        using var observing = Log.Observe(status.Record);
        var secret = "ghs_" + Guid.NewGuid().ToString("N");
        Log.Protect(secret);
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();
        Assert.Equal(Title, Published().Title);

        Log.Write("app-refresh-error", $"GitHub App installation token refresh failed: HTTP 401 Bad credentials {secret}");
        await clock.Armed();
        var failed = Published();
        Assert.Equal(Attention, failed.Title);
        Assert.Matches(@"\n- \*\*Authentication:\*\* failed at \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ: `GitHub App installation token refresh failed: HTTP 401 Bad credentials <redacted>`\. " +
            @"Check the controller's approval and credentials, then restart it\.\n- \*\*Codex authentication:\*\* unknown\n", failed.Body);
        Assert.DoesNotContain(secret, failed.Body);

        Log.Write("app-token-refreshed", "installation=234");
        await clock.Armed();
        var recovered = Published();
        Assert.Equal(Title, recovered.Title);
        Assert.Contains("\n- **Authentication:** healthy\n", recovered.Body);
        Assert.Matches(@"### Recent failures\n- \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ `app-refresh-error`: `GitHub App installation token refresh failed: HTTP 401 Bad credentials <redacted>`\n$",
            recovered.Body);

        // The Codex account's credentials hold every turn, so they need the operator too; the App stays healthy.
        Log.Write("codex-auth-failed", "turn failed: unexpected status 401 Unauthorized");
        await clock.Armed();
        var codex = Published();
        Assert.Equal(Attention, codex.Title);
        Assert.Matches(@"\n- \*\*Authentication:\*\* healthy\n- \*\*Codex authentication:\*\* failed at \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ: `turn failed: unexpected status 401 Unauthorized`\. " +
            @"New Discussion input is held until the Codex account credentials are replaced\.\n", codex.Body);
        Assert.Contains("`codex-auth-failed`: `turn failed: unexpected status 401 Unauthorized`\n", codex.Body);

        Log.Write("codex-auth-supplied", "account credentials replaced");
        await clock.Armed();
        Assert.Equal(Title, Published().Title);
        Assert.Contains("\n- **Codex authentication:** supplied credentials installed; not yet verified by a completed turn\n", Published().Body);
    }

    [Fact]
    public async Task Unusable_repository_settings_are_listed_with_the_value_that_still_applies()
    {
        var files = new ControllerStateFiles(statePath);
        files.WriteSettings(new(new Dictionary<string, SettingValue>
        {
            ["REPOAGENT_MAX_ACTIVE_SESSIONS"] = new(SettingState.Invalid, null, "must be an integer from 1 to 64"),
            ["REPOAGENT_MODEL"] = new(SettingState.ReadFailed, "gpt-5.5", "HTTP 503") { Since = Start },
            ["REPOAGENT_REASONING"] = new(SettingState.Configured, "high") { Since = Start },
            ["REPOAGENT_ENROLLMENT_CATEGORIES"] = new(SettingState.Absent, null)
        }));
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();
        Assert.Contains("""

            - **Repository settings:** 2 unusable
              - `REPOAGENT_MAX_ACTIVE_SESSIONS` is invalid: `must be an integer from 1 to 64`; no repository value applies
              - `REPOAGENT_MODEL` could not be read: `HTTP 503`; the earlier value `gpt-5.5` still applies
            - **Sessions:**
            """, Published().Body);
        Assert.Equal(Title, Published().Title);

        files.WriteSettings(new(new Dictionary<string, SettingValue> { ["REPOAGENT_MODEL"] = new(SettingState.Configured, "gpt-5.5") { Since = Start } }));
        clock.Advance(ControllerDashboard.HeartbeatInterval);
        await clock.Armed();
        Assert.Contains("\n- **Repository settings:** read without problems\n- **Sessions:**", Published().Body);

        files.WriteSettings(new(new Dictionary<string, SettingValue> { ["REPOAGENT_MODEL"] = new(SettingState.Disabled, null) }));
        clock.Advance(ControllerDashboard.HeartbeatInterval);
        await clock.Armed();
        Assert.Contains("\n- **Repository settings:** not read; the controller runs without --repository-settings\n", Published().Body);
    }

    [Fact]
    public async Task A_dashboard_write_refused_by_a_rate_limit_is_reported_on_the_dashboard_once_delivery_recovers()
    {
        var ct = TestContext.Current.CancellationToken;
        using var observing = Log.Observe(status.Record);
        await using var dashboard = Dashboard();
        dashboard.Start(ct);
        await clock.Armed();
        Assert.True(await outbox.Flush(ct));
        var created = Assert.Single(github.Dashboards);

        github.Fail(new(GitHubFailure.RateLimited, "You have exceeded a secondary rate limit.", retryAfter: TimeSpan.FromSeconds(120)));
        clock.Advance(ControllerDashboard.HeartbeatInterval);
        await clock.Armed();
        Assert.False(await outbox.Flush(ct));
        await clock.Armed(); // The refusal is a status failure, which republishes.

        // GitHub's signalled wait spans four heartbeats; only the latest publication is written after it.
        for (var heartbeat = 1; heartbeat <= 4; heartbeat++)
        {
            clock.Advance(ControllerDashboard.HeartbeatInterval);
            await clock.Armed();
            Assert.Equal(heartbeat == 4, await outbox.Flush(ct));
        }
        Assert.Equal([$"updateDiscussion {created.Id}", $"updateDiscussion {created.Id}"], github.Calls.Skip(3));
        var recovered = Assert.Single(github.Dashboards);
        Assert.Contains("\n- **Heartbeat:** 2026-10-24T10:02:30Z.", recovered.Body);
        Assert.Contains($"`dashboard-error`: `{created.Url} attempt=1 RateLimited: You have exceeded a secondary rate limit.; retained for retry at 2026-10-24T10:02:30.0000000+00:00`\n",
            recovered.Body);
        Assert.Empty(state.Outbox!.Operations);
    }

    [Fact]
    public async Task Each_busy_session_is_listed_with_what_it_is_doing_and_the_most_specific_reason_its_input_waits()
    {
        Limit(1);
        Store(
            ("D1", new JsonObject { ["url"] = Url(1), ["sessionId"] = "s1", ["running"] = new JsonObject { ["turn"] = "t1", ["since"] = "2026-10-24T09:30:00.0000000+00:00" }, ["waiting"] = Waiting(1) }),
            ("D2", new JsonObject
            {
                ["url"] = Url(2), ["sessionId"] = "s2",
                ["pending"] = new JsonObject { ["operation"] = "send", ["since"] = "2026-10-24T09:59:00.0000000+00:00", ["sourceIds"] = new JsonArray("DC_2"), ["inputs"] = 1 }
            }),
            ("D3", new JsonObject { ["url"] = Url(3), ["sessionId"] = "s3", ["waiting"] = Waiting(1, new JsonObject { ["attempts"] = 2, ["after"] = "2026-10-24T10:05:00.0000000+00:00" }) }),
            ("D4", new JsonObject { ["url"] = Url(4), ["sessionId"] = "s4", ["waiting"] = Waiting(2) }),
            ("D5", new JsonObject { ["url"] = Url(5), ["sessionId"] = "s5" }));
        await using (var dashboard = Dashboard())
        {
            dashboard.Start(TestContext.Current.CancellationToken);
            await clock.Armed();
            Assert.Contains("\n- **Sessions:** 5 stored; 1 of 1 turns running (active-session limit); 1 awaiting native acknowledgement; 4 inputs waiting\n", Published().Body);
            Assert.Equal($"""
                - [Discussion #1]({Url(1)}): running a turn since 2026-10-24T09:30:00Z; 1 inputs waiting for that turn to finish
                - [Discussion #2]({Url(2)}): awaiting native acknowledgement
                - [Discussion #3]({Url(3)}): delivery failed or was unacknowledged; attempt 3 at 2026-10-24T10:05:00Z; 1 inputs waiting
                - [Discussion #4]({Url(4)}): 2 inputs waiting for a free active-session slot
                """, Listed());

            // Rejected Codex credentials hold idle input; input behind a turn or a delivery keeps its own reason.
            status.Record(new(Start, "codex-auth-failed", "turn failed: unexpected status 401 Unauthorized"));
            await clock.Armed();
            Assert.Equal($"""
                - [Discussion #1]({Url(1)}): running a turn since 2026-10-24T09:30:00Z; 1 inputs waiting for that turn to finish
                - [Discussion #2]({Url(2)}): awaiting native acknowledgement
                - [Discussion #3]({Url(3)}): delivery failed or was unacknowledged; attempt 3 at 2026-10-24T10:05:00Z; 1 inputs waiting
                - [Discussion #4]({Url(4)}): 2 inputs held until the Codex account credentials are replaced
                """, Listed());

            Limit(4);
            status.Record(new(Start, "codex-auth-verified", "turn completed"));
            await clock.Armed();
            Assert.EndsWith($"\n- [Discussion #4]({Url(4)}): 2 inputs waiting for dispatch", Listed());
            Limit(1);
        }

        // Turns end with the controller, so its stop claims no slot is awaited even at the limit.
        Assert.Equal($"""
            - [Discussion #1]({Url(1)}): its turn ended with the controller; 1 inputs waiting
            - [Discussion #2]({Url(2)}): awaiting native acknowledgement
            - [Discussion #3]({Url(3)}): delivery failed or was unacknowledged; attempt 3 at 2026-10-24T10:05:00Z; 1 inputs waiting
            - [Discussion #4]({Url(4)}): 2 inputs waiting for dispatch
            """, Listed(Assert.Single(github.Dashboards).Body));
    }

    [Fact]
    public async Task Idle_sessions_are_not_listed_and_at_most_20_busy_ones_are_with_only_GitHub_Discussion_links()
    {
        Store(("D0", new JsonObject { ["url"] = Url(1), ["sessionId"] = "s0" }));
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();
        Assert.Equal("None.", Listed());

        Store([.. Enumerable.Range(1, 22).Select(number => ($"D{number}", new JsonObject { ["url"] = number == 1 ? "javascript:alert(1)" : Url(number), ["waiting"] = Waiting(1) }))]);
        clock.Advance(ControllerDashboard.HeartbeatInterval);
        await clock.Armed();
        var listed = Listed().Split('\n');
        Assert.Equal(21, listed.Length);
        Assert.Equal("- an unlinked Discussion: 1 inputs waiting for dispatch", listed[0]);
        Assert.Equal($"- [Discussion #20]({Url(20)}): 1 inputs waiting for dispatch", listed[19]);
        Assert.Equal("- and 2 more", listed[20]);
    }

    static string Url(int number) => $"https://github.com/owner/repository/discussions/{number}";

    // The dashboard's session list, from the last publication handed to the outbox unless another body is given.
    string Listed(string? body = null) => (body ?? Published().Body!).Split("\n### Active and queued sessions\n")[1].Split("\n\n### Recent failures\n")[0];

    // Discussion records as the bridge keeps them in the state file the status reads.
    void Store(params (string Id, JsonObject Record)[] records)
    {
        using var store = new StateStore(statePath, "owner/repository");
        foreach (var (id, record) in records) store.Discussions[id] = record;
        store.Save();
    }

    // Observed input waiting for its session, as the bridge queues it; a failed delivery also schedules its retry there.
    static JsonObject Waiting(int inputs, JsonObject? retry = null)
    {
        var waiting = new JsonObject
        {
            ["inputs"] = new JsonArray([.. Enumerable.Range(1, inputs).Select(index => (JsonNode)new JsonObject { ["observedAt"] = Start.ToString("O"), ["change"] = new JsonObject { ["id"] = $"DC_{index}" } })])
        };
        if (retry is not null) waiting["retry"] = retry;
        return waiting;
    }

    void Limit(int sessions) => new ControllerStateFiles(statePath).WriteSettings(new(new Dictionary<string, SettingValue>
    {
        [Bridge.ActiveSessionSetting] = new(SettingState.Configured, sessions.ToString(CultureInfo.InvariantCulture)) { Since = Start }
    }));

    public void Dispose()
    {
        status.Dispose();
        Directory.Delete(directory, true);
    }
}
