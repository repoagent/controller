namespace RepoAgent.Auth;

// The GitHub App, and its installations as GitHub reports them.
public interface IAppInstallations
{
    // Signs one App authorization for the window. Every call is one signature with the App's key, so callers
    // call this once per durably reserved window, which the broker has already charged as one sign.
    Task<AppAuthorization> AuthorizeAsync(AppAuthorizationWindow window, CancellationToken ct);

    // The App's installation on the repository, read with the App authorization.
    Task<GitHubRead<AppInstallation>> FindAsync(AppAuthorization app, string repository, CancellationToken ct);

    // The first repositories the installation token can reach: at most two, with GitHub's total count.
    Task<GitHubRead<InstallationRepositories>> RepositoriesAsync(GitHubToken token, CancellationToken ct);
}

// Proof that the App authorized GitHub reads and mints for one window. Only the adapter that issued it can use it.
public abstract class AppAuthorization;

// The issue and expiry times an App authorization claims. They are fixed when its attempt is durably reserved.
public sealed record AppAuthorizationWindow(DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt)
{
    // An attempt reserved at one instant claims issue one minute earlier, allowing for clock skew at GitHub,
    // and expiry two minutes later. A window that differs was not reserved this way and is never signed.
    public static AppAuthorizationWindow Reserved(DateTimeOffset reservedAt, DateTimeOffset issuedAt, DateTimeOffset expiresAt) =>
        issuedAt == reservedAt.AddSeconds(-60) && expiresAt == reservedAt.AddSeconds(120)
            ? new(issuedAt, expiresAt) : throw new IssuanceDenied();
}

// Permissions maps each permission GitHub lists to its level, or to null when GitHub gave no string level.
public sealed record AppInstallation(long Id, long AppId, string AppSlug, bool Suspended, IReadOnlyDictionary<string, string?> Permissions);

public sealed record RepositoryIdentity(long Id, string FullName);

public sealed record InstallationRepositories(int TotalCount, IReadOnlyList<RepositoryIdentity> Repositories);
