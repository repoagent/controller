namespace RepoAgent.Auth;

// The records only cleanup writes. Each maps every property that any broker writer stores in its row shape (see the
// writer inventory in the #205 PR 7 report). Fields keep the stored names, except that an enrollment ID is stored as
// DeviceId.

// Reclamation's durable proof, in the repository partition, that it settled one charged allocation there: an issuance
// attempt (named by its operation ID) or a pending enrollment (named by its pending row's name). It is a copy of the
// allocation's ledger with the approval contract, written with the repository settlement and read before the global
// release. Retirement marks it once it deleted the allocation's repository row, and deletes it before the refund.
public sealed class ReleaseProof : BrokerRecord
{
    public string? RepositoryId, EnrollmentId, Epoch, Generation, OperationId, Purpose, PendingId;
    public string? ApprovalKind, PolicyVersion, SettlementReason;
    public long? GlobalSlot, RowCredits, ReclamationVersion, SetupVersion;
    public bool? RetirementTargetDeleted;
    public DateTimeOffset? ReservedAt, PayloadPurgeAt, CreatedAt, Deadline, RepositorySettledAt, RetirementAuditAt;

    public static StateKey<ReleaseProof> Issuance(string repositoryId, string operationId) =>
        new("repository:" + repositoryId, "release:" + operationId);

    public static StateKey<ReleaseProof> Enrollment(string repositoryId, string idempotencyId) =>
        new("repository:" + repositoryId, "release:pending:" + idempotencyId);

    protected override void Map(RecordMap map)
    {
        map.Text("RepositoryId", ref RepositoryId);
        map.Text("DeviceId", ref EnrollmentId);
        map.Text("Epoch", ref Epoch);
        map.Text("Generation", ref Generation);
        map.Text("OperationId", ref OperationId);
        map.Text("Purpose", ref Purpose);
        map.Text("PendingId", ref PendingId);
        map.Number("GlobalSlot", ref GlobalSlot);
        map.Time("ReservedAt", ref ReservedAt);
        map.Time("PayloadPurgeAt", ref PayloadPurgeAt);
        map.Time("CreatedAt", ref CreatedAt);
        map.Time("Deadline", ref Deadline);
        map.Number("RowCredits", ref RowCredits);
        map.Number("ReclamationVersion", ref ReclamationVersion);
        map.Text("ApprovalKind", ref ApprovalKind);
        map.Number("SetupVersion", ref SetupVersion);
        map.Text("PolicyVersion", ref PolicyVersion);
        map.Text("SettlementReason", ref SettlementReason);
        map.Time("RepositorySettledAt", ref RepositorySettledAt);
        map.Flag("RetirementTargetDeleted", ref RetirementTargetDeleted);
        map.Time("RetirementAuditAt", ref RetirementAuditAt);
    }
}

// Where one cleanup lane is in each family's ledgers, in the global enrollment partition: payload (redaction) or audit
// (reclamation and retirement). Each family keeps the last ledger the lane finished, the stored continuation of an
// empty page, the cycle number and when the cycle started, last progressed and last completed. The payload cursor also
// names the lane the next turn starts with; each cursor names its next family.
public sealed class CleanupCursor : BrokerRecord
{
    public long? SchemaVersion, NextFamily, NextLane;
    public readonly CleanupPosition Enrollment = new(), Issuance = new();

    public static readonly StateKey<CleanupCursor> Payload = new(IssuanceCapacity.EnrollmentScope, "cleanup:payload-v1");
    public static readonly StateKey<CleanupCursor> Audit = new(IssuanceCapacity.EnrollmentScope, "cleanup:audit-v1");

    public CleanupPosition Family(int family) => family == 0 ? Enrollment : Issuance;

    protected override void Map(RecordMap map)
    {
        map.Number("SchemaVersion", ref SchemaVersion);
        map.Number("NextFamily", ref NextFamily);
        map.Number("NextLane", ref NextLane);
        map.Text("EnrollmentLastKey", ref Enrollment.LastKey);
        map.Text("EnrollmentContinuation", ref Enrollment.Continuation);
        map.Number("EnrollmentCycle", ref Enrollment.Cycle);
        map.Time("EnrollmentStartedAt", ref Enrollment.StartedAt);
        map.Time("EnrollmentProgressAt", ref Enrollment.ProgressAt);
        map.Time("EnrollmentCompletedAt", ref Enrollment.CompletedAt);
        map.Text("IssuanceLastKey", ref Issuance.LastKey);
        map.Text("IssuanceContinuation", ref Issuance.Continuation);
        map.Number("IssuanceCycle", ref Issuance.Cycle);
        map.Time("IssuanceStartedAt", ref Issuance.StartedAt);
        map.Time("IssuanceProgressAt", ref Issuance.ProgressAt);
        map.Time("IssuanceCompletedAt", ref Issuance.CompletedAt);
    }
}

// One family's position in a cleanup cursor, stored with the family's name as prefix (EnrollmentLastKey, ...).
public sealed class CleanupPosition
{
    public string? LastKey, Continuation;
    public long? Cycle;
    public DateTimeOffset? StartedAt, ProgressAt, CompletedAt;
}
