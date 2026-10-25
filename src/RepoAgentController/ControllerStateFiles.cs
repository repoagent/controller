using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>The <see cref="IControllerState"/> adapter: files beside the controller's state file.</summary>
sealed class ControllerStateFiles(string statePath) : IControllerState
{
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
                var key = operation.Text("key");
                _ = key[0];
                return new OutboxOperation(key, operation.Text("kind"), operation.Text("subjectId"), operation.Text("url"), Optional(operation, "category"),
                    Optional(operation, "title"), Optional(operation, "body"), operation["attempts"]!.GetValue<int>(), operation["dueAt"] is { } due ? Time(due) : DateTimeOffset.MinValue,
                    operation["uncertain"]!.GetValue<bool>(), operation["lastError"].OptionalText());
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
            var operation = Json.Object(("key", retained.Key), ("kind", retained.Kind), ("subjectId", retained.SubjectId), ("url", retained.Url));
            if (retained.Category is not null) operation["category"] = retained.Category;
            if (retained.Title is not null) operation["title"] = retained.Title;
            if (retained.Body is not null) operation["body"] = retained.Body;
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
        File.WriteAllText(temporary, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }
}
