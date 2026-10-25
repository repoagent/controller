using static RepoAgent.Auth.IssuancePolicy;
using static RepoAgent.Auth.IssuanceValues;

namespace RepoAgent.Auth;

// Global capacity: the retention schema both global quotas carry, the candidate repositories and pending enrollments
// enrollment admits, the 128 fixed slots and the retained ledgers. Quotas are initialized offline. Reclamation,
// redaction and cleanup still use the Table version of these rules (RetainedState) until their own slice moves them here.
public sealed class IssuanceCapacity(IBrokerState state, long retainedIssuanceLimit)
{
    public const string Scope = "global:issuance-v1", EnrollmentScope = "global:enrollment-v1";

    // The global issuance quota, once both quotas carry retention schema 1 and every count is within its ceiling.
    public async Task<Versioned<IssuanceQuota>> SchemaAsync(CancellationToken ct) => (await QuotasAsync(ct)).Issuance;

    // Both global quotas, once both carry retention schema 1 and every count is within its ceiling.
    public async Task<(Versioned<EnrollmentQuota> Enrollment, Versioned<IssuanceQuota> Issuance)> QuotasAsync(CancellationToken ct)
    {
        var enrollmentRead = await state.ReadAsync(EnrollmentQuota.Key, ct) ?? throw new IssuanceDenied();
        var enrollment = enrollmentRead.Value;
        var issuance = await state.ReadAsync(IssuanceQuota.Key, ct) ?? throw new IssuanceDenied();
        var quota = issuance.Value;
        if (Number(enrollment.RetentionSchemaVersion) != 1 || Number(quota.RetentionSchemaVersion) != 1
            || Number(enrollment.CandidateCount) > 128 || Number(enrollment.RetainedEnrollmentCount) > 256
            || Number(enrollment.Count) > GlobalPending || Number(quota.RetainedIssuanceCount) > 4096
            || Number(quota.Slots) > GlobalAttempts
            || Number(enrollment.Count) > Number(enrollment.RetainedEnrollmentCount)
            || Number(quota.Slots) > Number(quota.RetainedIssuanceCount)) throw new IssuanceDenied();
        foreach (var counter in new[] { quota.Window, quota.Admissions, quota.Reads, quota.Signs, quota.Mints }) _ = Number(counter);
        return (enrollmentRead, issuance);
    }

    // Only a repository that enrollment admitted can be charged for issuance.
    public async Task RequireCandidateAsync(string repositoryId, CancellationToken ct) =>
        _ = await CandidateAsync(repositoryId, ct) ?? throw new IssuanceDenied();

    // The candidate repository enrollment admitted, or null before its first admission.
    public async Task<Versioned<CandidateRepository>?> CandidateAsync(string repositoryId, CancellationToken ct)
    {
        _ = Id(repositoryId);
        var read = await state.ReadAsync(CandidateRepository.Key(repositoryId), ct);
        if (read is { Value: var candidate } && (Text(candidate.RepositoryId) != repositoryId || Number(candidate.PendingCount) > RepositoryPending))
            throw new IssuanceDenied();
        return read;
    }

    // Charges one retained ledger and one pending enrollment, globally and on the candidate repository, and a new
    // candidate repository when it is the repository's first admission. 429 at a limit; nothing is charged then.
    public static void ChargeEnrollment(EnrollmentQuota quota, CandidateRepository candidate, bool newCandidate,
        long retainedEnrollmentLimit, long candidateRepositoryLimit)
    {
        if (!newCandidate && Number(quota.CandidateCount) == 0
            || Number(candidate.PendingCount) > Number(quota.Count)) throw new IssuanceDenied();
        if (Number(quota.RetainedEnrollmentCount) >= retainedEnrollmentLimit
            || newCandidate && Number(quota.CandidateCount) >= candidateRepositoryLimit
            || Number(quota.Count) >= GlobalPending || Number(candidate.PendingCount) >= RepositoryPending)
            throw new IssuanceDenied(429);
        quota.RetainedEnrollmentCount = Number(quota.RetainedEnrollmentCount) + 1;
        quota.Count = Number(quota.Count) + 1;
        if (newCandidate) quota.CandidateCount = Number(quota.CandidateCount) + 1;
        candidate.PendingCount = Number(candidate.PendingCount) + 1;
    }

    // The first slot no attempt occupies. 429 when all below the ceiling are occupied.
    public async Task<long> FreeSlotAsync(long ceiling, CancellationToken ct)
    {
        for (long slot = 0; slot < ceiling; slot++)
            if (await state.ReadAsync(IssuanceSlot.Key(slot), ct) is null) return slot;
        throw new IssuanceDenied(429);
    }

    // The attempt's ledger and slot exist, are still charged and bind exactly this operation.
    public async Task RequireIssuanceAsync(string operation, string generation, string repository,
        string enrollmentId, string purpose, string epoch, long index, CancellationToken ct)
    {
        var ledger = (await state.ReadAsync(IssuanceLedger.Key(operation), ct) ?? throw new IssuanceDenied()).Value;
        var slot = (await state.ReadAsync(IssuanceSlot.Key(index), ct) ?? throw new IssuanceDenied()).Value;
        if (ledger.RetirementState is not null || ledger.CapacityState is not null && ledger.CapacityState != "charged"
            || Text(ledger.EnrollmentId) != enrollmentId || Number(ledger.RowCredits) != 4
            || Text(ledger.AttemptRowKey) != "attempt:" + operation) throw new IssuanceDenied();
        var approval = ApprovalContract.Stored(ledger.ApprovalKind, ledger.SetupVersion, ledger.PolicyVersion, false);
        foreach (var (operationId, rowGeneration, repositoryId, rowPurpose, rowEpoch, globalSlot, kind, setupVersion, policy) in new[]
        {
            (ledger.OperationId, ledger.Generation, ledger.RepositoryId, ledger.Purpose, ledger.Epoch, ledger.GlobalSlot,
                ledger.ApprovalKind, ledger.SetupVersion, ledger.PolicyVersion),
            (slot.OperationId, slot.Generation, slot.RepositoryId, slot.Purpose, slot.Epoch, slot.GlobalSlot,
                slot.ApprovalKind, slot.SetupVersion, slot.PolicyVersion)
        })
        {
            if (Text(operationId) != operation || Text(rowGeneration) != generation || Text(repositoryId) != repository
                || Text(rowPurpose) != purpose || Text(rowEpoch) != epoch || Number(globalSlot) != index
                || ApprovalContract.Stored(kind, setupVersion, policy, false) != approval) throw new IssuanceDenied();
        }
    }

    // Charges one retained ledger for the slot's attempt. 429 at the retained ceiling.
    public IssuanceLedger ChargeIssuance(IssuanceQuota quota, IssuanceSlot slot, string enrollmentId, string? pendingId = null)
    {
        if (Number(quota.RetainedIssuanceCount) >= retainedIssuanceLimit) throw new IssuanceDenied(429);
        quota.RetainedIssuanceCount = Number(quota.RetainedIssuanceCount) + 1;
        var operation = Text(slot.OperationId);
        var approval = ApprovalContract.Stored(slot.ApprovalKind, slot.SetupVersion, slot.PolicyVersion, false);
        return new()
        {
            OperationId = slot.OperationId, Generation = slot.Generation, RepositoryId = slot.RepositoryId, Purpose = slot.Purpose,
            Epoch = slot.Epoch, ReservedAt = slot.ReservedAt, PayloadPurgeAt = slot.PayloadPurgeAt, GlobalSlot = slot.GlobalSlot,
            EnrollmentId = enrollmentId, AttemptRowKey = "attempt:" + operation, RowCredits = 4, PendingId = pendingId,
            ApprovalKind = approval.Kind, SetupVersion = approval.SetupVersion, PolicyVersion = approval.Policy
        };
    }
}
