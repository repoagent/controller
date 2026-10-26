namespace RepoAgentController;

/// <summary>Where a controller lifetime records its own status, so a later lifetime and a stopped controller's status read it.</summary>
public interface IStatusRecord
{
    /// <summary>The last recorded status, or null when none was recorded or it cannot be read.</summary>
    LifetimeStatus? Read();

    /// <summary>Replaces the recorded status. A failed write is reported by the record itself, never through the controller log,
    /// whose events status records; the lifetime continues.</summary>
    void Write(LifetimeStatus status);
}

/// <summary>
/// What a controller lifetime records of itself: its repository, the controller's and both credentials' states, its recent
/// failures and latest activity, and the agent sessions running a turn. Times are the recorded text (<c>O</c>).
/// </summary>
public sealed record LifetimeStatus(string? Repository, ControllerRun? Controller, CredentialStatus? Authentication, CredentialStatus? CodexAuthentication)
{
    /// <summary>The recent failures, oldest first.</summary>
    public IReadOnlyList<StatusEvent> Failures { get; init; } = [];

    /// <summary>The recorded failures' JSON as read, when they are not a list; <see cref="Failures"/> is then empty.</summary>
    public string? FailuresAsRead { get; init; }

    public StatusEvent? LatestActivity { get; init; }

    /// <summary>The agent sessions running a turn in this lifetime, in ordinal order; none while no turn runs.</summary>
    public IReadOnlyList<string> RunningSessions { get; init; } = [];
}

/// <summary>A lifetime's state (<c>starting</c>, <c>running</c>, <c>failed</c> or <c>stopped</c>), its mode and its times.</summary>
public sealed record ControllerRun(string State)
{
    public string? Mode { get; init; }
    public string? StartedAt { get; init; }
    public string? UpdatedAt { get; init; }
    public string? StoppedAt { get; init; }

    /// <summary>The recorded JSON as read, when it is not in the form a writer leaves; the other properties are then what reads
    /// from it, and it is reported as read.</summary>
    public string? AsRead { get; init; }
}

/// <summary>A credential's state (<c>unknown</c>, <c>supplied</c>, <c>healthy</c> or <c>failed</c>), since when, and why it failed.</summary>
public sealed record CredentialStatus(string State)
{
    public string? UpdatedAt { get; init; }
    public string? Error { get; init; }

    /// <summary>The recorded JSON as read, when it is not in the form a writer leaves; it is reported as read.</summary>
    public string? AsRead { get; init; }
}

/// <summary>A logged event status reports: when, its kind and message, and for activity the GitHub URL it concerns.</summary>
public sealed record StatusEvent(string At, string Kind, string Message)
{
    public string? Url { get; init; }

    /// <summary>The recorded JSON as read, when it is not in the form a writer leaves; it is reported as read.</summary>
    public string? AsRead { get; init; }

    /// <summary>Whether the JSON kept as read is an object, whose text fields the other properties hold; anything else holds none.</summary>
    public bool AsReadIsObject { get; init; }
}
