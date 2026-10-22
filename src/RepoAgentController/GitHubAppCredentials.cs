using System.Text;
using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>Owns the App identity and a renewable, repository-scoped installation credential.</summary>
public sealed class GitHubAppCredentials : IDisposable
{
    readonly IInstallationTokenSource source;
    readonly TimeProvider clock;
    readonly SemaphoreSlim refreshLock = new(1);
    readonly CancellationTokenSource brokerCredentialLost = new();
    string? token;
    DateTimeOffset expiresAt;
    string appSlug = "";

    public string AgentLogin => appSlug.Length == 0 ? "" : appSlug + "[bot]";
    public string TokenFile { get; }
    public CancellationToken BrokerCredentialLost => source is BrokerTokenSource ? brokerCredentialLost.Token : CancellationToken.None;
    internal long? RepositoryId => source.RepositoryId;

    public GitHubAppCredentials(HttpClient http, string appId, string privateKeyPath, string repository,
        string credentialDirectory, TimeProvider? clock = null, bool repositoryWebhooks = false, bool clone = false)
    {
        this.clock = clock ?? TimeProvider.System;
        SecureDirectory(credentialDirectory);
        TokenFile = Path.Combine(credentialDirectory, "installation-token.json");
        Invalidate();
        source = new DirectAppTokenSource(http, appId, privateKeyPath, repository, this.clock, repositoryWebhooks, clone);
    }

    GitHubAppCredentials(IInstallationTokenSource source, string credentialDirectory, TimeProvider clock)
    {
        this.source = source;
        this.clock = clock;
        SecureDirectory(credentialDirectory);
        TokenFile = Path.Combine(credentialDirectory, "installation-token.json");
        Invalidate();
    }

    public static GitHubAppCredentials FromBroker(HttpClient http, string brokerOrigin, string enrollmentFile,
        string appId, string repository, string credentialDirectory, TimeProvider? clock = null, bool repositoryWebhooks = false, bool clone = false,
        long? expectedRepositoryId = null)
    {
        ClearCache(credentialDirectory);
        var time = clock ?? TimeProvider.System;
        return new(new BrokerTokenSource(http, brokerOrigin, enrollmentFile, appId, repository, time, repositoryWebhooks, clone, expectedRepositoryId), credentialDirectory, time);
    }

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

    public void Invalidate()
    {
        token = null;
        File.Delete(TokenFile);
        File.Delete(TokenFile + ".tmp");
    }

    internal static void ClearCache(string credentialDirectory)
    {
        if (!Directory.Exists(credentialDirectory)) return;
        File.Delete(Path.Combine(credentialDirectory, "installation-token.json"));
        File.Delete(Path.Combine(credentialDirectory, "installation-token.json.tmp"));
    }

    public bool IsAgentActor(JsonNode? actor)
    {
        if (appSlug.Length == 0 || actor is null) return false;
        var login = actor.Text("login");
        return string.Equals(login, AgentLogin, StringComparison.OrdinalIgnoreCase) ||
            (actor.Text("__typename") == "Bot" && string.Equals(login, appSlug, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string> GetToken(CancellationToken ct)
    {
        await refreshLock.WaitAsync(ct);
        try
        {
            RequireLiveBrokerCredential();
            if (appSlug.Length == 0) throw new InvalidOperationException("GitHub App authentication has not been initialized");
            if (token is not null && File.Exists(TokenFile) && expiresAt > clock.GetUtcNow().AddMinutes(5)) return token;
            try
            {
                var lease = await source.Acquire(ct);
                var freshToken = lease.Token;
                var expiry = lease.ExpiresAt;
                Log.Protect(freshToken);
                var temporary = TokenFile + ".tmp";
                var options = new FileStreamOptions
                {
                    Mode = FileMode.Create, Access = FileAccess.Write
                };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var file = new FileStream(temporary, options))
                {
                    var cache = Json.Object(("token", freshToken), ("expiresAt", expiry.ToString("O")));
                    if (source.GitRepository is { } repository)
                    {
                        cache["repository"] = repository;
                        cache["profile"] = source.GitProfile;
                    }
                    file.Write(Encoding.UTF8.GetBytes(cache.ToJsonString()));
                    file.Flush(true);
                }
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                using (AppGitCredential.Lock(TokenFile)) File.Move(temporary, TokenFile, true);
                token = freshToken;
                expiresAt = expiry;
                Log.Write("app-token-refreshed", $"actor={AgentLogin} expires={expiry:O}");
                return token;
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

    public void Dispose() { source.Dispose(); refreshLock.Dispose(); brokerCredentialLost.Dispose(); }
}
