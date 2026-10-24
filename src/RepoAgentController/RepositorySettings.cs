using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RepoAgentController;

/// <summary>One supported nonsecret setting declared in the shared repository-settings.json catalogue.</summary>
/// <remarks>scripts/repository-settings.ps1 applies the same catalogue and rules before an operator writes a value.
/// An integer is bounded by Minimum and Maximum; a category-names list by its name count. A token has no bounds.</remarks>
sealed record RepositorySetting(string Name, string Type, long Minimum, long Maximum)
{
    public const int MaximumCategoryName = 100;

    /// <summary>Returns the normalized value, or null with a reason when the repository value is invalid.</summary>
    public string? Normalize(string value, out string reason)
    {
        if (Type == "category-names")
        {
            reason = $"must list {Minimum} to {Maximum} distinct Discussion category names separated by commas, each at most {MaximumCategoryName} characters";
            var names = value.Split(',', StringSplitOptions.TrimEntries);
            return names.Length >= Minimum && names.Length <= Maximum && names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length &&
                names.All(name => name.Length is > 0 and <= MaximumCategoryName && !name.Any(char.IsControl)) ? string.Join(", ", names) : null;
        }
        if (Type == "token")
        {
            // A model or reasoning name; whether the Codex account offers it is checked when a turn uses it.
            reason = "must be a name of at most 64 letters, digits, '.', '_' or '-'";
            return ModelSelection.Normalize(value);
        }
        reason = $"must be an integer from {Minimum} to {Maximum}";
        var text = value.Trim();
        return Regex.IsMatch(text, @"\A[0-9]{1,18}\z") && long.Parse(text, CultureInfo.InvariantCulture) is var number &&
            number >= Minimum && number <= Maximum ? number.ToString(CultureInfo.InvariantCulture) : null;
    }
}

/// <summary>Reads validated controller settings from direct repository Actions variables with bounded refresh.</summary>
/// <remarks>Without a GitHub adapter the runtime has no explicit Variables: read permission; every setting stays disabled.
/// Each refresh records the observed settings beside the controller state (<see cref="PathFor"/>) for status, and a later
/// lifetime starts from the effective values recorded there, so a failed or invalid read never discards a known value.</remarks>
sealed class RepositorySettings
{
    /// <summary>Changes apply within one interval; each refresh is one GET per catalogue entry.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    public static IReadOnlyList<RepositorySetting> Catalogue { get; } = Load();
    readonly GitHub? github;
    readonly TimeProvider clock;
    readonly string? path;
    IReadOnlyDictionary<string, SettingValue> values;

    /// <summary>The observed settings of the controller whose state file is <paramref name="statePath"/>.</summary>
    public static string PathFor(string statePath) => statePath + ".settings.json";

    public RepositorySettings(GitHub? github, TimeProvider clock, string? statePath = null)
    {
        (this.github, this.clock, path) = (github, clock, statePath is null ? null : PathFor(statePath));
        var recorded = github is null ? null : Recorded(path);
        values = Catalogue.ToDictionary(s => s.Name, s => recorded?.GetValueOrDefault(s.Name) is { } known
            ? new SettingValue("pending", known.Value) { Since = known.Since } : new SettingValue(github is null ? "disabled" : "pending", null));
    }

    public SettingValue this[string name] => Volatile.Read(ref values)[name];

    /// <summary>Raised after a refresh that changed any setting's state, value or reason.</summary>
    public event Action? Changed;

    /// <summary>Reads every setting once; returns whether any setting's state, value or reason changed.</summary>
    public async Task<bool> Refresh(CancellationToken ct)
    {
        if (github is null)
        {
            Log.Write("repository-settings", "state=disabled reason=\"start with --repository-settings and Variables: read to use REPOAGENT_ variables\"");
            Record(values);
            return false;
        }
        var previous = Volatile.Read(ref values);
        var next = new Dictionary<string, SettingValue>();
        var changed = false;
        foreach (var setting in Catalogue)
        {
            var retained = previous[setting.Name].Value;
            SettingValue observed;
            try
            {
                // Absence is confirmed by GitHub. Invalid values and failed reads keep the last effective value.
                var raw = await github.Variable(setting.Name, ct);
                observed = raw is null ? new("absent", null)
                    : setting.Normalize(raw, out var reason) is { } value ? new("configured", value) : new("invalid", retained, reason);
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                observed = new("read-failed", retained, error is GitHubRequestException { Status: { } status } ? $"HTTP {status}" : "GitHub read failed");
            }
            observed = observed with { Since = observed.Value == previous[setting.Name].Value ? previous[setting.Name].Since : clock.GetUtcNow() };
            next[setting.Name] = observed;
            if (observed != previous[setting.Name]) { changed = true; Log.Write("repository-setting", Describe(setting.Name, observed)); }
        }
        Volatile.Write(ref values, next);
        if (changed || path is not null && !File.Exists(path)) Record(next);
        if (changed) Changed?.Invoke();
        return changed;
    }

    // Diagnostic and best effort, like status: a failed write keeps the controller running on the values in memory.
    void Record(IReadOnlyDictionary<string, SettingValue> current)
    {
        if (path is null) return;
        var settings = new JsonObject();
        foreach (var (name, value) in current)
        {
            var item = Json.Object(("state", value.State));
            if (value.Value is not null) { item["value"] = value.Value; item["since"] = value.Since.ToString("O", CultureInfo.InvariantCulture); }
            if (value.Reason is not null) item["reason"] = value.Reason;
            settings[name] = item;
        }
        var temporary = path + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options))
                file.Write(System.Text.Encoding.UTF8.GetBytes(Json.Object(("version", 1), ("settings", settings)).ToJsonString()));
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Write("repository-settings-error", $"the observed settings could not be recorded: {error.Message}");
        }
    }

    /// <summary>The settings a controller last recorded at <paramref name="file"/>, or null when there are none.</summary>
    public static JsonObject? Read(string file)
    {
        try { return File.Exists(file) && JsonNode.Parse(File.ReadAllText(file))?["settings"] is JsonObject settings ? settings.Copy().AsObject() : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    // Effective values a previous lifetime recorded, for catalogue entries whose recorded value is still valid.
    static Dictionary<string, (string Value, DateTimeOffset Since)>? Recorded(string? file)
    {
        if (file is null || Read(file) is not { } settings) return null;
        var known = new Dictionary<string, (string, DateTimeOffset)>();
        foreach (var setting in Catalogue)
            if (settings[setting.Name] is JsonObject item && item["value"].OptionalText() is { } text &&
                setting.Normalize(text, out _) == text && item["since"].OptionalText() is { } at &&
                DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var observed))
                known[setting.Name] = (text, observed);
        return known;
    }

    /// <summary>Refreshes every <see cref="RefreshInterval"/>. After a refresh that changed a setting, <paramref name="apply"/>
    /// completes before the next refresh, so it observes every changed value in order.</summary>
    public async Task RefreshUntilCancelled(Func<CancellationToken, Task> apply, CancellationToken ct)
    {
        if (github is null) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RefreshInterval, clock, ct);
                if (await Refresh(ct)) await apply(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    static string Describe(string name, SettingValue value) => $"name={name} state={value.State}" +
        (value.Value is null ? "" : $" value={value.Value}") + (value.Reason is null ? "" : $" reason=\"{value.Reason}\"");

    static IReadOnlyList<RepositorySetting> Load()
    {
        using var stream = typeof(RepositorySettings).Assembly.GetManifestResourceStream("repository-settings.json")
            ?? throw new InvalidOperationException("Missing repository settings catalogue");
        var catalogue = JsonNode.Parse(stream) ?? throw new InvalidOperationException("Empty repository settings catalogue");
        if (catalogue["schemaVersion"]?.GetValue<int>() != 1 || catalogue["settings"] is not JsonArray entries ||
            catalogue["reserved"] is not JsonArray reservedNames)
            throw new InvalidOperationException("Unsupported repository settings catalogue");
        // Reserved REPOAGENT_ variables belong to other owners (the approval marker); they are never settings.
        var reserved = reservedNames.Select(name => name?.GetValue<string>() is { } text && Regex.IsMatch(text, @"\AREPOAGENT_[A-Z0-9_]{1,90}\z")
            ? text : throw new InvalidOperationException("Invalid reserved repository variable")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settings = entries.Select(entry => entry is JsonObject fields &&
            Regex.IsMatch(fields.Text("name"), @"\AREPOAGENT_[A-Z0-9_]{1,90}\z") && fields.Text("description").Length is > 0 and <= 200
            ? fields.Text("type") switch
            {
                "integer" or "category-names" when fields.Count == 5 && fields["minimum"]!.GetValue<long>() is >= 0 and var minimum &&
                    fields["maximum"]!.GetValue<long>() is var maximum && maximum >= minimum => new RepositorySetting(fields.Text("name"), fields.Text("type"), minimum, maximum),
                "token" when fields.Count == 3 => new RepositorySetting(fields.Text("name"), "token", 0, 0),
                _ => throw new InvalidOperationException("Invalid repository settings catalogue entry")
            }
            : throw new InvalidOperationException("Invalid repository settings catalogue entry")).ToList();
        if (settings.DistinctBy(setting => setting.Name).Count() != settings.Count) throw new InvalidOperationException("Duplicate repository setting");
        if (settings.Any(setting => reserved.Contains(setting.Name))) throw new InvalidOperationException("Reserved repository variable used as a setting");
        return settings;
    }
}
