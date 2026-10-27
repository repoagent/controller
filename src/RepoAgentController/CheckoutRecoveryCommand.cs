namespace RepoAgentController;

/// <summary>The offline operator action; no runtime or provider is started, and Git only verifies the retained local checkout.</summary>
static class CheckoutRecoveryCommand
{
    public static async Task Recover(string statePath, string workspace, bool stopped, TimeProvider clock, CancellationToken ct)
    {
        if (!stopped) throw new IOException("Checkout recovery requires --confirm-runtime-stopped: stop the controller, agent runtime and ALL agent-launched tools first");
        statePath = Path.GetFullPath(statePath);
        workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        ManagedCheckout.CheckPath(statePath); ManagedCheckout.CheckPath(workspace);
        ManagedCheckout.CheckPath(statePath + ".lock");
        PrivateState(statePath);
        if (Path.Exists(statePath + ".tmp")) throw new IOException("Incomplete Discussion state; retain it and use an isolated fresh instance");
        using var stateLease = ExclusiveFileLease.Open(statePath + ".lock");
        var workspaceLock = Path.Combine(workspace, CheckoutInspection.LockName);
        ManagedCheckout.CheckPath(workspaceLock);
        using var checkoutLease = ExclusiveFileLease.Open(workspaceLock);
        CheckoutInspection.Workspace(Directory.EnumerateFileSystemEntries(workspace).Select(Path.GetFileName)!);
        var recordPath = Path.Combine(workspace, CheckoutInspection.RecordName);
        if (Path.Exists(recordPath + ".tmp")) throw new IOException("Incomplete checkout identity; retain it and use an isolated fresh instance");
        var record = ManagedCheckout.ParseRecord(ManagedCheckout.ReadPrivate(recordPath));
        var root = Path.Combine(workspace, CheckoutInspection.RootName);
        var files = new ControllerStateFiles(statePath);
        var state = files.ReadDiscussions() ?? throw new IOException("Discussion state is missing");
        if (record.Unreadable || record.Version != 1 || record.State != statePath || record.Root != root ||
            !Guid.TryParseExact(record.Owner, "N", out _) || record.RepositoryId is not > 0 || record.Repository is null ||
            record.Remote is null || record.Branch is null || record.Commit is not { Length: 40 } ||
            record.Commit.Any(character => !Uri.IsHexDigit(character)))
            throw new IOException("Checkout recovery binding is missing or differs; retain this instance");
        var repository = RepositoryCheckout.Approved(state.Repository, record.RepositoryId.Value, record.Repository, record.Remote, record.Branch);
        var facts = new ManagedCheckout.CheckoutFacts(root);
        await CheckoutInspection.Verify(facts, root, repository, ct);
        if (ManagedCheckout.ReadPrivate(Path.Combine(root, ".git", "repoagent-owner")) != record.Owner)
            throw new IOException("Checkout recovery owner differs; retain this instance");
        // Verify all identity before normalization. There is no reset, network lookup, process adoption or inferred completion.
        CheckoutRecovery.Recover(new(root, record.Owner!, repository.Id, "refs/heads/" + repository.Branch), files,
            new RefreshJournalFile(statePath), stopped, clock);
        Log.Write("checkout-recovered", $"Native uncertainty explicitly settled; evidence retained at {statePath}.checkout-evidence. Restart with the existing configuration. Input may already have executed or lost output; pending input remains a redelivery.");
    }

    static void PrivateState(string path)
    {
        if (!File.Exists(path) || !OperatingSystem.IsWindows() &&
            (File.GetUnixFileMode(path) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
            throw new IOException("Discussion state is missing or not private");
    }
}
