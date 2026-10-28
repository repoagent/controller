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
    const string Category = "Controller status", Title = "💓 24 Oct 2026 10:00 UTC · 0/4 active · 0 queued — RepoAgent", Attention = "🔴 GitHub authentication failed · 💓 24 Oct 2026 10:00 UTC — RepoAgent";
    const string First = "https://github.com/owner/repository/discussions/1";
    static readonly DateTimeOffset Start = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
    readonly SteppedClock clock = new(Start);
    readonly InMemoryDiscussions github = new();
    // The settings and the outbox, as the controller's state files hold both.
    readonly InMemoryControllerState state = new();
    readonly InMemoryDiscussionStore discussions = new();
    readonly RecordingLog log = new();
    readonly InMemoryCheckoutWork checkout = new();
    readonly ControllerStatus status;
    readonly GitHubOutbox outbox;

    public ControllerDashboardPublicationTests()
    {
        status = new("owner/repository", "polling", new InMemoryStatusRecord(), discussions, state, checkout, clock);
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2000", Justification = "ControllerDashboard owns and asynchronously disposes its BaselineReleaseCheck.")]
    ControllerDashboard Dashboard(BaselineReleaseCheck? releases = null, IControllerLog? reported = null) => new(Category, status, outbox,
            releases ?? new BaselineReleaseCheck(new InMemoryBaselineReleases(), async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return null; }, clock, new RecordingLog(), text => text),
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Usage_is_invalidated_immediately_when_the_runtime_or_account_is_replaced(bool account)
    {
        var runtime = new FakeAgentRuntime
        {
            Usage = _ => Task.FromResult(new AgentUsageObservation(AgentUsageAvailability.Available,
                [new("account", null, null, null, [new("primary", 100, 300, null)])])
            { AccountKey = "old-owner" })
        };
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken, runtime);
        Assert.Contains("Usage exhausted", Published().Title);
        if (account) runtime.AccountChanged();
        else runtime.Disconnect();
        Record("app-token-refreshed", "token renewed");
        await UsagePublication(publication => publication.Body.Contains("**GitHub authentication:** healthy", StringComparison.Ordinal));
        Assert.DoesNotContain("Usage exhausted", Published().Title);
        Assert.Contains("stale", Published().Body);
        Assert.Contains("0% remaining", Published().Body);
        Assert.DoesNotContain("old-owner", Published().Body);
        runtime.Usage = _ => Task.FromResult(new AgentUsageObservation(AgentUsageAvailability.Available,
            [new("account", null, null, null, [new("primary", 1, 300, null)])])
        { AccountKey = "new-owner" });
        clock.Advance(TimeSpan.FromMinutes(1));
        await UsagePublication(publication => publication.Body.Contains("99% remaining", StringComparison.Ordinal));
        Assert.DoesNotContain("stale", Published().Body);
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Usage_does_not_claim_exhaustion_when_the_provider_permits_use_or_reports_credits(bool ordinary, bool credits)
    {
        var runtime = new FakeAgentRuntime
        {
            Usage = _ => Task.FromResult(new AgentUsageObservation(AgentUsageAvailability.Available,
                [new("account", null, null, null, [new("primary", 100, 300, null)]) { AdditionalUsageAvailable = credits }])
            { OrdinaryUsageAllowed = ordinary ? true : null })
        };
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken, runtime);
        Assert.DoesNotContain("Usage exhausted", Published().Title);
        Assert.Contains("0% remaining", Published().Body);
        if (credits) Assert.Contains("additional credits available", Published().Body);
    }

    [Fact]
    public async Task Usage_discards_a_read_from_the_previous_account_context()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRead = new TaskCompletionSource<AgentUsageObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeAgentRuntime
        {
            Usage = _ => Task.FromResult(new AgentUsageObservation(AgentUsageAvailability.Available,
                [new("account", null, null, null, [new("primary", 20, 300, null)])]))
        };
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken, runtime);
        runtime.Usage = _ => { entered.SetResult(); return oldRead.Task; };
        clock.Advance(TimeSpan.FromMinutes(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        runtime.AccountChanged();
        oldRead.SetResult(new(AgentUsageAvailability.Available,
            [new("old account", null, null, null, [new("primary", 100, 300, null)])]));
        while (clock.Delays.Count(delay => delay == TimeSpan.FromMinutes(1)) < 2) await clock.Armed();
        Record("app-token-refreshed", "token renewed");
        await UsagePublication(publication => publication.Body.Contains("**GitHub authentication:** healthy", StringComparison.Ordinal));
        Assert.Contains("80% remaining", Published().Body);
        Assert.DoesNotContain("old account", Published().Body);
        Assert.DoesNotContain("Usage exhausted", Published().Title);
        Assert.Contains("stale", Published().Body);
    }

    [Fact]
    public async Task Usage_read_timeout_marks_last_known_data_stale_without_affecting_agent_work()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource<AgentUsageObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeAgentRuntime
        {
            Usage = _ => Task.FromResult(new AgentUsageObservation(AgentUsageAvailability.Available,
                [new("account", null, null, null, [new("primary", 100, 300, null)])]))
        };
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken, runtime);
        runtime.Usage = _ => { entered.SetResult(); return held.Task; };
        clock.Advance(TimeSpan.FromMinutes(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(10));
        await UsagePublication(publication => publication.Body.Contains("Usage refresh failed", StringComparison.Ordinal));
        Assert.DoesNotContain("Usage exhausted", Published().Title);
        Assert.Contains("0% remaining", Published().Body);
        Assert.Equal("unknown", status.Snapshot().CodexAuthentication.State);
        Assert.Empty(runtime.Requests);
    }
    [Fact]
    public async Task Usage_metadata_is_redacted_and_kept_literal_without_publishing_private_identity()
    {
        var runtime = new FakeAgentRuntime
        {
            Usage = _ => Task.FromResult(new AgentUsageObservation(AgentUsageAvailability.Available,
                [new("meter", "ghs_secret <details> @someone", "`model`", null, [new("window", 1, null, null)])])
            { AccountKey = "private-account" })
        };
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken, runtime);
        Assert.DoesNotContain("ghs_secret", Published().Body);
        Assert.DoesNotContain("private-account", Published().Body);
        Assert.Contains("`<redacted> <details> @someone`", Published().Body);
        Assert.Contains("`'model'`", Published().Body);
    }

    [Theory]
    [InlineData(AgentUsageAvailability.Unsupported, "Usage unsupported")]
    [InlineData(AgentUsageAvailability.Unavailable, "Usage unavailable")]
    public async Task Usage_absence_is_distinct_from_known_zero_without_failing_authentication(AgentUsageAvailability availability, string label)
    {
        var runtime = new FakeAgentRuntime { Usage = _ => Task.FromResult(new AgentUsageObservation(availability, [])) };
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken, runtime);
        Assert.Contains(label, Published().Body);
        Assert.DoesNotContain("0% remaining", Published().Body);
        Assert.DoesNotContain("Usage exhausted", Published().Title);
        Assert.Equal("unknown", status.Snapshot().CodexAuthentication.State);
        Assert.Empty(runtime.Requests);
    }
    [Fact]
    public async Task Usage_expires_by_observation_time_while_a_new_read_is_pending()
    {
        var reads = 0;
        var pending = new TaskCompletionSource<AgentUsageObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeAgentRuntime
        {
            Usage = _ => ++reads == 1 ? Task.FromResult(new AgentUsageObservation(AgentUsageAvailability.Available,
                [new("account", null, null, null, [new("primary", 100, 300, null)])])) : pending.Task
        };
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken, runtime);
        clock.Advance(TimeSpan.FromMinutes(2));
        await UsagePublication(publication => publication.Body.Contains("two-minute freshness budget", StringComparison.Ordinal));
        Assert.DoesNotContain("Usage exhausted", Published().Title);
        Assert.Contains("Observed at 2026-10-24T10:00:00Z", Published().Body);
        Assert.Equal("unknown", status.Snapshot().CodexAuthentication.State);
        Assert.Empty(runtime.Requests);
    }
    [Fact]
    public async Task Usage_refresh_failure_keeps_last_known_values_stale_and_recovers_without_authentication_failure()
    {
        var runtime = new FakeAgentRuntime
        {
            Usage = _ => Task.FromResult(new AgentUsageObservation(AgentUsageAvailability.Available,
                [new("account", null, null, null, [new("primary", 100, 300, null)])]))
        };
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken, runtime);
        Assert.Contains("Usage exhausted", Published().Title);
        runtime.Usage = _ => Task.FromException<AgentUsageObservation>(new IOException("private provider failure"));
        clock.Advance(TimeSpan.FromMinutes(1));
        await UsagePublication(publication => publication.Body.Contains("Usage refresh failed", StringComparison.Ordinal));
        Assert.DoesNotContain("Usage exhausted", Published().Title);
        Assert.Contains("0% remaining", Published().Body);
        Assert.Contains("stale", Published().Body);
        Assert.DoesNotContain("private provider failure", Published().Body);
        Assert.Equal("unknown", status.Snapshot().CodexAuthentication.State);

        runtime.Usage = _ => Task.FromResult(new AgentUsageObservation(AgentUsageAvailability.Available,
            [new("account", null, null, null, [new("primary", 99, 300, null)])]));
        clock.Advance(TimeSpan.FromMinutes(1));
        await UsagePublication(publication => publication.Body.Contains("1% remaining", StringComparison.Ordinal));
        Assert.DoesNotContain("Usage exhausted", Published().Title);
        Assert.DoesNotContain("last-known values are stale", Published().Body);
    }
    [Fact]
    public async Task Usage_exhaustion_is_current_evidence_below_authentication_admission_and_stop()
    {
        var runtime = new FakeAgentRuntime
        {
            Usage = _ => Task.FromResult(new AgentUsageObservation(AgentUsageAvailability.Available,
                [new("account", null, null, null, [new("primary", 100, 300, Start.AddHours(1))])]))
        };
        await using (var dashboard = Dashboard())
        {
            dashboard.Start(TestContext.Current.CancellationToken, runtime);
            Assert.StartsWith("🟠 Usage exhausted · resets 24 Oct 2026 11:00 UTC", Published().Title);
            Assert.Contains("0% remaining", Published().Body);
            Record("codex-auth-failed", "credentials rejected");
            await UsagePublication(publication => publication.Title.Contains("Codex authentication failed", StringComparison.Ordinal));
            Record("codex-auth-verified", "credentials verified");
            await UsagePublication(publication => publication.Title.Contains("Usage exhausted", StringComparison.Ordinal));
            checkout.Work = new(new HashSet<string>(), true);
            Record("input-waiting", "checkout admission changed");
            await UsagePublication(publication => publication.Title.Contains("Dispatch blocked", StringComparison.Ordinal));
            Assert.Contains("0% remaining", Published().Body);
        }
        Assert.StartsWith("⏹ Stopped", Assert.Single(github.Dashboards).Title);
    }

    async Task UsagePublication(Func<DashboardOperation, bool> expected)
    {
        while (!expected(Published())) await clock.Armed();
    }
    [Fact]
    public async Task Usage_reports_all_buckets_as_remaining_capacity_and_preserves_scope_and_reset()
    {
        var runtime = new FakeAgentRuntime
        {
            Usage = _ => Task.FromResult(new AgentUsageObservation(AgentUsageAvailability.Available,
            [new("meter-a", "Shared allowance", "model-a", "pro", [new("primary", 28, 300, Start.AddHours(2))]),
             new("meter-b", "Other allowance", "model-b", null, [new("secondary", 82, 10080, null)])])
            { AccountKey = "private-account-id" })
        };
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken, runtime);
        await clock.Armed();

        Assert.Contains("72% remaining", Published().Body);
        Assert.Contains("18% remaining", Published().Body);
        Assert.Contains("`model-a`", Published().Body);
        Assert.Contains("2026-10-24T12:00:00Z", Published().Body);
        Assert.Contains("Observed at 2026-10-24T10:00:00Z", Published().Body);
        Assert.DoesNotContain("private-account-id", Published().Body);
        Assert.Empty(runtime.Requests);
    }
    [Fact]
    public async Task Usage_without_a_runtime_is_explicitly_unavailable_not_zero()
    {
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();

        Assert.Contains("### Agent account usage", Published().Body);
        Assert.Contains("Usage unavailable", Published().Body);
        Assert.DoesNotContain("Usage exhausted", Published().Title);
    }
    [Fact]
    public async Task Heartbeat_leads_the_overview_and_title_without_claiming_indefinite_availability()
    {
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();

        Assert.Equal("💓 24 Oct 2026 10:00 UTC · 0/4 active · 0 queued — RepoAgent", Published().Title);
        Assert.StartsWith(AppIdentity.DashboardMarker + "\n**Last heartbeat:** 2026-10-24T10:00:00Z", Published().Body);
        Assert.Contains("more than two minutes", Published().Body);
        Assert.Contains("failure to publish", Published().Body);
        Assert.DoesNotContain("**Controller:** running", Published().Body);
    }

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
                **Last heartbeat:** 2026-10-24T10:00:00Z. Refreshed about every 30 seconds while the controller runs, even when idle.
                Treat more than two minutes without a heartbeat as offline. A failure to publish can produce the same symptom; GitHub cannot update this frozen status automatically.

                - **Controller:** `polling`; lifetime started at {Started}
                ### Work capacity
                - **Sessions:** 0 stored; 0 of 4 turns running (active-session limit); 0 awaiting agent acknowledgement; 0 inputs waiting

                ### Authentication and settings
                - **GitHub authentication:** healthy
                - **Codex authentication:** unknown
                - **Repository settings:** not read; the controller runs without --repository-settings
                - **Latest activity:** input delivered from [a comment on Discussion #1]({First}#discussioncomment-7) at 2026-10-24T10:00:00Z

                ### Updates
                The baseline release check has not completed yet.

                ### Agent account usage
                Usage unavailable; no agent account observation has been collected.

                ### Tracked Discussions
                None.

                ### Historical failures
                None recorded.

                """.ReplaceLineEndings("\n"), Published().Body);

            // Nothing changes; the heartbeat alone republishes, once.
            var writes = state.OutboxWrites;
            clock.Advance(TimeSpan.FromSeconds(29));
            clock.Advance(TimeSpan.FromSeconds(1));
            await clock.Armed();
            Assert.Equal(writes + 1, state.OutboxWrites);
            Assert.Contains("\n**Last heartbeat:** 2026-10-24T10:00:30Z. Refreshed about every 30 seconds", Published().Body);

            // A status change republishes at once, between heartbeats.
            clock.Advance(TimeSpan.FromSeconds(10));
            status.Record(new(Start.AddSeconds(40), "session-created", $"{First.Replace("/1", "/2")} session=s2"));
            await clock.Armed();
            Assert.Contains($"\n- **Latest activity:** session started for [Discussion #2]({First.Replace("/1", "/2")}) at 2026-10-24T10:00:40Z\n", Published().Body);
            Assert.Contains("\n**Last heartbeat:** 2026-10-24T10:00:40Z.", Published().Body);
            Assert.Empty(github.Dashboards); // Delivery is the outbox's, which this test does not start.
            clock.Advance(TimeSpan.FromSeconds(5));
        }

        // An operator stop publishes the stop with its time, claims no heartbeat and is delivered before disposal completes.
        var stopped = Assert.Single(github.Dashboards);
        Assert.Equal("⏹ Stopped · 💓 24 Oct 2026 10:00 UTC — RepoAgent", stopped.Title);
        Assert.Contains($"\n- **Controller:** stopped at 2026-10-24T10:00:45Z after a graceful shutdown; it had started at {Started}\n", stopped.Body);
        Assert.Contains("\n- **Sessions:** 0 stored; no turns run while the controller is stopped; 0 awaiting agent acknowledgement; 0 inputs waiting\n", stopped.Body);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2000", Justification = "The dashboard owns and disposes the release check.")]
    public async Task A_known_legacy_image_reaches_the_dashboard_as_migration_guidance_without_losing_its_identity()
    {
        var published = new InMemoryBaselineReleases();
        var legacy = Release(999, image: "running");
        var semantic = Release(1) with { Tag = "controller-v1.0.0" };
        published.Publish(legacy);
        published.Publish(semantic);
        var releases = new BaselineReleases(published, _ => Task.FromResult<string?>(Inventory("running")), clock, log, text => text);
        await using var dashboard = Dashboard(releases);
        dashboard.Start(TestContext.Current.CancellationToken);

        var body = await Naming(semantic);

        Assert.Contains("**Legacy release migration**", body);
        Assert.Contains(legacy.Url, body);
        Assert.DoesNotContain("**Release identity uncertain**", body);
        Assert.DoesNotContain("**Update available:**", body);
    }
    // The controller's options reaching the check is the composition's; BaselineReleaseCheckTests owns the check's decisions.
    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2000", Justification = "The dashboard takes ownership of releases and disposes it after its worker ends.")]
    public async Task Newer_baseline_release_reaches_the_dashboard_after_a_later_hourly_check()
    {
        var published = new InMemoryBaselineReleases();
        var current = Release(101, image: "running");
        published.Publish(current);
        var releases = new BaselineReleaseCheck(published, _ => Task.FromResult<string?>(Inventory("running")), clock, new RecordingLog(), text => text);
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
        Assert.Contains("\n- 2026-10-24T10:00:00Z `dashboard-error`: `No space left on device; publication is retried at the next heartbeat`\n", Published().Body);
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
        Assert.Matches(@"\n- \*\*GitHub authentication:\*\* failed at \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ: `GitHub App credential renewal failed: HTTP 401 Bad credentials <redacted>`\. " +
            @"Check the controller's approval and credentials, then restart it\.\n- \*\*Codex authentication:\*\* unknown\n", failed.Body);

        Record("app-token-refreshed", "installation=234");
        await clock.Armed();
        var recovered = Published();
        Assert.Equal(Title, recovered.Title);
        Assert.Contains("\n- **GitHub authentication:** healthy\n", recovered.Body);
        Assert.Matches(@"- \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ `app-refresh-error`: `GitHub App credential renewal failed: HTTP 401 Bad credentials <redacted>`\n",
            recovered.Body);

        // The Codex account's credentials hold every turn, so they need the operator too; the App stays healthy.
        Record("codex-auth-failed", "turn failed: unexpected status 401 Unauthorized");
        await clock.Armed();
        var codex = Published();
        Assert.Equal("🔴 Codex authentication failed · 💓 24 Oct 2026 10:00 UTC — RepoAgent", codex.Title);
        Assert.Matches(@"\n- \*\*GitHub authentication:\*\* healthy\n- \*\*Codex authentication:\*\* failed at \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ: `turn failed: unexpected status 401 Unauthorized`\. " +
            @"New Discussion input is held until the Codex account credentials are replaced\.\n", codex.Body);
        Assert.Contains("`codex-auth-failed`: `turn failed: unexpected status 401 Unauthorized`\n", codex.Body);

        Record("codex-auth-supplied", "account credentials replaced");
        await clock.Armed();
        Assert.Equal(Title, Published().Title);
        Assert.Contains("\n- **Codex authentication:** supplied credentials installed; not yet verified by a completed turn\n", Published().Body);
    }

    [Fact]
    public async Task Repeated_historical_failures_have_a_compact_count_and_every_retained_event_in_expandable_details()
    {
        Record("poll-error", First + " request failed");
        clock.Advance(TimeSpan.FromSeconds(10));
        Record("poll-error", First + " request failed");
        clock.Advance(TimeSpan.FromSeconds(10));
        Record("poll-error", Url(2) + " request failed");
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();

        var publication = Published();
        Assert.Equal(Title, publication.Title);
        Assert.Contains("### Historical failures\nHistorical evidence does not establish recovery", publication.Body);
        Assert.Contains("**2 occurrences; latest 2026-10-24T10:00:10Z** `poll-error`", publication.Body);
        Assert.Contains("**1 occurrence; latest 2026-10-24T10:00:20Z** `poll-error`", publication.Body);
        Assert.Contains("[Discussion #1](" + First + ")", publication.Body);
        var details = publication.Body.Split("<summary>All 3 retained failure events</summary>\n\n")[1].Split("\n</details>")[0];
        Assert.Contains("- 2026-10-24T10:00:00Z `poll-error`", details);
        Assert.Contains("- 2026-10-24T10:00:10Z `poll-error`", details);
        Assert.Contains("- 2026-10-24T10:00:20Z `poll-error`", details);
        Assert.Equal(3, details.Split('\n').Count(line => line.StartsWith("- ", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(350, false)]
    [InlineData(2100, true)]
    public async Task Historical_details_disclose_truncation_and_keep_hostile_text_and_known_secrets_protected(int length, bool retainedTruncated)
    {
        Record("poll-error", First + " [hostile](javascript:alert(1)) </summary> `escape` ghs_secret " + new string('x', length));
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();

        var body = Published().Body;
        Assert.DoesNotContain("ghs_secret", body);
        Assert.Contains("[Discussion #1](" + First + ")", body);
        Assert.Contains("(dashboard text truncated at 300 characters)", body);
        Assert.Equal(retainedTruncated, body!.Contains("(retained status text was truncated)", StringComparison.Ordinal));
        Assert.Contains("`https://github.com/owner/repository/discussions/1 [hostile](javascript:alert(1)) </summary> 'escape' <redacted>", body);
        Assert.Single(Regex.Matches(body!, "(?m)^</details>$"));
        Assert.DoesNotContain("](javascript:", Regex.Replace(body!, "`[^`]*`", ""));
    }

    [Fact]
    public async Task Historical_failure_text_does_not_invent_links_from_embedded_or_malformed_urls()
    {
        Record("poll-error", "fakehttps://github.com/owner/repository/discussions/91 https://evil.example/https://github.com/owner/repository/discussions/92 https://github.com/owner/repository/discussions/93/invalid");
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();

        Assert.DoesNotContain("[Discussion #91]", Published().Body);
        Assert.DoesNotContain("[Discussion #92]", Published().Body);
        Assert.DoesNotContain("[Discussion #93]", Published().Body);
    }

    [Fact]
    public async Task History_summaries_are_bounded_without_losing_the_twenty_retained_events()
    {
        for (var occurrence = 1; occurrence <= 23; occurrence++)
        {
            Record("poll-error", new string('x', 310) + occurrence.ToString(CultureInfo.InvariantCulture));
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();

        var body = Published().Body!;
        var summary = body.Split("### Historical failures\n")[1].Split("<details>")[0];
        Assert.Equal(10, Regex.Count(summary, "(?m)^- "));
        Assert.Contains("10 additional failure groups", summary);
        Assert.DoesNotContain("**2 occurrences", summary);
        var details = body.Split("<summary>All 20 retained failure events</summary>\n\n")[1].Split("\n</details>")[0];
        Assert.Equal(20, Regex.Count(details, "(?m)^- "));
        Assert.DoesNotContain("2026-10-24T10:00:02Z", details);
        for (var second = 3; second < 23; second++)
            Assert.Contains(Start.AddSeconds(second).ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture), details);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The dashboard owns the inline release checker and disposes it asynchronously.")]
    public async Task Earlier_lifetime_failures_remain_historical_without_claiming_checkout_recovery()
    {
        var previous = new InMemoryStatusRecord();
        using (var earlier = new ControllerStatus("owner/repository", "polling", previous, discussions, state, checkout, clock))
            earlier.Record(new(Start.AddMinutes(-1), "refresh-blocked", First + " checkout state uncertain"));
        clock.Advance(TimeSpan.FromMinutes(1));
        using var restarted = new ControllerStatus("owner/repository", "polling", previous, discussions, state, checkout, clock);
        restarted.Record(new(clock.GetUtcNow(), "app-token-refreshed", "token renewed"));
        await using var dashboard = new ControllerDashboard(Category, restarted, outbox,
            new BaselineReleaseCheck(new InMemoryBaselineReleases(), async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return null; }, clock, log, text => text),
            clock, log, Redact);
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();

        Assert.Equal("💓 24 Oct 2026 10:01 UTC · 0/4 active · 0 queued — RepoAgent", Published().Title);
        Assert.Contains("(before this controller lifetime)", Published().Body);
        Assert.Contains("Historical evidence does not establish recovery or a current blocker.", Published().Body);
        Assert.DoesNotContain("recovered", Published().Body);
        Assert.Contains("checkout state uncertain", Published().Body);
    }

    [Fact]
    public async Task Current_admission_block_precedes_work_but_authentication_and_known_stop_have_priority()
    {
        checkout.Work = new(new HashSet<string>(), true);
        Store(("D1", new(First) { SessionId = "s1", Waiting = Waiting(3) }));
        await using (var dashboard = Dashboard())
        {
            dashboard.Start(TestContext.Current.CancellationToken);
            await clock.Armed();
            Assert.Equal("🟠 Dispatch blocked · 3 queued · 💓 24 Oct 2026 10:00 UTC — RepoAgent", Published().Title);
            checkout.Work = new(new HashSet<string>(), false);
            Record("poll-error", "historical failure does not establish a current blocker");
            await clock.Armed();
            Assert.Equal("💓 24 Oct 2026 10:00 UTC · 0/4 active · 3 queued — RepoAgent", Published().Title);
            checkout.Work = new(new HashSet<string>(), true);
            Record("codex-auth-failed", "credentials rejected");
            await clock.Armed();
            Assert.StartsWith("🔴 Codex authentication failed", Published().Title);
            Record("app-refresh-error", "GitHub credential rejected");
            await clock.Armed();
            Assert.StartsWith("🔴 GitHub authentication failed", Published().Title);
            dashboard.Failed("shutdown after failure");
        }
        Assert.Equal("🔴 Stopped after failure · 💓 24 Oct 2026 10:00 UTC — RepoAgent", Assert.Single(github.Dashboards).Title);
    }

    [Fact]
    public async Task Title_counts_turns_and_waiting_inputs_separately_from_pending_and_stored_sessions_across_year_boundaries()
    {
        Store(("D1", new(First) { SessionId = "s1", Running = new("t1", Start), Waiting = Waiting(2) }),
            ("D2", new(Url(2)) { SessionId = "s2", Pending = new("send", Start, ["DC_2"], 1) }),
            ("D3", new(Url(3)) { SessionId = "s3" }));
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();
        Assert.Equal("💓 24 Oct 2026 10:00 UTC · 1/4 active · 2 queued — RepoAgent", Published().Title);
        Assert.Contains("3 stored; 1 of 4 turns running (active-session limit); 1 awaiting agent acknowledgement; 2 inputs waiting", Published().Body);
        clock.Advance(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero) - Start);
        await clock.Armed();
        Assert.StartsWith("💓 01 Jan 2027 00:00 UTC", Published().Title);
        Assert.Contains("**Last heartbeat:** 2027-01-01T00:00:00Z", Published().Body);
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
            """.ReplaceLineEndings("\n"), Published().Body);
        Assert.Equal(Title, Published().Title);

        state.WriteSettings(new(new Dictionary<string, SettingValue> { ["REPOAGENT_MODEL"] = new(SettingState.Configured, "gpt-5.5") { Since = Start } }));
        clock.Advance(ControllerDashboard.HeartbeatInterval);
        await clock.Armed();
        Assert.Contains("\n- **Repository settings:** read without problems\n", Published().Body);

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
        Assert.Contains("\n**Last heartbeat:** 2026-10-24T10:02:30Z.", recovered.Body);
        Assert.Contains($"`dashboard-error`: `{created.Url} attempt=1 RateLimited: You have exceeded a secondary rate limit.; retained for retry at 2026-10-24T10:02:30.0000000+00:00`",
            recovered.Body);
        Assert.Empty(state.Outbox!.Operations);
    }

    [Fact]
    public async Task Each_tracked_Discussion_is_listed_with_what_it_is_doing_and_the_most_specific_reason_its_input_waits()
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
            Assert.Contains("\n- **Sessions:** 5 stored; 1 of 1 turns running (active-session limit); 1 awaiting agent acknowledgement; 4 inputs waiting\n", Published().Body);
            Assert.Equal($"""
                - [Discussion #1]({Url(1)}): running a turn since 2026-10-24T09:30:00Z; 1 inputs waiting for that turn to finish
                - [Discussion #2]({Url(2)}): awaiting agent acknowledgement
                - [Discussion #3]({Url(3)}): delivery failed or was unacknowledged; attempt 3 at 2026-10-24T10:05:00Z; 1 inputs waiting
                - [Discussion #4]({Url(4)}): 2 inputs waiting for a free active-session slot
                - [Discussion #5]({Url(5)}): idle; no input waiting
                """.ReplaceLineEndings("\n"), Listed());

            // Rejected Codex credentials hold idle input; input behind a turn or a delivery keeps its own reason.
            status.Record(new(Start, "codex-auth-failed", "turn failed: unexpected status 401 Unauthorized"));
            await clock.Armed();
            Assert.Equal($"""
                - [Discussion #1]({Url(1)}): running a turn since 2026-10-24T09:30:00Z; 1 inputs waiting for that turn to finish
                - [Discussion #2]({Url(2)}): awaiting agent acknowledgement
                - [Discussion #3]({Url(3)}): delivery failed or was unacknowledged; attempt 3 at 2026-10-24T10:05:00Z; 1 inputs waiting
                - [Discussion #4]({Url(4)}): 2 inputs held until the Codex account credentials are replaced
                - [Discussion #5]({Url(5)}): idle; no input waiting
                """.ReplaceLineEndings("\n"), Listed());

            Limit(4);
            status.Record(new(Start, "codex-auth-verified", "turn completed"));
            await clock.Armed();
            Assert.Contains($"\n- [Discussion #4]({Url(4)}): 2 inputs waiting for dispatch\n", Listed());
            Limit(1);
        }

        // Turns end with the controller, so its stop claims no slot is awaited even at the limit.
        Assert.Equal($"""
            - [Discussion #1]({Url(1)}): its turn ended with the controller; 1 inputs waiting
            - [Discussion #2]({Url(2)}): awaiting agent acknowledgement
            - [Discussion #3]({Url(3)}): delivery failed or was unacknowledged; attempt 3 at 2026-10-24T10:05:00Z; 1 inputs waiting
            - [Discussion #4]({Url(4)}): 2 inputs waiting for dispatch
            - [Discussion #5]({Url(5)}): idle; no input waiting
            """.ReplaceLineEndings("\n"), Listed(Assert.Single(github.Dashboards).Body));
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The dashboard owns and asynchronously disposes the inline BaselineReleaseCheck after its worker ends.")]
    public async Task Before_the_lifetime_is_ready_its_state_and_mode_are_published_and_a_turn_without_waiting_input_is_listed()
    {
        Store(("D1", new(Url(1)) { SessionId = "s1", Running = new("t1", Start.AddMinutes(-30)) }));
        using var starting = new ControllerStatus("owner/repository", "webhook", new InMemoryStatusRecord(), discussions, state, checkout, clock);
        await using var dashboard = new ControllerDashboard(Category, starting, outbox, new BaselineReleaseCheck(new InMemoryBaselineReleases(),
            async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return null; }, clock, new RecordingLog(), text => text), clock, log, Redact);
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();

        Assert.Contains("\n- **Controller:** starting; `webhook`; lifetime started at 2026-10-24T10:00:00Z\n", Published().Body);
        Assert.Equal($"- [Discussion #1]({Url(1)}): running a turn since 2026-10-24T09:30:00Z", Listed());
    }

    [Fact]
    public async Task Idle_tracked_Discussions_are_linked_in_the_published_inventory()
    {
        Store(("D0", new(Url(1)) { SessionId = "s0" }));
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();
        Assert.Equal($"- [Discussion #1]({Url(1)}): idle; no input waiting", Listed());

        discussions.WriteDiscussions(new DiscussionState("owner/repository"));
        Store([.. Enumerable.Range(1, 22).Select(number => ($"D{number}", new DiscussionRecord(number == 1 ? "javascript:alert(1)" : Url(number)) { Waiting = Waiting(1) }))]);
        clock.Advance(ControllerDashboard.HeartbeatInterval);
        await clock.Armed();
        var listed = Listed().Split('\n');
        Assert.Equal(21, listed.Length);
        Assert.Equal($"- [Discussion #10]({Url(10)}): 1 inputs waiting for dispatch", listed[0]);
        Assert.Equal($"- [Discussion #8]({Url(8)}): 1 inputs waiting for dispatch", listed[19]);
        Assert.Equal("- and 2 more", listed[20]);
    }

    [Fact]
    public async Task Current_work_precedes_idle_inventory_in_a_deterministic_order_and_preserves_comment_activity()
    {
        Store(
            ("idle9", new(Url(9)) { SessionId = "s9" }),
            ("queued", new(Url(4)) { SessionId = "s4", Waiting = Waiting(1) }),
            ("pending", new(Url(2)) { SessionId = "s2", Pending = new("send", Start, ["DC_2"], 1) }),
            ("retry", new(Url(3)) { SessionId = "s3", Waiting = Waiting(1, new(2, Start.AddMinutes(5))) }),
            ("running", new(Url(1)) { SessionId = "s1", Running = new("t1", Start.AddMinutes(-30)) }),
            ("idle5", new(Url(5)) { SessionId = "s5" }));
        Record("forwarded", Url(4) + " session=s4 latest=" + Url(4) + "#discussioncomment-123");
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();
        Assert.Equal(["1", "2", "3", "4", "5", "9"],
            Regex.Matches(Listed(), @"\[Discussion #(\d+)\]").Select(match => match.Groups[1].Value));
        Assert.Contains($"[a comment on Discussion #4]({Url(4)}#discussioncomment-123)", Published().Body);
        Assert.DoesNotContain("recovered", Listed(), StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task Waiting_inventory_explains_checkout_admission_and_GitHub_credentials_without_claiming_idle_recovery()
    {
        Store(("D1", new(Url(1)) { SessionId = "s1", Waiting = Waiting(1) }));
        checkout.Work = new(new HashSet<string>(), true);
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();
        Assert.Equal($"- [Discussion #1]({Url(1)}): 1 inputs held for a checkout refresh", Listed());

        Record("app-refresh-error", "GitHub App credential renewal failed");
        await clock.Armed();
        Assert.Equal($"- [Discussion #1]({Url(1)}): 1 inputs held until GitHub authentication recovers", Listed());
    }
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://github.com/owner/repository/discussions/1)\n@participant")]
    [InlineData("https://github.com/owner/repository/discussions/not-a-number")]
    public async Task Malformed_stored_Discussion_links_are_unlinked_without_publishing_their_text(string url)
    {
        Store(("D1", new(url) { SessionId = "s1" }));
        await using var dashboard = Dashboard();
        dashboard.Start(TestContext.Current.CancellationToken);
        await clock.Armed();
        Assert.Equal("- an unlinked Discussion: idle; no input waiting", Listed());
        Assert.DoesNotContain(url, Published().Body, StringComparison.Ordinal);
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
    [InlineData("""{"failures":[{"url":"https://github.com/owner/repository/discussions/1"}]}""", "none recorded", "- an unknown time (none): (none) — [Discussion #1](https://github.com/owner/repository/discussions/1)")]
    [InlineData("""{"failures":[{"url":"https://github.com/ghs_secret/repository/discussions/1"}]}""", "none recorded", "- an unknown time (none): (none)")]
    [InlineData("""{"failures":[5]}""", "none recorded", "None recorded.")]
    [InlineData("""{"latestActivity":{}}""", "input delivered from an unlinked Discussion at an unknown time", "None recorded.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The dashboard owns and asynchronously disposes the inline BaselineReleaseCheck after its worker ends.")]
    public async Task Damaged_failures_and_latest_activity_carried_from_an_earlier_lifetime_are_shown_from_what_reads_from_them(string record, string activity, string failures)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(record)!.AsObject();
        json["version"] = 1;
        json["repository"] = "owner/repository";
        using var restarted = new ControllerStatus("owner/repository", "polling", new InMemoryStatusRecord { Recorded = OperatorStatusJson.ReadRecord(json) },
            discussions, state, checkout, clock);
        await using (var dashboard = new ControllerDashboard(Category, restarted, outbox, new BaselineReleaseCheck(new InMemoryBaselineReleases(),
            async ct => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return null; }, clock, new RecordingLog(), text => text), clock, log, Redact))
        {
            dashboard.Start(TestContext.Current.CancellationToken);
            await clock.Armed();
            Assert.Contains($"\n- **Latest activity:** {activity}\n", Published().Body);
            Assert.Equal(failures, RetainedFailureDetails(Published().Body!));
        }
        Assert.Equal(failures, RetainedFailureDetails(Assert.Single(github.Dashboards).Body));
    }

    static string RetainedFailureDetails(string body)
    {
        const string start = "</summary>\n\n";
        var index = body.IndexOf(start, StringComparison.Ordinal);
        return index < 0 ? "None recorded." : body[(index + start.Length)..].Split("\n\n</details>")[0]
            .Replace(" (before this controller lifetime)", "", StringComparison.Ordinal);
    }

    static string Url(int number) => $"https://github.com/owner/repository/discussions/{number}";

    // The dashboard's session list, from the last publication handed to the outbox unless another body is given.
    string Listed(string? body = null) => (body ?? Published().Body!).ReplaceLineEndings("\n").Split("\n### Tracked Discussions\n")[1].Split("\n\n### Historical failures\n")[0];

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
