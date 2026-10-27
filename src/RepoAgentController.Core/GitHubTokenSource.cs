namespace RepoAgentController;

/// <summary>A validated GitHub token lease. The adapter registers the token for log redaction before returning it.</summary>
public readonly record struct GitHubTokenLease(string Token, DateTimeOffset ExpiresAt);

/// <summary>A current GitHub token and its optional repository/profile binding for agent Git commands.</summary>
public sealed record GitHubToken(string Token, DateTimeOffset ExpiresAt, string? Repository = null, string? Profile = null);

/// <summary>The controller's source of validated, repository-scoped GitHub tokens.</summary>
public interface IGitHubTokenSource : IDisposable
{
    /// <summary>Whether an acquisition failure other than caller cancellation ends authority for this controller lifetime.</summary>
    bool RequiresNewLifetimeOnFailure { get; }
    string AppSlug { get; }
    long? RepositoryId => null;
    string? GitRepository => null;
    string? GitProfile => null;
    string? SettingsUnreadable => null;
    Task Initialize(long? expectedInstallation, CancellationToken ct);
    Task<GitHubTokenLease> Acquire(CancellationToken ct);
}
