using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace RepoAgent.Auth.Tests;

// GitHub as the broker's approval and installation ports see it: the App installed on owner/project, and the
// variables an operator wrote. State answers every read unless a test replaces an answer; nothing here decides.
// Token mints and revocations still arrive over HTTP until the GitHub tokens slice moves them behind a port.
internal sealed class InMemoryGitHub(TimeProvider? clock = null) : HttpMessageHandler, IAppInstallations, IControllerApprovals
{
    public static readonly RepositoryIdentity Project = new(123456, "owner/project");
    // The App's installation on owner/project, with every permission a setup version asks for.
    public static readonly AppInstallation Installed = new(789, IssuancePolicy.AppId, IssuancePolicy.AppSlug, false, new Dictionary<string, string>
    {
        ["discussions"] = "write", ["repository_hooks"] = "write", ["contents"] = "read", ["environments"] = "read",
        ["actions"] = "read", ["actions_variables"] = "read", ["agent_variables"] = "read"
    });
    private string marker = "";
    private int authorizations, reads;

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

    public int Mints, Revokes, VerifierMints;
    public TimeSpan RuntimeLifetime = TimeSpan.FromHours(1);
    // Replaces GitHub's answer to a mint (POST) or revocation (DELETE); a null result keeps the default answer.
    public Func<HttpRequestMessage, Task<HttpResponseMessage?>>? Before = static _ => Task.FromResult<HttpResponseMessage?>(null);

    public int Reads => reads;
    public int Authorizations => authorizations;
    public List<string> Requests { get; } = [];
    public List<AppAuthorizationWindow> Windows { get; } = [];
    public List<GitHubToken> VerifierTokens { get; } = [];

    public async Task<AppAuthorization> AuthorizeAsync(AppAuthorizationWindow window, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref authorizations);
        lock (Windows) Windows.Add(window);
        if (BeforeAuthorize is not null) await BeforeAuthorize();
        return new GitHubAppAuthorization("app-jwt-" + n);
    }

    public async Task<GitHubRead<AppInstallation>> FindAsync(AppAuthorization app, string repository, CancellationToken ct)
    {
        await ReadAsync("installation " + repository, null);
        return app is GitHubAppAuthorization ? Installation : new GitHubRead<AppInstallation>.Refused(401);
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

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (Requests) Requests.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
        if (request.Method == HttpMethod.Post) Mints++;
        else if (request.Method == HttpMethod.Delete) Revokes++;
        else throw new InvalidOperationException("The broker reads GitHub through its ports, never over HTTP.");
        if (Before is not null && await Before(request) is { } replacement) return replacement;
        if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.NoContent);
        var input = await request.Content!.ReadFromJsonAsync<JsonElement>(ct);
        var ids = input.GetProperty("repository_ids");
        if (ids.GetArrayLength() != 1 || ids[0].GetInt64() != 123456) throw new InvalidOperationException("Unexpected requested repository scope.");
        var permissions = input.GetProperty("permissions");
        var verifier = permissions.TryGetProperty("environments", out _);
        if (verifier) VerifierMints++;
        return Json(new
        {
            token = verifier ? "verifier-token" : "runtime-token-" + Mints,
            expires_at = (clock ?? TimeProvider.System).GetUtcNow().Add(verifier ? TimeSpan.FromHours(1) : RuntimeLifetime),
            repository_selection = "selected", permissions, repositories = new[] { new { id = 123456, full_name = "owner/project" } }
        }, HttpStatusCode.Created);
    }

    public static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(value) };
}
