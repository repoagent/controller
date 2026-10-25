using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RepoAgentController;

/// <summary>
/// Publishes this controller's nonsecret operator status to its owned dashboard Discussion through the GitHub outbox,
/// whenever status changes and as a heartbeat about every 30 seconds, including while idle. Only a stop this lifetime
/// handles publishes a final state; after an abrupt end the heartbeat goes stale instead of claiming a stop.
/// </summary>
sealed class ControllerDashboard : IAsyncDisposable
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
    readonly CancellationTokenSource stop = new();
    readonly WakeLoop loop;
    Task running = Task.CompletedTask;
    bool started;
    string? failure;

    public ControllerDashboard(string category, ControllerStatus status, GitHubOutbox outbox, BaselineReleases releases, TimeProvider clock)
    {
        this.category = category;
        this.status = status;
        this.outbox = outbox;
        this.releases = releases;
        this.clock = clock;
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

    /// <summary>Records that this lifetime is ending on an error rather than an operator stop.</summary>
    public void Failed(string message) => failure = Log.Redact(message);

    bool Publish((DateTimeOffset At, string? Failure)? stopped)
    {
        try
        {
            var snapshot = status.Snapshot();
            // Either credential failing needs the operator: the App's stops GitHub work, the Codex account's holds every turn.
            var attention = snapshot["authentication"]?.Text("state") == "failed" || snapshot["codexAuthentication"]?.Text("state") == "failed";
            outbox.Dashboard(category, attention ? AttentionTitle : Title, Render(snapshot, clock.GetUtcNow(), stopped, releases.Current));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Write("dashboard-error", $"{error.Message}; publication is retried at the next heartbeat");
            return false;
        }
    }

    static string Render(JsonObject snapshot, DateTimeOffset now, (DateTimeOffset At, string? Failure)? stopped, ReleaseNotice? release)
    {
        var text = new StringBuilder();
        text.AppendLine(AppIdentity.DashboardMarker);
        text.AppendLine("Operational status of this repository's RepoAgent controller. The controller replaces this text on every update; comments and reactions here are never delivered to the agent.");
        text.AppendLine();
        var controller = snapshot["controller"] ?? new JsonObject();
        var mode = controller.Text("mode");
        text.AppendLine("- **Controller:** " + stopped switch
        {
            null => $"{controller.Text("state")} ({mode}) since {Time(controller.Text("startedAt"))}",
            { Failure: null } end => $"stopped at {Time(end.At)} after a graceful shutdown; it had started at {Time(controller.Text("startedAt"))}",
            { } end => $"stopped at {Time(end.At)} after a failure: {Code(end.Failure!)}"
        });
        text.AppendLine(stopped is null
            ? $"- **Heartbeat:** {Time(now)}. Refreshed about every {HeartbeatInterval.TotalSeconds:F0} seconds while the controller runs, even when idle. An older heartbeat means this status is unconfirmed, not that a particular failure occurred."
            : "- **Heartbeat:** none while stopped.");
        var authentication = snapshot["authentication"] ?? new JsonObject();
        text.AppendLine("- **Authentication:** " + authentication.Text("state") switch
        {
            "failed" => $"failed at {Time(authentication.Text("updatedAt"))}: {Code(authentication.Text("error"))}. Check the controller's approval and credentials, then restart it.",
            "" => "unknown",
            var state => state
        });
        var codexAuthentication = snapshot["codexAuthentication"] ?? new JsonObject();
        text.AppendLine("- **Codex authentication:** " + codexAuthentication.Text("state") switch
        {
            "failed" => $"failed at {Time(codexAuthentication.Text("updatedAt"))}: {Code(codexAuthentication.Text("error"))}. New Discussion input is held until the Codex account credentials are replaced.",
            "supplied" => "supplied credentials installed; not yet verified by a completed turn",
            "" => "unknown",
            var state => state
        });
        Settings(text, snapshot["settings"] as JsonObject);
        var work = snapshot["work"] ?? new JsonObject();
        var full = Count(work["running"]) >= Count(work["limit"]);
        text.AppendLine($"- **Sessions:** {Count(work["sessions"])} stored; " +
            (stopped is null ? $"{Count(work["running"])} of {Count(work["limit"])} turns running (active-session limit)" : "no turns run while the controller is stopped") +
            $"; {Count(work["pending"])} awaiting native acknowledgement; {Count(work["waiting"])} inputs waiting" +
            (work["admissionBlocked"] is JsonValue blocked && blocked.TryGetValue<bool>(out var held) && held ? "; new turns are held for a checkout refresh" : ""));
        var activity = snapshot["latestActivity"];
        text.AppendLine("- **Latest activity:** " + (activity is null ? "none recorded" :
            $"{(activity.Text("kind") == "session-created" ? "session started for" : "input delivered from")} {Link(activity.Text("url"))} at {Time(activity.Text("at"))}"));
        text.AppendLine();
        Updates(text, release);
        text.AppendLine();
        text.AppendLine("### Active and queued sessions");
        var busy = (snapshot["sessions"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(session => session.Text("state") is "pending" or "running" || Count(session["waiting"]) != 0).ToArray();
        var codexFailed = snapshot["codexAuthentication"]?.Text("state") == "failed";
        foreach (var session in busy.Take(MaximumSessions))
        {
            var waiting = Count(session["waiting"]);
            var state = session.Text("state");
            string? doing = state switch
            {
                "pending" => "awaiting native acknowledgement",
                "retrying" => $"delivery failed or was unacknowledged; attempt {Count(session["attempts"]) + 1} at {Time(session.Text("retryAt"))}",
                "running" => stopped is null ? $"running a turn since {Time(session.Text("since"))}" : "its turn ended with the controller",
                _ => null
            };
            // Why idle input still waits, from the most specific cause status shows; others appear under recent failures.
            var reason = waiting == 0 ? null : state switch
            {
                "running" when stopped is null => $"{waiting} inputs waiting for that turn to finish",
                "pending" or "retrying" or "running" => $"{waiting} inputs waiting",
                _ when codexFailed => $"{waiting} inputs held until the Codex account credentials are replaced",
                _ when full && stopped is null => $"{waiting} inputs waiting for a free active-session slot",
                _ => $"{waiting} inputs waiting for dispatch"
            };
            text.AppendLine($"- {Link(session.Text("discussion"))}: " + string.Join("; ", new[] { doing, reason }.OfType<string>()));
        }
        if (busy.Length > MaximumSessions) text.AppendLine($"- and {busy.Length - MaximumSessions} more");
        if (busy.Length == 0) text.AppendLine("None.");
        text.AppendLine();
        text.AppendLine("### Recent failures");
        var failures = (snapshot["failures"] as JsonArray ?? []).OfType<JsonObject>().Reverse().Take(MaximumFailures).ToArray();
        foreach (var item in failures) text.AppendLine($"- {Time(item.Text("at"))} {Code(item.Text("kind"))}: {Code(item.Text("message"))}");
        if (failures.Length == 0) text.AppendLine("None recorded.");
        return text.ToString();
    }

    // Invalid and unreadable settings stay listed until a read succeeds; raw invalid values are never recorded.
    static void Settings(StringBuilder text, JsonObject? settings)
    {
        if (settings is null || settings.All(setting => setting.Value?.Text("state") == "disabled"))
        {
            text.AppendLine("- **Repository settings:** not read; the controller runs without --repository-settings");
            return;
        }
        var problems = settings.Where(setting => setting.Value?.Text("state") is "invalid" or "read-failed").ToArray();
        text.AppendLine("- **Repository settings:** " + (problems.Length == 0 ? "read without problems" : $"{problems.Length} unusable"));
        foreach (var (name, entry) in problems)
        {
            var value = entry!;
            text.AppendLine($"  - {Code(name)} {(value.Text("state") == "invalid" ? "is invalid" : "could not be read")}: {Code(value.Text("reason"))}; " +
                (value["value"] is null ? "no repository value applies" : $"the earlier value {Code(value.Text("value"))} still applies"));
        }
    }

    // Only a published release that this controller's image identifies, and that a newer release supersedes, is offered.
    internal static void Updates(StringBuilder text, ReleaseNotice? notice)
    {
        text.AppendLine("### Updates");
        if (notice is null) { text.AppendLine("The baseline release check has not completed yet."); return; }
        var at = Time(notice.CheckedAt);
        if (notice.Failure is { } failure)
        {
            text.AppendLine($"The baseline release check failed at {at}: {Code(failure)}. No update is claimed; the next check is in about an hour.");
            return;
        }
        if (notice.Newest is not { } newest) { text.AppendLine($"No published baseline release was found at {at}."); return; }
        if (notice.Uncertainty is not null || notice.Running is not { } running)
        {
            text.AppendLine($"**Release identity uncertain** (checked at {at}): {notice.Uncertainty}" +
                (notice.Running is { } matched ? $"; its image inventory matches {Release(matched)}" : "") + ". No replacement is suggested.");
            text.AppendLine($"- Newest published baseline release: {Release(newest)}, image {Image(newest)} with gh {Code(newest.Gh)} and Codex {Code(newest.Codex)}. " +
                "Compare it with this controller's image before deciding whether to rebuild or recreate it.");
            return;
        }
        if (notice.Newer == 0) { text.AppendLine($"This controller's image matches {Release(running)}, the newest published baseline release at {at}."); return; }
        text.AppendLine($"**Update available:** baseline release {Release(newest)} is newer than this controller's {Release(running)} ({notice.Newer} newer {(notice.Newer == 1 ? "release" : "releases")}; checked at {at}).");
        text.AppendLine($"- Image {Image(newest)} with gh {Code(newest.Gh)} and Codex {Code(newest.Codex)}.");
        text.AppendLine("- Publication is not deployment: this controller keeps running its current image. It never replaces itself and has no Docker host or deployment access.");
        text.AppendLine("- To update, an operator recreates the controller from that image, or rebuilds an image derived from the baseline on it, keeping the controller's external configuration, credentials and session data; see " +
            $"[baseline releases](https://github.com/{BaselineReleases.Repository}/blob/{newest.Commit}/docs/ci-cd.md#baseline-releases).");
    }

    // Release fields are validated before use, so these links only reach repoagent/controller's release and package pages.
    static string Release(BaselineRelease release) => $"[{release.Tag}]({release.Url})";

    static string Image(BaselineRelease release) => $"`{release.Image}` ([package](https://github.com/{BaselineReleases.Repository}/pkgs/container/controller))";

    static int Count(JsonNode? value) => value is JsonValue number && number.TryGetValue<int>(out var count) ? count : 0;

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
            using var budget = new CancellationTokenSource(StopBudget);
            try { await outbox.Finish(budget.Token); }
            catch (Exception error) when (error is OperationCanceledException or IOException) { }
        }
        stop.Dispose();
    }
}
