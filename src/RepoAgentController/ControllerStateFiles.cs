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
    // stops startup as it did. Each Discussion record is read whole: one in a form no writer leaves (a key every writer writes is
    // missing, or a value has another type) is kept as read. A key no writer writes is kept and written back, at any depth.
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

    // A known key holding null reads as absent, as every reader of the untyped file read it.
    static DiscussionRecord Record(JsonNode? node)
    {
        try
        {
            var item = node as JsonObject ?? throw new InvalidDataException("a Discussion record is not an object");
            var record = new DiscussionRecord(Required(item, "url"))
            {
                Labels = item["labels"] is { } labels ? [.. labels.AsArray().Select(label => label?.GetValue<string>() ?? throw Missing("labels"))] : null,
                SessionId = item["sessionId"]?.GetValue<string>(), Waiting = item["waiting"] is { } waiting ? Waiting(waiting.AsObject()) : null,
                Pending = item["pending"]?.AsObject() is { } pending ? new(Required(pending, "operation"), At(pending, "since"),
                    [.. Part<JsonArray>(pending, "sourceIds").Select(source => source?.GetValue<string>() ?? throw Missing("sourceIds"))], pending["inputs"]?.GetValue<int>())
                    { Unknown = Unknown(pending, PendingKeys) } : null,
                Running = item["running"]?.AsObject() is { } running ? new(Required(running, "turn"), At(running, "since")) { Unknown = Unknown(running, RunningKeys) } : null,
                Unknown = Unknown(item, RecordKeys)
            };
            foreach (var (id, checkpoint) in Part<JsonObject>(item, "sources"))
                record.Sources[id] = ReadCheckpoint(checkpoint as JsonObject ?? throw Missing("sources"));
            return record;
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or FormatException)
        {
            var item = node as JsonObject;
            return new(item?["url"].OptionalText() ?? "") { SessionId = item?["sessionId"].OptionalText(), Unreadable = new(node?.ToJsonString() ?? "null", error.Message) };
        }
    }

    static JsonNode? Record(DiscussionRecord record)
    {
        if (record.Unreadable is { } unreadable) return JsonNode.Parse(unreadable.Json);
        var sources = new JsonObject();
        foreach (var (id, checkpoint) in record.Sources) sources[id] = Checkpoint(checkpoint);
        var item = new JsonObject { ["url"] = record.Url, ["sources"] = sources };
        if (record.Labels is { } labels) item["labels"] = new JsonArray([.. labels.Select(label => (JsonNode?)JsonValue.Create(label))]);
        if (record.SessionId is { } session) item["sessionId"] = session;
        if (record.Waiting is { } waiting) item["waiting"] = Waiting(waiting);
        if (record.Pending is { } pending)
        {
            var written = new JsonObject
            {
                ["operation"] = pending.Operation, ["since"] = Time(pending.Since),
                ["sourceIds"] = new JsonArray([.. pending.SourceIds.Select(source => (JsonNode?)JsonValue.Create(source))])
            };
            if (pending.Inputs is { } inputs) written["inputs"] = inputs;
            item["pending"] = Kept(written, pending.Unknown);
        }
        if (record.Running is { } running) item["running"] = Kept(new JsonObject { ["turn"] = running.Turn, ["since"] = Time(running.Since) }, running.Unknown);
        return Kept(item, record.Unknown);
    }

    // Agent input (the Discussion, the bootstrap context and each change) is kept as the text the bridge sends.
    static WaitingInput Waiting(JsonObject item)
    {
        var waiting = new WaitingInput(Part<JsonObject>(item, "discussion").ToJsonString())
        {
            Context = item["context"]?.AsObject().ToJsonString(),
            Retry = item["retry"]?.AsObject() is { } retry ? new(Number(retry, "attempts"), At(retry, "after")) { Unknown = Unknown(retry, RetryKeys) } : null,
            Unknown = Unknown(item, WaitingKeys)
        };
        foreach (var node in Part<JsonArray>(item, "inputs"))
        {
            var input = node as JsonObject ?? throw Missing("inputs");
            // Any redelivery value marks a redelivery, as the bridge read it by its presence.
            waiting.Inputs.Add(new(At(input, "observedAt"), Part<JsonObject>(input, "change").ToJsonString(),
                input["receipt"] is { } receipt ? ReadReceipt(receipt.AsObject()) : null, input["redelivery"] is not null) { Unknown = Unknown(input, InputKeys) });
        }
        return waiting;
    }

    static JsonObject Waiting(WaitingInput waiting)
    {
        var item = new JsonObject
        {
            ["inputs"] = new JsonArray([.. waiting.Inputs.Select(input =>
            {
                var item = new JsonObject { ["observedAt"] = Time(input.ObservedAt), ["change"] = JsonNode.Parse(input.Change) };
                if (input.Receipt is { } receipt) item["receipt"] = Receipt(receipt);
                if (input.Redelivery) item["redelivery"] = true;
                return (JsonNode?)Kept(item, input.Unknown);
            })]),
            ["discussion"] = JsonNode.Parse(waiting.Discussion)
        };
        if (waiting.Context is { } context) item["context"] = JsonNode.Parse(context);
        if (waiting.Retry is { } retry) item["retry"] = Kept(new JsonObject { ["attempts"] = retry.Attempts, ["after"] = Time(retry.After) }, retry.Unknown);
        return Kept(item, waiting.Unknown);
    }

    /// <summary>A message's checkpoint as the state file holds it, and as ec1ccf7 saved it.</summary>
    internal static JsonObject Checkpoint(SourceCheckpoint checkpoint)
    {
        if (checkpoint.AsRead is { } json) return JsonNode.Parse(json)!.AsObject();
        var reactions = new JsonObject();
        foreach (var reaction in checkpoint.Reactions) reactions[reaction.Id] = DiscussionJson.Public(reaction);
        var version = new JsonObject
        {
            // Actor type is observation metadata; adding it must not replay accepted content.
            ["content"] = new JsonObject { ["body"] = checkpoint.Content.Body, ["author"] = checkpoint.Content.Author is { } login ? new JsonObject { ["login"] = login } : null },
            ["state"] = checkpoint.State is { } state ? new JsonObject
            {
                ["title"] = state.Title, ["closed"] = state.Closed, ["locked"] = state.Locked, ["answer"] = DiscussionJson.Answer(state.AnswerId),
                ["category"] = DiscussionJson.Category(state.Category)
            } : new JsonObject(),
            ["reactions"] = reactions
        };
        if (checkpoint.ReplyTo is { } root) version["replyTo"] = root;
        return version;
    }

    internal static SourceCheckpoint ReadCheckpoint(JsonObject checkpoint)
    {
        var content = Part<JsonObject>(checkpoint, "content");
        var state = Part<JsonObject>(checkpoint, "state");
        var read = new SourceCheckpoint(new(Required(content, "body"), content["author"] is { } author ? Required(author.AsObject(), "login") : null),
            state.Count == 0 ? null : new(Required(state, "title"), Flag(state, "closed"), Flag(state, "locked"),
                state["answer"] is { } answer ? Required(answer.AsObject(), "id") : null,
                state["category"]?.AsObject() is { } category ? new(Required(category, "id"), Required(category, "name")) : null),
            [.. Part<JsonObject>(checkpoint, "reactions").Select(entry => entry.Value as JsonObject ?? throw Missing("reactions")).Select(reaction =>
                new Reaction(Required(reaction, "id"), Required(reaction, "content"),
                    reaction["user"]?.AsObject() is { } user ? new Actor(Required(user, "login"), Required(user, "__typename")) : null,
                    DateTimeOffset.Parse(Required(reaction, "createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)))],
            checkpoint["replyTo"]?.GetValue<string>());
        return JsonNode.DeepEquals(Checkpoint(read), checkpoint) ? read : read with { AsRead = checkpoint.ToJsonString() };
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
    static int Number(JsonObject item, string name) => item[name]?.GetValue<int>() ?? throw Missing(name);
    static bool Flag(JsonObject item, string name) => item[name]?.GetValue<bool>() ?? throw Missing(name);
    static DateTimeOffset At(JsonObject item, string name) =>
        DateTimeOffset.Parse(Required(item, name), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    static InvalidDataException Missing(string name) => new($"'{name}' is missing or not of its type");
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
