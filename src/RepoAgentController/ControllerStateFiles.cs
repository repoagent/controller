using System.Globalization;
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
}
