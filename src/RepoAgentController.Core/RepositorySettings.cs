using System.Globalization;
using System.Text.RegularExpressions;

namespace RepoAgentController;

/// <summary>One supported nonsecret setting declared in the shared repository-settings.json catalogue.</summary>
/// <remarks>scripts/repository-settings.ps1 applies the same catalogue and rules before an operator writes a value.
/// An integer is bounded by Minimum and Maximum; a category-names list by its name count. A token has no bounds.</remarks>
public sealed record RepositorySetting(string Name, string Type, long Minimum, long Maximum)
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

/// <summary>Reads validated controller settings from direct repository Agent variables with bounded refresh.</summary>
/// <remarks>Without a GitHub adapter the runtime has no explicit Agent variables: read permission; every setting stays disabled.
/// Each refresh records the observed settings in the controller state for status, and a later lifetime starts from the
/// effective values recorded there, so a failed or invalid read never discards a known value.</remarks>
public sealed class RepositorySettings
{
    /// <summary>Changes apply within one interval; each refresh is one read per catalogue entry.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    readonly IReadOnlyList<RepositorySetting> catalogue;
    readonly IRepositorySettings? variables;
    readonly IControllerState state;
    readonly TimeProvider clock;
    readonly IControllerLog log;
    IReadOnlyDictionary<string, SettingValue> values;

    /// <param name="variables">The repository's settings, or null when the controller runs without repository settings.</param>
    public RepositorySettings(IReadOnlyList<RepositorySetting> catalogue, IRepositorySettings? variables, IControllerState state, TimeProvider clock,
        IControllerLog log)
    {
        (this.catalogue, this.variables, this.state, this.clock, this.log) = (catalogue, variables, state, clock, log);
        var recorded = variables is null ? null : state.ReadSettings()?.Settings;
        values = catalogue.ToDictionary(s => s.Name, s => Retained(s, recorded) is { } known
            ? new SettingValue(SettingState.Pending, known.Value) { Since = known.Since } : new SettingValue(variables is null ? SettingState.Disabled : SettingState.Pending, null));
    }

    public SettingValue this[string name] => Volatile.Read(ref values)[name];

    /// <summary>Raised after a refresh that changed any setting's state, value or reason.</summary>
    public event Action? Changed;

    /// <summary>Reads every setting once; returns whether any setting's state, value or reason changed.</summary>
    public async Task<bool> Refresh(CancellationToken ct)
    {
        if (variables is null)
        {
            log.Write("repository-settings", "state=disabled reason=\"start with --repository-settings and Agent variables: read to use REPOAGENT_ variables\"");
            Record(values);
            return false;
        }
        var previous = Volatile.Read(ref values);
        var next = new Dictionary<string, SettingValue>();
        var changed = false;
        foreach (var setting in catalogue)
        {
            var retained = previous[setting.Name].Value;
            SettingValue observed;
            try
            {
                // Absence is confirmed by GitHub. Invalid values and failed reads keep the last effective value.
                var raw = await variables.Read(setting.Name, ct);
                observed = raw is null ? new(SettingState.Absent, null)
                    : setting.Normalize(raw, out var reason) is { } value ? new(SettingState.Configured, value) : new(SettingState.Invalid, retained, reason);
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                observed = new(SettingState.ReadFailed, retained, error switch
                {
                    RepositorySettingRejectedException { Status: var status } => $"HTTP {status}",
                    RepositorySettingsUnreadableException unreadable => unreadable.Reason,
                    _ => "GitHub read failed"
                });
            }
            observed = observed with { Since = observed.Value == previous[setting.Name].Value ? previous[setting.Name].Since : clock.GetUtcNow() };
            next[setting.Name] = observed;
            if (observed != previous[setting.Name]) { changed = true; log.Write("repository-setting", Describe(setting.Name, observed)); }
        }
        Volatile.Write(ref values, next);
        if (changed || state.ReadSettings() is null) Record(next);
        if (changed) Changed?.Invoke();
        return changed;
    }

    // Diagnostic and best effort, like status: a failed write keeps the controller running on the values in memory.
    void Record(IReadOnlyDictionary<string, SettingValue> current)
    {
        try { state.WriteSettings(new RecordedSettings(current)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log.Write("repository-settings-error", $"the observed settings could not be recorded: {error.Message}");
        }
    }

    // The effective value a previous lifetime recorded, when that value is still valid.
    static SettingValue? Retained(RepositorySetting setting, IReadOnlyDictionary<string, SettingValue>? recorded) =>
        recorded?.GetValueOrDefault(setting.Name) is { Value: { } text } known && setting.Normalize(text, out _) == text ? known : null;

    /// <summary>Refreshes every <see cref="RefreshInterval"/>. After a refresh that changed a setting, <paramref name="apply"/>
    /// completes before the next refresh, so it observes every changed value in order.</summary>
    public async Task RefreshUntilCancelled(Func<CancellationToken, Task> apply, CancellationToken ct)
    {
        if (variables is null) return;
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

    static string Describe(string name, SettingValue value) => $"name={name} state={value.State.Name()}" +
        (value.Value is null ? "" : $" value={value.Value}") + (value.Reason is null ? "" : $" reason=\"{value.Reason}\"");
}
