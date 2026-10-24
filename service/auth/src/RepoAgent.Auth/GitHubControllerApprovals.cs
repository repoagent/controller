using System.Globalization;
using System.Text.Json;

namespace RepoAgent.Auth;

// Controller approvals as GitHub stores them, read with the broker's verifier token.
internal sealed class GitHubControllerApprovals(GitHubIssuance github) : IControllerApprovals
{
    public Task<GitHubRead<ApprovalEnvironment>> EnvironmentAsync(GitHubToken verifier, string repository, string environment, CancellationToken ct) =>
        github.ReadAsync(EnvironmentPath(repository, environment), verifier.Value,
            root => new ApprovalEnvironment(root.GetProperty("id").GetInt64(), GitHubIssuance.JsonText(root.GetProperty("name"))), ct);

    public Task<GitHubRead<GitHubVariable>> VariableAsync(GitHubToken verifier, ApprovalVariable variable, CancellationToken ct) =>
        github.ReadAsync($"{VariablesPath(variable)}/{Uri.EscapeDataString(variable.Name)}", verifier.Value, Variable, ct);

    public Task<GitHubRead<GitHubVariablePage>> VariablesAsync(GitHubToken verifier, ApprovalVariable variable, int perPage, int? page, CancellationToken ct) =>
        github.ReadAsync($"{VariablesPath(variable)}?per_page={perPage.ToString(CultureInfo.InvariantCulture)}"
            + (page is { } n ? "&page=" + n.ToString(CultureInfo.InvariantCulture) : ""), verifier.Value,
            root => new GitHubVariablePage(root.GetProperty("total_count").GetInt32(), [.. root.GetProperty("variables").EnumerateArray().Select(Variable)]), ct);

    // Each contract has one location; locations never fall back to each other.
    private static string VariablesPath(ApprovalVariable variable) =>
        variable.Contract.IsActions ? $"repos/{GitHubIssuance.RepositoryPath(variable.Repository)}/actions/variables"
        : variable.Contract.IsAgent ? $"repos/{GitHubIssuance.RepositoryPath(variable.Repository)}/agents/variables"
        : EnvironmentPath(variable.Repository, variable.Environment ?? throw new IssuanceDenied()) + "/variables";

    private static string EnvironmentPath(string repository, string environment) =>
        $"repos/{GitHubIssuance.RepositoryPath(repository)}/environments/{Uri.EscapeDataString(environment)}";

    private static GitHubVariable Variable(JsonElement variable) =>
        new(GitHubIssuance.JsonText(variable.GetProperty("name")), GitHubIssuance.JsonText(variable.GetProperty("value")));
}
