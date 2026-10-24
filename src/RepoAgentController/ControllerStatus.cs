using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
        "app-refresh-error", "native-request-blocked", "refresh-blocked", "webhook-error", "webhook-disable-error", "dashboard-error",
        "codex-auth-failed", "codex-auth-invalid", "codex-auth-error", "selection-error", "label-read-error"];
    static readonly HashSet<string> ActivityKinds = ["session-created", "forwarded"];
    // Events after which the state file holds different work, such as input now waiting for its session.
    static readonly HashSet<string> WorkKinds = ["input-waiting", "input-requeued", "turn-ended"];
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

    /// <summary>
    /// Records the agent sessions running a turn in this lifetime. GitHub's deletion of a Discussion forgets its record while
    /// the agent still runs its turn, so work counts that turn by its session until the agent ends it.
    /// </summary>
    public void Running(string[] sessions)
    {
        lock (gate)
        {
            var current = sessions.Order(StringComparer.Ordinal).ToArray();
            if (closed || ((runtime["runningSessions"] as JsonArray)?.Select(session => session.OptionalText()) ?? []).SequenceEqual(current)) return;
            // Recorded only while a turn runs. Status output copies named fields only, so this stays internal.
            if (current.Length == 0) runtime.Remove("runningSessions");
            else runtime["runningSessions"] = new JsonArray(current.Select(session => (JsonNode?)JsonValue.Create(session)).ToArray());
            Save();
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
        // Managed clone mode also records native turns in its refresh journal, for checkout refresh admission.
        var journal = ReadObject(statePath + ".refresh.json");
        var active = journal?["active"] as JsonObject;
        var settings = RepositorySettings.Read(RepositorySettings.PathFor(statePath));
        var limit = settings?[Bridge.ActiveSessionSetting]?["value"].OptionalText() is { } text &&
            int.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : Bridge.DefaultActiveSessions;
        // Turns end with their lifetime, so only a running controller can report one.
        var live = recorded?["controller"]?.Text("state") is "starting" or "running";
        var sessions = new JsonArray();
        var held = new HashSet<string>(StringComparer.Ordinal);
        int pending = 0, retrying = 0, running = 0, started = 0, waiting = 0;
        foreach (var (_, value) in state?["discussions"] as JsonObject ?? new JsonObject())
        {
            if (value is not JsonObject discussion) continue;
            var session = discussion.Text("sessionId");
            if (session.Length != 0) { started++; held.Add(session); }
            var marker = discussion["pending"];
            // A failed or unacknowledged delivery waiting for its next attempt.
            var retry = discussion["waiting"]?["retry"];
            var turn = live && (discussion["running"] is JsonObject ||
                session.Length != 0 && active is not null && active.Any(entry => entry.Value?.Text("thread") == session));
            var activity = marker is not null ? "pending" : retry is not null ? "retrying" : turn ? "running" : "idle";
            if (marker is not null) pending++;
            else if (retry is not null) retrying++;
            else if (turn) running++;
            var item = Json.Object(("discussion", discussion.Text("url")), ("session", session.Length == 0 ? null : session), ("state", activity));
            if (marker is not null) { item["since"] = marker["since"]?.Copy(); item["operation"] = marker["operation"]?.Copy(); }
            else if (retry is not null) { item["attempts"] = retry["attempts"]?.Copy(); item["retryAt"] = retry["after"]?.Copy(); }
            else if (turn && discussion["running"]?["since"] is { } since) item["since"] = since.Copy();
            // Observed input held in the controller until its session is idle.
            if (discussion["waiting"]?["inputs"] is JsonArray inputs && inputs.Count != 0) { item["waiting"] = inputs.Count; waiting += inputs.Count; }
            sessions.Add((JsonNode)item);
        }
        // A turn whose Discussion record was forgotten still runs, and holds its slot, until the agent ends it.
        if (live && recorded?["runningSessions"] is JsonArray turns) running += turns.Count(turn => turn.OptionalText() is { } session && !held.Contains(session));
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
            ["settings"] = settings,
            // Running native turns against the effective active-session limit.
            ["work"] = Json.Object(("sessions", started), ("pending", pending), ("retrying", retrying), ("waiting", waiting), ("running", running),
                ("limit", limit), ("admissionBlocked", journal?["blocked"]?.Copy())),
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

/// <summary>
/// What GET /status reports (ADR 0009): busy while an agent turn runs or input is pending, retrying or waiting, else idle,
/// with the work counts of the operator status. A hosting environment decides what busy means for it.
/// </summary>
sealed record StatusSnapshot(string State, WorkCounts Work)
{
    public static StatusSnapshot From(JsonObject status)
    {
        var work = status["work"]!;
        var counts = new WorkCounts(Count("sessions"), Count("pending"), Count("retrying"), Count("waiting"), Count("running"), Count("limit"),
            work["admissionBlocked"] is JsonValue blocked && blocked.TryGetValue<bool>(out var held) ? held : null);
        var busy = counts.Running + counts.Pending + counts.Retrying + counts.Waiting > 0;
        return new(busy ? "busy" : "idle", counts);

        int Count(string name) => work[name]!.GetValue<int>();
    }
}

/// <summary>The operator status's work counts, by the same names.</summary>
sealed record WorkCounts(int Sessions, int Pending, int Retrying, int Waiting, int Running, int Limit, bool? AdmissionBlocked);

// Native AOT: the status endpoint serializes through generated metadata, never reflection.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StatusSnapshot))]
sealed partial class StatusJson : JsonSerializerContext;
