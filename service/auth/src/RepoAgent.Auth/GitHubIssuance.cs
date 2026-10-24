using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static RepoAgent.Auth.IssuancePolicy;

namespace RepoAgent.Auth;

// Mints and revokes GitHub tokens over GitHubRest's headers, timeout and answer bounds.
public sealed class GitHubIssuance(HttpClient client, TimeProvider clock)
{
    public Task<IssuedToken> MintAsync(GrantSnapshot grant, string profile, AppAuthorization app, CancellationToken ct) =>
        MintAsync(grant.RepositoryId, grant.Repository, grant.InstallationId, profile, app, ct);

    internal async Task<IssuedToken> MintAsync(string repositoryId, string repository, long installationId, string profile, AppAuthorization app, CancellationToken ct)
    {
        using var request = GitHubRest.Request(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", GitHubAppAuthorization.Jwt(app));
        request.Content = JsonContent.Create(new { repository_ids = new[] { Id(repositoryId) }, permissions = RuntimeAuthority.For(profile).Permissions });
        using var response = await SendAsync(request, HttpStatusCode.Created, ct);
        var root = response.RootElement;
        var token = root.GetProperty("token").GetString();
        if (token is null || !IsBearer(token)) throw new IssuanceDenied();
        // Once token material is known, return it even if scope/expiry fails: the caller durably
        // protects and attempts to revoke it, rather than losing a known excessive capability.
        var valid = false;
        var expiry = DateTimeOffset.MaxValue;
        try
        {
            expiry = root.GetProperty("expires_at").GetDateTimeOffset();
            ValidateRepository(root.GetProperty("repositories"), repositoryId, repository);
            var expected = RuntimeAuthority.For(profile).Permissions;
            var actual = root.GetProperty("permissions");
            if (actual.ValueKind != JsonValueKind.Object) throw new IssuanceDenied();
            var actualMap = actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
            valid = root.GetProperty("repository_selection").GetString() == "selected"
                && expected.All(p => actualMap.TryGetValue(p.Key, out var value) && value == p.Value)
                && actualMap.All(p => expected.TryGetValue(p.Key, out var value) && value == p.Value || p.Key == "metadata" && p.Value == "read")
                && expiry > clock.GetUtcNow().AddMinutes(5) && expiry <= clock.GetUtcNow().AddMinutes(65);
        }
        catch (Exception ex) when (ex is IssuanceDenied or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException) { }
        return new(token, expiry, valid);
    }

    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        using var request = GitHubRest.Request(HttpMethod.Delete, "installation/token", token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != HttpStatusCode.NoContent) throw new IssuanceDenied();
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, HttpStatusCode expected, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != expected || await GitHubRest.BodyAsync(response, timeout.Token) is not { } body) throw new IssuanceDenied();
        var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
        if (!GitHubRest.HasDuplicateMember(document.RootElement)) return document;
        document.Dispose();
        throw new IssuanceDenied();
    }
    // Shape validation is separate from comparison: malformed evidence never proves withdrawal.
    private static void ValidateRepository(JsonElement repositories, string repositoryId, string repository)
    {
        if (repositories.ValueKind != JsonValueKind.Array || repositories.GetArrayLength() != 1) throw new IssuanceDenied();
        var id = repositories[0].GetProperty("id").GetInt64();
        var name = repositories[0].GetProperty("full_name").GetString();
        if (id <= 0 || !IsRepositoryName(name)) throw new IssuanceDenied();
        if (id != Id(repositoryId) || !string.Equals(name, repository, StringComparison.OrdinalIgnoreCase)) throw new IssuanceDenied();
    }
}
