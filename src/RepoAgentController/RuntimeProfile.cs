using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>
/// What an GitHub token serves: polling or webhook delivery, optionally a managed clone, repository
/// settings and image publication. A broker enrollment names it (profile and runtime protocol version), and both token sources request and accept
/// exactly its permissions.
/// </summary>
public readonly record struct RuntimeProfile(bool Webhooks = false, bool ManagedClone = false, bool Settings = false, bool Images = false)
{
    /// <summary>The broker profile name: polling or webhook, then -clone, then -settings, then -images.</summary>
    public string Name => (Webhooks ? "webhook" : "polling") + (ManagedClone ? "-clone" : "") + (Settings ? "-settings" : "") + (Images ? "-images" : "");

    /// <summary>The runtime protocol version: 4 with image publication, 3 with repository settings, 2 for a frozen clone profile, otherwise 1.</summary>
    public int Version => Images ? 4 : Settings ? 3 : ManagedClone ? 2 : 1;

    /// <summary>GitHub token permissions beyond metadata: read, in request order.</summary>
    public JsonObject Permissions(bool issuePrRead = true)
    {
        var permissions = Json.Object(("discussions", "write"));
        if (Webhooks) permissions["repository_hooks"] = "write";
        if (ManagedClone || Images) permissions["contents"] = Images ? "write" : "read";
        if (Settings) permissions["agent_variables"] = "read";
        if (issuePrRead) { permissions["issues"] = "read"; permissions["pull_requests"] = "read"; }
        return permissions;
    }
}
