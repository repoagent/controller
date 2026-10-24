namespace RepoAgentController;

/// <summary>The baseline releases that repoagent/controller publishes.</summary>
public interface IBaselineReleases
{
    /// <summary>The newest published baseline releases, in any order: published, non-prerelease baseline tags whose release
    /// metadata describes the public linux/amd64 image. Other releases are left out. Throws when the releases cannot be read.</summary>
    Task<IReadOnlyList<BaselineRelease>> Published(CancellationToken ct);
}
