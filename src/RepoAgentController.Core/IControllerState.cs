namespace RepoAgentController;

/// <summary>What the controller keeps beside its state file across lifetimes.</summary>
public interface IControllerState
{
    /// <summary>The repository settings this controller last recorded, or null when none are recorded or the record is unreadable.</summary>
    RecordedSettings? ReadSettings();

    /// <summary>Records the observed repository settings, replacing the earlier record. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when they cannot be recorded.</summary>
    void WriteSettings(RecordedSettings settings);

    /// <summary>The GitHub operations the outbox retains, or null when none were ever retained. Throws
    /// <see cref="InvalidOperationException"/> for a record of another version or with an unreadable operation, such as a receipt
    /// or dashboard in a form the outbox never writes.</summary>
    OutboxState? ReadOutbox();

    /// <summary>Replaces the retained operations. Throws <see cref="IOException"/> when they cannot be written.</summary>
    void WriteOutbox(OutboxState outbox);
}

/// <summary>Each repository setting's latest observation, by name: its state, effective value, since when and why it is unusable.</summary>
public sealed record RecordedSettings(IReadOnlyDictionary<string, SettingValue> Settings);

/// <summary>The outbox's retained GitHub operations, in delivery order, and the installation-wide rate-limit pause.</summary>
public sealed record OutboxState(string Repository, DateTimeOffset? PausedUntil, IReadOnlyList<OutboxOperation> Operations);

/// <summary>
/// One retained GitHub operation. A newer operation with the same key supersedes it. It is due at <see cref="DueAt"/>; uncertain
/// means an earlier attempt may have reached GitHub.
/// </summary>
public abstract record OutboxOperation(string Key)
{
    public int Attempts { get; init; }
    public DateTimeOffset DueAt { get; init; }
    public bool Uncertain { get; init; }
    public string? LastError { get; init; }
}

/// <summary>An EYES receipt for accepted input from the subject at <paramref name="Url"/>.</summary>
public sealed record ReceiptOperation(string SubjectId, string Url) : OutboxOperation("receipt:" + SubjectId);

/// <summary>The dashboard's complete category, title and body.</summary>
public sealed record DashboardOperation(string Category, string Title, string Body) : OutboxOperation("dashboard");

/// <summary>An operation of a kind this controller does not deliver, as a later controller might retain it: kept as read until delivery drops it.</summary>
public sealed record UnsupportedOperation(string Key, string Kind, string SubjectId, string Url, string? Category, string? Title, string? Body) : OutboxOperation(Key);
