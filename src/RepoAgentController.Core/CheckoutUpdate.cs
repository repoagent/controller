namespace RepoAgentController;

/// <summary>The fetched default branch: its single recorded commit, or none when the fetch recorded several entries.
/// An oversized fetch record is not read.</summary>
public sealed record FetchedBranch(string? Commit, bool Oversized = false);

/// <summary>The workspace operations needed to refresh its owned checkout.</summary>
public interface ICheckoutUpdateWorkspace : ICheckoutWorkspace
{
    /// <summary>Whether the ownership record has the same contents as when last read or written. Throws when unsafe or unreadable.</summary>
    bool Unchanged();

    Task<string> AuthoritativeCommit(string branch, CancellationToken ct);

    /// <summary>Checks that the fetch destination is safe before beginning a fetch. Throws on an unsafe path.</summary>
    void PrepareFetch();

    /// <summary>Fetches with the controller's credentials and reads the resulting default-branch record.</summary>
    Task<FetchedBranch> Fetch(CancellationToken ct);

    /// <summary>Replaces the ownership record and remembers its new contents for the next comparison.</summary>
    void Rewrite(CheckoutRecord record);
}

/// <summary>The checkout facts and the operation that replaces local state at an idle refresh boundary.</summary>
public interface ICheckoutUpdateFacts : ICheckoutFacts
{
    /// <summary>Forces the default branch to the captured commit, clearing hidden index flags and non-ignored untracked files.
    /// Ignored files, nested repositories, other branches and other worktrees are retained.</summary>
    Task Force(string branch, string target, IReadOnlyList<string> hidden, CancellationToken ct);
}

/// <summary>Refreshes an owned checkout only to a captured, fetched and revalidated authoritative default-branch commit.</summary>
public sealed class CheckoutUpdate(ICheckoutUpdateWorkspace workspace, ICheckoutUpdateFacts facts, string root,
    OpenedCheckout opened, IControllerLog log) : IRepositoryCheckout
{
    static readonly string[] BlockingGitFiles = ["MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD"];
    readonly RepositoryCheckout repository = opened.Repository;
    readonly CheckoutRecord metadata = opened.Record;

    async Task VerifyOwnership(CancellationToken ct)
    {
        if (!workspace.Unchanged() || (workspace.Owner() ?? throw CheckoutInspection.Blocked("private state is missing or unsafe")) != metadata.Owner)
            throw CheckoutInspection.Blocked("ownership record changed");
        await CheckoutInspection.Verify(facts, root, repository, ct);
    }

    public async Task<bool> Refresh(Action<string, string> beforeApply, CancellationToken ct)
    {
        await VerifyOwnership(ct);
        RepositoryCheckout current;
        string target;
        try { current = await workspace.AuthoritativeRepository(ct); }
        catch (IOException error) when (error is not CheckoutIdentityException)
        { throw new CheckoutDeferred("authoritative repository lookup unavailable; retry pending"); }
        if (current.Id != repository.Id || current.Name != repository.Name || current.Remote != repository.Remote)
            throw CheckoutInspection.Blocked("authoritative repository identity changed");
        if (current.Branch != repository.Branch) throw new CheckoutDeferred("default branch changed; inspect the recorded binding");
        try { target = await workspace.AuthoritativeCommit(current.Branch, ct); }
        catch (IOException) { throw new CheckoutDeferred("authoritative default-branch lookup unavailable; retry pending"); }
        var previous = metadata.Commit!;
        // An unmoved origin still restores local edits, commits, branch switches and merge state left in the owned checkout.
        if (target == previous && await Matches(target, ct)) return false;
        workspace.PrepareFetch();
        try
        {
            var fetched = await workspace.Fetch(ct);
            if (fetched.Oversized) throw new CheckoutDeferred("ambiguous fetched default branch");
            if (fetched.Commit != target || await facts.Commit("FETCH_HEAD^{commit}", ct) != target ||
                await workspace.AuthoritativeCommit(repository.Branch, ct) != target)
                throw new CheckoutDeferred("default branch changed during fetch; retry pending");
        }
        catch (IOException error) { throw new CheckoutDeferred("fetch refused: " + error.Message); }
        try { current = await workspace.AuthoritativeRepository(ct); }
        catch (IOException error) when (error is not CheckoutIdentityException)
        { throw new CheckoutDeferred("authoritative repository lookup unavailable after fetch; retry pending"); }
        if (current.Id != repository.Id || current.Name != repository.Name || current.Remote != repository.Remote)
            throw CheckoutInspection.Blocked("authoritative repository identity changed during fetch");
        if (current.Branch != repository.Branch) throw new CheckoutDeferred("default branch changed during fetch");
        await VerifyOwnership(ct);
        beforeApply(previous, target);
        // Origin is authoritative inside the owned checkout; the adapter applies this captured target, never a mutable ref.
        await facts.Force(repository.Branch, target, [.. CheckoutInspection.Hidden(await facts.Index(ct))], ct);
        if (await CheckoutInspection.Inspect(facts, root, repository, ct) != target) throw CheckoutInspection.Blocked("applied commit differs");
        metadata.Commit = target;
        workspace.Rewrite(metadata);
        log.Write("checkout-refreshed", $"root={root} commit={target}" + (target == previous ? " restored=local-changes" : ""));
        return true;
    }

    // Ignored files do not count as local changes.
    async Task<bool> Matches(string commit, CancellationToken ct) =>
        await facts.HeadName(ct) == "refs/heads/" + repository.Branch &&
        await facts.Commit("HEAD^{commit}", ct) == commit &&
        !await facts.Changed(true, ct) &&
        !CheckoutInspection.Hidden(await facts.Index(ct)).Any() &&
        !BlockingGitFiles.Any(facts.Holds);
}
