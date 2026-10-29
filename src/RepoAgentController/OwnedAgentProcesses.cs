using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace RepoAgentController;

/// <summary>Owns the agent's launch-time Unix session and waits for captured processes even after reparenting.</summary>
internal sealed partial class OwnedAgentProcesses
{
    readonly Process root;
    readonly DateTime rootStarted;
    readonly Lock sync = new();
    Task? stopped;

    public OwnedAgentProcesses(Process root)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Owned agent termination requires Linux or macOS");
        this.root = root;
        rootStarted = root.StartTime;
        if (Session(root.Id) != root.Id) throw new IOException("Agent process did not start in its own Unix session");
    }

    // Credential loss and normal disposal share one freeze/capture/kill/wait operation.
    public Task Stop()
    {
        lock (sync) return stopped ??= StopSession();
    }

    async Task StopSession()
    {
        Trace("stop-enter");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var original = new Identity(root, rootStarted, root.Id);
        var captured = new Dictionary<int, Identity> { [root.Id] = original };
        var sessions = new Dictionary<int, Identity> { [root.Id] = original };
        try
        {
            await Freeze(original, deadline.Token);
            Trace("root-frozen");
            while (true)
            {
                var table = await Observe(deadline.Token);
                Trace("capture-observed");
                // A frozen live witness pins each session's identity until capture is complete.
                foreach (var witness in sessions.Values)
                    if (!Same(witness) || !table.TryGetValue(witness.Process.Id, out var row) || row.State is not ('T' or 't') || row.UnixSession != witness.UnixSession)
                        throw new IOException("Owned Unix session witness changed before capture could be proved");
                var members = table.Values.Where(row => sessions.ContainsKey(row.UnixSession)).ToArray();
                var changed = false;
                foreach (var row in members.Where(row => row.State != 'Z'))
                {
                    if (!captured.TryGetValue(row.Id, out var identity) || !Same(identity))
                    {
                        identity = Capture(row, captured);
                        changed = true;
                        if (identity is null) continue;
                    }
                    if (row.State is 'T' or 't') continue;
                    // A short-lived member can exit here: its children retain the owned Unix session and are recaptured.
                    Signal(identity, OperatingSystem.IsMacOS() ? 17 : 19);
                    changed = true;
                }
                // Preserve tree coverage for observable descendants that deliberately create another Unix session.
                foreach (var row in table.Values.Where(row => row.State != 'Z' && !sessions.ContainsKey(row.UnixSession)))
                {
                    if (!captured.TryGetValue(row.Parent, out var parent) || !table.TryGetValue(row.Parent, out var owner) || owner.State is not ('T' or 't')) continue;
                    if (!Same(parent)) throw new IOException("Owned descendant ancestry changed before session capture");
                    var identity = captured.GetValueOrDefault(row.Id) ?? Capture(row, captured)
                        ?? throw new IOException("Owned descendant exited before its new Unix session could be captured");
                    await Freeze(identity, deadline.Token);
                    sessions.Add(identity.UnixSession, identity);
                    changed = true;
                }
                if (!changed) break; // Every current member is stopped/zombie, with live frozen session witnesses.
                await Task.Delay(5, deadline.Token);
            }
            foreach (var item in captured.Values.Reverse()) Signal(item, 9);
            Trace("kill-delivered");
            while (true)
            {
                var table = await Observe(deadline.Token);
                if (captured.Values.All(item => !table.TryGetValue(item.Process.Id, out var row) || row.State == 'Z' || !Same(item))) return;
                await Task.Delay(5, deadline.Token);
            }
        }
        catch (Exception failure)
        {
            // Best-effort cleanup never converts unprovable capture or termination to successful shutdown.
            foreach (var item in captured.Values.Reverse())
                try { Signal(item, 9); }
                catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException) { }
            try { if (Same(original)) root.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException or AggregateException) { }
            if (failure is OperationCanceledException && deadline.IsCancellationRequested)
                throw new IOException("Owned agent termination could not be confirmed within its five-second budget", failure);
            throw;
        }
        finally
        {
            foreach (var item in captured.Values.Where(item => item.Process != root)) item.Process.Dispose();
        }
    }

    static Identity? Capture(Observation row, Dictionary<int, Identity> captured)
    {
        if (captured.Count >= 512 && !captured.ContainsKey(row.Id)) throw new IOException("Owned agent capture exceeded its process bound");
        Process? process = null;
        try
        {
            process = Process.GetProcessById(row.Id);
            var identity = new Identity(process, process.StartTime, row.UnixSession);
            var session = Session(process.Id);
            if (session == -1 && Marshal.GetLastPInvokeError() == 3) { process.Dispose(); return null; }
            if (session != row.UnixSession) throw new IOException("Owned process changed Unix session during capture");
            if (captured.Remove(row.Id, out var previous)) previous.Process.Dispose();
            captured.Add(row.Id, identity);
            return identity;
        }
        catch (ArgumentException) { process?.Dispose(); return null; }
        catch (Win32Exception error) when (error.NativeErrorCode == 3) { process?.Dispose(); return null; }
        catch { process?.Dispose(); throw; }
    }

    static async Task Freeze(Identity identity, CancellationToken ct)
    {
        Trace("freeze-before-signal");
        Signal(identity, OperatingSystem.IsMacOS() ? 17 : 19);
        Trace("freeze-after-signal");
        while (true)
        {
            var table = await Observe(ct);
            if (!Same(identity) || !table.TryGetValue(identity.Process.Id, out var row) || row.State == 'Z' || row.UnixSession != identity.UnixSession)
                throw new IOException("Owned Unix session witness exited before capture could be proved");
            if (row.State is 'T' or 't') return;
            await Task.Delay(5, ct);
        }
    }

    static bool Same(Identity identity)
    {
        Trace("same-enter");
        Process? current = null;
        try
        {
            current = Process.GetProcessById(identity.Process.Id);
            Trace("same-before-start-time");
            var same = current.StartTime == identity.Started;
            Trace("same-after-start-time");
            return same;
        }
        catch (ArgumentException) { return false; }
        catch (Win32Exception error) when (error.NativeErrorCode == 3) { return false; }
        finally { Trace("same-before-dispose"); current?.Dispose(); Trace("same-after-dispose"); }
    }

    static void Signal(Identity identity, int signal)
    {
        Trace("signal-enter");
        if (!Same(identity)) return;
        Trace("signal-same");
        var session = Session(identity.Process.Id);
        if (session == -1 && Marshal.GetLastPInvokeError() == 3) return;
        if (session != identity.UnixSession) throw new IOException("Owned process changed Unix session before signaling");
        if (Kill(identity.Process.Id, signal) != 0 && Marshal.GetLastPInvokeError() != 3)
            throw new IOException("Owned agent signal could not be delivered", new Win32Exception(Marshal.GetLastPInvokeError()));
        Trace("signal-delivered");
    }

    static async Task<Dictionary<int, Observation>> Observe(CancellationToken ct)
    {
        Trace("observe-enter");
        // Fixed system facility on both supported Unix platforms; never agent PATH, command lines or environments.
        var start = new ProcessStartInfo("/bin/ps") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-ax", "-o", "pid=", "-o", "ppid=", "-o", "state=" }) start.ArgumentList.Add(argument);
        start.Environment["LC_ALL"] = "C";
        var process = Process.Start(start) ?? throw new IOException("Owned agent process observation could not start");
        Trace("observe-started");
        try
        {
            var result = new Dictionary<int, Observation>();
            var rows = 0;
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (line.Length > 128 || ++rows > 8192) throw new IOException("Owned agent process observation exceeded its bound");
                var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length != 3 || !int.TryParse(columns[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 ||
                    !int.TryParse(columns[1], NumberStyles.None, CultureInfo.InvariantCulture, out var parent) || parent < 0 || columns[2].Length == 0)
                    throw new IOException("Owned agent process observation is malformed");
                var session = Session(id);
                if (session == -1 && Marshal.GetLastPInvokeError() == 3) continue;
                // Host/kernel processes can legitimately belong to session zero; our launch-time sessions are positive.
                if (session < 0 || !result.TryAdd(id, new(id, parent, columns[2][0], session))) throw new IOException("Owned Unix session observation is unavailable");
            }
            await WaitForObserverExit(process, ct);
            Trace("observe-exited");
            if (process.ExitCode != 0) throw new IOException("Owned agent process observation failed");
            return result;
        }
        finally
        {
            Trace("observe-cleanup");
            try
            {
                await StopObserver(process);
                Trace("observe-cleaned");
            }
            finally
            {
                Trace("observe-before-dispose");
                process.Dispose();
                Trace("observe-after-dispose");
            }
        }
    }

    static async Task WaitForObserverExit(Process process, CancellationToken ct)
    {
        while (!process.HasExited) await Task.Delay(5, ct);
    }

    static async Task StopObserver(Process process)
    {
        if (process.HasExited) return;
        process.Kill();
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await WaitForObserverExit(process, cleanup.Token); }
        catch (OperationCanceledException error) when (cleanup.IsCancellationRequested)
        { throw new IOException("Owned process observer cleanup exceeded its one-second budget", error); }
    }

    static void Trace(string phase)
    {
        if (OperatingSystem.IsMacOS()) Console.Error.WriteLine("OWNED_STOP phase=" + phase);
    }

    sealed record Identity(Process Process, DateTime Started, int UnixSession);
    sealed record Observation(int Id, int Parent, char State, int UnixSession);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
    [LibraryImport("libc", EntryPoint = "getsid", SetLastError = true)]
    private static partial int Session(int pid);
}
