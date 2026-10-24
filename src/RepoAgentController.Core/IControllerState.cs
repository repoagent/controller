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
    /// <see cref="InvalidOperationException"/> for a record of another version or with an unreadable operation.</summary>
    OutboxState? ReadOutbox();

    /// <summary>Replaces the retained operations. Throws <see cref="IOException"/> when they cannot be written.</summary>
    void WriteOutbox(OutboxState outbox);
}

/// <summary>Each repository setting's latest observation, by name: its state, effective value, since when and why it is unusable.</summary>
public sealed record RecordedSettings(IReadOnlyDictionary<string, SettingValue> Settings);

/// <summary>The outbox's retained GitHub operations, in delivery order, and the installation-wide rate-limit pause.</summary>
public sealed record OutboxState(string Repository, DateTimeOffset? PausedUntil, IReadOnlyList<OutboxOperation> Operations);

/// <summary>
/// One retained GitHub operation: an EYES receipt for a subject, or the dashboard's complete category, title and body. It is
/// due at <paramref name="DueAt"/>; uncertain means an earlier attempt may have reached GitHub.
/// </summary>
public sealed record OutboxOperation(string Key, string Kind, string SubjectId, string Url, string? Category, string? Title, string? Body,
    int Attempts, DateTimeOffset DueAt, bool Uncertain, string? LastError);
