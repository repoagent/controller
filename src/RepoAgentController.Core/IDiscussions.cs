namespace RepoAgentController;

/// <summary>
/// The repository's GitHub Discussions, as the controller's App reads and writes them. A failed call throws a
/// <see cref="GitHubRequestException"/> that says whether to retry and whether GitHub may have applied a write.
/// </summary>
public interface IDiscussions
{
    /// <summary>Adds the App's EYES reaction to a Discussion or comment.</summary>
    Task AddEyesReceipt(string subjectId, CancellationToken ct);

    /// <summary>Whether the App's EYES reaction exists, to reconcile an uncertain earlier write.</summary>
    Task<bool> HasEyesReceipt(string subjectId, CancellationToken ct);

    /// <summary>The repository and the named Discussion category that hold the dashboard.</summary>
    Task<DashboardLocation> DashboardCategory(string name, CancellationToken ct);

    /// <summary>The oldest Discussion in the category that is the App's dashboard (<see cref="AppIdentity.IsDashboard"/>), if any.</summary>
    Task<DashboardDiscussion?> FindDashboard(DashboardLocation at, CancellationToken ct);

    Task<DashboardDiscussion> CreateDashboard(DashboardLocation at, string title, string body, CancellationToken ct);

    /// <summary>Replaces the dashboard's whole title and body.</summary>
    Task UpdateDashboard(string id, string title, string body, CancellationToken ct);
}

/// <summary>Where the dashboard Discussion lives: the repository's and the category's GitHub node IDs.</summary>
public sealed record DashboardLocation(string RepositoryId, string CategoryId);

/// <summary>The controller's dashboard Discussion: its GitHub node ID and URL.</summary>
public sealed record DashboardDiscussion(string Id, string Url);
