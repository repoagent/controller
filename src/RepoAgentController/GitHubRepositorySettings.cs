using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>The <see cref="IRepositorySettings"/> adapter: direct repository Agent variables, read through gh with the
/// GitHub token, which needs Agent variables: read.</summary>
sealed class GitHubRepositorySettings(GitHub github) : IRepositorySettings
{
    public async Task<string?> Read(string name, CancellationToken ct)
    {
        var (status, body) = await github.Get($"repos/{github.Repository}/agents/variables/{Uri.EscapeDataString(name)}", "variable", ct);
        if (status == 404) return null;
        if (status != 200) throw new RepositorySettingRejectedException(status);
        var variable = JsonNode.Parse(body);
        if (!string.Equals(variable?.Text("name"), name, StringComparison.OrdinalIgnoreCase) ||
            variable?["value"] is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
            throw new IOException("Malformed GitHub variable response");
        return value.GetValue<string>();
    }
}

/// <summary>The <see cref="IRepositorySettings"/> adapter for a runtime token without Agent variables: read. It reads nothing,
/// so a setting is never taken as absent because GitHub hid it.</summary>
sealed class UnreadableRepositorySettings(string reason) : IRepositorySettings
{
    public Task<string?> Read(string name, CancellationToken ct) => Task.FromException<string?>(new RepositorySettingsUnreadableException(reason));
}
