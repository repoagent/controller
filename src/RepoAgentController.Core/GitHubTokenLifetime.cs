namespace RepoAgentController;

/// <summary>The in-memory GitHub token lifetime: serialized renewal, current agent authority and irreversible loss.</summary>
public sealed class GitHubTokenLifetime : IDisposable
{
    readonly IGitHubTokenSource source;
    readonly TimeProvider clock;
    readonly IControllerLog log;
    readonly IDisposable? toolBinding;
    readonly SemaphoreSlim refreshLock = new(1);
    readonly CancellationTokenSource authorityLost = new();
    GitHubToken? credential;
    string appSlug = "";
    DateTimeOffset retryAt;
    int throttles;
    readonly Lock expiryGate = new();
    ITimer? throttleExpiry;
    bool disposed;
    long renewalGeneration;

    public GitHubTokenLifetime(IGitHubTokenSource source, TimeProvider clock, IControllerLog log, IAgentToolCredentials? tools = null)
    {
        (this.source, this.clock, this.log) = (source, clock, log);
        try { toolBinding = tools?.Bind(ForAgent, Reject); }
        catch { Dispose(); throw; }
    }

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
            while (true)
            {
                RequireLiveAuthority();
                var now = clock.GetUtcNow();
                if (retryAt > now)
                {
                    if (ForAgent() is { } usable) return usable;
                    using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct, authorityLost.Token);
                    try { await Task.Delay(retryAt - now > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : retryAt - now, clock, waiting.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { RequireLiveAuthority(); throw; }
                    continue;
                }
                try
                {
                    var lease = await source.Acquire(ct);
                    var fresh = new GitHubToken(lease.Token, lease.ExpiresAt, source.GitRepository, source.GitProfile);
                    lock (expiryGate)
                    {
                        ct.ThrowIfCancellationRequested();
                        RequireLiveAuthority();
                        throttleExpiry?.Dispose();
                        throttleExpiry = null;
                        renewalGeneration++;
                        Volatile.Write(ref credential, fresh);
                    }
                    throttles = 0;
                    retryAt = default;
                    log.Write("app-token-refreshed", $"actor={AgentLogin} expires={fresh.ExpiresAt:O}");
                    return fresh;
                }
                catch (GitHubTokenThrottledException error)
                {
                    throttles = Math.Min(throttles + 1, 5);
                    var delay = error.RetryAfter is { } supplied && supplied > TimeSpan.Zero ? supplied
                        : TimeSpan.FromSeconds(Math.Min(300, 30 * (1 << (throttles - 1))) + Random.Shared.Next(6));
                    now = clock.GetUtcNow();
                    retryAt = delay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;
                    lock (expiryGate)
                    {
                        if (source.RequiresNewLifetimeOnFailure && throttleExpiry is null && Volatile.Read(ref credential) is { } expiring)
                        {
                            var until = expiring.ExpiresAt - TimeSpan.FromSeconds(30) - clock.GetUtcNow();
                            if (until <= TimeSpan.Zero)
                            {
                                Invalidate();
                                authorityLost.Cancel();
                                RequireLiveAuthority();
                            }
                            var generation = renewalGeneration;
                            throttleExpiry = clock.CreateTimer(_ =>
                            {
                                lock (expiryGate)
                                {
                                    if (disposed || throttleExpiry is null || generation != renewalGeneration) return;
                                    Invalidate();
                                    // Cancellation happens before optional diagnostics. An observer may fail after the
                                    // token is cancelled; its aggregate cannot escape a thread-pool timer callback.
                                    try { authorityLost.Cancel(); }
                                    catch (AggregateException) { }
                                    ThrottleLog("app-refresh-error", "Broker token renewal remained throttled at the token safety cutoff; authority ended.");
                                }
                            }, null, until, Timeout.InfiniteTimeSpan);
                        }
                    }
                    ThrottleLog("app-token-throttled", $"GitHub token acquisition throttled; retry at {retryAt:O}. Existing token remains usable only until its safety cutoff.");
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

    // Diagnostics are best effort only for the throttle/cutoff path: output or retained-status storage can be unavailable.
    void ThrottleLog(string kind, string message)
    {
        try { log.Write(kind, message); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        try { toolBinding?.Dispose(); }
        finally
        {
            lock (expiryGate) { disposed = true; throttleExpiry?.Dispose(); throttleExpiry = null; Invalidate(); }
            source.Dispose(); refreshLock.Dispose(); authorityLost.Dispose();
        }
    }
}
