using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RepoAgentController;

/// <summary>
/// Publishes this controller's nonsecret operator status to its owned dashboard Discussion through the GitHub outbox,
/// whenever status changes and as a heartbeat about every 30 seconds, including while idle. Only a stop this lifetime
/// handles publishes a final state; after an abrupt end the heartbeat goes stale instead of claiming a stop.
/// </summary>
public sealed class ControllerDashboard : IAsyncDisposable
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    // Bounded so shutdown cannot hang on GitHub; an undelivered stop stays retained and the next lifetime supersedes it.
    static readonly TimeSpan StopBudget = TimeSpan.FromSeconds(10);
    const int MaximumSessions = 20, MaximumFailures = 10, MaximumFailureEvents = 20, MaximumText = 300;
    readonly string category;
    readonly ControllerStatus status;
    readonly GitHubOutbox outbox;
    readonly BaselineReleaseCheck releases;
    readonly TimeProvider clock;
    readonly IControllerLog log;
    readonly Func<string, string> redact;
    readonly CancellationTokenSource stop = new();
    readonly WakeLoop loop;
    Task running = Task.CompletedTask;
    bool started;
    AgentUsageCollection? usage;
    string? failure;

    /// <param name="redact">Removes the secrets the controller knows from the failure that ends a lifetime, before it is published.</param>
    public ControllerDashboard(string category, ControllerStatus status, GitHubOutbox outbox, BaselineReleaseCheck releases, TimeProvider clock, IControllerLog log,
        Func<string, string> redact)
    {
        this.category = category;
        this.status = status;
        this.outbox = outbox;
        this.releases = releases;
        this.clock = clock;
        this.log = log;
        this.redact = redact;
        loop = new(clock);
        status.Changed += Wake;
        releases.Changed += Wake;
    }

    void Wake() => loop.Signal();

    /// <summary>Publishes the current status now, then on every change, release check and heartbeat until disposal.</summary>
    public void Start(CancellationToken ct, IAgentRuntime? runtime = null)
    {
        started = true;
        if (runtime is not null)
        {
            usage = new(runtime, clock);
            usage.Changed += Wake;
            usage.Start(ct);
        }
        // A failed publication waits for the heartbeat, so reporting that failure cannot spin this loop.
        running = loop.Run(CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token), _ => Task.FromResult((HeartbeatInterval, Publish(null))));
        releases.Start(ct);
    }

    /// <summary>Records that this lifetime is ending on an error rather than an operator stop; its stop publishes
    /// <paramref name="message"/> redacted.</summary>
    public void Failed(string message) => failure = redact(message);

    bool Publish((DateTimeOffset At, string? Failure)? stopped)
    {
        try
        {
            var snapshot = status.Snapshot();
            var now = clock.GetUtcNow();
            var accountUsage = usage?.Current;
            outbox.Dashboard(category, Title(snapshot, now, stopped, accountUsage), Render(snapshot, now, stopped, releases.Current, accountUsage));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log.Write("dashboard-error", $"{error.Message}; publication is retried at the next heartbeat");
            return false;
        }
    }

    // Year is always present so a frozen title remains unambiguous across year boundaries.
    static string Title(OperatorStatus snapshot, DateTimeOffset now, (DateTimeOffset At, string? Failure)? stopped, UsageSnapshot? usage)
    {
        var heartbeat = "💓 " + now.UtcDateTime.ToString("dd MMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var summary = stopped is { Failure: not null } ? "🔴 Stopped after failure"
            : stopped is not null ? "⏹ Stopped"
            : snapshot.Authentication.State == "failed" ? "🔴 GitHub authentication failed"
            : snapshot.CodexAuthentication.State == "failed" ? "🔴 Codex authentication failed"
            : snapshot.Work.AdmissionBlocked == true ? $"🟠 Dispatch blocked · {snapshot.Work.Waiting} queued"
            : usage?.Fresh(now) == true && usage.Exhausted ? "🟠 Usage exhausted" + (usage.Reset is { } reset ? " · resets " + reset.UtcDateTime.ToString("dd MMM yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture) : "")
            : null;
        return summary is null
            ? $"{heartbeat} · {snapshot.Work.Running}/{snapshot.Work.Limit} active · {snapshot.Work.Waiting} queued — RepoAgent"
            : $"{summary} · {heartbeat} — RepoAgent";
    }

    string Render(OperatorStatus snapshot, DateTimeOffset now, (DateTimeOffset At, string? Failure)? stopped, ReleaseNotice? release, UsageSnapshot? usage)
    {
        var text = new StringBuilder();
        text.AppendLine(AppIdentity.DashboardMarker);
        text.AppendLine(CultureInfo.InvariantCulture, $"**Last heartbeat:** {Time(now)}. Refreshed about every {HeartbeatInterval.TotalSeconds:F0} seconds while the controller runs, even when idle.");
        text.AppendLine("Treat more than two minutes without a heartbeat as offline. A failure to publish can produce the same symptom; GitHub cannot update this frozen status automatically.");
        text.AppendLine();
        var controller = snapshot.Controller;
        var startedAt = controller.StartedAt ?? "";
        if (stopped is { } end)
            text.AppendLine(end.Failure is null
                ? $"- **Controller:** stopped at {Time(end.At)} after a graceful shutdown; it had started at {Time(startedAt)}"
                : $"- **Controller:** stopped at {Time(end.At)} after a failure: {Code(end.Failure)}");
        else text.AppendLine(CultureInfo.InvariantCulture, $"- **Controller:** {(controller.State == "starting" ? "starting; " : "")}{Code(controller.Mode ?? "unknown mode")}; lifetime started at {Time(startedAt)}");
        text.AppendLine("### Work capacity");
        var work = snapshot.Work;
        var full = work.Running >= work.Limit;
        text.AppendLine($"- **Sessions:** {work.Sessions} stored; " +
            (stopped is null ? $"{work.Running} of {work.Limit} turns running (active-session limit)" : "no turns run while the controller is stopped") +
            $"; {work.Pending} awaiting native acknowledgement; {work.Waiting} inputs waiting" +
            (work.AdmissionBlocked == true ? "; new turns are held for a checkout refresh" : ""));
        text.AppendLine();
        text.AppendLine("### Authentication and settings");
        var authentication = snapshot.Authentication;
        text.AppendLine("- **GitHub authentication:** " + authentication.State switch
        {
            "degraded" => $"temporarily throttled at {Time(authentication.UpdatedAt ?? "")}: {Code(authentication.Error ?? "")}. Renewal retries automatically; an existing token is used only while valid.",
            "failed" => $"failed at {Time(authentication.UpdatedAt ?? "")}: {Code(authentication.Error ?? "")}. Check the controller's approval and credentials, then restart it.",
            "" => "unknown",
            var state => state
        });
        var codexAuthentication = snapshot.CodexAuthentication;
        var codexFailed = codexAuthentication.State == "failed";
        text.AppendLine("- **Codex authentication:** " + codexAuthentication.State switch
        {
            "failed" => $"failed at {Time(codexAuthentication.UpdatedAt ?? "")}: {Code(codexAuthentication.Error ?? "")}. New Discussion input is held until the Codex account credentials are replaced.",
            "supplied" => "supplied credentials installed; not yet verified by a completed turn",
            "" => "unknown",
            var state => state
        });
        Settings(text, snapshot.Settings);
        var activity = Listed(snapshot.LatestActivity);
        text.AppendLine("- **Latest activity:** " + (activity is null ? "none recorded" :
            $"{(activity.Kind == "session-created" ? "session started for" : "input delivered from")} {Link(activity.Url ?? "")} at {Time(activity.At)}"));
        text.AppendLine();
        Updates(text, release);
        text.AppendLine();
        Usage(text, usage, now, redact);
        text.AppendLine();
        text.AppendLine("### Tracked Discussions");
        var inventory = snapshot.Sessions.OrderBy(session => session.Activity switch
        {
            SessionActivity.Running => 0,
            SessionActivity.Pending => 1,
            SessionActivity.Retrying => 2,
            _ when session.Waiting != 0 => 3,
            _ => 4
        }).ThenBy(session => session.Discussion, StringComparer.Ordinal).ToArray();
        foreach (var session in inventory.Take(MaximumSessions))
        {
            var waiting = session.Waiting;
            string? doing = session.Activity switch
            {
                SessionActivity.Pending => "awaiting native acknowledgement",
                SessionActivity.Retrying retry => $"delivery failed or was unacknowledged; attempt {(retry.Attempts ?? 0) + 1} at {Time(retry.RetryAt ?? "")}",
                SessionActivity.Running turn => stopped is null ? $"running a turn since {Time(turn.Since ?? "")}" : "its turn ended with the controller",
                _ => session.Waiting == 0 ? "idle; no input waiting" : null
            };
            // Why idle input still waits, from the most specific cause status shows; others appear under recent failures.
            var reason = waiting == 0 ? null : session.Activity switch
            {
                SessionActivity.Running when stopped is null => $"{waiting} inputs waiting for that turn to finish",
                SessionActivity.Pending or SessionActivity.Retrying or SessionActivity.Running => $"{waiting} inputs waiting",
                _ when codexFailed => $"{waiting} inputs held until the Codex account credentials are replaced",
                _ when authentication.State == "failed" => $"{waiting} inputs held until GitHub authentication recovers",
                _ when work.AdmissionBlocked == true && stopped is null => $"{waiting} inputs held for a checkout refresh",
                _ when full && stopped is null => $"{waiting} inputs waiting for a free active-session slot",
                _ => $"{waiting} inputs waiting for dispatch"
            };
            text.AppendLine($"- {Link(session.Discussion)}: " + string.Join("; ", new[] { doing, reason }.OfType<string>()));
        }
        if (inventory.Length > MaximumSessions) text.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"- and {inventory.Length - MaximumSessions} more");
        if (inventory.Length == 0) text.AppendLine("None.");
        text.AppendLine();
        History(text, snapshot);

        return text.ToString().ReplaceLineEndings("\n");
    }

    static void Usage(StringBuilder text, UsageSnapshot? usage, DateTimeOffset now, Func<string, string> redact)
    {
        string Safe(string value) => Code(redact(value));
        text.AppendLine("### Agent account usage");
        if (usage?.Problem is { } problem) text.AppendLine(problem);
        else if (usage?.Observation is null) text.AppendLine("Usage unavailable; no agent account observation has been collected.");
        if (usage is { Problem: null, Observation: { Availability: AgentUsageAvailability.Available } } && !usage.Fresh(now)) text.AppendLine("Usage stale; last-known observation is outside the two-minute freshness budget.");
        if (usage?.Observation is { Availability: AgentUsageAvailability.Available } observation)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Observed at {Time(usage.ObservedAt!.Value)}. Account allowances can span repositories; they are separate from the active-session limit.");
            text.AppendLine("Ordinary included usage: " + (observation.OrdinaryUsageAllowed is { } allowed ? (allowed ? "allowed" : "not allowed") : "unknown") + ".");
            foreach (var bucket in observation.Buckets.Take(20))
            {
                text.AppendLine($"- **{Safe(bucket.Name ?? bucket.Id)}**" +
                    (bucket.Name is null || bucket.Name == bucket.Id ? "" : $"; meter {Safe(bucket.Id)}") +
                    (bucket.Model is null ? "" : $"; model {Safe(bucket.Model)}") + (bucket.Plan is null ? "" : $"; plan {Safe(bucket.Plan)}"));
                if (bucket.AdditionalUsageAvailable) text.AppendLine("  - Provider reports additional credits available; depleted included windows do not establish account exhaustion.");
                if (bucket.SpendControlReached == true) text.AppendLine("  - Provider reports spend control reached.");
                if (bucket.ReachedType is { } reason) text.AppendLine(CultureInfo.InvariantCulture, $"  - Provider limit reason: {Safe(reason)}.");
                foreach (var window in bucket.Windows.Take(3))
                    text.AppendLine(CultureInfo.InvariantCulture, $"  - {Safe(window.Label)}: {window.RemainingPercent:0.##}% remaining{(window.DurationMinutes is null ? "" : $" ({window.DurationMinutes} minutes)")}{(window.ResetsAt is { } reset ? $"; resets {Time(reset)}" : "; reset unknown")}");
            }
            if (observation.Buckets.Count > 20) text.AppendLine(CultureInfo.InvariantCulture, $"- {observation.Buckets.Count - 20} more usage buckets omitted.");
            if (observation.Buckets.Count == 0) text.AppendLine("Usage windows unavailable; no numeric capacity is known.");
        }
    }

    void History(StringBuilder text, OperatorStatus snapshot)
    {
        text.AppendLine("### Historical failures");
        var failures = snapshot.Failures.Select(Listed).OfType<StatusEvent>().TakeLast(MaximumFailureEvents)
            .Select(item => item with { Kind = redact(item.Kind), Message = redact(item.Message), Url = item.Url is null ? null : redact(item.Url) }).Reverse().ToArray();
        if (failures.Length == 0) { text.AppendLine("None recorded."); return; }
        text.AppendLine("Historical evidence does not establish recovery or a current blocker. Events before this controller lifetime are identified below.");
        var groups = failures.GroupBy(item => (Kind: Flat(item.Kind), Message: Flat(item.Message), Links: string.Join(" ", FailureLinks(item))))
            .Select(group => (group.Key, Events: group.ToArray(), Latest: group.Max(item => At(item.At))))
            .OrderByDescending(group => group.Latest).ThenBy(group => group.Key.Kind, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Message, StringComparer.Ordinal).ThenBy(group => group.Key.Links, StringComparer.Ordinal).ToArray();
        foreach (var group in groups.Take(MaximumFailures))
        {
            var latest = group.Latest == DateTimeOffset.MinValue ? "an unknown time" : Time(group.Latest);
            var count = group.Events.Length;
            text.AppendLine(CultureInfo.InvariantCulture, $"- **{count} {(count == 1 ? "occurrence" : "occurrences")}; latest {latest}** {Code(group.Key.Kind)}: {FailureMessage(group.Key.Message)}{Links(group.Events[0])}");
        }
        if (groups.Length > MaximumFailures)
        {
            var omitted = groups.Length - MaximumFailures;
            text.AppendLine(CultureInfo.InvariantCulture, $"{omitted} additional failure {(omitted == 1 ? "group appears" : "groups appear")} in the retained event details below.");
        }
        text.AppendLine();
        text.AppendLine("<details>");
        text.AppendLine(CultureInfo.InvariantCulture, $"<summary>All {failures.Length} retained failure {(failures.Length == 1 ? "event" : "events")}</summary>");
        text.AppendLine();
        foreach (var item in failures)
        {
            var at = At(item.At);
            var started = At(snapshot.Controller.StartedAt ?? "");
            var earlier = at != DateTimeOffset.MinValue && started != DateTimeOffset.MinValue && at < started
                ? " (before this controller lifetime)" : "";
            text.AppendLine(CultureInfo.InvariantCulture, $"- {Time(item.At)} {Code(item.Kind)}: {FailureMessage(item.Message)}{Links(item)}{earlier}");
        }
        text.AppendLine();
        text.AppendLine("</details>");
    }

    static DateTimeOffset At(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
        ? at : DateTimeOffset.MinValue;

    static string Flat(string value) => Regex.Replace(value, @"\s+", " ").Trim();

    static string FailureMessage(string message) => Code(message) +
        (Flat(message).Length > MaximumText ? $" (dashboard text truncated at {MaximumText} characters)" : "") +
        (message.EndsWith(" <truncated>", StringComparison.Ordinal) ? " (retained status text was truncated)" : "");

    static string[] FailureLinks(StatusEvent item) => Regex.Matches(Flat(item.Message),
            @"(?<![^\s(])https://github\.com/[\w.-]+/[\w.-]+/discussions/\d+(?:#discussioncomment-\d+)?(?=$|[\s),.;])")
        .Where(match => match.Index + match.Length <= MaximumText).Select(match => match.Value)
        .Concat(item.Url is { Length: <= MaximumText } url && Link(url) != "an unlinked Discussion" ? [url] : [])
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    static string Links(StatusEvent item) => FailureLinks(item) is { Length: > 0 } links ? " — " + string.Join(", ", links.Select(Link)) : "";

    // An event recorded in a form no writer leaves is shown with what reads from it, unless it is not an object at all.
    static StatusEvent? Listed(StatusEvent? item) => item is { AsRead: not null, AsReadIsObject: false } ? null : item;

    // Invalid and unreadable settings stay listed until a read succeeds; raw invalid values are never recorded.
    static void Settings(StringBuilder text, RecordedSettings? settings)
    {
        if (settings is null || settings.Settings.Values.All(setting => setting.State == SettingState.Disabled))
        {
            text.AppendLine("- **Repository settings:** not read; the controller runs without --repository-settings");
            return;
        }
        var problems = settings.Settings.Where(setting => setting.Value.State is SettingState.Invalid or SettingState.ReadFailed).ToArray();
        text.AppendLine("- **Repository settings:** " + (problems.Length == 0 ? "read without problems" : $"{problems.Length} unusable"));
        foreach (var (name, value) in problems)
        {
            text.AppendLine($"  - {Code(name)} {(value.State == SettingState.Invalid ? "is invalid" : "could not be read")}: {Code(value.Reason ?? "")}; " +
                (value.Value is null ? "no repository value applies" : $"the earlier value {Code(value.Value)} still applies"));
        }
    }

    // Only a published release that this controller's image identifies, and that a newer release supersedes, is offered.
    public static void Updates(StringBuilder text, ReleaseNotice? notice)
    {
        text.AppendLine("### Updates");
        if (notice is null) { text.AppendLine("The baseline release check has not completed yet."); return; }
        var at = Time(notice.CheckedAt);
        if (notice.Failure is { } failure)
        {
            text.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"The baseline release check failed at {at}: {Code(failure)}. No update is claimed; the next check is in about an hour.");
            return;
        }
        if (notice.Newest is not { } newest) { text.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"No published baseline release was found at {at}."); return; }
        if (notice.Uncertainty is not null || notice.Running is not { } running)
        {
            text.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"**Release identity uncertain** (checked at {at}): {notice.Uncertainty}. No replacement is suggested.");
            text.AppendLine($"- Newest published baseline release: {Release(newest)}, image {Image(newest)} with gh {Code(newest.Gh)} and Codex {Code(newest.Codex)}. " +
                "Compare it with this controller's image before deciding whether to rebuild or recreate it.");
            return;
        }
        if (notice.Newer == 0) { text.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"This controller's image matches {Release(running)}, the newest published baseline release at {at}."); return; }
        text.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"**Update available:** baseline release {Release(newest)} is newer than this controller's {Release(running)} ({notice.Newer} newer {(notice.Newer == 1 ? "release" : "releases")}; checked at {at}).");
        text.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"- Image {Image(newest)} with gh {Code(newest.Gh)} and Codex {Code(newest.Codex)}.");
        text.AppendLine("- Publication is not deployment: this controller keeps running its current image. It never replaces itself and has no Docker host or deployment access.");
        text.AppendLine("- To update, an operator recreates the controller from that image, or rebuilds an image derived from the baseline on it, keeping the controller's external configuration, credentials and session data; see " +
            $"[baseline releases](https://github.com/{BaselineReleaseCheck.Repository}/blob/{newest.Commit}/docs/ci-cd.md#baseline-releases).");
    }

    // Release fields are validated before use, so these links only reach repoagent/controller's release and package pages.
    static string Release(BaselineRelease release) => $"[{release.Tag}]({release.Url})";

    static string Image(BaselineRelease release) => $"`{release.Image}` ([package](https://github.com/{BaselineReleaseCheck.Repository}/pkgs/container/controller))";

    static string Time(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    static string Time(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? Time(at) : "an unknown time";

    // Only this repository's GitHub Discussion links are rendered as links.
    static string Link(string url) => Regex.Match(url, @"\Ahttps://github\.com/[\w.-]+/[\w.-]+/discussions/(\d+)(#discussioncomment-\d+)?\z") is { Success: true } match
        ? $"[{(match.Groups[2].Success ? "a comment on " : "")}Discussion #{match.Groups[1].Value}]({url})" : "an unlinked Discussion";

    // Code spans keep reported text literal: no mentions, links or markup reach the rendered dashboard.
    static string Code(string value)
    {
        var flat = Regex.Replace(value, @"\s+", " ").Replace('`', '\'').Trim();
        if (flat.Length > MaximumText) flat = flat[..MaximumText] + "...";
        return flat.Length == 0 ? "(none)" : $"`{flat}`";
    }

    public async ValueTask DisposeAsync()
    {
        status.Changed -= Wake;
        releases.Changed -= Wake;
        await stop.CancelAsync();
        await running;
        await releases.DisposeAsync();
        if (usage is not null) { usage.Changed -= Wake; await usage.DisposeAsync(); }
        if (started)
        {
            // Reached only when this lifetime ends under control; a killed controller leaves its last heartbeat to go stale.
            Publish((clock.GetUtcNow(), failure));
            using var budget = new CancellationTokenSource(StopBudget, clock);
            try { await outbox.Finish(budget.Token); }
            catch (Exception error) when (error is OperationCanceledException or IOException) { }
        }
        stop.Dispose();
    }
}
