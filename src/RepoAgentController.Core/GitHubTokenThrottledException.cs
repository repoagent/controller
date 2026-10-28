namespace RepoAgentController;

/// <summary>The token provider refused an acquisition temporarily without rejecting the existing token.</summary>
public sealed class GitHubTokenThrottledException(TimeSpan? retryAfter = null) : Exception("GitHub token renewal is throttled")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
