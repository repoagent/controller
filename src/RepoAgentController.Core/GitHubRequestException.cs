namespace RepoAgentController;

public enum GitHubFailure { Transient, RateLimited, Permanent }

/// <summary>
/// A classified failure of a controller-owned GitHub read or write. Uncertain means GitHub may have applied a write; Status is
/// the HTTP status GitHub returned, when the request got that far.
/// </summary>
public sealed class GitHubRequestException(GitHubFailure kind, string message, bool uncertain = false, TimeSpan? retryAfter = null,
    DateTimeOffset? resetAt = null, int? status = null) : IOException(message)
{
    public GitHubFailure Kind { get; } = kind;
    public bool Uncertain { get; } = uncertain;
    public int? Status { get; } = status;

    /// <summary>A REST read GitHub answered with an unexpected HTTP status.</summary>
    public static GitHubRequestException Rejected(int status) => new(status switch
    {
        429 => GitHubFailure.RateLimited,
        400 or 403 or 404 or 410 or 422 => GitHubFailure.Permanent,
        _ => GitHubFailure.Transient
    }, $"GitHub returned HTTP {status}", status: status);

    /// <summary>The earliest retry GitHub signalled, if any.</summary>
    public DateTimeOffset? NotBefore(DateTimeOffset now)
    {
        var after = retryAfter is { } delay ? now + delay : (DateTimeOffset?)null;
        return after is null ? resetAt : resetAt is null || after > resetAt ? after : resetAt;
    }
}
