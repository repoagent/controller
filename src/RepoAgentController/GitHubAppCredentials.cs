using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>Owns the App identity and a renewable, repository-scoped installation credential held only in memory.</summary>
public sealed class GitHubAppCredentials : IDisposable
{
    readonly IInstallationTokenSource source;
    readonly TimeProvider clock;
    readonly SemaphoreSlim refreshLock = new(1);
    readonly CancellationTokenSource brokerCredentialLost = new();
    readonly CredentialHandoff handoff;
    InstallationCredential? credential;
    string appSlug = "";

    public string AgentLogin => Identity.AgentLogin;
    /// <summary>Private gh/Git configuration, generated wrappers and the nonsecret pointer to this lifetime's credential handoff.</summary>
    public string CredentialDirectory { get; }
    public CancellationToken BrokerCredentialLost => source is BrokerTokenSource ? brokerCredentialLost.Token : CancellationToken.None;
    internal long? RepositoryId => source.RepositoryId;
    /// <summary>Why the runtime token cannot read repository settings, or null when it can.</summary>
    internal string? SettingsUnreadable => source.SettingsUnreadable;

    public GitHubAppCredentials(HttpClient http, string appId, string privateKeyPath, string repository,
        string credentialDirectory, TimeProvider? clock = null, RuntimeProfile profile = default)
        : this(credentialDirectory, clock ?? TimeProvider.System,
            time => new DirectAppTokenSource(http, appId, privateKeyPath, repository, time, profile))
    { }

    GitHubAppCredentials(string credentialDirectory, TimeProvider clock, Func<TimeProvider, IInstallationTokenSource> createSource)
    {
        this.clock = clock;
        SecureDirectory(credentialDirectory);
        CredentialDirectory = credentialDirectory;
        ClearCache(credentialDirectory);
        source = createSource(clock);
        try { handoff = new CredentialHandoff(credentialDirectory, Handoff, Reject); }
        catch { source.Dispose(); throw; }
    }

    public static GitHubAppCredentials FromBroker(HttpClient http, string brokerOrigin, string enrollmentFile,
        string appId, string repository, string credentialDirectory, RuntimeProfile profile, TimeProvider? clock = null,
        long? expectedRepositoryId = null, long? expectedInstallationId = null) =>
        FromBroker(http, brokerOrigin, BrokerTokenSource.EnrollmentFile(enrollmentFile), appId, repository, credentialDirectory, profile, clock,
            expectedRepositoryId, expectedInstallationId);

    /// <summary>Uses an enrollment from any reader, such as one injected through the environment.</summary>
    internal static GitHubAppCredentials FromBroker(HttpClient http, string brokerOrigin, Func<ReadOnlyMemory<byte>> readEnrollment,
        string appId, string repository, string credentialDirectory, RuntimeProfile profile, TimeProvider? clock = null,
        long? expectedRepositoryId = null, long? expectedInstallationId = null) =>
        new(credentialDirectory, clock ?? TimeProvider.System, time => new BrokerTokenSource(http, brokerOrigin, readEnrollment, appId, repository,
            time, profile, expectedRepositoryId, expectedInstallationId));

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

    /// <summary>Removes installation-token files that earlier controller versions persisted beside the state.</summary>
    internal static void ClearCache(string credentialDirectory)
    {
        if (!Directory.Exists(credentialDirectory)) return;
        File.Delete(Path.Combine(credentialDirectory, "installation-token.json"));
        File.Delete(Path.Combine(credentialDirectory, "installation-token.json.tmp"));
    }

    /// <summary>This App, once initialized; until then no actor is the App.</summary>
    public AppIdentity Identity => new(appSlug);

    public bool IsAgentActor(JsonNode? actor) => Identity.IsAgentActor(DiscussionJson.ReadActor(actor));

    public async Task<string> GetToken(CancellationToken ct) => (await GetCredential(ct)).Token;

    internal async Task<InstallationCredential> GetCredential(CancellationToken ct)
    {
        await refreshLock.WaitAsync(ct);
        try
        {
            RequireLiveBrokerCredential();
            if (appSlug.Length == 0) throw new InvalidOperationException("GitHub App authentication has not been initialized");
            if (Volatile.Read(ref credential) is { } held && held.ExpiresAt > clock.GetUtcNow().AddMinutes(5)) return held;
            try
            {
                var lease = await source.Acquire(ct);
                Log.Protect(lease.Token);
                var fresh = new InstallationCredential(lease.Token, lease.ExpiresAt, source.GitRepository, source.GitProfile);
                Volatile.Write(ref credential, fresh);
                Log.Write("app-token-refreshed", $"actor={AgentLogin} expires={fresh.ExpiresAt:O}");
                return fresh;
            }
            catch (Exception error)
            {
                try { Invalidate(); }
                finally
                {
                    if (source is BrokerTokenSource && !(error is OperationCanceledException && ct.IsCancellationRequested))
                        brokerCredentialLost.Cancel();
                }
                throw;
            }
        }
        finally { refreshLock.Release(); }
    }

    // Child gh/Git launches receive only a live, unexpired credential; renewal stays with this manager.
    InstallationCredential? Handoff() => !brokerCredentialLost.IsCancellationRequested &&
        Volatile.Read(ref credential) is { } held && held.ExpiresAt > clock.GetUtcNow().AddSeconds(30) ? held : null;

    // Git reports a credential it saw rejected. Renewal then reacquires; a newer credential is never dropped.
    void Reject(string token)
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
                Log.Write("app-refresh-error", error.Message);
                if (brokerCredentialLost.IsCancellationRequested) break;
            }
        }
        RequireLiveBrokerCredential();
    }

    void RequireLiveBrokerCredential()
    {
        if (brokerCredentialLost.IsCancellationRequested)
            throw new InvalidOperationException("Broker credential renewal failed; this controller lifetime cannot resume. Check authorization and pending recovery state before restarting.");
    }

    internal static void SecureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose() { handoff.Dispose(); Invalidate(); source.Dispose(); refreshLock.Dispose(); brokerCredentialLost.Dispose(); }
}
