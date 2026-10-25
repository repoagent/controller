using static RepoAgent.Auth.IssuanceValues;

namespace RepoAgent.Auth;

// The identity every record of one charged allocation carries, as stored: a ledger (pending enrollment or issuance), its
// repository row, its release proof, and the controller enrollment and receipt it binds. Null fields are absent.
internal readonly record struct RetainedAllocation(string? RepositoryId, string? EnrollmentId, string? Epoch, string? Generation,
    string? OperationId, string? Purpose, string? PendingId, long? GlobalSlot,
    string? ApprovalKind, long? SetupVersion, string? PolicyVersion, bool HasEnvironment)
{
    // Ledgers, attempts and release proofs never carry an environment; setup v1 pending rows and enrollments may.
    public static RetainedAllocation Of(EnrollmentLedger l) => new(l.RepositoryId, l.EnrollmentId, l.Epoch, l.Generation,
        null, null, l.PendingId, null, l.ApprovalKind, l.SetupVersion, l.PolicyVersion, false);
    public static RetainedAllocation Of(IssuanceLedger l) => new(l.RepositoryId, l.EnrollmentId, l.Epoch, l.Generation,
        l.OperationId, l.Purpose, l.PendingId, l.GlobalSlot, l.ApprovalKind, l.SetupVersion, l.PolicyVersion, false);
    public static RetainedAllocation Of(PendingControllerEnrollment p) => new(p.RepositoryId, p.EnrollmentId, p.Epoch, p.Generation,
        null, null, p.PendingId, null, p.ApprovalKind, p.SetupVersion, p.PolicyVersion, p.Environment is not null || p.EnvironmentId is not null);
    public static RetainedAllocation Of(IssuanceAttempt a) => new(a.RepositoryId, a.EnrollmentId, a.Epoch, a.Generation,
        a.OperationId, a.Purpose, a.PendingId, a.GlobalSlot, a.ApprovalKind, a.SetupVersion, a.PolicyVersion, false);
    public static RetainedAllocation Of(ReleaseProof r) => new(r.RepositoryId, r.EnrollmentId, r.Epoch, r.Generation,
        r.OperationId, r.Purpose, r.PendingId, r.GlobalSlot, r.ApprovalKind, r.SetupVersion, r.PolicyVersion, false);
    public static RetainedAllocation Of(ControllerEnrollment e) => new(e.RepositoryId, e.EnrollmentId, e.Epoch, null,
        null, null, e.PendingId, null, e.ApprovalKind, e.SetupVersion, e.PolicyVersion, e.Environment is not null || e.EnvironmentId is not null);
    public static RetainedAllocation Of(RevocationReceipt r) => new(r.RepositoryId, r.EnrollmentId, r.Epoch, null,
        null, null, null, null, null, null, null, false);

    public static RetainedAllocation Of(BrokerRecord record) => record switch
    {
        EnrollmentLedger l => Of(l), IssuanceLedger l => Of(l), PendingControllerEnrollment p => Of(p), IssuanceAttempt a => Of(a),
        ReleaseProof r => Of(r), ControllerEnrollment e => Of(e), RevocationReceipt r => Of(r),
        _ => throw new ArgumentException("Not a record of a retained allocation.", nameof(record))
    };

    public ApprovalContract Approval => ApprovalContract.Stored(ApprovalKind, SetupVersion, PolicyVersion, HasEnvironment);

    // Reclamation's binding: the same repository, enrollment, epoch and approval contract, and for a row of the
    // allocation itself the same generation and every allocation field the ledger names.
    public void Bind(RetainedAllocation row, bool allocation)
    {
        if (Text(RepositoryId) != Text(row.RepositoryId) || Text(EnrollmentId) != Text(row.EnrollmentId) || Text(Epoch) != Text(row.Epoch))
            throw new IssuanceDenied();
        if (Approval != row.Approval) throw new IssuanceDenied();
        if (!allocation) return;
        if (Text(Generation) != Text(row.Generation)
            || OperationId is not null && Text(OperationId) != Text(row.OperationId)
            || Purpose is not null && Text(Purpose) != Text(row.Purpose)
            || PendingId is not null && Text(PendingId) != Text(row.PendingId)
            || GlobalSlot is not null && Number(GlobalSlot) != Number(row.GlobalSlot)) throw new IssuanceDenied();
    }
}

// The two retained ledgers, read for one charged allocation by cleanup. Each must be exactly the allocation the cleanup
// page named: its generation, row credits, repository, enrollment, epoch and approval contract, and the family's fields.
internal static class RetainedLedgers
{
    public static async Task<Versioned<EnrollmentLedger>?> EnrollmentAsync(IBrokerState state, string idempotencyId, string generation,
        CancellationToken ct)
    {
        RequireIdentity(idempotencyId, generation, ct);
        var read = await state.ReadAsync(EnrollmentLedger.Key(idempotencyId), ct);
        if (read is not { Value: var ledger }) return read;
        Require(RetainedAllocation.Of(ledger), ledger.RowCredits, generation, 8);
        if (Text(ledger.PendingId) != Text(ledger.RepositoryId) + "_" + idempotencyId) throw new IssuanceDenied();
        return read;
    }

    public static async Task<Versioned<IssuanceLedger>?> IssuanceAsync(IBrokerState state, string operationId, string generation,
        CancellationToken ct)
    {
        RequireIdentity(operationId, generation, ct);
        var read = await state.ReadAsync(IssuanceLedger.Key(operationId), ct);
        if (read is not { Value: var ledger }) return read;
        Require(RetainedAllocation.Of(ledger), ledger.RowCredits, generation, 4);
        if (Text(ledger.OperationId) != operationId || Text(ledger.AttemptRowKey) != "attempt:" + operationId
            || Text(ledger.Purpose) is not ("verification" or "runtime")) throw new IssuanceDenied();
        return read;
    }

    private static void RequireIdentity(string id, string generation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(id, "N", out _) || !Guid.TryParseExact(generation, "N", out _)) throw new IssuanceDenied();
    }

    private static void Require(RetainedAllocation ledger, long? rowCredits, string generation, long credits)
    {
        if (Text(ledger.Generation) != generation || Number(rowCredits) != credits) throw new IssuanceDenied();
        _ = IssuancePolicy.Id(Text(ledger.RepositoryId));
        if (!Guid.TryParseExact(Text(ledger.EnrollmentId), "N", out _)) throw new IssuanceDenied();
        _ = Text(ledger.Epoch);
        _ = ledger.Approval;
    }
}

// The repository gate reclamation and redaction write with every repository transaction: the stored gate, which must
// be the repository's and carry a sequence, or for a repository that has never had one a vacant gate of the
// allocation's epoch. An existing gate's epoch, owner and counters are never reset, so old-epoch payloads remain due.
internal static class RetainedGate
{
    public static async Task<Tracked<RepositoryGate>> ReadAsync(IBrokerState state, string repository, string? epoch, CancellationToken ct)
    {
        var key = RepositoryGate.Key(repository);
        if (await state.ReadAsync(key, ct) is not { } read) return new(key, RepositoryGate.Vacant(repository, Text(epoch)), null);
        if (Text(read.Value.RepositoryId) != repository) throw new IssuanceDenied();
        _ = Number(read.Value.Sequence);
        return new(key, read.Value, read.Version);
    }
}

// The rows cleanup writes for an allocation whose repository row is missing, built from the ledger's stored properties
// so that they keep the ledger's stored values and types.
internal static class RetainedRows
{
    // What reclamation copies into a settled repository row or a release proof, with the ledger's approval contract.
    private static readonly string[] Copied = ["RepositoryId", "DeviceId", "Epoch", "Generation", "OperationId", "Purpose", "PendingId",
        "GlobalSlot", "ReservedAt", "PayloadPurgeAt", "CreatedAt", "Deadline", "RowCredits", "ReclamationVersion"];

    // What redaction copies into a tombstone, as stored.
    private static readonly string[] Tombstoned = ["Generation", "RepositoryId", "DeviceId", "Epoch", "RowCredits", "PendingId", "OperationId",
        "Purpose", "GlobalSlot", "ReservedAt", "CreatedAt", "Deadline", "PayloadPurgeAt", "ApprovalKind", "SetupVersion", "PolicyVersion", "ReclamationVersion"];

    public static T Copy<T>(BrokerRecord ledger, ApprovalContract approval) where T : BrokerRecord, new()
    {
        var row = Select(ledger, Copied);
        row["ApprovalKind"] = approval.Kind;
        row["SetupVersion"] = approval.SetupVersion;
        row["PolicyVersion"] = approval.Policy;
        return BrokerRecord.Read<T>(row);
    }

    // A redacted stand-in for a missing repository row.
    public static T Tombstone<T>(BrokerRecord ledger) where T : BrokerRecord, new()
    {
        var row = Select(ledger, Tombstoned);
        row["State"] = "redacted";
        return BrokerRecord.Read<T>(row);
    }

    // #78 creates this exact shape only when the repository row was absent: the tombstone, redacted, and for an
    // enrollment ended at the ledger's deadline. Published rows keep their authority state and payload metadata during
    // redaction, so any other property set is a published row.
    public static bool UnpublishedTombstone(EnrollmentLedger ledger, PendingControllerEnrollment row) =>
        IsTombstone(ledger, row, PayloadClock.Read(ledger.Deadline));

    public static bool UnpublishedTombstone(IssuanceLedger ledger, IssuanceAttempt row) => IsTombstone(ledger, row, null);

    private static bool IsTombstone(BrokerRecord ledger, BrokerRecord row, DateTimeOffset? deadline)
    {
        var expected = Select(ledger, Tombstoned);
        expected["State"] = "redacted";
        expected["PayloadRedacted"] = true;
        if (deadline is { } ended) expected["FirstTerminalAt"] = ended;
        var stored = row.ToRow();
        return stored.Count == expected.Count && stored.All(p => expected.TryGetValue(p.Key, out var value) && Equals(value, p.Value));
    }

    private static Dictionary<string, object> Select(BrokerRecord ledger, string[] names)
    {
        var stored = ledger.ToRow();
        var row = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var name in names)
            if (stored.TryGetValue(name, out var value)) row[name] = value;
        return row;
    }
}

// Payload retention times. DateTimeOffset.MaxValue is never a real time, and a time that would overflow is unknown.
internal static class PayloadClock
{
    public static DateTimeOffset? Read(DateTimeOffset? date) => date is { } value && value != DateTimeOffset.MaxValue ? value : null;
    public static DateTimeOffset? Add(DateTimeOffset? date, TimeSpan age) =>
        date is { } value && value <= DateTimeOffset.MaxValue - age ? value + age : null;
    public static DateTimeOffset? After(DateTimeOffset? date, TimeSpan age) => Add(Read(date), age);
    public static DateTimeOffset? Earlier(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null || first <= second ? first : second;
    // The stored purge time, or the original time plus the retention, whichever is earlier.
    public static DateTimeOffset? Fallback(DateTimeOffset? purgeAt, DateTimeOffset? original, TimeSpan age) =>
        Earlier(Read(purgeAt), After(original, age));
}
