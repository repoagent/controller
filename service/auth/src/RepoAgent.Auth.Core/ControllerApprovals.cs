namespace RepoAgent.Auth;

// Where GitHub holds controller approvals, as the broker's verifier token reads them.
public interface IControllerApprovals
{
    // A setup v1 approval environment, read by name.
    Task<GitHubRead<ApprovalEnvironment>> EnvironmentAsync(GitHubToken verifier, string repository, string environment, CancellationToken ct);

    // The approval variable, read by name. GitHub matches the name case-insensitively and answers with the stored name.
    Task<GitHubRead<GitHubVariable>> VariableAsync(GitHubToken verifier, ApprovalVariable variable, CancellationToken ct);

    // One page of the variables stored alongside the approval variable. Page null asks for GitHub's first page.
    Task<GitHubRead<GitHubVariablePage>> VariablesAsync(GitHubToken verifier, ApprovalVariable variable, int perPage, int? page, CancellationToken ct);
}

// The variable that holds a controller approval's marker. The contract alone chooses where it lives: setup v3 in the
// repository's Actions variables, setup v2 in its Agent variables, setup v1 in the named environment's variables.
public sealed record ApprovalVariable(ApprovalContract Contract, string Repository, string? Environment, string Name);

public sealed record ApprovalEnvironment(long Id, string Name);

public sealed record GitHubVariable(string Name, string Value);

public sealed record GitHubVariablePage(int TotalCount, IReadOnlyList<GitHubVariable> Variables);
