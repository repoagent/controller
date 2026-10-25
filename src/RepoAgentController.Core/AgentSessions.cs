namespace RepoAgentController;

/// <summary>
/// Records the runtime's turn lifecycle for the managed checkout's refresh exclusion. <see cref="AgentSessions"/> passes it every
/// runtime event after its own tracking and before credential health and the wake of dispatch.
/// </summary>
public interface IAgentTurnJournal
{
    /// <summary>The runtime process is being replaced; later events carry <paramref name="epoch"/>. Throws while work is unresolved.</summary>
    void NewEpoch(int epoch);

    void Observe(AgentRuntimeEvent change);
}

/// <summary>
/// The agent sessions and turns of the runtime's current process, tracked from its responses and events: which sessions are
/// loaded and oriented, their latest reported status, each accepted turn until its completion, completions that overtook their
/// acceptance, and the agent account's credential health, which the runtime reports only through turn outcomes.
/// </summary>
public sealed class AgentSessions
{
    /// <summary>
    /// Whether a loaded session in <paramref name="status"/> can receive its next batch. Pinned Codex leaves the thread of a
    /// failed turn in systemError until its next turn; it accepts turn/start there and runs that turn normally, while resume
    /// and reload keep reporting systemError. Active, unloaded and unrecognized statuses never admit input.
    /// </summary>
    public static bool Admits(AgentSessionStatus status) => status is AgentSessionStatus.Idle or AgentSessionStatus.SystemError;

    /// <summary>Whether the runtime refused a turn request, so no turn holds its input. After a timeout or a lost process, one may.</summary>
    public static bool Refused(Exception error) => error is AgentRequestRejected;

    readonly IAgentRuntime runtime;
    readonly IControllerLog log;
    readonly IAgentTurnJournal? journal;
    readonly bool suppliedCredentials;
    readonly CancellationToken credentialLost;
    int epoch = 1;
    // Guarded by statusLock for this epoch: loaded sessions, sessions oriented since loading, their latest native status
    // with a change version, each session's accepted turn until its turn/completed, and completions that overtook their acceptance.
    readonly object statusLock = new();
    readonly HashSet<string> resumed = [], oriented = [];
    readonly Dictionary<string, (AgentSessionStatus Status, long Version)> statuses = [];
    readonly Dictionary<string, string> turns = [];
    readonly HashSet<(string Session, string Turn)> finished = [];
    // Guarded by statusLock across epochs: turn completions not yet taken by TakeTurns.
    readonly List<TurnOutcome> ended = [];
    /// <summary>Raised on the runtime's reader after a status report, turn completion or transport loss.</summary>
    public event Action? StatusChanged;
    /// <summary>
    /// Raised with the sessions running a turn whenever they may have changed: as <see cref="StatusChanged"/>, and after a turn is
    /// accepted. Raised under the turn lock, so handlers see every change in order; they must not wait for another thread.
    /// </summary>
    public event Action<string[]>? TurnsChanged;

    /// <summary>A turn the native reported completed with its status as reported, and whether the provider rejected the Codex account credentials for it.</summary>
    public sealed record TurnOutcome(string Session, string Turn, string Status, bool CredentialsRejected);

    public AgentSessions(IAgentRuntime runtime, IControllerLog log, IAgentTurnJournal? journal = null, CancellationToken credentialLost = default,
        bool suppliedCredentials = false)
    {
        this.runtime = runtime;
        this.log = log;
        this.journal = journal;
        this.credentialLost = credentialLost;
        this.suppliedCredentials = suppliedCredentials;
        runtime.Changed += Observe;
    }

    void Observe(AgentRuntimeEvent change)
    {
        var changed = false;
        AgentTurnCompleted? completion = null;
        lock (statusLock)
        {
            if (change.Epoch == epoch)
                switch (change)
                {
                    case AgentRuntimeDisconnected: Forget(); changed = true; break;
                    case AgentSessionStatusChanged { SessionId.Length: > 0 } report:
                        Report(report.SessionId, report.Status); changed = true;
                        log.Write("session-status", $"session={report.SessionId} status={Native(report.SessionId)}");
                        break;
                    case AgentTurnCompleted { SessionId.Length: > 0, TurnId: { } turn } completed:
                        var id = completed.SessionId;
                        // The native reports idle before turn/completed; the session admits input after both.
                        if (turns.GetValueOrDefault(id) == turn) turns.Remove(id);
                        else { if (finished.Count >= 256) finished.Clear(); finished.Add((id, turn)); }
                        completion = completed;
                        // Held before the outcome is visible, so input returned from a rejected turn waits for new credentials.
                        var rejection = completed.Status == AgentTurnStatus.Failed ? completed.CredentialRejection : null;
                        if (rejection is not null) authenticationFailure = rejection;
                        if (ended.Count >= 256) ended.RemoveAt(0);
                        ended.Add(new(id, turn, completed.ReportedStatus, rejection is not null));
                        changed = true;
                        break;
                }
            if (changed) TurnsChanged?.Invoke([.. Running()]);
        }
        // The refresh journal records the same event before a dispatch can be woken by it.
        journal?.Observe(change);
        // Before waking dispatch, so input never follows a rejected turn.
        if (completion is not null) Authentication(completion);
        if (changed) StatusChanged?.Invoke();
    }

    // Caller holds statusLock.
    void Forget() { statuses.Clear(); resumed.Clear(); oriented.Clear(); turns.Clear(); finished.Clear(); }

    // Caller holds statusLock.
    void Report(string id, AgentSessionStatus status)
    {
        statuses[id] = (status, Version(id) + 1);
        if (status == AgentSessionStatus.NotLoaded) { resumed.Remove(id); oriented.Remove(id); }
    }
    long Version(string id) => statuses.TryGetValue(id, out var value) ? value.Version : 0;
    AgentSessionStatus Native(string id) => statuses.TryGetValue(id, out var value) ? value.Status : AgentSessionStatus.Unknown;
    // An accepted turn holds its session until that turn's completion, whatever status the native reports meanwhile.
    AgentSessionStatus Current(string id) => turns.ContainsKey(id) ? AgentSessionStatus.Active : Native(id);

    /// <summary>Broker credential loss has begun terminating the native process; this controller lifetime is ending.</summary>
    public bool CredentialLost => credentialLost.IsCancellationRequested;

    public AgentSessionStatus Status(string id) { lock (statusLock) return Current(id); }
    public bool Loaded(string id) { lock (statusLock) return resumed.Contains(id); }
    /// <summary>Sessions running a native turn in this process; idle, unloaded and failed sessions are not counted.</summary>
    public int ActiveTurns { get { lock (statusLock) return Running().Count(); } }

    /// <summary>
    /// Takes the turns completed since the last call, together with the sessions still running a turn. Read atomically, so a
    /// session that runs no turn and has no outcome here lost its turn with the native process.
    /// </summary>
    public (TurnOutcome[] Ended, HashSet<string> Running) TakeTurns()
    {
        lock (statusLock)
        {
            var taken = ended.ToArray();
            ended.Clear();
            return (taken, Running().ToHashSet());
        }
    }

    // Caller holds statusLock.
    IEnumerable<string> Running() => statuses.Keys.Union(turns.Keys).Where(id => Current(id) == AgentSessionStatus.Active);

    /// <summary>
    /// Restarts the runtime when it runs no accepted turn and reports no active session, calling <paramref name="prepare"/>
    /// while no runtime process runs. Sessions resume on their next input. False while work runs.
    /// </summary>
    /// <remarks>The caller excludes concurrent dispatch, which loads sessions and starts turns.</remarks>
    public async Task<bool> RestartWhenIdle(Action prepare, CancellationToken ct)
    {
        lock (statusLock)
            if (turns.Count != 0 || statuses.Values.Any(value => value.Status == AgentSessionStatus.Active)) return false;
        await Restart(prepare, ct);
        authenticationFailure = null;
        Volatile.Write(ref authenticated, 0);
        StatusChanged?.Invoke(); // Input held for replacement credentials can dispatch now.
        return true;
    }

    // The native reports account-credential health only through turn outcomes.
    volatile string? authenticationFailure;
    int authenticated;

    /// <summary>The native's latest account-credential rejection; cleared by applied replacement credentials or a completed turn.</summary>
    public string? AuthenticationFailure => authenticationFailure;

    void Authentication(AgentTurnCompleted turn)
    {
        if (turn.Status == AgentTurnStatus.Completed)
        {
            authenticationFailure = null;
            if (Interlocked.Exchange(ref authenticated, 1) == 0) log.Write("codex-auth-verified", "Codex account credentials authenticated a completed turn");
        }
        else if (turn.Status == AgentTurnStatus.Failed && turn.CredentialRejection is { } message)
        {
            Volatile.Write(ref authenticated, 0);
            authenticationFailure = message;
            log.Write("codex-auth-failed", $"session={turn.SessionId} turn={turn.TurnId}: Codex account credentials were rejected ({message}). " +
                (suppliedCredentials ? "Replace the --codex-auth file; the controller applies it at the next idle boundary." :
                    "Supply fresh credentials with --codex-auth, or renew the native sign-in and restart.") +
                " New Discussion input is held until then.");
        }
    }

    /// <summary>Restarts the runtime, so a refreshed checkout's project configuration applies. Sessions resume on their next input.</summary>
    public Task Reload(CancellationToken ct) => Restart(null, ct);

    Task Restart(Action? prepare, CancellationToken ct) => runtime.Restart(next =>
    {
        prepare?.Invoke();
        lock (statusLock) { epoch = next; Forget(); }
        journal?.NewEpoch(next);
    }, ct);

    /// <summary>Starts a new session in this runtime process and returns its ID.</summary>
    public async Task<string> Start(CancellationToken ct)
    {
        var (id, status) = await runtime.StartSession(ct);
        lock (statusLock)
        {
            resumed.Add(id);
            // A status notification processed before this continuation is newer than the response.
            if (Version(id) == 0) Report(id, status);
        }
        return id;
    }

    /// <summary>Loads a stored session into this runtime process if needed; returns its native-reported status.</summary>
    public async Task<AgentSessionStatus> Load(string id, CancellationToken ct)
    {
        long before;
        lock (statusLock)
        {
            if (resumed.Contains(id)) return Current(id);
            before = Version(id);
        }
        var reported = await runtime.ResumeSession(id, ct);
        AgentSessionStatus status;
        lock (statusLock)
        {
            resumed.Add(id);
            if (Version(id) == before) Report(id, reported);
            status = Current(id);
        }
        log.Write("session-resumed", $"{id} status={status}");
        return status;
    }

    public Task<IReadOnlyList<NativeModel>> Models(CancellationToken ct) => runtime.Models(ct);

    public Task<(string? Model, string? Effort)> ConfiguredModel(CancellationToken ct) => runtime.ConfiguredModel(ct);

    /// <summary>
    /// Starts a turn carrying <paramref name="input"/> and returns its turn ID. <paramref name="accepted"/> sees the accepted turn
    /// once it holds its session, before the acceptance is logged.
    /// </summary>
    public async Task<string> Accept(string id, string input, ModelChoice selection, Action<string, string>? accepted, CancellationToken ct)
    {
        bool orient;
        lock (statusLock)
        {
            // Native turn/start adds input to an active turn (steering); only a loaded session that admits input receives a batch.
            if (!resumed.Contains(id) || !Admits(Current(id)))
                throw new InvalidOperationException("Native session is not loaded and ready for input; input must wait");
            // Each start or resume in this native process receives the shared orientation once.
            orient = !oriented.Contains(id);
        }
        // Native keeps a turn's model and effort for later turns, so every turn states its selection.
        var response = await runtime.StartTurn(new(id, input, selection, orient), ct);
        var turn = response.TurnId;
        lock (statusLock)
        {
            if (orient && resumed.Contains(id)) oriented.Add(id);
            // Native reports active only after this response, and its completion can overtake it.
            if (!finished.Remove((id, turn)) && !response.Ended) turns[id] = turn;
            TurnsChanged?.Invoke([.. Running()]);
        }
        accepted?.Invoke(id, turn);
        log.Write("native-accepted", $"session={id} model={selection.Model} effort={selection.Effort} selection={selection.Source} turn={turn}");
        return turn;
    }
}
