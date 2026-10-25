namespace RepoAgentController;

/// <summary>A session's runtime status as its runtime process reports it; see <see cref="AgentSessions.Admits"/>. The names appear in logs.</summary>
public enum AgentSessionStatus { Unknown, NotLoaded, Idle, Active, SystemError }

/// <summary>
/// The agent runtime (ADR 0010): the owned Codex app-server process and the sessions it hosts. It reports what its process
/// answers and announces, and decides nothing; <see cref="AgentSessions"/> tracks sessions and turns over it.
/// </summary>
public interface IAgentRuntime
{
    /// <summary>
    /// Raised in order, on the runtime's reader, for each session status report, turn start, turn completion and loss of the
    /// process. Each event carries the epoch of the process that reported it; the first process is epoch 1.
    /// </summary>
    event Action<AgentRuntimeEvent>? Changed;

    /// <summary>
    /// Stops the runtime process, calls <paramref name="whileStopped"/> with the next epoch while no process runs, then starts
    /// and initializes the next process. Its project skills must reload cleanly.
    /// </summary>
    Task Restart(Action<int> whileStopped, CancellationToken ct);

    /// <summary>Starts a new session, with the status its start response reported.</summary>
    Task<(string SessionId, AgentSessionStatus Status)> StartSession(CancellationToken ct);

    /// <summary>Loads a stored session into this process; returns the status its resume response reported.</summary>
    Task<AgentSessionStatus> ResumeSession(string sessionId, CancellationToken ct);

    /// <summary>
    /// Starts a turn carrying the request's input. Throws <see cref="AgentRequestRejected"/> when the runtime refuses it;
    /// a timeout or a lost process throws something else, and the turn may then have started.
    /// </summary>
    Task<AgentTurnAccepted> StartTurn(AgentTurnRequest request, CancellationToken ct);

    /// <summary>The model and reasoning effort native configuration sets for the workspace; null where unset.</summary>
    Task<(string? Model, string? Effort)> ConfiguredModel(CancellationToken ct);

    /// <summary>The models the agent account offers for selection, without hidden ones.</summary>
    Task<IReadOnlyList<NativeModel>> Models(CancellationToken ct);
}

/// <summary>A turn to start: the bridge envelope as its input text, the selected model and effort, and whether the session still
/// needs the shared orientation in this process.</summary>
public sealed record AgentTurnRequest(string SessionId, string Input, ModelChoice Model, bool Orientation);

/// <summary>The runtime accepted a turn. Ended when its acceptance already reported it completed, interrupted or failed.</summary>
public sealed record AgentTurnAccepted(string TurnId, bool Ended);

/// <summary>The runtime answered a request with an error, so it did not perform it. Timeouts and transport loss are not rejections.</summary>
public sealed class AgentRequestRejected(string message) : IOException(message);

/// <summary>Something the runtime process with this epoch reported.</summary>
public abstract record AgentRuntimeEvent(int Epoch);

/// <summary>The runtime reported a session's status.</summary>
public sealed record AgentSessionStatusChanged(int Epoch, string SessionId, AgentSessionStatus Status) : AgentRuntimeEvent(Epoch);

/// <summary>The runtime reported a turn started. An ID the report lacked is empty.</summary>
public sealed record AgentTurnStarted(int Epoch, string SessionId, string TurnId) : AgentRuntimeEvent(Epoch);

/// <summary>
/// The runtime reported a turn ended, with its runtime status. A session ID the report lacked is empty and a turn ID null.
/// <paramref name="CredentialRejection"/> is the provider's message when it rejected the agent account's credentials.
/// </summary>
public sealed record AgentTurnCompleted(int Epoch, string SessionId, string? TurnId, string Status, string? CredentialRejection) : AgentRuntimeEvent(Epoch);

/// <summary>The runtime process stopped answering; its sessions and turns are gone.</summary>
public sealed record AgentRuntimeDisconnected(int Epoch) : AgentRuntimeEvent(Epoch);
