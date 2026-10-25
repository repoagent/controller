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

/// <summary>
/// One Discussion's session and what it is doing (<c>pending</c>, <c>retrying</c>, <c>running</c> or <c>idle</c>): a pending
/// delivery's start and operation, a retry's attempts and due time, a running turn's start, and how many inputs wait.
/// </summary>
public sealed record SessionStatus(string Discussion, string? Session, string State)
{
    public string? Since { get; init; }
    public string? Operation { get; init; }
    public int? Attempts { get; init; }
    public string? RetryAt { get; init; }
    public int Waiting { get; init; }
}

/// <summary>The operator status's work counts, by the same names.</summary>
public sealed record WorkCounts(int Sessions, int Pending, int Retrying, int Waiting, int Running, int Limit, bool? AdmissionBlocked);

/// <summary>
/// What GET /status reports (ADR 0009): busy while an agent turn runs or input is pending, retrying or waiting, else idle,
/// with the work counts of the operator status. A hosting environment decides what busy means for it.
/// </summary>
public sealed record StatusSnapshot(string State, WorkCounts Work)
{
    public static StatusSnapshot From(OperatorStatus status)
    {
        var counts = status.Work;
        var busy = counts.Running + counts.Pending + counts.Retrying + counts.Waiting > 0;
        return new(busy ? "busy" : "idle", counts);
    }
}
