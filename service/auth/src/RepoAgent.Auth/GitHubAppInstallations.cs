using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Keys.Cryptography;
using static RepoAgent.Auth.IssuancePolicy;

namespace RepoAgent.Auth;

// The GitHub App's JWT. Key Vault owns the App's private key; the reserved window owns the JWT's timestamps,
// so a slow or retried operation can never stretch them.
internal sealed class GitHubAppAuthorization(string jwt) : AppAuthorization
{
    private string Value { get; } = jwt;
    public static string Jwt(AppAuthorization app) => (app as GitHubAppAuthorization
        ?? throw new InvalidOperationException("Only GitHubAppInstallations issues the broker's App authorizations.")).Value;
}

// App installations on GitHub, read with the App's JWT or the broker's verifier token.
internal sealed class GitHubAppInstallations(GitHubRest github, CryptographyClient key, bool developerSigning) : IAppInstallations
{
    public async Task<AppAuthorization> AuthorizeAsync(AppAuthorizationWindow window, CancellationToken ct)
    {
        var header = Base64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var payload = Base64(JsonSerializer.SerializeToUtf8Bytes(new { iat = window.IssuedAt.ToUnixTimeSeconds(), exp = window.ExpiresAt.ToUnixTimeSeconds(), iss = AppId }));
        var unsigned = header + "." + payload;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Developer signing includes the bounded Azure CLI process before Key Vault's challenge/sign requests.
        // Keep its three-second process/network bounds, with room for both inside the operation's 15-second lifetime.
        // Production managed identity retains the existing three-second signing deadline.
        timeout.CancelAfter(TimeSpan.FromSeconds(developerSigning ? 6 : 3));
        SignResult signature;
        try { signature = await key.SignAsync(SignatureAlgorithm.RS256, SHA256.HashData(Encoding.ASCII.GetBytes(unsigned)), timeout.Token); }
        catch (Exception ex) when (ex is RequestFailedException or AuthenticationFailedException)
        {
            throw new GitHubUnavailable("The App signing key did not sign.", ex);
        }
        if (signature.Algorithm != SignatureAlgorithm.RS256) throw new GitHubUnavailable("The App signing key used another algorithm.");
        return new GitHubAppAuthorization(unsigned + "." + Base64(signature.Signature));
    }

    public Task<GitHubRead<AppInstallation>> FindAsync(AppAuthorization app, string repository, CancellationToken ct) =>
        github.ReadAsync($"repos/{GitHubRest.RepositoryPath(repository)}/installation", GitHubAppAuthorization.Jwt(app), Installation, ct);

    public Task<GitHubRead<InstallationRepositories>> RepositoriesAsync(GitHubToken token, CancellationToken ct) =>
        github.ReadAsync("installation/repositories?per_page=2", token.Value, Repositories, ct);

    // A permission level that is not a JSON string is passed on as null, so a permission the broker
    // does not need never fails the read.
    private static AppInstallation Installation(JsonElement root) => new(root.GetProperty("id").GetInt64(),
        root.GetProperty("app_id").GetInt64(), GitHubRest.JsonText(root.GetProperty("app_slug")),
        root.GetProperty("suspended_at").ValueKind != JsonValueKind.Null,
        root.GetProperty("permissions").EnumerateObject().ToDictionary(p => p.Name,
            p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null, StringComparer.Ordinal));

    private static InstallationRepositories Repositories(JsonElement root) => new(root.GetProperty("total_count").GetInt32(),
        [.. root.GetProperty("repositories").EnumerateArray()
            .Select(r => new RepositoryIdentity(r.GetProperty("id").GetInt64(), GitHubRest.JsonText(r.GetProperty("full_name"))))]);

    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
