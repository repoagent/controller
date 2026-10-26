using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>
/// What an installation token serves: polling or webhook delivery, optionally a managed clone and optionally repository
/// settings. A broker enrollment names it (profile and runtime protocol version), and both token sources request and accept
/// exactly its permissions.
/// </summary>
public readonly record struct RuntimeProfile(bool Webhooks = false, bool ManagedClone = false, bool Settings = false)
{
    /// <summary>The broker profile name: polling or webhook, then -clone, then -settings.</summary>
    public string Name => (Webhooks ? "webhook" : "polling") + (ManagedClone ? "-clone" : "") + (Settings ? "-settings" : "");

    /// <summary>The runtime protocol version: 3 with repository settings, 2 for a frozen clone profile, otherwise 1.</summary>
    public int Version => Settings ? 3 : ManagedClone ? 2 : 1;

    /// <summary>Installation-token permissions beyond metadata: read, in request order.</summary>
    public JsonObject Permissions()
    {
        var permissions = Json.Object(("discussions", "write"));
        if (Webhooks) permissions["repository_hooks"] = "write";
        if (ManagedClone) permissions["contents"] = "read";
        if (Settings) permissions["agent_variables"] = "read";
        return permissions;
    }
}
