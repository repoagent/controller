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
    readonly TaskCompletionSource launched = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task? stopped;

    // Only the first command forks. After the notice this trusted supervisor uses shell builtins only, and
    // remains alive until controller stdin closes even when the agent exits with pipe-inheriting descendants.
    public const string Launcher = """
        exec 3<&0
        "$@" <&3 3<&- &
        agent=$!
        exec 3<&-
        printf '\nREPOAGENT_AGENT_LAUNCHED\n' >&2
        exec 1>&-
        result=0
        wait "$agent" || result=$?
        printf '\nREPOAGENT_AGENT_EXITED:%s\n' "$result" >&2
        exec 2>&-
        while IFS= read -r ignored; do :; done
        exit "$result"
        """;

    public bool ConsumeNotice(string line, out int? exitCode)
    {
        exitCode = null;
        if (line == "REPOAGENT_AGENT_LAUNCHED") { launched.TrySetResult(); return true; }
        const string exited = "REPOAGENT_AGENT_EXITED:";
        if (!line.StartsWith(exited, StringComparison.Ordinal) ||
            !int.TryParse(line.AsSpan(exited.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var code) || code > 255) return false;
        exitCode = code;
        return true;
    }

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
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var original = new Identity(root, rootStarted, root.Id);
        var captured = new Dictionary<int, Identity> { [root.Id] = original };
        var sessions = new Dictionary<int, Identity> { [root.Id] = original };
        try
        {
            // A tracked SIGSTOP child makes the pinned runtime's macOS SIGCHLD loop spin. The trusted
            // supervisor never freezes: after this notice it cannot fork and keeps the session identity alive.
            await launched.Task.WaitAsync(deadline.Token);
            while (true)
            {
                var table = await Observe(deadline.Token);
                // The trusted supervisor or a frozen live descendant pins each session's identity.
                foreach (var witness in sessions.Values)
                    if (!Witness(witness, table))
                        throw new IOException("Owned Unix session witness changed before capture could be proved");
                var members = table.Values.Where(row => sessions.ContainsKey(row.UnixSession)).ToArray();
                var changed = false;
                foreach (var row in members.Where(row => row.State != 'Z'))
                {
                    if (row.Id == root.Id) continue;
                    if (!captured.TryGetValue(row.Id, out var identity) || !Same(identity))
                    {
                        if (sessions.Values.Any(witness => witness.Process.Id == row.Id))
                            throw new IOException("Owned session witness changed during member capture");
                        identity = Capture(row, captured);
                        changed = true;
                        if (identity is null) continue;
                    }
                    if (row.State is 'T' or 't') continue;
                    // A short-lived member can exit here: its children retain the owned Unix session and are recaptured.
                    Signal(identity, OperatingSystem.IsMacOS() ? 17 : 19);
                    changed = true;
                }
                // Keep this original snapshot through adoption: a listed live foreign descendant cannot disappear
                // or lose its parent before proof and then silently cease to be a teardown obligation.
                changed |= await AdoptSessions(table, sessions, captured, deadline.Token);
                if (!changed) break; // Only the trusted no-fork supervisor can still execute.
                await Task.Delay(5, deadline.Token);
            }
            var descendants = captured.Values.Where(item => item.Process != root).Reverse().ToArray();
            foreach (var item in descendants) Signal(item, 9);
            await ConfirmStopped(descendants, deadline.Token);
            Signal(original, 9);
            await ConfirmStopped([original], deadline.Token);
        }
        catch (Exception failure)
        {
            // Best-effort cleanup never converts unprovable capture or termination to successful shutdown.
            foreach (var item in captured.Values.Reverse())
                try { Signal(item, 9); }
                catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException) { }
            if (failure is OperationCanceledException && deadline.IsCancellationRequested)
                throw new IOException("Owned agent termination could not be confirmed within its five-second budget", failure);
            throw;
        }
        finally
        {
            foreach (var item in captured.Values.Where(item => item.Process != root)) item.Process.Dispose();
        }
    }

    bool Witness(Identity identity, Dictionary<int, Observation> table) =>
        Same(identity) && table.TryGetValue(identity.Process.Id, out var row) && row.State != 'Z' &&
        (identity.Process == root || row.State is 'T' or 't') && row.UnixSession == identity.UnixSession;

    async Task<bool> AdoptSessions(Dictionary<int, Observation> table, Dictionary<int, Identity> sessions,
        Dictionary<int, Identity> captured, CancellationToken ct)
    {
        var remaining = table.Values.Where(row => row.State != 'Z' && !sessions.ContainsKey(row.UnixSession)).ToDictionary(row => row.Id);
        var changed = false;
        bool adopted;
        do
        {
            adopted = false;
            foreach (var row in remaining.Values.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                if (sessions.ContainsKey(row.UnixSession)) { remaining.Remove(row.Id); continue; }
                if (!table.TryGetValue(row.Parent, out var owner) || !sessions.ContainsKey(owner.UnixSession)) continue;
                if (!captured.TryGetValue(owner.Id, out var parent))
                    parent = Capture(owner, captured) ?? throw new IOException("Observed owned parent exited before foreign-session capture");
                if (!Same(parent)) throw new IOException("Observed owned parent changed before foreign-session capture");
                if (parent.Process != root) await Freeze(parent, ct);

                // A foreign SID is not pinned yet. Do not promote this candidate into failure cleanup's kill set
                // until a newer observation binds its captured birth/session to the same confirmed owned parent.
                var candidate = ReadIdentity(row) ?? throw new IOException("Observed foreign descendant exited before origin could be proved");
                var retained = false;
                try
                {
                    var current = await Observe(ct);
                    if (!Witness(parent, current) || !current.TryGetValue(row.Id, out var child) || child.State == 'Z' ||
                        child.Parent != parent.Process.Id || child.UnixSession != candidate.UnixSession || !Same(candidate))
                        throw new IOException("Foreign descendant origin changed before capture could be proved");
                    Retain(candidate, captured);
                    retained = true;
                    await Freeze(candidate, ct);
                    sessions.Add(candidate.UnixSession, candidate);
                }
                finally { if (!retained) candidate.Process.Dispose(); }
                remaining.Remove(row.Id);
                adopted = changed = true;
            }
        } while (adopted);
        return changed;
    }

    static async Task ConfirmStopped(IReadOnlyCollection<Identity> identities, CancellationToken ct)
    {
        while (identities.Count != 0)
        {
            var table = await Observe(ct);
            if (identities.All(item => !table.TryGetValue(item.Process.Id, out var row) || row.State == 'Z' || !Same(item))) return;
            await Task.Delay(5, ct);
        }
    }

    static Identity? Capture(Observation row, Dictionary<int, Identity> captured)
    {
        var identity = ReadIdentity(row);
        if (identity is null) return null;
        try { Retain(identity, captured); return identity; }
        catch { identity.Process.Dispose(); throw; }
    }

    static void Retain(Identity identity, Dictionary<int, Identity> captured)
    {
        if (captured.Count >= 512 && !captured.ContainsKey(identity.Process.Id)) throw new IOException("Owned agent capture exceeded its process bound");
        if (captured.Remove(identity.Process.Id, out var previous)) previous.Process.Dispose();
        captured.Add(identity.Process.Id, identity);
    }

    static Identity? ReadIdentity(Observation row)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(row.Id);
            var identity = new Identity(process, process.StartTime, row.UnixSession);
            var session = Session(process.Id);
            if (session == -1 && Marshal.GetLastPInvokeError() == 3) { process.Dispose(); return null; }
            if (session != row.UnixSession) throw new IOException("Owned process changed Unix session during capture");
            return identity;
        }
        catch (ArgumentException) { process?.Dispose(); return null; }
        catch (Win32Exception error) when (error.NativeErrorCode == 3) { process?.Dispose(); return null; }
        catch { process?.Dispose(); throw; }
    }

    static async Task Freeze(Identity identity, CancellationToken ct)
    {
        Signal(identity, OperatingSystem.IsMacOS() ? 17 : 19);
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
        Process? current = null;
        try
        {
            current = Process.GetProcessById(identity.Process.Id);
            return current.StartTime == identity.Started;
        }
        catch (ArgumentException) { return false; }
        catch (Win32Exception error) when (error.NativeErrorCode == 3) { return false; }
        finally { current?.Dispose(); }
    }

    static void Signal(Identity identity, int signal)
    {
        if (!Same(identity)) return;
        var session = Session(identity.Process.Id);
        if (session == -1 && Marshal.GetLastPInvokeError() == 3) return;
        if (session != identity.UnixSession) throw new IOException("Owned process changed Unix session before signaling");
        if (Kill(identity.Process.Id, signal) != 0 && Marshal.GetLastPInvokeError() != 3)
            throw new IOException("Owned agent signal could not be delivered", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    static async Task<Dictionary<int, Observation>> Observe(CancellationToken ct)
    {
        // Fixed system facility on both supported Unix platforms; never agent PATH, command lines or environments.
        var start = new ProcessStartInfo("/bin/ps") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-ax", "-o", "pid=", "-o", "ppid=", "-o", "state=" }) start.ArgumentList.Add(argument);
        start.Environment["LC_ALL"] = "C";
        var process = Process.Start(start) ?? throw new IOException("Owned agent process observation could not start");
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
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) throw new IOException("Owned agent process observation failed");
            return result;
        }
        finally
        {
            try
            {
                await StopObserver(process);
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    static async Task StopObserver(Process process)
    {
        if (process.HasExited) return;
        process.Kill();
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await process.WaitForExitAsync(cleanup.Token); }
        catch (OperationCanceledException error) when (cleanup.IsCancellationRequested)
        { throw new IOException("Owned process observer cleanup exceeded its one-second budget", error); }
    }

    sealed record Identity(Process Process, DateTime Started, int UnixSession);
    sealed record Observation(int Id, int Parent, char State, int UnixSession);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
    [LibraryImport("libc", EntryPoint = "getsid", SetLastError = true)]
    private static partial int Session(int pid);
}
