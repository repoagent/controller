namespace RepoAgentController;

/// <summary>Read-only activity on the controller's existing agent runtime. Inspection never starts or resumes work.</summary>
public interface IAgentActivity
{
    int ActivityEpoch { get; }
    event Action<AgentActivityEvent>? ActivityChanged;
    Task<AgentActivityPage> ReadActivity(AgentActivityQuery query, CancellationToken ct);
}

/// <summary>The controller's current, serialized Discussion-to-session mapping.</summary>
public interface IActivitySessions
{
    Task<IReadOnlyList<ActivitySession>> ActivitySessions(CancellationToken ct);
}

public sealed record ActivitySession(string Discussion, string SessionId, bool Dashboard = false);
public sealed record AgentActivityQuery(string SessionId, int Limit, string? Cursor = null);
public sealed record AgentActivityPage(IReadOnlyList<ActivityItem> Items, string? NextCursor = null,
    int SuppressedItems = 0, bool Incomplete = false, string? Notice = null);

public enum ActivityItemKind { Response, Command, Tool, FileChange, Unsupported, Omitted }
public enum ActivityItemState { Unknown, Running, Completed, Failed }

/// <summary>Provider-classified read evidence, not proof that the file was fully read or its instructions followed.</summary>
public sealed record InstructionReadEvidence(string Path);

/// <summary>A user-visible item. Fields contain text only; provider reasoning, raw responses and binary bodies never enter this port.</summary>
public sealed record ActivityItem(string SessionId, string TurnId, string Id, ActivityItemKind Kind,
    ActivityItemState State, string? Name = null, string? Arguments = null, string? Result = null,
    string? Text = null, int? ExitCode = null, IReadOnlyList<InstructionReadEvidence>? Reads = null,
    DateTimeOffset? StartedAt = null, DateTimeOffset? CompletedAt = null, bool Truncated = false,
    bool Incomplete = false);

public abstract record AgentActivityEvent(int Epoch);
public sealed record ActivityItemObserved(int Epoch, ActivityItem Item) : AgentActivityEvent(Epoch);
public sealed record ActivityTextAppended(int Epoch, string SessionId, string TurnId, string ItemId, string Text,
    bool ToolOutput = false) : AgentActivityEvent(Epoch);
public sealed record ActivityRuntimeChanged(int Epoch) : AgentActivityEvent(Epoch);
public sealed record ActivityStreamGap(int Epoch, string SessionId) : AgentActivityEvent(Epoch);
public sealed class ActivityHistoryUnavailable(string reason) : IOException(reason);

public sealed record ActivityInventory(string Lifetime, IReadOnlyList<ActivitySession> Sessions, int Omitted);
public sealed record ActivitySnapshot(string Lifetime, int Epoch, ActivitySession Session, IReadOnlyList<ActivityItem> Items,
    string? NextCursor, int SuppressedItems, bool Incomplete, string? Notice, long Sequence = 0);
public sealed record ActivityFrame(string Kind, string Lifetime, ActivitySnapshot? Snapshot = null,
    ActivityItem? Item = null, string? Notice = null, long Sequence = 0, ActivityInventory? Inventory = null);
