namespace RepoAgentController;

/// <summary>
/// The repository's GitHub Discussions, as the controller's App reads and writes them. A failed call throws an
/// <see cref="IOException"/>. <see cref="Find"/>, the receipts and the dashboard throw a <see cref="GitHubRequestException"/>
/// that says whether to retry and whether GitHub may have applied a write.
/// </summary>
public interface IDiscussions
{
    /// <summary>Every Discussion in the repository, read page by page.</summary>
    Task<IReadOnlyList<Discussion>> List(CancellationToken ct);

    /// <summary>The Discussion with this number; null only when GitHub confirms the repository has none.</summary>
    Task<Discussion?> Find(int number, CancellationToken ct);

    /// <summary>The Discussion with every page of its reactions, comments and replies; a failed or partial read throws.</summary>
    Task<DiscussionConversation> Read(Discussion discussion, CancellationToken ct);

    /// <summary>The names of the labels applied to a Discussion now.</summary>
    Task<IReadOnlyList<string>> Labels(string discussionId, CancellationToken ct);

    /// <summary>Adds the App's EYES reaction to a Discussion or comment.</summary>
    Task AddEyesReceipt(string subjectId, CancellationToken ct);

    /// <summary>Whether the App's EYES reaction exists, to reconcile an uncertain earlier write.</summary>
    Task<bool> HasEyesReceipt(string subjectId, CancellationToken ct);

    /// <summary>The repository and the named Discussion category that hold the dashboard.</summary>
    Task<DashboardLocation> DashboardCategory(string name, CancellationToken ct);

    /// <summary>The category's Discussions, oldest first, each page read as the enumeration reaches it.</summary>
    IAsyncEnumerable<CategoryDiscussion> CategoryDiscussions(DashboardLocation at, CancellationToken ct);

    Task<DashboardDiscussion> CreateDashboard(DashboardLocation at, string title, string body, CancellationToken ct);

    /// <summary>Replaces the dashboard's whole title and body.</summary>
    Task UpdateDashboard(string id, string title, string body, CancellationToken ct);
}

/// <summary>Where the dashboard Discussion lives: the repository's and the category's GitHub node IDs.</summary>
public sealed record DashboardLocation(string RepositoryId, string CategoryId);

/// <summary>The controller's dashboard Discussion: its GitHub node ID and URL.</summary>
public sealed record DashboardDiscussion(string Id, string Url);
