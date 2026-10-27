#pragma warning disable CA1051 // These storage records use ref fields in RecordMap to preserve typed, lossless row mapping.
namespace RepoAgent.Auth;

// The records enrollment reads and writes besides the gate, enrollment, locator, receipt and the issuance records. Each
// maps every property that any broker writer stores in its row shape (see the writer inventory in the #205 PR 6
// report). Fields keep the stored names, except where the glossary renames a concept: an enrollment ID is stored as
// DeviceId, a setup secret's hash as RecoveryHash.

// The retained global record of one admitted enrollment request, keyed by its idempotency ID. It charges the global
// pending capacity until reclamation releases it, and later retires it; redaction removes the setup secret's hash and
// the request's body hash.
public sealed class EnrollmentLedger : BrokerRecord, IRetiredLedger
{
    public string? RepositoryId, PendingId, SetupSecretHash, BodyHash, Epoch, Generation, EnrollmentId, Challenge;
    public string? ApprovalKind, PolicyVersion, RetirementState;
    public long? RowCredits, ReclamationVersion, SetupVersion;
    public bool? PendingCapacityReleased, PayloadRedacted;
    public DateTimeOffset? CreatedAt, Deadline, PayloadPurgeAt, RetirementAuditAt;

    string? IRetiredLedger.RetirementState { get => RetirementState; set => RetirementState = value; }
    DateTimeOffset? IRetiredLedger.RetirementAuditAt { get => RetirementAuditAt; set => RetirementAuditAt = value; }

    public static StateKey<EnrollmentLedger> Key(string idempotencyId) => new(IssuanceCapacity.EnrollmentScope, "pending:" + idempotencyId);

    protected override void Map(RecordMap map)
    {
        map.Text("RepositoryId", ref RepositoryId);
        map.Text("PendingId", ref PendingId);
        map.Text("RecoveryHash", ref SetupSecretHash);
        map.Text("BodyHash", ref BodyHash);
        map.Text("Epoch", ref Epoch);
        map.Time("CreatedAt", ref CreatedAt);
        map.Time("Deadline", ref Deadline);
        map.Time("PayloadPurgeAt", ref PayloadPurgeAt);
        map.Text("Generation", ref Generation);
        map.Number("RowCredits", ref RowCredits);
        map.Number("ReclamationVersion", ref ReclamationVersion);
        map.Text("DeviceId", ref EnrollmentId);
        map.Text("Challenge", ref Challenge);
        map.Text("ApprovalKind", ref ApprovalKind);
        map.Number("SetupVersion", ref SetupVersion);
        map.Text("PolicyVersion", ref PolicyVersion);
        map.Flag("PendingCapacityReleased", ref PendingCapacityReleased);
        map.Text("RetirementState", ref RetirementState);
        map.Time("RetirementAuditAt", ref RetirementAuditAt);
        map.Flag("PayloadRedacted", ref PayloadRedacted);
    }
}

// A pending controller enrollment in its repository partition: a copy of its ledger, what setup asked for, the work it
// claimed, the verifier attempt it minted, the binding GitHub confirmed and, once approved, the protected completion.
// Reclamation closes it and releases its pending capacity; redaction removes its payload or writes a tombstone.
public sealed class PendingControllerEnrollment : BrokerRecord
{
    // Copied from the ledger at admission.
    public string? RepositoryId, PendingId, SetupSecretHash, BodyHash, Epoch, Generation, EnrollmentId, Challenge;
    public string? ApprovalKind, PolicyVersion;
    public long? RowCredits, ReclamationVersion, SetupVersion;
    public DateTimeOffset? CreatedAt, Deadline, PayloadPurgeAt;
    // What setup asked for and what GitHub confirmed.
    public string? RequestedRepository, Repository, Environment, Variable, Profile, Marker;
    public long? InstallationId, EnvironmentId;
    // Lifecycle, work claims and the completion.
    public string? State, Attempt, CompletionId, ProtectedCompletion;
    public bool? RepositoryCharged, PositiveApproval, MintAdmissionStarted, PendingCapacityReleased, PayloadRedacted;
    public DateTimeOffset? NextWorkAt, WorkStartedAt, ObservationReservedAt, ObservationJwtIssuedAt, ObservationJwtExpiresAt, FirstTerminalAt;

    public static StateKey<PendingControllerEnrollment> Key(string repositoryId, string idempotencyId) =>
        new("repository:" + repositoryId, "pending:" + idempotencyId);

    protected override void Map(RecordMap map)
    {
        map.Text("RepositoryId", ref RepositoryId);
        map.Text("PendingId", ref PendingId);
        map.Text("RecoveryHash", ref SetupSecretHash);
        map.Text("BodyHash", ref BodyHash);
        map.Text("Epoch", ref Epoch);
        map.Time("CreatedAt", ref CreatedAt);
        map.Time("Deadline", ref Deadline);
        map.Time("PayloadPurgeAt", ref PayloadPurgeAt);
        map.Text("Generation", ref Generation);
        map.Number("RowCredits", ref RowCredits);
        map.Number("ReclamationVersion", ref ReclamationVersion);
        map.Text("DeviceId", ref EnrollmentId);
        map.Text("Challenge", ref Challenge);
        map.Text("ApprovalKind", ref ApprovalKind);
        map.Number("SetupVersion", ref SetupVersion);
        map.Text("PolicyVersion", ref PolicyVersion);
        map.Text("RequestedRepository", ref RequestedRepository);
        map.Text("Repository", ref Repository);
        map.Text("Environment", ref Environment);
        map.Text("Variable", ref Variable);
        map.Text("Profile", ref Profile);
        map.Text("State", ref State);
        map.Flag("RepositoryCharged", ref RepositoryCharged);
        map.Time("NextWorkAt", ref NextWorkAt);
        map.Flag("PositiveApproval", ref PositiveApproval);
        map.Time("WorkStartedAt", ref WorkStartedAt);
        map.Flag("MintAdmissionStarted", ref MintAdmissionStarted);
        map.Text("Attempt", ref Attempt);
        map.Time("ObservationReservedAt", ref ObservationReservedAt);
        map.Time("ObservationJwtIssuedAt", ref ObservationJwtIssuedAt);
        map.Time("ObservationJwtExpiresAt", ref ObservationJwtExpiresAt);
        map.Number("InstallationId", ref InstallationId);
        map.Number("EnvironmentId", ref EnvironmentId);
        map.Text("Marker", ref Marker);
        map.Text("CompletionId", ref CompletionId);
        map.Text("ProtectedCompletion", ref ProtectedCompletion);
        map.Time("FirstTerminalAt", ref FirstTerminalAt);
        map.Flag("PendingCapacityReleased", ref PendingCapacityReleased);
        map.Flag("PayloadRedacted", ref PayloadRedacted);
    }
}

#pragma warning restore CA1051
