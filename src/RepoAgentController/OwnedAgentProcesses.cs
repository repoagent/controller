using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace RepoAgentController;

/// <summary>Retains the agent tree until every captured process is no longer executing, including after reparenting.</summary>
internal sealed partial class OwnedAgentProcesses(Process root)
{
    readonly Lock sync = new();
    readonly DateTime rootStarted = root.StartTime;
    Task? stopped;

    // Credential loss and normal disposal share one freeze/capture/kill/wait operation.
    public Task Stop()
    {
        lock (sync) return stopped ??= StopTree();
    }

    async Task StopTree()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Owned agent termination requires Linux or macOS");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var captured = new List<Identity> { new(root, rootStarted) };
        try
        {
            // A confirmed stop precedes enumeration, so this process cannot fork after its children are captured.
            for (var index = 0; index < captured.Count; index++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var current = captured[index];
                if (current.Parent is { } parent)
                {
                    var ancestry = await Observe(deadline.Token);
                    if (!Same(current) || !Same(parent) || !ancestry.TryGetValue(parent.Process.Id, out var owner) || owner.State is not ('T' or 't') ||
                        !ancestry.TryGetValue(current.Process.Id, out var child) || child.Parent != parent.Process.Id)
                        throw new IOException("Owned agent ancestry changed before descendant capture could be proved");
                }
                Signal(current, OperatingSystem.IsMacOS() ? 17 : 19); // SIGSTOP, from each platform's signal.h.
                Dictionary<int, Observation> table;
                while (true)
                {
                    table = await Observe(deadline.Token);
                    if (!Same(current) || !table.TryGetValue(current.Process.Id, out var row) || row.State == 'Z')
                        throw new IOException("Owned agent exited before descendant capture could be proved");
                    if (row.State is 'T' or 't') break;
                    await Task.Delay(5, deadline.Token);
                }
                foreach (var row in table.Values.Where(row => row.Parent == current.Process.Id))
                {
                    // First observed already non-executing under this frozen parent; a zombie cannot fork or resume.
                    // A child first observed alive is retained below and still fails capture if it exits/reparents.
                    if (row.State == 'Z') continue;
                    if (captured.Count >= 512 || captured.Any(item => item.Process.Id == row.Id))
                        throw new IOException("Owned agent descendant capture exceeded its bound or changed identity");
                    var child = Process.GetProcessById(row.Id);
                    try { captured.Add(new(child, child.StartTime, current)); }
                    catch { child.Dispose(); throw; }
                }
            }
            // Keep all identities, not just the parent: Process.Kill(true)/WaitForExit does not await descendants.
            foreach (var item in captured.AsEnumerable().Reverse()) Signal(item, 9);
            while (true)
            {
                var table = await Observe(deadline.Token);
                if (captured.All(item => !table.TryGetValue(item.Process.Id, out var row) || row.State == 'Z' || !Same(item))) return;
                await Task.Delay(5, deadline.Token);
            }
        }
        catch (Exception failure)
        {
            // Best-effort termination on an unprovable capture never converts it to successful shutdown.
            foreach (var item in captured.AsEnumerable().Reverse())
                try { Signal(item, 9); }
                catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException) { }
            try { if (Same(captured[0])) root.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or AggregateException) { }
            // This is the adapter's proof budget, not user cancellation; the command must not map it to exit zero.
            if (failure is OperationCanceledException && deadline.IsCancellationRequested)
                throw new IOException("Owned agent termination could not be confirmed within its five-second budget", failure);
            throw;
        }
        finally
        {
            foreach (var item in captured.Skip(1)) item.Process.Dispose();
        }
    }

    static bool Same(Identity identity)
    {
        try
        {
            using var current = Process.GetProcessById(identity.Process.Id);
            return current.StartTime == identity.Started;
        }
        catch (ArgumentException) { return false; }
    }

    static void Signal(Identity identity, int signal)
    {
        if (!Same(identity)) return; // A recycled PID is not ours to signal.
        if (Kill(identity.Process.Id, signal) != 0 && Marshal.GetLastPInvokeError() != 3) // ESRCH: already exited.
            throw new IOException("Owned agent signal could not be delivered", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    static async Task<Dictionary<int, Observation>> Observe(CancellationToken ct)
    {
        // Both supported Unix platforms provide these fixed ps columns. Never resolve through agent-controlled PATH,
        // request command/environment fields, or retain the host process inventory in output.
        var start = new ProcessStartInfo("/bin/ps") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-ax", "-o", "pid=", "-o", "ppid=", "-o", "state=" }) start.ArgumentList.Add(argument);
        start.Environment["LC_ALL"] = "C";
        using var process = Process.Start(start) ?? throw new IOException("Owned agent process observation could not start");
        try
        {
            var result = new Dictionary<int, Observation>();
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (line.Length > 128 || result.Count >= 8192) throw new IOException("Owned agent process observation exceeded its bound");
                var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length != 3 || !int.TryParse(columns[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 ||
                    !int.TryParse(columns[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent) || parent < 0 || columns[2].Length == 0 ||
                    !result.TryAdd(id, new(id, parent, columns[2][0]))) throw new IOException("Owned agent process observation is malformed");
            }
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) throw new IOException("Owned agent process observation failed");
            return result;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None); }
        }
    }

    sealed record Identity(Process Process, DateTime Started, Identity? Parent = null);
    sealed record Observation(int Id, int Parent, char State);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
}
