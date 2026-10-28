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
    const int MaximumSessions = 20, MaximumFailures = 10, MaximumText = 300;
    const string Title = "RepoAgent controller status", AttentionTitle = "Attention: RepoAgent controller authentication failed";
    readonly string category;
    readonly ControllerStatus status;
    readonly GitHubOutbox outbox;
    readonly BaselineReleases releases;
    readonly TimeProvider clock;
    readonly IControllerLog log;
    readonly Func<string, string> redact;
    readonly CancellationTokenSource stop = new();
    readonly WakeLoop loop;
    Task running = Task.CompletedTask;
    bool started;
    string? failure;

    /// <param name="redact">Removes the secrets the controller knows from the failure that ends a lifetime, before it is published.</param>
    public ControllerDashboard(string category, ControllerStatus status, GitHubOutbox outbox, BaselineReleases releases, TimeProvider clock, IControllerLog log,
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
    public void Start(CancellationToken ct)
    {
        started = true;
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
            // Either credential failing needs the operator: the App's stops GitHub work, the Codex account's holds every turn.
            var attention = snapshot.Authentication.State == "failed" || snapshot.CodexAuthentication.State == "failed";
            outbox.Dashboard(category, attention ? AttentionTitle : Title, Render(snapshot, clock.GetUtcNow(), stopped, releases.Current));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log.Write("dashboard-error", $"{error.Message}; publication is retried at the next heartbeat");
            return false;
        }
    }

    static string Render(OperatorStatus snapshot, DateTimeOffset now, (DateTimeOffset At, string? Failure)? stopped, ReleaseNotice? release)
    {
        var text = new StringBuilder();
        text.AppendLine(AppIdentity.DashboardMarker);
        text.AppendLine("Operational status of this repository's RepoAgent controller. The controller replaces this text on every update; comments and reactions here are never delivered to the agent.");
        text.AppendLine();
        var controller = snapshot.Controller;
        var startedAt = controller.StartedAt ?? "";
        text.AppendLine("- **Controller:** " + stopped switch
        {
            null => $"{controller.State} ({controller.Mode}) since {Time(startedAt)}",
            { Failure: null } end => $"stopped at {Time(end.At)} after a graceful shutdown; it had started at {Time(startedAt)}",
            { } end => $"stopped at {Time(end.At)} after a failure: {Code(end.Failure!)}"
        });
        text.AppendLine(stopped is null
            ? $"- **Heartbeat:** {Time(now)}. Refreshed about every {HeartbeatInterval.TotalSeconds:F0} seconds while the controller runs, even when idle. An older heartbeat means this status is unconfirmed, not that a particular failure occurred."
            : "- **Heartbeat:** none while stopped.");
        var authentication = snapshot.Authentication;
        text.AppendLine("- **Authentication:** " + authentication.State switch
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
        var work = snapshot.Work;
        var full = work.Running >= work.Limit;
        text.AppendLine($"- **Sessions:** {work.Sessions} stored; " +
            (stopped is null ? $"{work.Running} of {work.Limit} turns running (active-session limit)" : "no turns run while the controller is stopped") +
            $"; {work.Pending} awaiting native acknowledgement; {work.Waiting} inputs waiting" +
            (work.AdmissionBlocked == true ? "; new turns are held for a checkout refresh" : ""));
        var activity = Listed(snapshot.LatestActivity);
        text.AppendLine("- **Latest activity:** " + (activity is null ? "none recorded" :
            $"{(activity.Kind == "session-created" ? "session started for" : "input delivered from")} {Link(activity.Url ?? "")} at {Time(activity.At)}"));
        text.AppendLine();
        Updates(text, release);
        text.AppendLine();
        text.AppendLine("### Active and queued sessions");
        var busy = snapshot.Sessions.Where(session => session.Activity is SessionActivity.Pending or SessionActivity.Running || session.Waiting != 0).ToArray();
        foreach (var session in busy.Take(MaximumSessions))
        {
            var waiting = session.Waiting;
            string? doing = session.Activity switch
            {
                SessionActivity.Pending => "awaiting native acknowledgement",
                SessionActivity.Retrying retry => $"delivery failed or was unacknowledged; attempt {(retry.Attempts ?? 0) + 1} at {Time(retry.RetryAt ?? "")}",
                SessionActivity.Running turn => stopped is null ? $"running a turn since {Time(turn.Since ?? "")}" : "its turn ended with the controller",
                _ => null
            };
            // Why idle input still waits, from the most specific cause status shows; others appear under recent failures.
            var reason = waiting == 0 ? null : session.Activity switch
            {
                SessionActivity.Running when stopped is null => $"{waiting} inputs waiting for that turn to finish",
                SessionActivity.Pending or SessionActivity.Retrying or SessionActivity.Running => $"{waiting} inputs waiting",
                _ when codexFailed => $"{waiting} inputs held until the Codex account credentials are replaced",
                _ when full && stopped is null => $"{waiting} inputs waiting for a free active-session slot",
                _ => $"{waiting} inputs waiting for dispatch"
            };
            text.AppendLine($"- {Link(session.Discussion)}: " + string.Join("; ", new[] { doing, reason }.OfType<string>()));
        }
        if (busy.Length > MaximumSessions) text.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"- and {busy.Length - MaximumSessions} more");
        if (busy.Length == 0) text.AppendLine("None.");
        text.AppendLine();
        text.AppendLine("### Recent failures");
        var failures = snapshot.Failures.Select(Listed).OfType<StatusEvent>().Reverse().Take(MaximumFailures).ToArray();
        foreach (var item in failures) text.AppendLine(System.Globalization.CultureInfo.CurrentCulture, $"- {Time(item.At)} {Code(item.Kind)}: {Code(item.Message)}");
        if (failures.Length == 0) text.AppendLine("None recorded.");
        return text.ToString();
    }

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
            $"[baseline releases](https://github.com/{BaselineReleases.Repository}/blob/{newest.Commit}/docs/ci-cd.md#baseline-releases).");
    }

    // Release fields are validated before use, so these links only reach repoagent/controller's release and package pages.
    static string Release(BaselineRelease release) => $"[{release.Tag}]({release.Url})";

    static string Image(BaselineRelease release) => $"`{release.Image}` ([package](https://github.com/{BaselineReleases.Repository}/pkgs/container/controller))";

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
