using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>Owns the App identity and a renewable, repository-scoped installation credential held only in memory.</summary>
public sealed class GitHubAppCredentials : IDisposable
{
    readonly GitHubTokenLifetime lifetime;

    public string AgentLogin => Identity.AgentLogin;
    /// <summary>Private gh/Git configuration, generated wrappers and the nonsecret pointer to this lifetime's credential handoff.</summary>
    public string CredentialDirectory { get; }
    public CancellationToken BrokerCredentialLost => lifetime.AuthorityLost;
    internal long? RepositoryId => lifetime.RepositoryId;
    /// <summary>Why the runtime token cannot read repository settings, or null when it can.</summary>
    internal string? SettingsUnreadable => lifetime.SettingsUnreadable;

    public GitHubAppCredentials(HttpClient http, string appId, string privateKeyPath, string repository,
        string credentialDirectory, TimeProvider? clock = null, RuntimeProfile profile = default)
        : this(credentialDirectory, clock ?? TimeProvider.System,
            time => new DirectAppTokenSource(http, appId, privateKeyPath, repository, time, profile))
    { }

    GitHubAppCredentials(string credentialDirectory, TimeProvider clock, Func<TimeProvider, IGitHubTokenSource> createSource)
    {
        SecureDirectory(credentialDirectory);
        CredentialDirectory = credentialDirectory;
        ClearCache(credentialDirectory);
        lifetime = new(createSource(clock), clock, new ControllerLog(), new SocketAgentToolCredentials(credentialDirectory));
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

    public Task Initialize(long? expectedInstallation, CancellationToken ct) => lifetime.Initialize(expectedInstallation, ct);
    public void Invalidate() => lifetime.Invalidate();

    /// <summary>Removes installation-token files that earlier controller versions persisted beside the state.</summary>
    internal static void ClearCache(string credentialDirectory)
    {
        if (!Directory.Exists(credentialDirectory)) return;
        File.Delete(Path.Combine(credentialDirectory, "installation-token.json"));
        File.Delete(Path.Combine(credentialDirectory, "installation-token.json.tmp"));
    }

    /// <summary>This App, once initialized; until then no actor is the App.</summary>
    public AppIdentity Identity => lifetime.Identity;
    public bool IsAgentActor(JsonNode? actor) => Identity.IsAgentActor(DiscussionJson.ReadActor(actor));
    public Task<string> GetToken(CancellationToken ct) => lifetime.GetToken(ct);
    internal async Task<InstallationCredential> GetCredential(CancellationToken ct) => Credential(await lifetime.GetCredential(ct));
    static InstallationCredential Credential(GitHubToken token) => new(token.Token, token.ExpiresAt, token.Repository, token.Profile);
    public Task RefreshUntilCancelled(CancellationToken ct) => lifetime.RefreshUntilCancelled(ct);

    internal static void SecureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose() => lifetime.Dispose();
}
