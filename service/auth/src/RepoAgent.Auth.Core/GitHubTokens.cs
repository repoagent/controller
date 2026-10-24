namespace RepoAgent.Auth;

// GitHub tokens the App mints on an installation for one repository, and revokes.
public interface IGitHubTokens
{
    // Asks GitHub, with the App authorization, to mint one GitHub token on the installation for the repository and
    // exactly the runtime authority. Every call is one mint the broker has already charged and durably claimed.
    Task<GitHubRead<MintedGitHubToken>> MintAsync(AppAuthorization app, long installationId, RepositoryIdentity repository,
        RuntimeAuthority authority, CancellationToken ct);

    // Asks GitHub to revoke the token. True only when GitHub confirmed the revocation.
    Task<bool> RevokeAsync(GitHubToken token, CancellationToken ct);
}

// A token GitHub answered that it minted, and the scope GitHub reports for it. A scope member the answer did not carry
// readably is null. OnlyListedRepositories says whether GitHub limits the token to the repositories it lists, rather
// than every repository of the installation. Whether the token is in scope is the core's call.
public sealed record MintedGitHubToken(string Value, DateTimeOffset? ExpiresAt, bool? OnlyListedRepositories,
    IReadOnlyList<RepositoryIdentity>? Repositories, IReadOnlyDictionary<string, string?>? Permissions)
{
    public override string ToString() => "MintedGitHubToken { Value = [redacted] }";
}

// A token GitHub minted for the App on one repository that the broker holds: a controller enrollment's GitHub token,
// or the broker's own verifier token, which may only read controller approvals. ExpiresAt is the expiry GitHub
// reported when it minted the token. Its value never appears in logs or exception text.
public sealed record GitHubToken(string Value, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"GitHubToken {{ Value = [redacted], ExpiresAt = {ExpiresAt:O} }}";
}
