using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RepoAgentController;

/// <summary>One supported nonsecret setting declared in the shared repository-settings.json catalogue.</summary>
/// <remarks>scripts/repository-settings.ps1 applies the same catalogue and rules before an operator writes a value.</remarks>
sealed record RepositorySetting(string Name, long Minimum, long Maximum, bool Token = false)
{
    /// <summary>Returns the normalized value, or null with a reason when the repository value is invalid.</summary>
    public string? Normalize(string value, out string reason)
    {
        if (Token)
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

/// <summary>The effective value after the latest observation; Value is null when nothing applies.</summary>
sealed record SettingValue(string State, string? Value, string? Reason = null);

/// <summary>Reads validated controller settings from direct repository Actions variables with bounded refresh.</summary>
/// <remarks>Without a GitHub adapter the runtime has no explicit Variables: read permission; every setting stays disabled.</remarks>
sealed class RepositorySettings(GitHub? github, TimeProvider clock)
{
    /// <summary>Changes apply within one interval; each refresh is one GET per catalogue entry.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    public static IReadOnlyList<RepositorySetting> Catalogue { get; } = Load();
    IReadOnlyDictionary<string, SettingValue> values = Catalogue.ToDictionary(s => s.Name, _ => new SettingValue(github is null ? "disabled" : "pending", null));

    public SettingValue this[string name] => Volatile.Read(ref values)[name];

    public async Task Refresh(CancellationToken ct)
    {
        if (github is null)
        {
            Log.Write("repository-settings", "state=disabled reason=\"start with --repository-settings and Variables: read to use REPOAGENT_ variables\"");
            return;
        }
        var previous = Volatile.Read(ref values);
        var next = new Dictionary<string, SettingValue>();
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
                observed = new("read-failed", retained, error is GitHub.StatusException status ? $"HTTP {status.Status}" : "GitHub read failed");
            }
            next[setting.Name] = observed;
            if (observed != previous[setting.Name]) Log.Write("repository-setting", Describe(setting.Name, observed));
        }
        Volatile.Write(ref values, next);
    }

    public async Task RefreshUntilCancelled(CancellationToken ct)
    {
        if (github is null) return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RefreshInterval, clock, ct);
                await Refresh(ct);
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
                "integer" when fields.Count == 5 && fields["minimum"]!.GetValue<long>() is >= 0 and var minimum &&
                    fields["maximum"]!.GetValue<long>() is var maximum && maximum >= minimum => new RepositorySetting(fields.Text("name"), minimum, maximum),
                "token" when fields.Count == 3 => new RepositorySetting(fields.Text("name"), 0, 0, Token: true),
                _ => throw new InvalidOperationException("Invalid repository settings catalogue entry")
            }
            : throw new InvalidOperationException("Invalid repository settings catalogue entry")).ToList();
        if (settings.DistinctBy(setting => setting.Name).Count() != settings.Count) throw new InvalidOperationException("Duplicate repository setting");
        if (settings.Any(setting => reserved.Contains(setting.Name))) throw new InvalidOperationException("Reserved repository variable used as a setting");
        return settings;
    }
}
