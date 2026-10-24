using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RepoAgentController;

/// <summary>
/// Operator status for one controller state path. Lifetime facts are projected from this lifetime's
/// redacted log events, so every reported failure has a correlated stdout and retained-log entry.
/// Sessions and work come from the durable checkpoints whenever a snapshot is taken.
/// </summary>
sealed class ControllerStatus : IDisposable
{
    const int MaximumFailures = 20, MaximumMessage = 2000;
    // Events that report failed or blocked work. New failure events become visible by adding their kind here.
    static readonly HashSet<string> FailureKinds = ["fatal", "poll-error", "observation-error", "dispatch-error", "delivery-retry", "receipt-error", "outbox-error",
        "app-refresh-error", "native-request-blocked", "refresh-blocked", "durable-work-lost", "webhook-error", "webhook-disable-error", "dashboard-error",
        "codex-auth-failed", "codex-auth-invalid", "codex-auth-error", "selection-error", "label-read-error"];
    static readonly HashSet<string> ActivityKinds = ["session-created", "forwarded"];
    // Events after which the state file holds different work, such as input now waiting for its session.
    static readonly HashSet<string> WorkKinds = ["input-waiting", "input-requeued"];
    // Terminal-readable output; status is never embedded in HTML.
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    readonly string statePath;
    readonly Lock gate = new();
    readonly JsonObject runtime;
    bool closed;

    /// <summary>Raised after recorded runtime status or composed work changes. Handlers run on the logging thread and must not log.</summary>
    public event Action? Changed;

    public static string PathFor(string statePath) => statePath + ".status.json";

    public ControllerStatus(string statePath, string repository, string mode)
    {
        this.statePath = statePath;
        var previous = ReadObject(PathFor(statePath));
        var now = DateTimeOffset.UtcNow.ToString("O");
        runtime = new JsonObject
        {
            ["version"] = 1, ["repository"] = repository,
            ["controller"] = Json.Object(("state", "starting"), ("mode", mode), ("startedAt", now), ("updatedAt", now)),
            ["authentication"] = Json.Object(("state", "unknown")),
            // Agent account credentials, separate from the GitHub App: supplied, healthy (a turn completed) or failed.
            ["codexAuthentication"] = Json.Object(("state", "unknown")),
            // Earlier failures stay visible after a restart; their timestamps place them before startedAt.
            ["failures"] = previous?["failures"] is JsonArray failures ? failures.Copy() : new JsonArray(),
            ["latestActivity"] = previous?["latestActivity"]?.Copy()
        };
        Save();
    }

    public void Record(LogEntry entry)
    {
        lock (gate)
        {
            if (closed) return;
            var at = entry.At.ToString("O");
            var item = Json.Object(("at", at), ("kind", entry.Kind),
                ("message", entry.Message.Length <= MaximumMessage ? entry.Message : entry.Message[..MaximumMessage] + " <truncated>"));
            var controller = runtime["controller"]!;
            bool changed = true, notifyOnly = false;
            switch (entry.Kind)
            {
                case "ready": controller["state"] = "running"; break;
                case "fatal": controller["state"] = "failed"; break;
                case "app-token-refreshed": runtime["authentication"] = Json.Object(("state", "healthy"), ("updatedAt", at)); break;
                case "app-refresh-error": runtime["authentication"] = Json.Object(("state", "failed"), ("updatedAt", at), ("error", item["message"]!.Copy())); break;
                case "codex-auth-supplied" or "codex-auth-applied": runtime["codexAuthentication"] = Json.Object(("state", "supplied"), ("updatedAt", at)); break;
                case "codex-auth-verified": runtime["codexAuthentication"] = Json.Object(("state", "healthy"), ("updatedAt", at)); break;
                case "codex-auth-failed": runtime["codexAuthentication"] = Json.Object(("state", "failed"), ("updatedAt", at), ("error", item["message"]!.Copy())); break;
                default: changed = false; break;
            }
            if (FailureKinds.Contains(entry.Kind))
            {
                var failures = runtime["failures"]!.AsArray();
                failures.Add((JsonNode)item);
                while (failures.Count > MaximumFailures) failures.RemoveAt(0);
            }
            else if (ActivityKinds.Contains(entry.Kind))
            {
                // The latest processed message, else its Discussion: the activity message's leading GitHub URL.
                var link = Regex.Match(entry.Message, @"(?:^|\s)latest=(https://github\.com/\S+)") is { Success: true } latest ? latest.Groups[1].Value
                    : Regex.Match(entry.Message, @"^https://github\.com/\S+") is { Success: true } leading ? leading.Value : null;
                if (link is not null) item["url"] = link;
                runtime["latestActivity"] = item;
            }
            else if (!changed)
            {
                // Work is composed from the state file, so a work event records nothing here; consumers still refresh.
                if (!WorkKinds.Contains(entry.Kind)) return;
                notifyOnly = true;
            }
            if (!notifyOnly)
            {
                controller["updatedAt"] = at;
                Save();
            }
        }
        Changed?.Invoke();
    }

    /// <summary>A detached snapshot for in-process consumers such as status publication.</summary>
    public JsonObject Snapshot()
    {
        JsonObject copy;
        lock (gate) copy = runtime.Copy().AsObject();
        return Compose(statePath, copy);
    }

    /// <summary>Reads the last recorded status of a running or stopped controller without taking its lease.</summary>
    public static JsonObject? Read(string statePath)
    {
        var recorded = ReadObject(PathFor(statePath));
        return recorded is null && !File.Exists(statePath) ? null : Compose(statePath, recorded);
    }

    public static string Render(JsonObject snapshot) => snapshot.ToJsonString(Indented);

    static JsonObject Compose(string statePath, JsonObject? recorded)
    {
        var state = ReadObject(statePath);
        // Managed clone mode tracks native turns in its refresh journal; other modes do not observe them.
        var journal = ReadObject(statePath + ".refresh.json");
        var active = journal?["active"] as JsonObject;
        var sessions = new JsonArray();
        int pending = 0, retrying = 0, running = 0, started = 0, waiting = 0;
        foreach (var (_, value) in state?["discussions"] as JsonObject ?? new JsonObject())
        {
            if (value is not JsonObject discussion) continue;
            var session = discussion.Text("sessionId");
            if (session.Length != 0) started++;
            var marker = discussion["pending"];
            // A failed or unacknowledged delivery waiting for its next attempt.
            var retry = discussion["waiting"]?["retry"];
            var turn = session.Length != 0 && active is not null && active.Any(entry => entry.Value?.Text("thread") == session);
            var activity = marker is not null ? "pending" : retry is not null ? "retrying" : turn ? "running" : journal is null ? "accepted" : "idle";
            if (marker is not null) pending++;
            else if (retry is not null) retrying++;
            else if (turn) running++;
            var item = Json.Object(("discussion", discussion.Text("url")), ("session", session.Length == 0 ? null : session), ("state", activity));
            if (marker is not null) { item["since"] = marker["since"]?.Copy(); item["operation"] = marker["operation"]?.Copy(); }
            else if (retry is not null) { item["attempts"] = retry["attempts"]?.Copy(); item["retryAt"] = retry["after"]?.Copy(); }
            // Observed input held in the controller until its session is idle.
            if (discussion["waiting"]?["inputs"] is JsonArray inputs && inputs.Count != 0) { item["waiting"] = inputs.Count; waiting += inputs.Count; }
            sessions.Add((JsonNode)item);
        }
        return new JsonObject
        {
            ["version"] = 1,
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["repository"] = state?["repository"]?.Copy() ?? recorded?["repository"]?.Copy(),
            ["actor"] = state?["agentLogin"]?.Copy(),
            ["controller"] = recorded?["controller"]?.Copy() ?? Json.Object(("state", "unknown")),
            ["authentication"] = recorded?["authentication"]?.Copy() ?? Json.Object(("state", "unknown")),
            ["codexAuthentication"] = recorded?["codexAuthentication"]?.Copy() ?? Json.Object(("state", "unknown")),
            // Each repository setting's latest observation: state, effective value and since when, and why it is unusable.
            ["settings"] = RepositorySettings.Read(RepositorySettings.PathFor(statePath)),
            ["work"] = Json.Object(("sessions", started), ("pending", pending), ("retrying", retrying), ("waiting", waiting), ("running", journal is null ? null : running),
                ("admissionBlocked", journal?["blocked"]?.Copy())),
            ["sessions"] = sessions,
            ["failures"] = recorded?["failures"]?.Copy() ?? new JsonArray(),
            ["latestActivity"] = recorded?["latestActivity"]?.Copy()
        };
    }

    static JsonObject? ReadObject(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    void Save()
    {
        var path = PathFor(statePath);
        var temporary = path + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options)) file.Write(System.Text.Encoding.UTF8.GetBytes(Render(runtime)));
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Status is diagnostic; its failure must not stop work. Never recurse into Log from its observer.
            Console.WriteLine($"{DateTimeOffset.UtcNow:O} status-error {error.Message}");
        }
    }

    /// <summary>Records the end of this lifetime; a fatal failure remains the reported state.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (closed) return;
            var controller = runtime["controller"]!;
            var now = DateTimeOffset.UtcNow.ToString("O");
            if (controller.Text("state") != "failed") controller["state"] = "stopped";
            controller["stoppedAt"] = now; controller["updatedAt"] = now;
            Save();
            closed = true;
        }
    }
}
