using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace RepoAgent.Auth;

// GitHub tokens on GitHub: minted with the App's JWT on an installation, and revoked with the token itself.
internal sealed class GitHubTokens(GitHubRest github) : IGitHubTokens
{
    public Task<GitHubRead<MintedGitHubToken>> MintAsync(AppAuthorization app, long installationId, RepositoryIdentity repository,
        RuntimeAuthority authority, CancellationToken ct) =>
        github.SendAsync(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", GitHubAppAuthorization.Jwt(app),
            JsonContent.Create(new { repository_ids = new[] { repository.Id }, permissions = authority.Permissions }),
            HttpStatusCode.Created, Minted, ct);

    // GitHub answers 204 when it revoked the token.
    public async Task<bool> RevokeAsync(GitHubToken token, CancellationToken ct) =>
        await github.StatusAsync(HttpMethod.Delete, "installation/token", token.Value, ct) == HttpStatusCode.NoContent;

    // An answer without a string token is malformed. A scope member that is missing or of another kind than GitHub
    // documents is passed on as null, so a minted token stays known even when its scope cannot be read.
    private static MintedGitHubToken Minted(JsonElement root) => new(GitHubRest.JsonText(root.GetProperty("token")),
        root.TryGetProperty("expires_at", out var expiry) && expiry.ValueKind == JsonValueKind.String && expiry.TryGetDateTimeOffset(out var at) ? at : null,
        root.TryGetProperty("repository_selection", out var selection) && selection.ValueKind == JsonValueKind.String
            ? selection.GetString() == "selected" : null,
        Repositories(root), Permissions(root));

    private static IReadOnlyList<RepositoryIdentity>? Repositories(JsonElement root)
    {
        if (!root.TryGetProperty("repositories", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        var repositories = new List<RepositoryIdentity>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var number)
                || !item.TryGetProperty("full_name", out var name) || name.ValueKind != JsonValueKind.String) return null;
            repositories.Add(new(number, name.GetString()!));
        }
        return repositories;
    }

    // A permission level that is not a JSON string is passed on as null.
    private static IReadOnlyDictionary<string, string?>? Permissions(JsonElement root) =>
        root.TryGetProperty("permissions", out var permissions) && permissions.ValueKind == JsonValueKind.Object
            ? permissions.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null, StringComparer.Ordinal)
            : null;
}
