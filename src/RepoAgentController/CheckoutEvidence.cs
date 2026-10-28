namespace RepoAgentController;

/// <summary>Private, never reused snapshots of the exact pre-normalization state and journal. An interrupted copy never authorizes recovery.</summary>
static class CheckoutEvidence
{
    public static void Preserve(string statePath)
    {
        var parent = statePath + ".checkout-evidence";
        ManagedCheckout.CheckPath(parent);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(parent);
        else
        {
            Directory.CreateDirectory(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if ((File.GetUnixFileMode(parent) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) != 0)
                throw new IOException("Checkout evidence directory is not private");
        }
        var snapshot = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(snapshot);
        else Directory.CreateDirectory(snapshot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var (source, name) in new[] { (statePath, "state.json"), (statePath + ".refresh.json", "refresh.json") })
        {
            ManagedCheckout.CheckPath(source);
            if (!File.Exists(source)) continue;
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var input = File.OpenRead(source);
            using var output = new FileStream(Path.Combine(snapshot, name), options);
            input.CopyTo(output);
            output.Flush(true);
        }
        ManagedCheckout.SavePrivate(Path.Combine(snapshot, "complete"), "Evidence preserved before checkout recovery or startup normalization\n");
    }
}
