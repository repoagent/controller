namespace RepoAgent.Auth.Tests;

// GitHub as the broker's ports see it: the App installed on owner/project, the variables an operator wrote, and the
// GitHub tokens the App mints. State answers every request unless a test replaces an answer; nothing here decides.
internal sealed class InMemoryGitHub(TimeProvider? clock = null) : IAppInstallations, IControllerApprovals, IGitHubTokens
{
    public static readonly RepositoryIdentity Project = new(123456, "owner/project");
    // A variable value only the repository may know. No broker response may contain it.
    public const string PrivateValue = "private-provider-value";
    // The App's installation on owner/project, with every permission a setup version asks for.
    public static readonly AppInstallation Installed = new(789, IssuancePolicy.AppId, IssuancePolicy.AppSlug, false, new Dictionary<string, string?>
    {
        ["discussions"] = "write",
        ["repository_hooks"] = "write",
        ["contents"] = "read",
        ["environments"] = "read",
        ["actions"] = "read",
        ["actions_variables"] = "read",
        ["agent_variables"] = "read"
    });
    private string marker = "";
    private int authorizations, reads, mints, verifierMints, revokes;

    public GitHubRead<AppInstallation> Installation { get; set; } = GitHubRead.Found(Installed);
    public GitHubRead<InstallationRepositories> Repositories { get; set; } = GitHubRead.Found(new InstallationRepositories(1, [Project]));
    public GitHubRead<ApprovalEnvironment> Environment { get; set; } = GitHubRead.Found(new ApprovalEnvironment(987, "approval/environment"));

    // Variables by where they are stored: the setup v1 environment's, and the repository's Agent and Actions variables.
    public Dictionary<string, string> EnvironmentVariables { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Agent { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Actions { get; } = new(StringComparer.Ordinal);

    // The APPROVAL variable in the setup v1 environment and in the Agent variables. Empty means absent.
    public string Marker
    {
        get => marker;
        set
        {
            marker = value;
            foreach (var store in new[] { EnvironmentVariables, Agent })
                if (value.Length == 0) store.Remove("APPROVAL");
                else store["APPROVAL"] = value;
        }
    }

    // Replace GitHub's answer to a named variable read or a listing page; null keeps the stored state's answer.
    public Func<ApprovalVariable, GitHubRead<GitHubVariable>?>? VariableAnswer { get; set; }
    public Func<ApprovalVariable, int?, GitHubRead<GitHubVariablePage>?>? PageAnswer { get; set; }

    // Runs after a read is counted and before it is answered, so a test can move time or act concurrently.
    public Func<string, Task>? BeforeRead { get; set; }
    public Func<Task>? BeforeAuthorize { get; set; }

    // How long GitHub lets a controller enrollment's GitHub token live; verifier tokens live an hour.
    public TimeSpan RuntimeLifetime { get; set; } = TimeSpan.FromHours(1);
    public List<TokenMint> MintRequests { get; } = [];
    public List<GitHubToken> Revoked { get; } = [];
    // Runs after a mint is counted and before it is answered, so a test can act concurrently or throw to lose GitHub's answer.
    public Func<TokenMint, Task>? BeforeMint { get; set; }
    // Replaces GitHub's answer to a mint, given the token GitHub mints by default.
    public Func<TokenMint, MintedGitHubToken, GitHubRead<MintedGitHubToken>>? MintAnswer { get; set; }
    // Runs after a revocation is counted and before it is answered, so a test can act or throw to lose GitHub's answer.
    public Func<GitHubToken, Task>? BeforeRevoke { get; set; }
    // False makes GitHub refuse every revocation.
    public bool RevocationConfirmed { get; set; } = true;

    public int Reads => reads;
    public int Authorizations => authorizations;
    public int Mints => mints;
    public int VerifierMints => verifierMints;
    public int Revokes => revokes;
    public List<string> Requests { get; } = [];
    public List<AppAuthorizationWindow> Windows { get; } = [];
    public List<GitHubToken> VerifierTokens { get; } = [];

    public async Task<AppAuthorization> AuthorizeAsync(AppAuthorizationWindow window, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref authorizations);
        lock (Windows) Windows.Add(window);
        if (BeforeAuthorize is not null) await BeforeAuthorize();
        return new Authorization(n);
    }

    public async Task<GitHubRead<AppInstallation>> FindAsync(AppAuthorization app, string repository, CancellationToken ct)
    {
        await ReadAsync("installation " + repository, null);
        return app is Authorization ? Installation : new GitHubRead<AppInstallation>.Refused();
    }

    public async Task<GitHubRead<InstallationRepositories>> RepositoriesAsync(GitHubToken token, CancellationToken ct)
    {
        await ReadAsync("repositories", token);
        return Repositories;
    }

    public async Task<GitHubRead<ApprovalEnvironment>> EnvironmentAsync(GitHubToken verifier, string repository, string environment, CancellationToken ct)
    {
        await ReadAsync($"environment {repository} {environment}", verifier);
        return Environment;
    }

    // GitHub reads one variable by name case-insensitively and answers with its stored name, or 404.
    public async Task<GitHubRead<GitHubVariable>> VariableAsync(GitHubToken verifier, ApprovalVariable variable, CancellationToken ct)
    {
        await ReadAsync($"variable {Store(variable)} {variable.Repository} {variable.Name}", verifier);
        if (VariableAnswer?.Invoke(variable) is { } answer) return answer;
        foreach (var (name, value) in Variables(variable))
            if (string.Equals(name, variable.Name, StringComparison.OrdinalIgnoreCase)) return GitHubRead.Found(new GitHubVariable(name, value));
        return new GitHubRead<GitHubVariable>.NotFound();
    }

    // GitHub pages repository variables at most 30 at a time, ordered by name.
    public async Task<GitHubRead<GitHubVariablePage>> VariablesAsync(GitHubToken verifier, ApprovalVariable variable, int perPage, int? page, CancellationToken ct)
    {
        await ReadAsync($"variables {Store(variable)} {variable.Repository} per_page={perPage}" + (page is { } n ? $" page={n}" : ""), verifier);
        if (PageAnswer?.Invoke(variable, page) is { } answer) return answer;
        var source = Variables(variable);
        var size = variable.Contract.IsRepositoryVariable ? Math.Min(30, perPage) : perPage;
        return GitHubRead.Found(new GitHubVariablePage(source.Count, [.. source.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Skip(((page ?? 1) - 1) * size).Take(size).Select(pair => new GitHubVariable(pair.Key, pair.Value))]));
    }

    // GitHub mints a token for exactly the repository and authority asked for: for an hour, or RuntimeLifetime for
    // a controller enrollment's token. GitHub answers with the repository's own identity.
    public async Task<GitHubRead<MintedGitHubToken>> MintAsync(AppAuthorization app, long installationId, RepositoryIdentity repository,
        RuntimeAuthority authority, CancellationToken ct)
    {
        var mint = new TokenMint(Interlocked.Increment(ref mints), app, installationId, repository, authority);
        if (mint.Verifier) Interlocked.Increment(ref verifierMints);
        lock (Requests)
        {
            Requests.Add($"mint {installationId} {repository.Id} " + string.Join(',', authority.Permissions.Select(p => p.Key + ":" + p.Value)));
            MintRequests.Add(mint);
        }
        if (BeforeMint is not null) await BeforeMint(mint);
        // A request cancelled while in flight fails, as HttpClient's does; its outcome at GitHub stays unknown.
        ct.ThrowIfCancellationRequested();
        if (app is not Authorization) return new GitHubRead<MintedGitHubToken>.Refused();
        if (repository.Id != Project.Id) throw new InvalidOperationException("Unexpected requested repository scope.");
        var token = new MintedGitHubToken(mint.Verifier ? "verifier-token" : "runtime-token-" + mint.Number,
            (clock ?? TimeProvider.System).GetUtcNow().Add(mint.Verifier ? TimeSpan.FromHours(1) : RuntimeLifetime), true, [Project],
            authority.Permissions.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.Ordinal));
        return MintAnswer is null ? GitHubRead.Found(token) : MintAnswer(mint, token);
    }

    public async Task<bool> RevokeAsync(GitHubToken token, CancellationToken ct)
    {
        Interlocked.Increment(ref revokes);
        lock (Requests)
        {
            Requests.Add("revoke");
            Revoked.Add(token);
        }
        if (BeforeRevoke is not null) await BeforeRevoke(token);
        return RevocationConfirmed;
    }

    private Dictionary<string, string> Variables(ApprovalVariable variable) =>
        variable.Contract.IsActions ? Actions : variable.Contract.IsAgent ? Agent : EnvironmentVariables;

    private static string Store(ApprovalVariable variable) =>
        variable.Contract.IsActions ? "actions" : variable.Contract.IsAgent ? "agents" : "environment " + variable.Environment;

    private async Task ReadAsync(string request, GitHubToken? token)
    {
        Interlocked.Increment(ref reads);
        lock (Requests) Requests.Add("read " + request);
        if (token is not null) lock (VerifierTokens) VerifierTokens.Add(token);
        if (BeforeRead is not null) await BeforeRead(request);
    }

    // An App authorization this fake signed: the test's Number-th signature.
    public sealed class Authorization(int number) : AppAuthorization
    {
        public int Number => number;
    }
}

// A mint the broker asked GitHub for; Number counts the test's mints from 1.
internal sealed record TokenMint(int Number, AppAuthorization App, long InstallationId, RepositoryIdentity Repository, RuntimeAuthority Authority)
{
    private static readonly RuntimeAuthority[] Verifiers =
        [.. new[] { ApprovalContract.Legacy, ApprovalContract.Agent, ApprovalContract.Actions }.Select(RuntimeAuthority.Verifier)];

    // The broker's verifier token carries one of the approval contracts' verifier authorities.
    public bool Verifier => Verifiers.Contains(Authority);
}
