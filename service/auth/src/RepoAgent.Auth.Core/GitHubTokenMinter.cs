using static RepoAgent.Auth.IssuancePolicy;

namespace RepoAgent.Auth;

// A GitHub token GitHub minted, and whether its scope is exactly what the broker asked for. A token out of scope is
// still known: the caller records it durably and revokes it, rather than lose a capability GitHub granted.
public sealed record KnownGitHubToken(GitHubToken Token, bool ScopeValid);

// Mints and revokes GitHub tokens, and decides what GitHub's answers prove. A minted token is in scope only when GitHub
// reports exactly the one repository and the runtime authority asked for, with a lifetime the broker can serve.
public sealed class GitHubTokenMinter(IGitHubTokens tokens, TimeProvider clock)
{
    // Only an answer that carries a usable token makes a token known. Any other answer denies, and the claimed
    // send stays unresolved.
    public async Task<KnownGitHubToken> MintAsync(AppAuthorization app, long installationId, RepositoryIdentity repository,
        RuntimeAuthority authority, CancellationToken ct)
    {
        if (await tokens.MintAsync(app, installationId, repository, authority, ct) is not GitHubRead<MintedGitHubToken>.Found { Value: var minted }
            || !IsBearer(minted.Value)) throw new IssuanceDenied();
        // An expiry GitHub did not report readably is never treated as passed.
        var token = new GitHubToken(minted.Value, minted.ExpiresAt ?? DateTimeOffset.MaxValue);
        return new(token, minted.ExpiresAt is { } expiry && Serviceable(expiry)
            && OnlyThe(repository, minted) && minted.Permissions is { } permissions && authority.MatchesExactly(permissions));
    }

    // Returns only when GitHub confirmed the revocation. Otherwise the token stays known, and charged, until it expires.
    public async Task RevokeAsync(GitHubToken token, CancellationToken ct)
    {
        if (!await tokens.RevokeAsync(token, ct)) throw new IssuanceDenied();
    }

    // GitHub tokens live one hour. The broker serves a token that outlives the five-minute renewal margin and
    // claims at most 65 minutes, allowing for clock skew.
    private bool Serviceable(DateTimeOffset expiry)
    {
        var now = clock.GetUtcNow();
        return expiry > now.AddMinutes(5) && expiry <= now.AddMinutes(65);
    }

    // GitHub limits the token to the repositories it lists, and lists exactly the one asked for. GitHub answers
    // with the repository's own name, which may differ from the asked-for name in case.
    private static bool OnlyThe(RepositoryIdentity repository, MintedGitHubToken minted) =>
        minted.OnlyListedRepositories == true && minted.Repositories is [var only]
        && only.Id == repository.Id && string.Equals(only.FullName, repository.FullName, StringComparison.OrdinalIgnoreCase);
}
