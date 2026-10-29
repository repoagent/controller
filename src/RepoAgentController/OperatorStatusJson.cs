using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RepoAgentController;

/// <summary>
/// The status JSON writer: the operator status as <c>status</c> prints it and the dashboard reads it, and the status record as
/// its file holds it, in the shapes and key order 1f36818 wrote. A recorded part that is not in a writer's form is read leniently
/// and written back as read.
/// </summary>
static class OperatorStatusJson
{
    // Terminal-readable output; status is never embedded in HTML.
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Text(JsonObject document) => document.ToJsonString(Indented);

    public static string Text(OperatorStatus status) => Text(Render(status));

    public static JsonObject Render(OperatorStatus status)
    {
        var sessions = new JsonArray();
        foreach (var session in status.Sessions)
        {
            var item = Json.Object(("discussion", session.Discussion), ("session", session.Session), ("state", session.Activity.State));
            switch (session.Activity)
            {
                case SessionActivity.Pending delivery: item["since"] = delivery.Since; item["operation"] = delivery.Operation; break;
                case SessionActivity.Retrying retry: item["attempts"] = retry.Attempts; item["retryAt"] = retry.RetryAt; break;
                case SessionActivity.Recovering recovery:
                    item["reason"] = "model-capacity"; item["attempts"] = recovery.Attempts; item["retryAt"] = recovery.RetryAt;
                    item["model"] = recovery.Model; item["effort"] = recovery.Effort; item["suspended"] = recovery.Suspended; break;
                case SessionActivity.Running { Since: { } since }: item["since"] = since; break;
            }
            if (session.Waiting != 0) item["waiting"] = session.Waiting;
            sessions.Add((JsonNode)item);
        }
        var work = status.Work;
        return new JsonObject
        {
            ["version"] = 1,
            ["generatedAt"] = status.GeneratedAt.ToString("O"),
            ["repository"] = status.Repository,
            ["actor"] = status.Actor,
            ["controller"] = Controller(status.Controller),
            ["authentication"] = Credential(status.Authentication),
            ["codexAuthentication"] = Credential(status.CodexAuthentication),
            // Each repository setting's latest observation: state, effective value and since when, and why it is unusable.
            ["settings"] = status.Settings is { } settings ? ControllerStateFiles.Settings(settings) : null,
            // Running agent turns against the effective active-session limit.
            ["work"] = Json.Object(("sessions", work.Sessions), ("pending", work.Pending), ("retrying", work.Retrying), ("waiting", work.Waiting),
                ("running", work.Running), ("limit", work.Limit), ("admissionBlocked", work.AdmissionBlocked)),
            ["sessions"] = sessions,
            ["failures"] = Failures(status.Failures, status.FailuresAsRead),
            ["latestActivity"] = Event(status.LatestActivity)
        };
    }

    /// <summary>The status record as its file holds it.</summary>
    public static JsonObject Record(LifetimeStatus status)
    {
        var record = new JsonObject
        {
            ["version"] = 1,
            ["repository"] = status.Repository,
            ["controller"] = Controller(status.Controller),
            ["authentication"] = Credential(status.Authentication),
            ["codexAuthentication"] = Credential(status.CodexAuthentication),
            ["failures"] = Failures(status.Failures, status.FailuresAsRead),
            ["latestActivity"] = Event(status.LatestActivity)
        };
        if (status.RunningSessions.Count != 0) record["runningSessions"] = new JsonArray([.. status.RunningSessions.Select(session => (JsonNode?)JsonValue.Create(session))]);
        return record;
    }

    /// <summary>The status record its file holds, each part read leniently: one that is not in a writer's form is kept as read.</summary>
    public static LifetimeStatus ReadRecord(JsonObject record) => new(record["repository"].OptionalText(), Controller(record["controller"]),
        Credential(record["authentication"]), Credential(record["codexAuthentication"]))
    {
        Failures = record["failures"] is JsonArray failures ? [.. failures.Select(failure => Event(failure) ?? new("", "", "") { AsRead = "null" })] : [],
        FailuresAsRead = record["failures"] is { } kept and not JsonArray ? kept.ToJsonString() : null,
        LatestActivity = Event(record["latestActivity"]),
        RunningSessions = record["runningSessions"] is JsonArray sessions ? [.. sessions.Select(session => session.OptionalText()).OfType<string>()] : []
    };

    // Each part reads what it holds as text; writing that back must give the JSON read, or the part is kept as read.
    static ControllerRun? Controller(JsonNode? node)
    {
        if (node is null) return null;
        var item = node as JsonObject;
        var run = new ControllerRun(item?["state"].OptionalText() ?? "")
        {
            Mode = item?["mode"].OptionalText(),
            StartedAt = item?["startedAt"].OptionalText(),
            UpdatedAt = item?["updatedAt"].OptionalText(),
            StoppedAt = item?["stoppedAt"].OptionalText()
        };
        return Same(Controller(run), node) ? run : run with { AsRead = node.ToJsonString() };
    }

    static CredentialStatus? Credential(JsonNode? node)
    {
        if (node is null) return null;
        var item = node as JsonObject;
        var credential = new CredentialStatus(item?["state"].OptionalText() ?? "") { UpdatedAt = item?["updatedAt"].OptionalText(), Error = item?["error"].OptionalText() };
        return Same(Credential(credential), node) ? credential : credential with { AsRead = node.ToJsonString() };
    }

    static StatusEvent? Event(JsonNode? node)
    {
        if (node is null) return null;
        var item = node as JsonObject;
        var read = new StatusEvent(item?["at"].OptionalText() ?? "", item?["kind"].OptionalText() ?? "", item?["message"].OptionalText() ?? "")
        { Url = item?["url"].OptionalText() };
        return Same(Event(read), node) ? read : read with { AsRead = node.ToJsonString(), AsReadIsObject = item is not null };
    }

    static bool Same(JsonNode? written, JsonNode read) => written?.ToJsonString() == read.ToJsonString();

    static JsonNode? Controller(ControllerRun? run)
    {
        if (run is null) return null;
        if (run.AsRead is { } json) return JsonNode.Parse(json);
        var item = Json.Object(("state", run.State));
        if (run.Mode is { } mode) item["mode"] = mode;
        if (run.StartedAt is { } started) item["startedAt"] = started;
        if (run.UpdatedAt is { } updated) item["updatedAt"] = updated;
        if (run.StoppedAt is { } stopped) item["stoppedAt"] = stopped;
        return item;
    }

    static JsonNode? Credential(CredentialStatus? credential)
    {
        if (credential is null) return null;
        if (credential.AsRead is { } json) return JsonNode.Parse(json);
        var item = Json.Object(("state", credential.State));
        if (credential.UpdatedAt is { } updated) item["updatedAt"] = updated;
        if (credential.Error is { } error) item["error"] = error;
        return item;
    }

    static JsonNode? Failures(IReadOnlyList<StatusEvent> failures, string? asRead) =>
        asRead is not null ? JsonNode.Parse(asRead) : new JsonArray([.. failures.Select(failure => Event(failure))]);

    static JsonNode? Event(StatusEvent? item)
    {
        if (item is null) return null;
        if (item.AsRead is { } json) return JsonNode.Parse(json);
        var node = Json.Object(("at", item.At), ("kind", item.Kind), ("message", item.Message));
        if (item.Url is { } url) node["url"] = url;
        return node;
    }
}

// Native AOT: the status endpoint serializes through generated metadata, never reflection.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StatusSnapshot))]
sealed partial class StatusJson : JsonSerializerContext;
