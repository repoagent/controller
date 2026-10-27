namespace RepoAgentController;

/// <summary>The in-memory GitHub token lifetime: serialized renewal, current agent authority and irreversible loss.</summary>
public sealed class GitHubTokenLifetime(IGitHubTokenSource source, TimeProvider clock, IControllerLog log) : IDisposable
{
    readonly SemaphoreSlim refreshLock = new(1);
    readonly CancellationTokenSource authorityLost = new();
    GitHubToken? credential;
    string appSlug = "";

    public string AgentLogin => Identity.AgentLogin;
    public CancellationToken AuthorityLost => source.RequiresNewLifetimeOnFailure ? authorityLost.Token : CancellationToken.None;
    public long? RepositoryId => source.RepositoryId;
    public string? SettingsUnreadable => source.SettingsUnreadable;

    public async Task Initialize(long? expectedInstallation, CancellationToken ct)
    {
        try
        {
            await source.Initialize(expectedInstallation, ct);
            appSlug = source.AppSlug;
            await GetToken(ct);
        }
        catch { Invalidate(); throw; }
    }

    public void Invalidate() => Volatile.Write(ref credential, null);

    /// <summary>This App, once initialized; until then no actor is the App.</summary>
    public AppIdentity Identity => new(appSlug);

    public async Task<string> GetToken(CancellationToken ct) => (await GetCredential(ct)).Token;

    public async Task<GitHubToken> GetCredential(CancellationToken ct)
    {
        await refreshLock.WaitAsync(ct);
        try
        {
            RequireLiveAuthority();
            if (appSlug.Length == 0) throw new InvalidOperationException("GitHub App authentication has not been initialized");
            if (Volatile.Read(ref credential) is { } held && held.ExpiresAt > clock.GetUtcNow().AddMinutes(5)) return held;
            try
            {
                var lease = await source.Acquire(ct);
                var fresh = new GitHubToken(lease.Token, lease.ExpiresAt, source.GitRepository, source.GitProfile);
                Volatile.Write(ref credential, fresh);
                log.Write("app-token-refreshed", $"actor={AgentLogin} expires={fresh.ExpiresAt:O}");
                return fresh;
            }
            catch (Exception error)
            {
                try { Invalidate(); }
                finally
                {
                    if (source.RequiresNewLifetimeOnFailure && !(error is OperationCanceledException && ct.IsCancellationRequested))
                        authorityLost.Cancel();
                }
                throw;
            }
        }
        finally { refreshLock.Release(); }
    }

    // Child gh/Git launches receive only a live, unexpired credential; renewal stays with this manager.
    public GitHubToken? ForAgent() => !authorityLost.IsCancellationRequested &&
        Volatile.Read(ref credential) is { } held && held.ExpiresAt > clock.GetUtcNow().AddSeconds(30) ? held : null;

    // Git reports a credential it saw rejected. Renewal then reacquires; a newer credential is never dropped.
    public void Reject(string token)
    {
        if (Volatile.Read(ref credential) is { } held && held.Token == token) Interlocked.CompareExchange(ref credential, null, held);
    }

    public async Task RefreshUntilCancelled(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), clock, ct);
                await GetToken(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                log.Write("app-refresh-error", error.Message);
                if (authorityLost.IsCancellationRequested) break;
            }
        }
        RequireLiveAuthority();
    }

    void RequireLiveAuthority()
    {
        if (authorityLost.IsCancellationRequested)
            throw new InvalidOperationException("Broker credential renewal failed; this controller lifetime cannot resume. Check authorization and pending recovery state before restarting.");
    }

    public void Dispose() { Invalidate(); source.Dispose(); refreshLock.Dispose(); authorityLost.Dispose(); }
}
