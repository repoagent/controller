using System.Text.RegularExpressions;

namespace RepoAgentController;

/// <summary>The repository the managed checkout is bound to: its ID, owner/name, HTTPS remote and default branch.</summary>
public sealed record RepositoryCheckout(long Id, string Name, string Remote, string Branch)
{
    /// <summary>
    /// The repository GitHub reports for the <paramref name="approved"/> one, when it is that repository: a positive ID, an
    /// owner/name of safe characters equal to the approved one apart from case, its HTTPS remote on github.com, and a default
    /// branch Git and the managed commands read as one branch name. Throws <see cref="CheckoutIdentityException"/> otherwise.
    /// </summary>
    public static RepositoryCheckout Approved(string approved, long id, string name, string remote, string branch)
    {
        if (id <= 0 || !Regex.IsMatch(name, @"\A[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}\z") ||
            name.Split('/').Any(part => part is "." or "..") || !string.Equals(name, approved, StringComparison.OrdinalIgnoreCase) ||
            remote != $"https://github.com/{name}.git" || string.IsNullOrWhiteSpace(branch) || branch.StartsWith('-') ||
            branch.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || branch.Contains("..", StringComparison.Ordinal) ||
            branch.IndexOfAny(['~', '^', ':', '?', '*', '[', '\\']) >= 0 || branch.Contains("@{", StringComparison.Ordinal) ||
            branch.Split('/').Any(part => part.Length == 0 || part.StartsWith('.') || part.EndsWith('.') || part.EndsWith(".lock", StringComparison.Ordinal)))
            throw new CheckoutIdentityException("Repository metadata does not match the approved checkout");
        return new(id, name, remote, branch);
    }
}

/// <summary>Repository metadata that does not describe the approved checkout.</summary>
public sealed class CheckoutIdentityException(string message) : IOException(message);

/// <summary>
/// The ownership record (<c>.repoagent-checkout.json</c>): its format version, the state file and checkout root it binds, the
/// owner the checkout's marker repeats, the repository, remote and default branch, and the commit last applied. A key that is
/// absent or holds a type no writer leaves reads as null, except the commit, which reads as empty when absent or null. Keys
/// this controller does not write are kept as read. A file that is not one JSON object of distinct keys is
/// <see cref="Unreadable"/>.
/// </summary>
public sealed class CheckoutRecord
{
    public int? Version { get; init; }
    public string? State { get; init; }
    public string? Root { get; init; }
    public string? Owner { get; init; }
    public long? RepositoryId { get; init; }
    public string? Repository { get; init; }
    public string? Remote { get; init; }
    public string? Branch { get; init; }
    public string? Commit { get; set; }
    public IReadOnlyList<UnknownKey> Unknown { get; init; } = [];
    public bool Unreadable { get; init; }
}

/// <summary>What an opened checkout is bound to: the authoritative repository, and its ownership record as read or written.</summary>
public sealed record OpenedCheckout(RepositoryCheckout Repository, CheckoutRecord Record);

/// <summary>What a checkout root path holds.</summary>
public enum CheckoutRoot { Missing, Directory, Other }

/// <summary>
/// The managed workspace as opening reads and writes it: the authoritative repository, the ownership record, the checkout root,
/// the clone, and the owner marker in the checkout's Git directory. Each read throws when its file is unsafe to read.
/// </summary>
public interface ICheckoutWorkspace
{
    /// <summary>The repository GitHub reports for the approved one, approved by <see cref="RepositoryCheckout.Approved"/>.</summary>
    Task<RepositoryCheckout> AuthoritativeRepository(CancellationToken ct);

    /// <summary>The ownership record, or null when there is none.</summary>
    CheckoutRecord? Record();

    CheckoutRoot Root();

    /// <summary>Clones the repository's remote into the missing root with the controller's credentials.</summary>
    Task Clone(RepositoryCheckout repository, CancellationToken ct);

    /// <summary>The owner marker's text, or null when there is none.</summary>
    string? Owner();

    /// <summary>Writes a new owner marker; throws when one exists.</summary>
    void WriteOwner(string owner);

    /// <summary>Writes the ownership record whole.</summary>
    void WriteRecord(CheckoutRecord record);
}

/// <summary>An index entry, and whether Git hides its working-tree changes as skip-worktree or assume-unchanged.</summary>
public sealed record IndexEntry(string Path, bool SkipWorktree, bool AssumeUnchanged);

/// <summary>
/// The checkout's Git directory and what Git reports about the checkout. Git runs without hooks, a file system monitor,
/// system or global configuration, or optional locks. A Git command that fails throws.
/// </summary>
public interface ICheckoutFacts
{
    /// <summary>Whether the checkout's <c>.git</c> is a directory. Throws on a symbolic link on its Git paths.</summary>
    bool OwnGitDirectory();

    /// <summary>Whether the Git directory holds an entry at <paramref name="path"/>, relative to it.</summary>
    bool Holds(string path);

    /// <summary>The key of each entry in the checkout's own configuration, read without includes, as Git lists it.</summary>
    Task<IReadOnlyList<string>> ConfigurationKeys(CancellationToken ct);

    /// <summary>The checkout's top-level directory.</summary>
    Task<string> TopLevel(CancellationToken ct);

    /// <summary>Whether Git reads the checkout as anything but a non-bare repository.</summary>
    Task<bool> IsBare(CancellationToken ct);

    /// <summary>Every URL configured for origin, in order.</summary>
    Task<IReadOnlyList<string>> OriginUrls(CancellationToken ct);

    /// <summary>The branch ref HEAD points at. Throws when HEAD is detached.</summary>
    Task<string> HeadBranch(CancellationToken ct);

    /// <summary>The full ref name HEAD resolves to: <c>HEAD</c> when it is detached.</summary>
    Task<string> HeadName(CancellationToken ct);

    /// <summary>Whether the index or working tree differs from HEAD in a tracked file, or, with
    /// <paramref name="untracked"/>, holds an untracked file that is not ignored.</summary>
    Task<bool> Changed(bool untracked, CancellationToken ct);

    /// <summary>Every index entry, in index order.</summary>
    Task<IReadOnlyList<IndexEntry>> Index(CancellationToken ct);

    /// <summary>The commit <paramref name="revision"/> names.</summary>
    Task<string> Commit(string revision, CancellationToken ct);
}

/// <summary>
/// What opening the managed checkout accepts. A workspace holds only the checkout root, its ownership record and its lock. The
/// record binds the state file, root and authoritative repository to an owner the checkout's marker repeats. A restart
/// accepts local edits, commits and branch switches, which the idle refresh replaces; a clone must settle on the default
/// branch's commit before it is owned. Any other checkout is refused and left as it is.
/// </summary>
public static class CheckoutInspection
{
    public const string RootName = "repository", RecordName = ".repoagent-checkout.json", LockName = ".repoagent-checkout.lock";

    static readonly string[] LinkedStorage = ["commondir", "gitdir", "objects/info/alternates", "info/grafts"];

    // A concurrent Git process, or a multi-step operation that a forced checkout would not discard.
    static readonly string[] Operations = ["REBASE_HEAD", "BISECT_LOG", "rebase-apply", "rebase-merge", "sequencer", "index.lock", "HEAD.lock"];

    public static IOException Blocked(string reason) =>
        new("Managed checkout blocked: " + reason + "; preserve this workspace and inspect it before retrying");

    /// <summary>Refuses a workspace holding anything but the checkout root, its ownership record and its lock.</summary>
    public static void Workspace(IEnumerable<string> entries)
    {
        if (entries.Any(name => name is not (RootName or RecordName or LockName))) throw Blocked("workspace contains unmanaged files");
    }

    /// <summary>
    /// Opens the checkout at <paramref name="root"/> for the <paramref name="state"/> file and the repository the credentials
    /// are bound to, which is none when <paramref name="credentialRepositoryId"/> is null. A recorded checkout is verified as it is; with no record, a missing root is cloned, inspected, and then
    /// owned by a new marker and record.
    /// </summary>
    public static async Task<OpenedCheckout> Open(ICheckoutWorkspace workspace, ICheckoutFacts checkout, string state, string root,
        long? credentialRepositoryId, CancellationToken ct)
    {
        var repository = await workspace.AuthoritativeRepository(ct);
        if (repository.Id != credentialRepositoryId || repository.Id <= 0) throw Blocked("repository identity differs from credential binding");
        if (workspace.Record() is { } saved)
        {
            if (saved.Unreadable) throw Blocked("invalid ownership record");
            if (saved.Version != 1 || saved.State != state || saved.Root != root || saved.RepositoryId != repository.Id ||
                saved.Repository != repository.Name || saved.Remote != repository.Remote || saved.Branch != repository.Branch ||
                !Guid.TryParseExact(saved.Owner, "N", out _)) throw Blocked("ownership or repository binding differs");
            if (workspace.Root() != CheckoutRoot.Directory) throw Blocked("owned checkout is missing");
            // Local edits, commits and branch switches are not ownership changes; the idle refresh replaces them.
            await Verify(checkout, root, repository, ct);
            var owner = workspace.Owner() ?? throw Blocked("private state is missing or unsafe");
            if (owner != saved.Owner) throw Blocked("checkout was replaced");
            if (saved.Commit is null) throw Blocked("invalid ownership record");
            return new(repository, saved);
        }
        if (workspace.Root() != CheckoutRoot.Missing) throw Blocked("checkout has no ownership record");
        await workspace.Clone(repository, ct);
        var commit = await Inspect(checkout, root, repository, ct);
        if (await checkout.Commit("refs/remotes/origin/" + repository.Branch + "^{commit}", ct) != commit)
            throw Blocked("cloned default branch changed during bootstrap");
        var created = Guid.NewGuid().ToString("N");
        var record = new CheckoutRecord
        {
            Version = 1,
            State = state,
            Root = root,
            Owner = created,
            RepositoryId = repository.Id,
            Repository = repository.Name,
            Remote = repository.Remote,
            Branch = repository.Branch,
            Commit = commit
        };
        workspace.WriteOwner(created);
        workspace.WriteRecord(record);
        return new(repository, record);
    }

    /// <summary>
    /// Refuses anything but this repository's own Git directory at <paramref name="root"/>: linked storage, local configuration
    /// beyond the allowed keys, or another top level, bareness or origin. Defers while a concurrent Git process or a multi-step
    /// operation that a forced checkout would not discard is present.
    /// </summary>
    public static async Task Verify(ICheckoutFacts checkout, string root, RepositoryCheckout repository, CancellationToken ct)
    {
        if (!checkout.OwnGitDirectory()) throw Blocked("checkout must have its own Git directory");
        if (LinkedStorage.Any(checkout.Holds)) throw Blocked("linked Git storage");
        if (Operations.Any(checkout.Holds)) throw new CheckoutDeferred("in-progress Git operation");
        // Only the local configuration, before any command that could invoke a filter.
        foreach (var key in await checkout.ConfigurationKeys(ct))
            if (!Allowed(key.ToLowerInvariant())) throw Blocked("unsupported local Git configuration");
        if (await checkout.TopLevel(ct) != root || await checkout.IsBare(ct) ||
            await checkout.OriginUrls(ct) is not [var origin] || origin != repository.Remote)
            throw Blocked("Git root or remote differs");
    }

    static bool Allowed(string key) =>
        key is "core.repositoryformatversion" or "core.filemode" or "core.bare" or "core.logallrefupdates" or "core.ignorecase" or
            "core.precomposeunicode" or "remote.origin.url" or "remote.origin.fetch" or "remote.origin.gh-resolved" ||
        key.StartsWith("branch.", StringComparison.Ordinal) && (key.EndsWith(".remote", StringComparison.Ordinal) || key.EndsWith(".merge", StringComparison.Ordinal));

    /// <summary>
    /// The commit of a verified, settled checkout: on its default branch, with its index and tracked files matching that
    /// commit and no index entry hiding changes. Refuses any other.
    /// </summary>
    public static async Task<string> Inspect(ICheckoutFacts checkout, string root, RepositoryCheckout repository, CancellationToken ct)
    {
        await Verify(checkout, root, repository, ct);
        if (await checkout.HeadBranch(ct) != "refs/heads/" + repository.Branch || await checkout.Changed(false, ct) ||
            Hidden(await checkout.Index(ct)).Any())
            throw Blocked("checkout does not match its default-branch commit");
        return await checkout.Commit("HEAD^{commit}", ct);
    }

    /// <summary>Index entries whose working-tree changes Git hides: skip-worktree or assume-unchanged.</summary>
    public static IEnumerable<string> Hidden(IEnumerable<IndexEntry> index) =>
        index.Where(entry => entry.SkipWorktree || entry.AssumeUnchanged).Select(entry => entry.Path);
}
