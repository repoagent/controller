#pragma warning disable CA1051 // These storage records use ref fields in RecordMap to preserve typed, lossless row mapping.
using System.Globalization;

namespace RepoAgent.Auth;

// The records GitHub token issuance reads and writes besides the gate, enrollment and revocation receipt. Each maps
// every property that any broker writer stores in its row shape (see the writer inventory in the #205 PR 5 report).
// Fields keep the stored names, except that an enrollment ID is stored as DeviceId.

// One GitHub token's reservation, send claim, known outcome, publication and settlement, in the repository partition.
// Enrollment creates verification attempts with a PendingId; reclamation and redaction may write a settled copy or a
// tombstone of the ledger (RowCredits) when the attempt itself is missing.
public sealed class IssuanceAttempt : BrokerRecord
{
    public string? OperationId, Generation, RepositoryId, EnrollmentId, Epoch, Purpose, Profile, PendingId, State;
    public string? ApprovalKind, PolicyVersion, ProtectedToken, SettlementReason;
    public long? InstallationId, GlobalSlot, SetupVersion, RowCredits;
    public bool? RepositoryCharged, ScopeValid, CapacityReleased, PayloadRedacted;
    public DateTimeOffset? ReservedAt, PayloadPurgeAt, JwtIssuedAt, JwtExpiresAt, SendClaimedAt, ExpiresAt, ReleasedAt, SettledAt, FirstTerminalAt;

    public static StateKey<IssuanceAttempt> Key(string repositoryId, string operationId) =>
        new("repository:" + repositoryId, "attempt:" + operationId);

    protected override void Map(RecordMap map)
    {
        map.Text("OperationId", ref OperationId);
        map.Text("Generation", ref Generation);
        map.Text("RepositoryId", ref RepositoryId);
        map.Text("DeviceId", ref EnrollmentId);
        map.Text("PendingId", ref PendingId);
        map.Text("Epoch", ref Epoch);
        map.Text("Purpose", ref Purpose);
        map.Text("Profile", ref Profile);
        map.Number("InstallationId", ref InstallationId);
        map.Text("ApprovalKind", ref ApprovalKind);
        map.Number("SetupVersion", ref SetupVersion);
        map.Text("PolicyVersion", ref PolicyVersion);
        map.Flag("RepositoryCharged", ref RepositoryCharged);
        map.Text("State", ref State);
        map.Time("ReservedAt", ref ReservedAt);
        map.Time("PayloadPurgeAt", ref PayloadPurgeAt);
        map.Time("JwtIssuedAt", ref JwtIssuedAt);
        map.Time("JwtExpiresAt", ref JwtExpiresAt);
        map.Number("GlobalSlot", ref GlobalSlot);
        map.Number("RowCredits", ref RowCredits);
        map.Time("SendClaimedAt", ref SendClaimedAt);
        map.Text("ProtectedToken", ref ProtectedToken);
        map.Time("ExpiresAt", ref ExpiresAt);
        map.Flag("ScopeValid", ref ScopeValid);
        map.Time("ReleasedAt", ref ReleasedAt);
        map.Time("SettledAt", ref SettledAt);
        map.Text("SettlementReason", ref SettlementReason);
        map.Flag("CapacityReleased", ref CapacityReleased);
        map.Time("FirstTerminalAt", ref FirstTerminalAt);
        map.Flag("PayloadRedacted", ref PayloadRedacted);
    }
}

// A fixed one-minute window of admissions, GitHub reads, App JWT signatures and GitHub token mints. A new window resets
// the counters; a window older than the stored one is a clock that went back, and denies.
public abstract class ChargedQuota : BrokerRecord
{
    public long? Window, Admissions, Reads, Signs, Mints, Slots;

    // Charges one admission and signature, the given GitHub reads and, when minting, one mint. 429 when a limit is
    // reached; nothing is charged then.
    public void Charge(DateTimeOffset now, int reads, long admissionLimit, long readLimit, long signLimit, long mintLimit, bool mint = true)
    {
        var window = now.ToUnixTimeSeconds() / 60;
        if (IssuanceValues.Number(Window) > window) throw new IssuanceDenied();
        if (Window != window)
        {
            Window = window;
            Admissions = Reads = Signs = Mints = 0;
        }
        var (admissions, used, signs, mints) = (IssuanceValues.Number(Admissions), IssuanceValues.Number(Reads),
            IssuanceValues.Number(Signs), IssuanceValues.Number(Mints));
        if (admissions + 1 > admissionLimit || used + reads > readLimit || signs + 1 > signLimit
            || mints + (mint ? 1 : 0) > mintLimit) throw new IssuanceDenied(429,
                this is IssuanceQuota ? BrokerRejectionReason.GlobalMinuteBudget : BrokerRejectionReason.RepositoryMinuteBudget);
        Admissions = admissions + 1;
        Reads = used + reads;
        Signs = signs + 1;
        Mints = mints + (mint ? 1 : 0);
    }

    protected override void Map(RecordMap map)
    {
        map.Number("Window", ref Window);
        map.Number("Admissions", ref Admissions);
        map.Number("Reads", ref Reads);
        map.Number("Signs", ref Signs);
        map.Number("Mints", ref Mints);
        map.Number("Slots", ref Slots);
    }
}

// A repository's issuance quota. Issuance creates it at the first charge.
public sealed class RepositoryQuota : ChargedQuota
{
    public static StateKey<RepositoryQuota> Key(string repositoryId) => new("repository:" + repositoryId, "quota");

    public static RepositoryQuota New() => new() { Window = 0, Admissions = 0, Reads = 0, Signs = 0, Mints = 0, Slots = 0 };
}

// The global issuance quota, the occupied global slot count and the retained ledger count. Initialized offline.
public sealed class IssuanceQuota : ChargedQuota
{
    public long? RetentionSchemaVersion, RetainedIssuanceCount;

    public static readonly StateKey<IssuanceQuota> Key = new(IssuanceCapacity.Scope, "quota");

    protected override void Map(RecordMap map)
    {
        base.Map(map);
        map.Number("RetentionSchemaVersion", ref RetentionSchemaVersion);
        map.Number("RetainedIssuanceCount", ref RetainedIssuanceCount);
    }
}

// The global enrollment quota: candidate repositories, retained enrollment ledgers and pending enrollments.
// Initialized offline; issuance only reads it to check the retention schema.
public sealed class EnrollmentQuota : BrokerRecord
{
    public long? RetentionSchemaVersion, CandidateCount, RetainedEnrollmentCount, Count;

    public static readonly StateKey<EnrollmentQuota> Key = new(IssuanceCapacity.EnrollmentScope, "quota");

    protected override void Map(RecordMap map)
    {
        map.Number("RetentionSchemaVersion", ref RetentionSchemaVersion);
        map.Number("CandidateCount", ref CandidateCount);
        map.Number("RetainedEnrollmentCount", ref RetainedEnrollmentCount);
        map.Number("Count", ref Count);
    }
}

// A repository that enrollment admitted, with its pending enrollment count. Issuance requires one to exist.
public sealed class CandidateRepository : BrokerRecord
{
    public string? RepositoryId;
    public long? PendingCount;

    public static StateKey<CandidateRepository> Key(string repositoryId) =>
        new(IssuanceCapacity.EnrollmentScope, "repository:" + repositoryId);

    protected override void Map(RecordMap map)
    {
        map.Text("RepositoryId", ref RepositoryId);
        map.Number("PendingCount", ref PendingCount);
    }
}

// The retained global record of one charged issuance attempt, written with its slot. Reclamation releases its
// capacity and retires it; it is never removed by issuance.
public sealed class IssuanceLedger : BrokerRecord, IRetiredLedger
{
    public string? OperationId, Generation, RepositoryId, EnrollmentId, Purpose, Epoch, AttemptRowKey, PendingId;
    public string? ApprovalKind, PolicyVersion, CapacityState, RetirementState;
    public long? GlobalSlot, RowCredits, SetupVersion;
    public DateTimeOffset? ReservedAt, PayloadPurgeAt, RetirementAuditAt;

    string? IRetiredLedger.RetirementState { get => RetirementState; set => RetirementState = value; }
    DateTimeOffset? IRetiredLedger.RetirementAuditAt { get => RetirementAuditAt; set => RetirementAuditAt = value; }

    public static StateKey<IssuanceLedger> Key(string operationId) => new(IssuanceCapacity.Scope, "operation:" + operationId);

    protected override void Map(RecordMap map)
    {
        map.Text("OperationId", ref OperationId);
        map.Text("Generation", ref Generation);
        map.Text("RepositoryId", ref RepositoryId);
        map.Text("Purpose", ref Purpose);
        map.Text("Epoch", ref Epoch);
        map.Time("ReservedAt", ref ReservedAt);
        map.Time("PayloadPurgeAt", ref PayloadPurgeAt);
        map.Number("GlobalSlot", ref GlobalSlot);
        map.Text("DeviceId", ref EnrollmentId);
        map.Text("AttemptRowKey", ref AttemptRowKey);
        map.Number("RowCredits", ref RowCredits);
        map.Text("PendingId", ref PendingId);
        map.Text("ApprovalKind", ref ApprovalKind);
        map.Number("SetupVersion", ref SetupVersion);
        map.Text("PolicyVersion", ref PolicyVersion);
        map.Text("CapacityState", ref CapacityState);
        map.Text("RetirementState", ref RetirementState);
        map.Time("RetirementAuditAt", ref RetirementAuditAt);
    }
}

// One of the fixed global slots an outstanding issuance attempt occupies until reclamation releases it.
public sealed class IssuanceSlot : BrokerRecord
{
    public string? OperationId, Generation, RepositoryId, Purpose, Epoch, ApprovalKind, PolicyVersion;
    public long? GlobalSlot, SetupVersion;
    public DateTimeOffset? ReservedAt, PayloadPurgeAt;

    // Exactly 128 possible slot names, so no caller chooses a global key.
    public static StateKey<IssuanceSlot> Key(long index) => index is < 0 or >= IssuancePolicy.GlobalAttempts
        ? throw new IssuanceDenied()
        : new(IssuanceCapacity.Scope, "slot:" + index.ToString("D3", CultureInfo.InvariantCulture));

    protected override void Map(RecordMap map)
    {
        map.Text("OperationId", ref OperationId);
        map.Text("Generation", ref Generation);
        map.Text("RepositoryId", ref RepositoryId);
        map.Text("Purpose", ref Purpose);
        map.Text("Epoch", ref Epoch);
        map.Time("ReservedAt", ref ReservedAt);
        map.Time("PayloadPurgeAt", ref PayloadPurgeAt);
        map.Number("GlobalSlot", ref GlobalSlot);
        map.Text("ApprovalKind", ref ApprovalKind);
        map.Number("SetupVersion", ref SetupVersion);
        map.Text("PolicyVersion", ref PolicyVersion);
    }
}

#pragma warning restore CA1051
