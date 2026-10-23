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
    readonly TimeProvider clock;
    readonly Lock sync = new();
    readonly CancellationTokenSource stop = new();
    TaskCompletionSource wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task running = Task.CompletedTask;
    bool started;
    string? failure;

    public ControllerDashboard(string category, ControllerStatus status, GitHubOutbox outbox, TimeProvider clock)
    {
        this.category = category;
        this.status = status;
        this.outbox = outbox;
        this.clock = clock;
        status.Changed += Wake;
    }

    void Wake() { lock (sync) wake.TrySetResult(); }

    /// <summary>Publishes the current status now, then on every change and heartbeat until disposal.</summary>
    public void Start(CancellationToken ct)
    {
        started = true;
        running = Run(CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token));
    }

    /// <summary>Records that this lifetime is ending on an error rather than an operator stop.</summary>
    public void Failed(string message) => failure = Log.Redact(message);

    async Task Run(CancellationTokenSource lifetime)
    {
        var ct = lifetime.Token;
        try
        {
            while (true)
            {
                Task signal;
                lock (sync)
                {
                    if (wake.Task.IsCompleted) wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    signal = wake.Task;
                }
                var published = Publish(null);
                using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var heartbeat = Task.Delay(HeartbeatInterval, clock, timer.Token);
                // A failed publication waits for the heartbeat, so reporting that failure cannot spin this loop.
                await (published ? Task.WhenAny(signal, heartbeat) : heartbeat);
                await timer.CancelAsync();
                ct.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { lifetime.Dispose(); }
    }

    bool Publish((DateTimeOffset At, string? Failure)? stopped)
    {
        try
        {
            var snapshot = status.Snapshot();
            // Either credential failing needs the operator: the App's stops GitHub work, the Codex account's holds every turn.
            var attention = snapshot["authentication"]?.Text("state") == "failed" || snapshot["codexAuthentication"]?.Text("state") == "failed";
            outbox.Dashboard(category, attention ? AttentionTitle : Title, Render(snapshot, clock.GetUtcNow(), stopped));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Write("dashboard-error", $"{error.Message}; publication is retried at the next heartbeat");
            return false;
        }
    }

    static string Render(JsonObject snapshot, DateTimeOffset now, (DateTimeOffset At, string? Failure)? stopped)
    {
        var text = new StringBuilder();
        text.AppendLine(GitHub.DashboardMarker);
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
        var work = snapshot["work"] ?? new JsonObject();
        text.AppendLine($"- **Sessions:** {Count(work["sessions"])} stored; {(work["running"] is null ? "running turns are not tracked in this mode" : Count(work["running"]) + " running")}; " +
            $"{Count(work["pending"])} awaiting native acknowledgement; {Count(work["waiting"])} inputs waiting" +
            (work["admissionBlocked"] is JsonValue blocked && blocked.TryGetValue<bool>(out var held) && held ? "; new turns are held for a checkout refresh" : ""));
        var activity = snapshot["latestActivity"];
        text.AppendLine("- **Latest activity:** " + (activity is null ? "none recorded" :
            $"{(activity.Text("kind") == "session-created" ? "session started for" : "input delivered from")} {Link(activity.Text("url"))} at {Time(activity.Text("at"))}"));
        text.AppendLine();
        text.AppendLine("### Active and queued sessions");
        var busy = (snapshot["sessions"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(session => session.Text("state") is "pending" or "running" || Count(session["waiting"]) != 0).ToArray();
        foreach (var session in busy.Take(MaximumSessions))
            text.AppendLine($"- {Link(session.Text("discussion"))}: " + session.Text("state") switch
            {
                "pending" => "awaiting native acknowledgement",
                "running" => "running a turn",
                "accepted" => "earlier input accepted; turns are not tracked in this mode",
                var state => state
            } + (Count(session["waiting"]) == 0 ? "" : $"; {Count(session["waiting"])} inputs waiting for the session to become idle"));
        if (busy.Length > MaximumSessions) text.AppendLine($"- and {busy.Length - MaximumSessions} more");
        if (busy.Length == 0) text.AppendLine("None.");
        text.AppendLine();
        text.AppendLine("### Recent failures");
        var failures = (snapshot["failures"] as JsonArray ?? []).OfType<JsonObject>().Reverse().Take(MaximumFailures).ToArray();
        foreach (var item in failures) text.AppendLine($"- {Time(item.Text("at"))} {Code(item.Text("kind"))}: {Code(item.Text("message"))}");
        if (failures.Length == 0) text.AppendLine("None recorded.");
        return text.ToString();
    }

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
        await stop.CancelAsync();
        await running;
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
