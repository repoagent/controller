using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>The pinned app-server's allowlisted, read-only activity projection. No second runtime or on-disk rollout reader.</summary>
sealed partial class Codex
{
    readonly object activityGate = new();
    readonly Dictionary<(string Session, string Turn, string Id), ActivityItem> activityItems = [];
    readonly HashSet<(string Session, string Turn)> activityTurns = [];
    bool activityTurnGap;
    static readonly string[] PrivateActivityKeys = ["data", "base64", "blob", "url", "auth", "token", "password", "secret", "credential", "config", "_meta"];
    public int ActivityEpoch => epoch;
    public event Action<AgentActivityEvent>? ActivityChanged;

    public async Task<AgentActivityPage> ReadActivity(AgentActivityQuery query, CancellationToken ct)
    {
        if (query.Cursor is null || query.Cursor.StartsWith("items:", StringComparison.Ordinal))
        {
            try
            {
                var cursor = query.Cursor?[6..];
                var response = await Request("thread/items/list", Json.Object(("threadId", query.SessionId),
                    ("limit", query.Limit), ("cursor", cursor), ("sortDirection", "desc")), ct);
                if (response["data"] is not JsonArray entries || entries.Count > query.Limit)
                    throw new ActivityHistoryUnavailable("Runtime returned an invalid activity page.");
                if (entries.Any(entry => entry is not JsonObject value || value["item"] is not JsonObject || value["turnId"].OptionalText() is null))
                    throw new ActivityHistoryUnavailable("Runtime returned an incomplete activity item envelope.");
                var visible = new List<ActivityItem>();
                var suppressed = 0;
                foreach (var entry in entries.OfType<JsonObject>())
                {
                    var item = ProjectActivity(query.SessionId, entry["turnId"].OptionalText() ?? "", entry["item"] as JsonObject);
                    if (item is null) suppressed++;
                    else
                    {
                        var completed = Milliseconds(entry["completedAtMs"]);
                        var state = item.State;
                        if (state == ActivityItemState.Unknown)
                        {
                            if (completed is not null) state = ActivityItemState.Completed;
                            else lock (activityGate)
                            {
                                if (activityItems.TryGetValue((item.SessionId, item.TurnId, item.Id), out var observed)) state = observed.State;
                                else if (activityTurns.Contains((item.SessionId, item.TurnId)) || activityTurnGap) state = ActivityItemState.Running;
                            }
                        }
                        visible.Add(item with { StartedAt = Milliseconds(entry["startedAtMs"]), CompletedAt = completed,
                            State = state, Incomplete = item.Incomplete || state == ActivityItemState.Unknown });
                    }
                }
                return new(visible, Continuation("items:", response["nextCursor"].OptionalText()), suppressed);
            }
            catch (ActivityHistoryUnsupported) when (query.Cursor is null) { }
        }
        // Pinned legacy stores explicitly reject items/list. Ask the same owned runtime for ONE full turn, then page its items.
        string? turnCursor = null, before = null, expectedTurn = null;
        if (query.Cursor is { } continuation)
        {
            try
            {
                if (!continuation.StartsWith("turns:", StringComparison.Ordinal)) throw new FormatException();
                var value = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(continuation[6..]))) as JsonObject;
                turnCursor = value?["cursor"].OptionalText();
                before = value?["before"].OptionalText();
                expectedTurn = value?["turn"].OptionalText();
            }
            catch (Exception error) when (error is FormatException or JsonException)
            { throw new ActivityHistoryUnavailable("Runtime activity continuation is invalid."); }
        }
        var turns = await Request("thread/turns/list", Json.Object(("threadId", query.SessionId), ("limit", 1),
            ("cursor", turnCursor), ("sortDirection", "desc"), ("itemsView", "full")), ct);
        if (turns["data"] is not JsonArray page || page.Count > 1)
            throw new ActivityHistoryUnavailable("Runtime returned an invalid turn history page.");
        if (page.Count == 0) return new([], Incomplete: expectedTurn is not null, Notice: expectedTurn is null ? null : "History changed.");
        var turn = page[0] as JsonObject;
        var turnId = turn?["id"].OptionalText() ?? "";
        if (turn?["items"] is not JsonArray items || turn["itemsView"].OptionalText() != "full" ||
            (expectedTurn is not null && expectedTurn != turnId))
            throw new ActivityHistoryUnavailable("Runtime cannot supply this complete turn page; read a new snapshot.");
        if (items.Any(item => item is not JsonObject))
            throw new ActivityHistoryUnavailable("Runtime returned an incomplete turn item envelope.");
        var ordered = items.Reverse().OfType<JsonObject>().ToArray();
        var offset = before is null ? 0 : Array.FindIndex(ordered, item => item["id"].OptionalText() == before) + 1;
        if (before is not null && offset == 0) throw new ActivityHistoryUnavailable("Runtime history changed; read a new snapshot.");
        var selected = ordered.Skip(offset).Take(query.Limit).ToArray();
        var projected = selected.Select(item => ProjectActivity(query.SessionId, turnId, item)).ToArray();
        string? next = null;
        if (offset + selected.Length < ordered.Length)
        {
            var anchor = selected.LastOrDefault()?["id"].OptionalText();
            if (anchor is null) throw new ActivityHistoryUnavailable("Runtime history has no stable item continuation.");
            next = TurnContinuation(turnCursor, turnId, anchor);
        }
        else if (turns["nextCursor"].OptionalText() is { } older) next = TurnContinuation(older, null, null);
        var turnState = turn!["status"].OptionalText() switch
        {
            "completed" => ActivityItemState.Completed,
            "inProgress" => ActivityItemState.Running,
            "failed" or "interrupted" => ActivityItemState.Failed,
            _ => ActivityItemState.Unknown
        };
        return new(projected.OfType<ActivityItem>().Select(item => item.State == ActivityItemState.Unknown
            ? item with { State = turnState, Incomplete = item.Incomplete || turnState == ActivityItemState.Unknown } : item).ToArray(),
            next, projected.Count(item => item is null));
    }

    static string? Continuation(string prefix, string? value) => value is null ? null : value.Length <= 2048
        ? prefix + value : throw new ActivityHistoryUnavailable("Runtime activity continuation is too large.");
    static string TurnContinuation(string? cursor, string? turn, string? before)
    {
        var text = Json.Encode(Json.Object(("cursor", cursor), ("turn", turn), ("before", before)));
        return Continuation("turns:", Convert.ToBase64String(Encoding.UTF8.GetBytes(text)))!;
    }

    void ResetActivity()
    {
        lock (activityGate) { activityItems.Clear(); activityTurns.Clear(); activityTurnGap = false; }
        ActivityChanged?.Invoke(new ActivityRuntimeChanged(epoch));
    }

    void ObserveActivity(int source, string method, JsonNode? parameters)
    {
        if (method == "disconnect") { ResetActivity(); return; }
        if (source != epoch || parameters is not JsonObject value) return;
        var session = value["threadId"].OptionalText() ?? "";
        var turn = value["turnId"].OptionalText() ?? (value["turn"] as JsonObject)?["id"].OptionalText() ?? "";
        if (session.Length == 0 || turn.Length == 0) return;
        if (method is "turn/started" or "turn/completed")
        {
            lock (activityGate)
            {
                if (method == "turn/completed")
                {
                    activityTurns.Remove((session, turn));
                    var unfinished = activityItems.Keys.Where(key => key.Session == session && key.Turn == turn).ToArray();
                    foreach (var key in unfinished) activityItems.Remove(key);
                    if (unfinished.Length != 0) ActivityChanged?.Invoke(new ActivityStreamGap(source, session));
                }
                else if (activityTurns.Count < 1024) activityTurns.Add((session, turn));
                else { activityTurnGap = true; ActivityChanged?.Invoke(new ActivityStreamGap(source, session)); }
            }
            return;
        }
        ActivityItem? projected = null;
        try
        {
            lock (activityGate)
            {
                if (method is "item/started" or "item/completed")
                {
                    projected = ProjectActivity(session, turn, value["item"] as JsonObject);
                    if (projected is not null) projected = projected with
                    { StartedAt = Milliseconds(value["startedAtMs"]), CompletedAt = Milliseconds(value["completedAtMs"]),
                        State = projected.State == ActivityItemState.Unknown
                            ? method == "item/completed" ? ActivityItemState.Completed : ActivityItemState.Running : projected.State };
                }
                else if (method is "item/agentMessage/delta" or "item/commandExecution/outputDelta")
                {
                    var id = value["itemId"].OptionalText() ?? "";
                    if (!activityItems.TryGetValue((session, turn, id), out var previous))
                    { ActivityChanged?.Invoke(new ActivityStreamGap(source, session)); return; }
                    if (previous.Kind is ActivityItemKind.Omitted or ActivityItemKind.Unsupported) return;
                    var delta = value["delta"].OptionalText() ?? "";
                    projected = method == "item/agentMessage/delta"
                        ? previous with { Text = BoundedLive((previous.Text ?? "") + delta), Truncated = previous.Truncated || (previous.Text?.Length ?? 0) + delta.Length > 131072 }
                        : previous with { Result = BoundedLive((previous.Result ?? "") + delta), Truncated = previous.Truncated || (previous.Result?.Length ?? 0) + delta.Length > 131072 };
                }
                if (projected is null) return;
                projected = BoundLiveItem(projected);
                // The process-wide live baseline is bounded too; eviction is an explicit gap for viewers of affected sessions.
                static long Size(ActivityItem item) => (long)(item.Text?.Length ?? 0) + (item.Result?.Length ?? 0) +
                    (item.Arguments?.Length ?? 0) + (item.Name?.Length ?? 0) + (item.Reads?.Sum(read => (long)read.Path.Length) ?? 0) + 1024;
                var key = (session, turn, projected.Id);
                if (projected.State is ActivityItemState.Completed or ActivityItemState.Failed)
                    activityItems.Remove(key);
                else
                {
                    activityItems.Remove(key);
                    var size = activityItems.Values.Sum(Size);
                    if (activityItems.Count >= 512 || size + Size(projected) > 256000)
                    {
                        var affected = activityItems.Keys.Select(entry => entry.Session).Distinct().ToArray();
                        activityItems.Clear();
                        foreach (var affectedSession in affected) ActivityChanged?.Invoke(new ActivityStreamGap(source, affectedSession));
                    }
                    activityItems[key] = projected;
                }
            }
            ActivityChanged?.Invoke(new ActivityItemObserved(source, projected));
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or JsonException)
        {
            ActivityChanged?.Invoke(new ActivityStreamGap(source, session));
        }
    }

    static string BoundedLive(string value) => value[..Math.Min(value.Length, 131072)];
    static ActivityItem BoundLiveItem(ActivityItem item)
    {
        var remaining = 131072;
        var truncated = item.Truncated;
        string? Keep(string? value)
        {
            if (value is null) return null;
            if (value.Length > remaining) { truncated = true; return "[oversized live field omitted]"; }
            remaining -= value.Length;
            return value;
        }
        var name = Keep(item.Name);
        var arguments = Keep(item.Arguments);
        var result = Keep(item.Result);
        var text = Keep(item.Text);
        var reads = item.Reads?.Take(20).Select(read => new InstructionReadEvidence(Keep(read.Path) ?? "")).ToArray();
        return item with { Name = name, Arguments = arguments, Result = result, Text = text, Reads = reads, Truncated = truncated };
    }
    static DateTimeOffset? Milliseconds(JsonNode? value) => value is JsonValue number && number.TryGetValue<long>(out var milliseconds) &&
        milliseconds is >= -62135596800000 and <= 253402300799999 ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;

    static ActivityItem? ProjectActivity(string session, string turn, JsonObject? item)
    {
        if (item is null) return null;
        var id = item["id"].OptionalText() ?? "";
        var type = item["type"].OptionalText() ?? "";
        if (type is "reasoning" or "userMessage") return null;
        var state = item["status"].OptionalText() switch
        {
            "inProgress" or "running" => ActivityItemState.Running,
            "completed" => ActivityItemState.Completed,
            "failed" or "declined" => ActivityItemState.Failed,
            _ => ActivityItemState.Unknown
        };
        if (type == "agentMessage") return new(session, turn, id, ActivityItemKind.Response, state, Text: item["text"].OptionalText());
        if (type == "commandExecution")
        {
            var command = item["command"].OptionalText();
            if (PrivateActivity(command)) return Omitted();
            var reads = (item["commandActions"] as JsonArray)?.OfType<JsonObject>()
                .Where(action => action["type"].OptionalText() == "read")
                .Select(action => new InstructionReadEvidence(action["path"].OptionalText() ?? ""))
                .Where(read => read.Path.Length > 0 && !PrivateActivity(read.Path)).Take(20).ToArray();
            return new(session, turn, id, ActivityItemKind.Command, state, Name: command,
                Result: item["aggregatedOutput"].OptionalText(), ExitCode: item["exitCode"] is JsonValue exit && exit.TryGetValue<int>(out var code) ? code : null, Reads: reads);
        }
        if (type is "mcpToolCall" or "dynamicToolCall" or "functionCallOutput")
        {
            var arguments = SafeActivityJson(item["arguments"], out var argumentsOmitted);
            var name = string.Join('/', new[] { item["server"].OptionalText(), item["namespace"].OptionalText(),
                item["tool"].OptionalText() ?? item["name"].OptionalText() }.OfType<string>());
            if (PrivateActivity(arguments) || PrivateActivity(name)) return Omitted();
            var result = SafeActivityJson(item["result"] ?? item["contentItems"] ?? item["output"] ?? item["error"], out var resultOmitted);
            return new(session, turn, id, ActivityItemKind.Tool, state, Name: name, Arguments: arguments,
                Result: result, Truncated: argumentsOmitted || resultOmitted, Incomplete: argumentsOmitted || resultOmitted);
        }
        if (type == "fileChange")
        {
            if (item["changes"] is JsonArray changes && changes.OfType<JsonObject>().Any(change => PrivateActivity(change["path"].OptionalText()))) return Omitted();
            var result = SafeActivityJson(item["changes"], out var omitted);
            return new(session, turn, id, ActivityItemKind.FileChange, state, Result: result, Truncated: omitted, Incomplete: omitted);
        }
        return new(session, turn, id, ActivityItemKind.Unsupported, state, Name: "Unsupported activity item", Incomplete: true);
        ActivityItem Omitted() => new(session, turn, id, ActivityItemKind.Omitted, state, Name: "Private runtime configuration or credentials omitted", Incomplete: true);
    }

    static bool PrivateActivity(string? text) => text is not null && (text.Contains("auth.json", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("config.toml", StringComparison.OrdinalIgnoreCase) || text.Contains("credential-handoff", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("config/", StringComparison.OrdinalIgnoreCase) || text.Contains("auth/", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("login/", StringComparison.OrdinalIgnoreCase));

    static string? SafeActivityJson(JsonNode? value, out bool omitted)
    {
        omitted = false;
        if (value is null) return null;
        var nodes = 0;
        var characters = 131072;
        var limited = false;
        JsonNode? Safe(JsonNode? node, int depth)
        {
            if (++nodes > 1024 || depth > 8) { limited = true; return JsonValue.Create("[structured content truncated]"); }
            if (node is JsonObject properties)
            {
                var type = properties["type"].OptionalText();
                if (type is not null && (type.Contains("image", StringComparison.OrdinalIgnoreCase) || type.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
                    type.Contains("resource", StringComparison.OrdinalIgnoreCase)))
                { limited = true; return JsonValue.Create("[binary/resource content omitted]"); }
                var safe = new JsonObject();
                if (properties.Count > 100) limited = true;
                foreach (var property in properties.Take(100))
                {
                    var key = property.Key;
                    if (key.Length > 256) { limited = true; safe["omittedField"] = "[oversized field name omitted]"; continue; }
                    if (PrivateActivityKeys.Any(privateKey => key.Contains(privateKey, StringComparison.OrdinalIgnoreCase)))
                    { limited = true; safe[key] = "[private/binary field omitted]"; }
                    else safe[key] = Safe(property.Value, depth + 1);
                }
                return safe;
            }
            if (node is JsonArray values)
            {
                if (values.Count > 100) limited = true;
                return new JsonArray(values.Take(100).Select(entry => Safe(entry, depth + 1)).ToArray());
            }
            if (node is JsonValue text && text.TryGetValue<string>(out var content))
            {
                if (content.Length > characters) { limited = true; return JsonValue.Create("[oversized text omitted]"); }
                characters -= content.Length;
                return JsonValue.Create(content);
            }
            return node?.DeepClone();
        }
        var projected = Safe(value, 0);
        omitted = limited;
        return projected is JsonValue text && text.TryGetValue<string>(out var content) ? content : projected?.ToJsonString();
    }
}

sealed class ActivityHistoryUnsupported : IOException;
