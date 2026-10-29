namespace RepoAgentController;

/// <summary>
/// The operator status: when it was generated, the repository and App login, the lifetime and credentials, the recorded
/// settings, the work counts, each Discussion's session, and the recent failures and latest activity.
/// </summary>
public sealed record OperatorStatus(DateTimeOffset GeneratedAt, string? Repository, string? Actor, ControllerRun Controller, CredentialStatus Authentication,
    CredentialStatus CodexAuthentication, RecordedSettings? Settings, WorkCounts Work, IReadOnlyList<SessionStatus> Sessions, IReadOnlyList<StatusEvent> Failures,
    StatusEvent? LatestActivity)
{
    /// <summary>The recorded failures' JSON as read, when they are not a list; reported as read.</summary>
    public string? FailuresAsRead { get; init; }
}

/// <summary>One Discussion's session, what it is doing, and how many inputs wait for it.</summary>
public sealed record SessionStatus(string Discussion, string? Session, SessionActivity Activity)
{
    public int Waiting { get; init; }
}

/// <summary>
/// What a Discussion's session is doing, by its state's name, with the facts reported for that state. A fact is null when the
/// saved delivery, retry or turn holds it in a form no writer leaves.
/// </summary>
public abstract record SessionActivity(string State)
{
    public sealed record Idle() : SessionActivity("idle");

    /// <summary>A delivery whose outcome is not yet known: since when, and its operation.</summary>
    public sealed record Pending(string? Since, string? Operation) : SessionActivity("pending");

    /// <summary>A failed or unacknowledged delivery waiting for its next attempt: the attempts so far and when it is due.</summary>
    public sealed record Retrying(int? Attempts, string? RetryAt) : SessionActivity("retrying");

    /// <summary>Unfinished accepted work awaiting capacity, with its durable selection and next due time.</summary>
    public sealed record Recovering(int Attempts, string RetryAt, string Model, string Effort, string? Suspended) : SessionActivity("recovering");

    /// <summary>A running turn, with its start time when the saved record holds one.</summary>
    public sealed record Running(string? Since) : SessionActivity("running");
}

/// <summary>The operator status's work counts, by the same names.</summary>
public sealed record WorkCounts(int Sessions, int Pending, int Retrying, int Waiting, int Running, int Limit, bool? AdmissionBlocked);

/// <summary>
/// What GET /status reports (ADR 0009): busy while an agent turn runs, input is pending, retrying or waiting, or checkout admission is blocked; else idle,
/// with the work counts of the operator status. A hosting environment decides what busy means for it.
/// </summary>
public sealed record StatusSnapshot(string State, WorkCounts Work)
{
    public static StatusSnapshot From(OperatorStatus status, bool managedCheckoutExpected = false)
    {
        var counts = status.Work;
        var busy = counts.AdmissionBlocked == true || managedCheckoutExpected && counts.AdmissionBlocked is null ||
            counts.Running + counts.Pending + counts.Retrying + counts.Waiting > 0;
        return new(busy ? "busy" : "idle", counts);
    }
}
