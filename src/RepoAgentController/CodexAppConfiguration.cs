using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>Translates operator app authority into pinned Codex configuration and checks its effective observations.</summary>
sealed class CodexAppConfiguration(ConnectedAppsPolicy policy)
{
    // Codex merges tables. Pin existing entries explicitly; future apps inherit _default.
    public JsonObject Overrides(IEnumerable<string> configuredIds, bool bootstrap = false)
    {
        var enabled = !bootstrap && policy.AppsEnabled;
        var result = new JsonObject { ["features.apps"] = enabled, ["apps._default.enabled"] = enabled && policy.UnlistedAppsEnabled };
        foreach (var id in configuredIds.Concat(policy.AllowedApps).Where(id => id != "_default").Distinct(StringComparer.Ordinal))
        {
            // CLI overrides split dotted keys literally; stable connector IDs need no quoting.
            if (!ConnectedAppsPolicy.IsValidAppId(id)) throw new IOException("Connected-app configuration contains an unsupported app ID; policy cannot be enforced");
            result[$"apps.{id}.enabled"] = enabled && policy.AllowsApp(id);
        }
        return result;
    }

    static bool? Boolean(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<bool>(out var result) ? result : null;

    public void Verify(JsonObject config)
    {
        var apps = config["apps"] as JsonObject;
        var feature = config["features"] as JsonObject;
        var defaults = apps?["_default"] as JsonObject;
        var states = apps?.Where(app => app.Key != "_default").ToDictionary(app => app.Key, app => Boolean((app.Value as JsonObject)?["enabled"]), StringComparer.Ordinal);
        if (!policy.Matches(Boolean(feature?["apps"]), Boolean(defaults?["enabled"]), states))
            throw new IOException("Connected-app policy could not be verified; agent work is blocked. Check runtime support and local app configuration, then restart.");
    }
}
