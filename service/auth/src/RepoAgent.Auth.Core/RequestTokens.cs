namespace RepoAgent.Auth;

// Credentials stay in request memory. Uncertain results fall back to GitHub expiry.
internal sealed class RequestTokens(IGitHubTokens tokens, IAppInstallations installations, TimeProvider clock) : IAsyncDisposable
{
    private readonly GitHubTokenMinter minter = new(tokens, clock);
    private readonly List<GitHubToken> temporary = [];
    public async Task<AppAuthorization> AuthorizeAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var window = AppAuthorizationWindow.Reserved(now, now.AddSeconds(-60), now.AddSeconds(120));
        var app = await installations.AuthorizeAsync(window, ct);
        ct.ThrowIfCancellationRequested();
        if (clock.GetUtcNow() >= window.ExpiresAt) throw new IssuanceDenied();
        return app;
    }
    public async Task<GitHubToken> MintAsync(AppAuthorization app, long installation, RepositoryIdentity repository,
        RuntimeAuthority authority, CancellationToken ct)
    {
        var known = await minter.MintAsync(app, installation, repository, authority, ct);
        temporary.Add(known.Token);
        if (!known.ScopeValid) throw new IssuanceDenied();
        return known.Token;
    }
    public void Published(GitHubToken token) => temporary.Remove(token);
    public async Task RevokeAsync(GitHubToken token)
    {
        temporary.Remove(token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6), clock);
        try { await minter.RevokeAsync(token, deadline.Token); }
        catch (Exception ex) when (IssuancePolicy.Answerable(ex)) { }
    }
    public async ValueTask DisposeAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6), clock);
        foreach (var token in temporary)
        {
            try { await minter.RevokeAsync(token, deadline.Token); }
            catch (Exception ex) when (IssuancePolicy.Answerable(ex)) { }
        }
    }
}
