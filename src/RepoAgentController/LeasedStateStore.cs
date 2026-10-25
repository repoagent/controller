namespace RepoAgentController;

/// <summary>
/// The state store as a controller lifetime opens it: the state file's lease is taken, <c>onLockAcquired</c> runs, and only then is
/// the file read, so one controller at a time holds it. Disposing releases the lease.
/// </summary>
sealed class LeasedStateStore : IDisposable
{
    readonly FileStream lease;
    public StateStore Store { get; }

    public LeasedStateStore(string path, string repository, Action? onLockAcquired = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lease = ExclusiveFileLease.Open(path + ".lock");
        try
        {
            onLockAcquired?.Invoke();
            Store = new(new ControllerStateFiles(path), repository);
        }
        catch { lease.Dispose(); throw; }
    }

    public void Dispose() => lease.Dispose();
}
