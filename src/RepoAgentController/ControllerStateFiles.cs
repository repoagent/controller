using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>The <see cref="IControllerState"/> adapter: the controller's state file and the files beside it.</summary>
sealed class ControllerStateFiles(string statePath) : IControllerState
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    static readonly string[] StateKeys = ["version", "repository", "discussions", "agentLogin", "enrollmentCategories"],
        RecordKeys = ["url", "sources", "labels", "sessionId", "waiting", "pending", "running"], WaitingKeys = ["inputs", "discussion", "context", "retry"],
        InputKeys = ["observedAt", "change", "receipt", "redelivery"], RetryKeys = ["attempts", "after"],
        PendingKeys = ["operation", "since", "sourceIds", "inputs"], RunningKeys = ["turn", "since"];

    // Checked as the state store checked it before the records were typed, so a file that is not a state, or of another version,
    // stops startup as it did. Each Discussion record is read key by key, and each value as the bridge read it then: a value in a
    // form no writer leaves is kept as read and written back. A key no writer writes is kept and written back, at any depth.
    public DiscussionState? ReadDiscussions()
    {
        if (!File.Exists(statePath)) return null;
        var state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
        if (state["version"]?.GetValue<int>() != 1)
            throw new InvalidOperationException("State belongs to a different repository or unsupported version");
        var discussions = state["discussions"] as JsonObject;
        // Starts that are not all text read as none, as a category enrollment start that is not an object did.
        var starts = state["enrollmentCategories"] is JsonObject recorded && recorded.All(start => start.Value.OptionalText() is not null) ? recorded : null;
        var read = new DiscussionState(state.Text("repository"))
        {
            // The identity check reads it as text, and stops startup when it is not.
            AgentLogin = state["agentLogin"]?.GetValue<string>(),
            Unknown = Unknown(state, [.. StateKeys.Where(key => key switch { "discussions" => discussions is not null, "enrollmentCategories" => starts is not null, _ => true })]),
            Unreadable = discussions is null ? "the state file's discussions are missing or not an object" : null
        };
        foreach (var (name, since) in starts ?? []) read.CategoryStarts[name] = since.OptionalText()!;
        foreach (var (id, record) in discussions ?? []) read.Discussions[id] = Record(record);
        return read;
    }

    public void WriteDiscussions(DiscussionState state)
    {
        var file = new JsonObject { ["version"] = 1, ["repository"] = state.Repository };
        if (state.Unreadable is null)
        {
            var discussions = new JsonObject();
            foreach (var (id, record) in state.Discussions) discussions[id] = Record(record);
            file["discussions"] = discussions;
        }
        if (state.AgentLogin is { } login) file["agentLogin"] = login;
        if (state.CategoryStarts.Count != 0)
        {
            var starts = new JsonObject();
            foreach (var (name, since) in state.CategoryStarts) starts[name] = since;
            file["enrollmentCategories"] = starts;
        }
        var temporary = statePath + ".tmp";
        File.WriteAllText(temporary, Kept(file, state.Unknown).ToJsonString(Indented));
        File.Move(temporary, statePath, true);
    }

    // A known key holding null reads as absent, as every reader of the untyped file read it. A known key in a form no writer
    // leaves is kept as read, so the bridge reads the record's other keys as it read them before the records were typed.
    static DiscussionRecord Record(JsonNode? node)
    {
        try
        {
            var item = node as JsonObject ?? throw new InvalidDataException("a Discussion record is not an object");
            var unreadable = new OrderedDictionary<string, UnreadableJson>();
            T? Read<T>(string name, Func<JsonNode, T> read, bool required = false) where T : class
            {
                if (item[name] is not { } value)
                {
                    if (required) unreadable[name] = new("null", Missing(name).Message);
                    return null;
                }
                try { return read(value); }
                catch (Exception error) when (Damaged(error)) { unreadable[name] = new(value.ToJsonString(), error.Message); return null; }
            }
            var record = new DiscussionRecord(Read("url", url => url.GetValue<string>()) ?? "")
            {
                // A message whose checkpoint is null is observed as new, as a missing one is.
                Sources = Read("sources", sources => new OrderedDictionary<string, SourceCheckpoint>(sources.AsObject().Where(checkpoint => checkpoint.Value is not null)
                    .Select(checkpoint => KeyValuePair.Create(checkpoint.Key, ReadCheckpoint(checkpoint.Value!.AsObject())))), required: true) ?? [],
                Labels = Read<IReadOnlyList<string>>("labels", labels => [.. labels.AsArray().Select(label => label?.GetValue<string>() ?? throw Missing("labels"))]),
                SessionId = Read("sessionId", session => session.GetValue<string>()), Waiting = Read("waiting", waiting => Waiting(waiting.AsObject())),
                Pending = item["pending"] is { } pending ? Pending(pending) : null,
                Running = Read("running", node =>
                {
                    var running = node.AsObject();
                    return new RunningTurn(Required(running, "turn"), At(running, "since")) { Unknown = Unknown(running, RunningKeys) };
                }),
                Unknown = Unknown(item, RecordKeys)
            };
            foreach (var (name, value) in unreadable) record.UnreadableKeys[name] = value;
            return record;
        }
        // Not an object, or one whose keys cannot be read (a duplicate key).
        catch (Exception error) when (Damaged(error)) { return new("") { Unreadable = new(node?.ToJsonString() ?? "null", error.Message) }; }
    }

    // Every start ends a pending delivery, so one in a form no writer leaves still says how many leading inputs it carried;
    // one whose keys cannot be read (a duplicate key) carries none.
    static PendingDelivery Pending(JsonNode node)
    {
        try
        {
            var pending = node.AsObject();
            return new(Required(pending, "operation"), At(pending, "since"), [.. Part<JsonArray>(pending, "sourceIds").Select(source =>
                source?.GetValue<string>() ?? throw Missing("sourceIds"))], pending["inputs"]?.GetValue<int>()) { Unknown = Unknown(pending, PendingKeys) };
        }
        catch (Exception error) when (Damaged(error))
        {
            return new("", default, [], error is not ArgumentException && node is JsonObject pending && pending["inputs"] is JsonValue inputs &&
                inputs.TryGetValue<int>(out var count) ? count : null) { AsRead = node.ToJsonString() };
        }
    }

    static JsonNode? Record(DiscussionRecord record)
    {
        if (record.Unreadable is { } unreadable) return JsonNode.Parse(unreadable.Json);
        var item = new JsonObject();
        foreach (var name in RecordKeys)
            if ((record.UnreadableKeys.TryGetValue(name, out var kept) ? JsonNode.Parse(kept.Json) : Written(record, name)) is { } value) item[name] = value;
        return Kept(item, record.Unknown);
    }

    static JsonNode? Written(DiscussionRecord record, string name)
    {
        switch (name)
        {
            case "url": return record.Url;
            case "sources":
                var sources = new JsonObject();
                foreach (var (id, checkpoint) in record.Sources) sources[id] = Checkpoint(checkpoint);
                return sources;
            case "labels": return record.Labels is { } labels ? new JsonArray([.. labels.Select(label => (JsonNode?)JsonValue.Create(label))]) : null;
            case "sessionId": return record.SessionId;
            case "waiting": return record.Waiting is { } waiting ? Waiting(waiting) : null;
            case "pending":
                if (record.Pending is not { } pending) return null;
                if (pending.AsRead is { } json) return JsonNode.Parse(json);
                var written = new JsonObject
                {
                    ["operation"] = pending.Operation, ["since"] = Time(pending.Since),
                    ["sourceIds"] = new JsonArray([.. pending.SourceIds.Select(source => (JsonNode?)JsonValue.Create(source))])
                };
                if (pending.Inputs is { } inputs) written["inputs"] = inputs;
                return Kept(written, pending.Unknown);
            default: return record.Running is { } running ? Kept(new JsonObject { ["turn"] = running.Turn, ["since"] = Time(running.Since) }, running.Unknown) : null;
        }
    }

    // Agent input (the Discussion, the bootstrap context and each change) is kept as the text the bridge sends; the Discussion
    // and context in any form, as the bridge copied them, and the bridge replaces the Discussion with each observed change.
    static WaitingInput Waiting(JsonObject item)
    {
        var waiting = new WaitingInput(item["discussion"]?.ToJsonString() ?? "null")
        {
            ContextJson = item["context"]?.ToJsonString(), Retry = item["retry"]?.AsObject() is { } retry ? Retry(retry) : null, Unknown = Unknown(item, WaitingKeys)
        };
        foreach (var node in (item["inputs"] ?? throw Missing("inputs")).AsArray())
        {
            var input = (node ?? throw Missing("inputs")).AsObject();
            // Any redelivery value marks a redelivery, as the bridge read it by its presence.
            // A time the bridge only ordered by is kept as read, and reads as the earliest.
            var at = input["observedAt"];
            var observed = at.OptionalText() is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time : DateTimeOffset.MinValue;
            waiting.Inputs.Add(new(observed, Part<JsonObject>(input, "change").ToJsonString(),
                input["receipt"] is { } receipt ? ReadReceipt(receipt.AsObject()) : null, input["redelivery"] is not null)
                { Unknown = Unknown(input, InputKeys), ObservedAtAsRead = at.OptionalText() == Time(observed) ? null : at?.ToJsonString() ?? "null" });
        }
        return waiting;
    }

    static JsonObject Waiting(WaitingInput waiting)
    {
        var item = new JsonObject
        {
            ["inputs"] = new JsonArray([.. waiting.Inputs.Select(input =>
            {
                var item = new JsonObject
                    { ["observedAt"] = input.ObservedAtAsRead is { } at ? JsonNode.Parse(at) : Time(input.ObservedAt), ["change"] = JsonNode.Parse(input.ChangeJson) };
                if (input.Receipt is { } receipt) item["receipt"] = Receipt(receipt);
                if (input.Redelivery) item["redelivery"] = true;
                return (JsonNode?)Kept(item, input.Unknown);
            })]),
        };
        if (JsonNode.Parse(waiting.DiscussionJson) is { } discussion) item["discussion"] = discussion;
        if (waiting.ContextJson is { } context) item["context"] = JsonNode.Parse(context);
        if (waiting.Retry is { } retry) item["retry"] = Retry(retry);
        return Kept(item, waiting.Unknown);
    }

    static JsonObject Retry(DeliveryRetry retry) => retry.AsRead is { } json ? JsonNode.Parse(json)!.AsObject()
        : Kept(new JsonObject { ["attempts"] = retry.Attempts, ["after"] = Time(retry.After) }, retry.Unknown);

    // As delivery read it: a retry without a time is due, and attempts that are not a number count as none.
    static DeliveryRetry Retry(JsonObject retry)
    {
        var read = new DeliveryRetry(retry["attempts"] is JsonValue attempts && attempts.TryGetValue<int>(out var count) ? count : 0,
            retry["after"] is null ? DateTimeOffset.MinValue : At(retry, "after")) { Unknown = Unknown(retry, RetryKeys) };
        return JsonNode.DeepEquals(Retry(read), retry) ? read : read with { AsRead = retry.ToJsonString() };
    }

    /// <summary>A message's checkpoint as the state file holds it, and as ec1ccf7 saved it.</summary>
    internal static JsonObject Checkpoint(SourceCheckpoint checkpoint)
    {
        if (checkpoint.AsRead is { } json) return JsonNode.Parse(json)!.AsObject();
        var reactions = new JsonObject();
        foreach (var reaction in checkpoint.Reactions) reactions[reaction.Id] = DiscussionJson.Public(reaction);
        var version = new JsonObject { ["content"] = Content(checkpoint.Content), ["state"] = State(checkpoint.State), ["reactions"] = reactions };
        if (checkpoint.ReplyTo is { } root) version["replyTo"] = root;
        return version;
    }

    // Actor type is observation metadata; adding it must not replay accepted content.
    static JsonObject Content(CheckpointContent content) =>
        new() { ["body"] = content.Body, ["author"] = content.Author is { } login ? new JsonObject { ["login"] = login } : null };

    static JsonObject State(CheckpointState? state) => state is null ? [] : new()
    {
        ["title"] = state.Title, ["closed"] = state.Closed, ["locked"] = state.Locked, ["answer"] = DiscussionJson.Answer(state.AnswerId),
        ["category"] = DiscussionJson.Category(state.Category)
    };

    // Read as observation compared it before the records were typed: each part is read where it can be, and its content, or
    // a state key, whose JSON differs from what is written for what was read compares as changed (ReadAsChanged). Reactions
    // were compared by key only; one that cannot be read keeps only its ID. A duplicate key throws, as comparing it did.
    internal static SourceCheckpoint ReadCheckpoint(JsonObject checkpoint)
    {
        var content = checkpoint["content"] as JsonObject;
        var read = new CheckpointContent(content?["body"].OptionalText() ?? "", content?["author"] is JsonObject author ? author["login"].OptionalText() : null);
        var changed = JsonNode.DeepEquals(Content(read), content) ? new List<string>() : ["content"];
        var state = checkpoint["state"] as JsonObject ?? [];
        var category = state["category"] as JsonObject;
        var observed = state.Count == 0 ? null : new CheckpointState(state["title"].OptionalText() ?? "", IsTrue(state["closed"]), IsTrue(state["locked"]),
            (state["answer"] as JsonObject)?["id"].OptionalText(),
            category?["id"].OptionalText() is { } id && category["name"].OptionalText() is { } name ? new(id, name) : null);
        var written = State(observed);
        changed.AddRange(written.Select(field => field.Key).Union(state.Select(field => field.Key)).Where(key => !JsonNode.DeepEquals(written[key], state[key])));
        var reactions = (checkpoint["reactions"]?.AsObject() ?? []).Select(entry => (entry.Value is JsonObject reaction ? Lenient(() => Reaction(reaction)) : null) ??
            new(entry.Key, "", null, default));
        var checkpointed = new SourceCheckpoint(read, observed, [.. reactions], checkpoint["replyTo"].OptionalText()) { ReadAsChanged = changed };
        return JsonNode.DeepEquals(Checkpoint(checkpointed), checkpoint) ? checkpointed : checkpointed with { AsRead = checkpoint.ToJsonString() };

        static bool IsTrue(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
        static Reaction Reaction(JsonObject reaction) => new(Required(reaction, "id"), Required(reaction, "content"),
            reaction["user"]?.AsObject() is { } user ? new Actor(Required(user, "login"), Required(user, "__typename")) : null,
            DateTimeOffset.Parse(Required(reaction, "createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));
        static Reaction? Lenient(Func<Reaction> read)
        {
            try { return read(); }
            catch (Exception error) when (error is not ArgumentException && Damaged(error)) { return null; }
        }
    }

    /// <summary>A waiting input's receipt as the state file holds it, and as ec1ccf7 saved it.</summary>
    internal static JsonObject Receipt(EyesReceipt receipt) => receipt.AsRead is { } json ? JsonNode.Parse(json)!.AsObject() : new JsonObject
    {
        ["id"] = receipt.SubjectId, ["url"] = receipt.Url,
        ["reactionGroups"] = new JsonArray([.. receipt.ReactionGroups.Select(group => (JsonNode?)new JsonObject
            { ["content"] = group.Content, ["viewerHasReacted"] = group.ViewerHasReacted })])
    };

    internal static EyesReceipt ReadReceipt(JsonObject receipt)
    {
        var read = new EyesReceipt(Required(receipt, "id"), Required(receipt, "url"), [.. Part<JsonArray>(receipt, "reactionGroups")
            .Select(node => node as JsonObject ?? throw Missing("reactionGroups")).Select(group => new ReactionGroup(Required(group, "content"), Flag(group, "viewerHasReacted")))]);
        return JsonNode.DeepEquals(Receipt(read), receipt) ? read : read with { AsRead = receipt.ToJsonString() };
    }

    // The state file's own documents read strictly: a key the writers always write is required, and another type throws.
    static string Required(JsonObject item, string name) => item[name]?.GetValue<string>() ?? throw Missing(name);
    static T Part<T>(JsonObject item, string name) where T : JsonNode => item[name] as T ?? throw Missing(name);
    static bool Flag(JsonObject item, string name) => item[name]?.GetValue<bool>() ?? throw Missing(name);
    static DateTimeOffset At(JsonObject item, string name) =>
        DateTimeOffset.Parse(Required(item, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    static InvalidDataException Missing(string name) => new($"'{name}' is missing or not of its type");

    // What reading a value in another form throws: another type (InvalidOperationException), a missing key
    // (InvalidDataException), an unreadable time (FormatException), or a duplicate key, once its object is first read
    // (ArgumentException).
    static bool Damaged(Exception error) => error is InvalidOperationException or InvalidDataException or FormatException or ArgumentException;
    static string Time(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    static IReadOnlyList<UnknownKey> Unknown(JsonObject item, string[] known) =>
        [.. item.Where(field => !known.Contains(field.Key)).Select(field => new UnknownKey(field.Key, field.Value?.ToJsonString() ?? "null"))];

    // Written after the keys this controller writes, unless one of those took its name.
    static JsonObject Kept(JsonObject item, IReadOnlyList<UnknownKey> unknown)
    {
        foreach (var key in unknown) if (!item.ContainsKey(key.Name)) item[key.Name] = JsonNode.Parse(key.Json);
        return item;
    }

    /// <summary>The recorded settings of the controller whose state file is <paramref name="statePath"/>.</summary>
    public static string SettingsPath(string statePath) => statePath + ".settings.json";

    /// <summary>The settings the controller whose state file is <paramref name="statePath"/> last recorded, as recorded, or null
    /// when there are none.</summary>
    public static JsonObject? SettingsJson(string statePath)
    {
        var file = SettingsPath(statePath);
        try { return File.Exists(file) && JsonNode.Parse(File.ReadAllText(file))?["settings"] is JsonObject settings ? settings.Copy().AsObject() : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    // An entry with a value but no readable time is ignored. A value is retained whatever state was recorded with it, so an
    // entry of an unknown state reads as pending: not yet observed by this lifetime.
    public RecordedSettings? ReadSettings()
    {
        if (SettingsJson(statePath) is not { } settings) return null;
        var recorded = new Dictionary<string, SettingValue>();
        foreach (var (name, entry) in settings)
        {
            if (entry is not JsonObject item) continue;
            var state = SettingStates.Parse(item["state"].OptionalText()) ?? SettingState.Pending;
            var value = item["value"].OptionalText();
            var since = default(DateTimeOffset);
            if (value is not null && !(item["since"].OptionalText() is { } at &&
                DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out since))) continue;
            recorded[name] = new SettingValue(state, value, item["reason"].OptionalText()) { Since = since };
        }
        return new RecordedSettings(recorded);
    }

    public void WriteSettings(RecordedSettings recorded)
    {
        var settings = new JsonObject();
        foreach (var (name, value) in recorded.Settings)
        {
            var item = Json.Object(("state", value.State.Name()));
            if (value.Value is not null) { item["value"] = value.Value; item["since"] = value.Since.ToString("O", CultureInfo.InvariantCulture); }
            if (value.Reason is not null) item["reason"] = value.Reason;
            settings[name] = item;
        }
        var path = SettingsPath(statePath);
        var temporary = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var file = new FileStream(temporary, options))
            file.Write(System.Text.Encoding.UTF8.GetBytes(Json.Object(("version", 1), ("settings", settings)).ToJsonString()));
        File.Move(temporary, path, true);
    }

    /// <summary>The GitHub operations the outbox of the controller whose state file is <paramref name="statePath"/> retains.</summary>
    public static string OutboxPath(string statePath) => statePath + ".outbox";

    public OutboxState? ReadOutbox()
    {
        var path = OutboxPath(statePath);
        if (!File.Exists(path)) return null;
        var state = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (state["version"]?.GetValue<int>() != 1 || state["operations"] is not JsonArray operations)
            throw new InvalidOperationException("GitHub outbox belongs to a different repository or unsupported version; preserve it for manual inspection");
        try
        {
            // Read every retained field once, as delivery will, so a damaged file stops startup instead of the loop.
            return new(state.Text("repository"), state["pausedUntil"] is { } paused ? Time(paused) : null, [.. operations.Select(item =>
            {
                var operation = item!.AsObject();
                var (key, kind, subject, url) = (operation.Text("key"), operation.Text("kind"), operation.Text("subjectId"), operation.Text("url"));
                _ = key[0];
                var (category, title, body) = (Optional(operation, "category"), Optional(operation, "title"), Optional(operation, "body"));
                // A receipt or dashboard with fields its record does not hold is kept as read, like an operation of another kind, so
                // the next write keeps them.
                OutboxOperation read = (kind, category, title, body) switch
                {
                    ("receipt", null, null, null) => new ReceiptOperation(subject, url) { Key = key },
                    ("dashboard", not null, not null, not null) when (subject, url) is ("", "") => new DashboardOperation(category, title, body) { Key = key },
                    _ => new UnsupportedOperation(key, kind, subject, url, category, title, body)
                };
                return read with
                {
                    Attempts = operation["attempts"]!.GetValue<int>(), DueAt = operation["dueAt"] is { } due ? Time(due) : DateTimeOffset.MinValue,
                    Uncertain = operation["uncertain"]!.GetValue<bool>(), LastError = operation["lastError"].OptionalText()
                };
            })]);
        }
        catch (Exception error) { throw new InvalidOperationException("GitHub outbox contains an unreadable operation; preserve it for manual inspection", error); }

        static string? Optional(JsonObject operation, string name) => operation[name] is null ? null : operation.Text(name);
        static DateTimeOffset Time(JsonNode value) => DateTimeOffset.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    public void WriteOutbox(OutboxState outbox)
    {
        var operations = new JsonArray();
        foreach (var retained in outbox.Operations)
        {
            var (kind, subject, url, category, title, body) = retained switch
            {
                ReceiptOperation receipt => ("receipt", receipt.SubjectId, receipt.Url, null, null, null),
                DashboardOperation update => ("dashboard", "", "", update.Category, update.Title, update.Body),
                UnsupportedOperation other => (other.Kind, other.SubjectId, other.Url, other.Category, other.Title, other.Body),
                _ => throw new ArgumentOutOfRangeException(nameof(outbox), $"outbox operation '{retained.Key}' has no file form")
            };
            var operation = Json.Object(("key", retained.Key), ("kind", kind), ("subjectId", subject), ("url", url));
            if (category is not null) operation["category"] = category;
            if (title is not null) operation["title"] = title;
            if (body is not null) operation["body"] = body;
            operation["attempts"] = retained.Attempts;
            operation["dueAt"] = retained.DueAt.ToString("O", CultureInfo.InvariantCulture);
            operation["uncertain"] = retained.Uncertain;
            operation["lastError"] = retained.LastError;
            operations.Add((JsonNode)operation);
        }
        var state = Json.Object(("version", 1), ("repository", outbox.Repository),
            ("pausedUntil", outbox.PausedUntil?.ToString("O", CultureInfo.InvariantCulture)), ("operations", operations));
        var path = OutboxPath(statePath);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, state.ToJsonString(Indented));
        File.Move(temporary, path, true);
    }
}
