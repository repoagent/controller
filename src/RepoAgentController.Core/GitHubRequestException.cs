namespace RepoAgentController;

public enum GitHubFailure { Transient, RateLimited, Permanent }

/// <summary>
/// A classified failure of a controller-owned GitHub read or write. Uncertain means GitHub may have applied a write. The GitHub
/// adapters classify GitHub's responses; the core decides only from the kind.
/// </summary>
public sealed class GitHubRequestException(GitHubFailure kind, string message, bool uncertain = false, TimeSpan? retryAfter = null,
    DateTimeOffset? resetAt = null) : IOException(message)
{
    public GitHubFailure Kind { get; } = kind;
    public bool Uncertain { get; } = uncertain;

    /// <summary>The earliest retry GitHub signalled, if any.</summary>
    public DateTimeOffset? NotBefore(DateTimeOffset now)
    {
        var after = retryAfter is { } delay ? now + delay : (DateTimeOffset?)null;
        return after is null ? resetAt : resetAt is null || after > resetAt ? after : resetAt;
    }
}
