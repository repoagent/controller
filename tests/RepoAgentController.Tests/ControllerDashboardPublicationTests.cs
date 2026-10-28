using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;
using static RepoAgentController.Tests.BaselineReleaseCheckTests;

namespace RepoAgentController.Tests;

// The dashboard in-process: what it publishes through the core outbox, and when, from a ControllerStatus over the status
// record, Discussion state, settings and checkout fakes. GitHub is the Discussions fake. The baseline release check completes
// only where its outcome must reach the dashboard; ControllerDashboardUpdatesTests owns that section's text and
// ControllerDashboardGoldenTests the whole body's bytes.
public sealed class ControllerDashboardPublicationTests : IAsyncDisposable
{
    const string Category = "Controller status", Title = "RepoAgent controller status", Attention = "Attention: RepoAgent controller authentication failed";
    const string First = "https://github.com/owner/repository/discussions/1";
    static readonly DateTimeOffset Start = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
    readonly SteppedClock clock = new(Start);
    readonly InMemoryDiscussions github = new();
    // The settings and the outbox, as the controller's state files hold both.
    readonly InMemoryControllerState state = new();
    readonly InMemoryDiscussionStore discussions = new();
    readonly RecordingLog log = new();
    readonly ControllerStatus status;
    readonly GitHubOutbox outbox;

    public ControllerDashboardPublicationTests()
    {
        status = new("owner/repository", "polling", new InMemoryStatusRecord(), discussions, state, new InMemoryCheckoutWork(), clock);
        outbox = new("owner/repository", github, InMemoryDiscussions.App, state, clock, new StatusLog(status, clock));
        github.Category(Category);
    }

    // Events reach the status, as the composition's log observer delivers them, and are also recorded when a log is given.
    sealed class StatusLog(ControllerStatus status, TimeProvider clock, RecordingLog? recorded = null) : IControllerLog
    {
        public void Write(string kind, string message)
        {
            recorded?.Write(kind, message);
            status.Record(new(clock.GetUtcNow(), kind, message));
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2000", Justification = "ControllerDashboard owns and asynchronously disposes its BaselineReleases.")]
    ControllerDashboard Dashboard(BaselineReleases? releases = null, IControllerLog? reported = null) => new(Category, status, outbox,
            releases ?? new BaselineReleases(new InMemoryBaselineReleases(), async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return null; }, clock, new RecordingLog(), text => text),
            clock, reported ?? log, Redact);

    // As the controller's redaction removes the secrets it knows.
    static string Redact(string text) => text.Replace("ghs_secret", "<redacted>", StringComparison.Ordinal);

    // The published body once the dashboard has republished it naming the release at this URL; each publication arms the next heartbeat.
    async Task<string> Naming(BaselineRelease release)
    {
        var url = "https://github.com/repoagent/controller/releases/tag/" + release.Tag;
        while (state.Outbox?.Operations.OfType<DashboardOperation>().SingleOrDefault() is not { } publication || !publication.Body.Contains(url, StringComparison.Ordinal))
            await clock.Armed();
        return Published().Body;
    }

    // What the dashboard last handed the outbox; delivery is the outbox's.
    DashboardOperation Published() => state.Outbox!.Operations.OfType<DashboardOperation>().Single();

    string Started => Time(DateTimeOffset.Parse(status.Snapshot().Controller.StartedAt!, CultureInfo.InvariantCulture));

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

    // The controller's options reaching the check is the composition's; BaselineReleaseCheckTests owns the check's decisions.
    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2000", Justification = "The dashboard takes ownership of releases and disposes it after its worker ends.")]
    public async Task Newer_baseline_release_reaches_the_dashboard_after_a_later_hourly_check()
    {
        var published = new InMemoryBaselineReleases();
        var current = Release(101, image: "running");
        published.Publish(current);
        var releases = new BaselineReleases(published, _ => Task.FromResult<string?>(Inventory("running")), clock, new RecordingLog(), text => text);
        await using var dashboard = Dashboard(releases);

        dashboard.Start(TestContext.Current.CancellationToken);
        Assert.Matches(@"### Updates\nThis controller's image matches \[" + Regex.Escape(current.Tag) + @"\][^\n]*newest", await Naming(current));

        var newest = Release(102, image: "newest", gh: "2.103.1");
        published.Publish(newest);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Matches(@"### Updates\n\*\*Update available:\*\* baseline release \[" + Regex.Escape(newest.Tag) + @"\][^\n]*\[" + Regex.Escape(current.Tag) + @"\]\([^)\n]*\) \(1 newer release;", await Naming(newest));
    }

    [Fact]
    public async Task A_failure_that_ends_the_lifetime_is_published_redacted_as_the_stop_and_a_dashboard_that_never_started_publishes_nothing()
    {
        await using (Dashboard()) { }
        Assert.Null(state.Outbox);

        await using (var dashboard = Dashboard())
        {
            dashboard.Start(TestContext.Current.CancellationToken);
            await clock.Armed();
            dashboard.Failed("Repository webhook registration failed for ghs_secret:\n `hooks` returned HTTP 422");
        }
        var stopped = Assert.Single(github.Dashboards).Body;
        Assert.Contains("\n- **Controller:** stopped at 2026-10-24T10:00:00Z after a failure: `Repository webhook registration failed for <redacted>: 'hooks' returned HTTP 422`\n",
            stopped);
        Assert.DoesNotContain("ghs_secret", stopped);
    }

    [Fact]
    public async Task A_stop_GitHub_never_answers_ends_disposal_once_10_seconds_pass_on_the_clock_and_stays_retained()
    {
        github.HoldDashboardWrites();
        var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();
        clock.Advance(TimeSpan.FromSeconds(5));

        var disposal = dashboard.DisposeAsync().AsTask();
        await clock.Armed(); // The stop's delivery budget.
        clock.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
        Assert.False(disposal.IsCompleted);
        clock.Advance(TimeSpan.FromTicks(1));
        await disposal.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Empty(github.Dashboards);
        Assert.Contains("\n- **Controller:** stopped at 2026-10-24T10:00:05Z after a graceful shutdown;", Published().Body);
    }

    [Fact]
    public async Task A_publication_the_outbox_cannot_retain_is_reported_and_retried_at_the_next_heartbeat()
    {
        // The report is a status failure, which changes status; only the heartbeat retries, so it cannot spin the loop.
        await using var dashboard = Dashboard(reported: new StatusLog(status, clock, log));
        state.FailWrites = true;
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();
        Assert.Equal([("dashboard-error", "No space left on device; publication is retried at the next heartbeat")], log.Entries);
        Assert.Null(state.Outbox);

        state.FailWrites = false;
        clock.Advance(ControllerDashboard.HeartbeatInterval);
        await clock.Armed();
        Assert.Single(log.Entries);
        Assert.Contains("\n### Recent failures\n- 2026-10-24T10:00:00Z `dashboard-error`: `No space left on device; publication is retried at the next heartbeat`\n", Published().Body);
    }

    [Fact]
    public async Task Throttled_credentials_publish_automatic_retry_without_claiming_rejection_then_recover()
    {
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();
        Record("app-token-throttled", "GitHub token acquisition throttled; retry at 2026-10-24T10:01:00Z");
        await clock.Armed();
        Assert.Equal(Title, Published().Title);
        Assert.Contains("temporarily throttled", Published().Body);
        Assert.Contains("retry at 2026-10-24T10:01:00Z", Published().Body);
        Assert.Contains("Renewal retries automatically", Published().Body);
        Record("app-token-refreshed", "actor=test-app[bot] expires=later");
        await clock.Armed();
        Assert.Equal("healthy", status.Snapshot().Authentication.State);
        Assert.DoesNotContain("temporarily throttled", Published().Body);
    }

    [Fact]
    public async Task Either_failed_credential_turns_the_title_to_attention_until_it_recovers_while_the_failure_stays_listed()
    {
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();
        Assert.Equal(Title, Published().Title);

        Record("app-refresh-error", "GitHub App credential renewal failed: HTTP 401 Bad credentials <redacted>");
        await clock.Armed();
        var failed = Published();
        Assert.Equal(Attention, failed.Title);
        Assert.Matches(@"\n- \*\*Authentication:\*\* failed at \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ: `GitHub App credential renewal failed: HTTP 401 Bad credentials <redacted>`\. " +
            @"Check the controller's approval and credentials, then restart it\.\n- \*\*Codex authentication:\*\* unknown\n", failed.Body);

        Record("app-token-refreshed", "installation=234");
        await clock.Armed();
        var recovered = Published();
        Assert.Equal(Title, recovered.Title);
        Assert.Contains("\n- **Authentication:** healthy\n", recovered.Body);
        Assert.Matches(@"### Recent failures\n- \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ `app-refresh-error`: `GitHub App credential renewal failed: HTTP 401 Bad credentials <redacted>`\n$",
            recovered.Body);

        // The Codex account's credentials hold every turn, so they need the operator too; the App stays healthy.
        Record("codex-auth-failed", "turn failed: unexpected status 401 Unauthorized");
        await clock.Armed();
        var codex = Published();
        Assert.Equal(Attention, codex.Title);
        Assert.Matches(@"\n- \*\*Authentication:\*\* healthy\n- \*\*Codex authentication:\*\* failed at \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ: `turn failed: unexpected status 401 Unauthorized`\. " +
            @"New Discussion input is held until the Codex account credentials are replaced\.\n", codex.Body);
        Assert.Contains("`codex-auth-failed`: `turn failed: unexpected status 401 Unauthorized`\n", codex.Body);

        Record("codex-auth-supplied", "account credentials replaced");
        await clock.Armed();
        Assert.Equal(Title, Published().Title);
        Assert.Contains("\n- **Codex authentication:** supplied credentials installed; not yet verified by a completed turn\n", Published().Body);
    }

    [Fact]
    public async Task Unusable_repository_settings_are_listed_with_the_value_that_still_applies()
    {
        state.WriteSettings(new(new Dictionary<string, SettingValue>
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

        state.WriteSettings(new(new Dictionary<string, SettingValue> { ["REPOAGENT_MODEL"] = new(SettingState.Configured, "gpt-5.5") { Since = Start } }));
        clock.Advance(ControllerDashboard.HeartbeatInterval);
        await clock.Armed();
        Assert.Contains("\n- **Repository settings:** read without problems\n- **Sessions:**", Published().Body);

        state.WriteSettings(new(new Dictionary<string, SettingValue> { ["REPOAGENT_MODEL"] = new(SettingState.Disabled, null) }));
        clock.Advance(ControllerDashboard.HeartbeatInterval);
        await clock.Armed();
        Assert.Contains("\n- **Repository settings:** not read; the controller runs without --repository-settings\n", Published().Body);
    }

    [Fact]
    public async Task A_dashboard_write_refused_by_a_rate_limit_is_reported_on_the_dashboard_once_delivery_recovers()
    {
        var ct = TestContext.Current.CancellationToken;
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
            ("D1", new(Url(1)) { SessionId = "s1", Running = new("t1", Start.AddMinutes(-30)), Waiting = Waiting(1) }),
            ("D2", new(Url(2)) { SessionId = "s2", Pending = new("send", Start.AddMinutes(-1), ["DC_2"], 1) }),
            ("D3", new(Url(3)) { SessionId = "s3", Waiting = Waiting(1, new(2, Start.AddMinutes(5))) }),
            ("D4", new(Url(4)) { SessionId = "s4", Waiting = Waiting(2) }),
            ("D5", new(Url(5)) { SessionId = "s5" }));
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
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The dashboard owns and asynchronously disposes the inline BaselineReleases after its worker ends.")]
    public async Task Before_the_lifetime_is_ready_its_state_and_mode_are_published_and_a_turn_without_waiting_input_is_listed()
    {
        Store(("D1", new(Url(1)) { SessionId = "s1", Running = new("t1", Start.AddMinutes(-30)) }));
        using var starting = new ControllerStatus("owner/repository", "webhook", new InMemoryStatusRecord(), discussions, state, new InMemoryCheckoutWork(), clock);
        await using var dashboard = new ControllerDashboard(Category, starting, outbox, new BaselineReleases(new InMemoryBaselineReleases(),
            async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return null; }, clock, new RecordingLog(), text => text), clock, log, Redact);
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();

        Assert.Contains("\n- **Controller:** starting (webhook) since 2026-10-24T10:00:00Z\n", Published().Body);
        Assert.Equal($"- [Discussion #1]({Url(1)}): running a turn since 2026-10-24T09:30:00Z", Listed());
    }

    [Fact]
    public async Task Idle_sessions_are_not_listed_and_at_most_20_busy_ones_are_with_only_GitHub_Discussion_links()
    {
        Store(("D0", new(Url(1)) { SessionId = "s0" }));
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();
        Assert.Equal("None.", Listed());

        Store([.. Enumerable.Range(1, 22).Select(number => ($"D{number}", new DiscussionRecord(number == 1 ? "javascript:alert(1)" : Url(number)) { Waiting = Waiting(1) }))]);
        clock.Advance(ControllerDashboard.HeartbeatInterval);
        await clock.Armed();
        var listed = Listed().Split('\n');
        Assert.Equal(21, listed.Length);
        Assert.Equal("- an unlinked Discussion: 1 inputs waiting for dispatch", listed[0]);
        Assert.Equal($"- [Discussion #20]({Url(20)}): 1 inputs waiting for dispatch", listed[19]);
        Assert.Equal("- and 2 more", listed[20]);
    }

    // An earlier lifetime's failures and latest activity in forms no writer leaves, as the status record file reads them: an
    // object is shown with the text fields that read from it, and anything else is not shown. Where a field was not text, the
    // dashboard's JSON rendering threw instead, so it published nothing that lifetime and its stop failed.
    [Theory]
    [InlineData("""{"failures":[{"at":5,"kind":"poll-error","message":"m"}]}""", "none recorded", "- an unknown time `poll-error`: `m`")]
    [InlineData("""{"failures":[{"at":"x","kind":true,"message":"m"}]}""", "none recorded", "- an unknown time (none): `m`")]
    [InlineData("""{"failures":[{"message":{"a":1}}]}""", "none recorded", "- an unknown time (none): (none)")]
    [InlineData("""{"failures":[{"kind":7,"message":"m"}]}""", "none recorded", "- an unknown time (none): `m`")]
    [InlineData("""{"failures":[{"at":"2026-10-24T09:00:00.0000000+00:00","kind":"poll-error","message":"m"},5,{},{"at":5},{"at":"2026-10-24T09:00:00.0000000+00:00","kind":"poll-error","message":"n"}]}""",
        "none recorded", "- 2026-10-24T09:00:00Z `poll-error`: `n`\n- an unknown time (none): (none)\n- an unknown time (none): (none)\n- 2026-10-24T09:00:00Z `poll-error`: `m`")]
    [InlineData("""{"latestActivity":5}""", "none recorded", "None recorded.")]
    [InlineData("""{"latestActivity":"text"}""", "none recorded", "None recorded.")]
    [InlineData("""{"latestActivity":[]}""", "none recorded", "None recorded.")]
    [InlineData("""{"latestActivity":{"at":"2026-10-24T09:00:00.0000000+00:00","kind":"session-created","message":"m","url":5}}""",
        "session started for an unlinked Discussion at 2026-10-24T09:00:00Z", "None recorded.")]
    [InlineData("""{"latestActivity":{"kind":"forwarded","at":7}}""", "input delivered from an unlinked Discussion at an unknown time", "None recorded.")]
    // Forms the JSON rendering also showed so.
    [InlineData("""{"failures":[{}]}""", "none recorded", "- an unknown time (none): (none)")]
    [InlineData("""{"failures":[{"url":"https://github.com/owner/repository/discussions/1"}]}""", "none recorded", "- an unknown time (none): (none)")]
    [InlineData("""{"failures":[5]}""", "none recorded", "None recorded.")]
    [InlineData("""{"latestActivity":{}}""", "input delivered from an unlinked Discussion at an unknown time", "None recorded.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The dashboard owns and asynchronously disposes the inline BaselineReleases after its worker ends.")]
    public async Task Damaged_failures_and_latest_activity_carried_from_an_earlier_lifetime_are_shown_from_what_reads_from_them(string record, string activity, string failures)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(record)!.AsObject();
        json["version"] = 1;
        json["repository"] = "owner/repository";
        using var restarted = new ControllerStatus("owner/repository", "polling", new InMemoryStatusRecord { Recorded = OperatorStatusJson.ReadRecord(json) },
            discussions, state, new InMemoryCheckoutWork(), clock);
        await using (var dashboard = new ControllerDashboard(Category, restarted, outbox, new BaselineReleases(new InMemoryBaselineReleases(),
            async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return null; }, clock, new RecordingLog(), text => text), clock, log, Redact))
        {
            dashboard.Start(TestContext.Current.CancellationToken);
            await clock.Armed();
            Assert.Contains($"\n- **Latest activity:** {activity}\n", Published().Body);
            Assert.EndsWith($"\n### Recent failures\n{failures}\n", Published().Body);
        }
        Assert.EndsWith($"\n### Recent failures\n{failures}\n", Assert.Single(github.Dashboards).Body);
    }

    static string Url(int number) => $"https://github.com/owner/repository/discussions/{number}";

    // The dashboard's session list, from the last publication handed to the outbox unless another body is given.
    string Listed(string? body = null) => (body ?? Published().Body!).Split("\n### Active and queued sessions\n")[1].Split("\n\n### Recent failures\n")[0];

    // Discussion records as the bridge saves them in the Discussion state the status reads.
    void Store(params (string Id, DiscussionRecord Record)[] records)
    {
        var saved = discussions.Saved ?? new DiscussionState("owner/repository");
        foreach (var (id, record) in records) saved.Discussions[id] = record;
        discussions.WriteDiscussions(saved);
    }

    // Observed input waiting for its session, as the bridge queues it; a failed delivery also schedules its retry there.
    static WaitingInput Waiting(int inputs, DeliveryRetry? retry = null)
    {
        var waiting = new WaitingInput("{}") { Retry = retry };
        waiting.Inputs.AddRange(Enumerable.Range(1, inputs).Select(index => new ObservedInput(Start, $$"""{"id":"DC_{{index}}"}""", null)));
        return waiting;
    }

    void Limit(int sessions) => state.WriteSettings(new(new Dictionary<string, SettingValue>
    {
        [Bridge.ActiveSessionSetting] = new(SettingState.Configured, sessions.ToString(CultureInfo.InvariantCulture)) { Since = Start }
    }));

    void Record(string kind, string message) => status.Record(new(clock.GetUtcNow(), kind, message));

    public async ValueTask DisposeAsync()
    {
        await outbox.DisposeAsync();
        status.Dispose();
    }
}
