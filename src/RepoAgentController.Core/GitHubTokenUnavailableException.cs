namespace RepoAgentController;

/// <summary>The broker could not complete this request; its enrollment remains usable on a later request.</summary>
public sealed class GitHubTokenUnavailableException : Exception
{
    public GitHubTokenUnavailableException() : base("GitHub token provider is temporarily unavailable") { }
}
