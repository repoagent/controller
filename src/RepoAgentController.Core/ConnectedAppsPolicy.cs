using System.Text.RegularExpressions;

namespace RepoAgentController;

/// <summary>Operator authority for account-connected apps, independent of repository input and provider configuration.</summary>
public sealed class ConnectedAppsPolicy
{
    public string Mode { get; }
    public IReadOnlyList<string> AllowedApps { get; }
    public bool AppsEnabled => Mode != "disabled";
    public bool UnlistedAppsEnabled => Mode == "all";
    public static ConnectedAppsPolicy Disabled { get; } = new("disabled", []);

    public ConnectedAppsPolicy(string mode, IEnumerable<string> allowedApps)
    {
        var apps = allowedApps.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (mode is not ("disabled" or "selected" or "all")) throw new ArgumentException("--connected-apps must be disabled, selected or all");
        if (apps.Any(id => !IsValidAppId(id)))
            throw new ArgumentException("--allowed-app requires stable app IDs of letters, digits, underscores or hyphens (maximum 200 characters)");
        if ((mode == "selected") != (apps.Length != 0)) throw new ArgumentException("Only selected connected-apps mode requires --allowed-app; other modes reject it");
        Mode = mode;
        AllowedApps = apps;
    }

    public static bool IsValidAppId(string id) => id != "_default" && Regex.IsMatch(id, @"\A[A-Za-z0-9][A-Za-z0-9_-]{0,199}\z");

    public bool AllowsApp(string id) => AppsEnabled && (UnlistedAppsEnabled || AllowedApps.Contains(id));

    /// <summary>Missing observations never prove enforcement. A disabled feature gate blocks explicit app enables too.</summary>
    public bool Matches(bool? enabled, bool? unlistedEnabled, IReadOnlyDictionary<string, bool?>? appStates)
    {
        if (enabled != AppsEnabled || unlistedEnabled != UnlistedAppsEnabled) return false;
        if (!AppsEnabled) return true;
        return appStates is not null && appStates.All(app => app.Value == AllowsApp(app.Key)) &&
            AllowedApps.All(id => appStates.TryGetValue(id, out var allowed) && allowed == true);
    }
}
