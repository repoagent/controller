using static RepoAgent.Auth.IssuanceValues;

namespace RepoAgent.Auth;

// One charged allocation per call: removes secret payloads that are due, and later the work and audit metadata. No
// provider or key dependency, no scans, refunds or settlement. Every repository write also writes the gate with the next
// sequence, so it fences every repository writer that came between.
public sealed class PayloadRedaction(IBrokerState state, TimeProvider clock)
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24), Audit = TimeSpan.FromDays(30);
    private readonly IssuanceCapacity capacity = new(state, retainedIssuanceLimit: 0);

    public async Task RedactEnrollmentAsync(string idempotencyId, string generation, CancellationToken ct)
    {
        var read = await LedgerAsync(RetainedLedgers.EnrollmentAsync(state, idempotencyId, generation, ct), ct);
        if (read is null) return;
        var repository = Text(read.Value.RepositoryId);
        var gate = await GateAsync(repository, read.Value.Epoch, ct);
        read = await LedgerAsync(RetainedLedgers.EnrollmentAsync(state, idempotencyId, generation, ct), ct);
        if (read is null || read.Value.RetirementState is not null) return;
        var ledger = read.Value;
        if (Text(gate.Value.RepositoryId) != Text(ledger.RepositoryId)) throw new IssuanceDenied();
        var pendingKey = PendingControllerEnrollment.Key(repository, idempotencyId);
        var enrollmentKey = ControllerEnrollment.Key(repository, Text(ledger.EnrollmentId));
        var receiptKey = RevocationReceipt.Key(repository, Text(ledger.EnrollmentId));
        var pendingRead = await state.ReadAsync(pendingKey, ct);
        var enrollmentRead = await state.ReadAsync(enrollmentKey, ct);
        var receiptRead = enrollmentRead is null ? null : await state.ReadAsync(receiptKey, ct);
        if (gate.Version is null && (pendingRead is not null || enrollmentRead is not null)) throw new IssuanceDenied();
        var binding = RetainedAllocation.Of(ledger);
        if (pendingRead is not null) Bind(binding, RetainedAllocation.Of(pendingRead.Value), true);
        if (enrollmentRead is { Value: var bound })
        {
            Bind(binding, RetainedAllocation.Of(bound), false);
            if (Text(bound.PendingId) != Text(ledger.PendingId)) throw new IssuanceDenied();
        }
        if (receiptRead is not null) Bind(binding, RetainedAllocation.Of(receiptRead.Value), false);

        var now = clock.GetUtcNow();
        var pending = pendingRead?.Value;
        var deadline = PayloadClock.Earlier(PayloadClock.Fallback(ledger.PayloadPurgeAt, ledger.Deadline, Retention),
            pending is null ? null : PayloadClock.After(pending.FirstTerminalAt, Retention));
        // A malformed repository copy cannot extend the immutable ledger deadline.
        var due = deadline is null || deadline <= now;
        var globalChanged = false;
        var changed = false;
        var freshPending = pending is null;
        if (due)
        {
            pending ??= RetainedRows.Tombstone<PendingControllerEnrollment>(ledger);
            changed = Redact(pending.SetupSecretHash is not null || pending.ProtectedCompletion is not null, ref pending.PayloadRedacted) || freshPending;
            pending.SetupSecretHash = pending.ProtectedCompletion = null;
            if (pending.FirstTerminalAt is null && PayloadClock.Read(ledger.Deadline) is { } expired)
            {
                pending.FirstTerminalAt = expired;
                changed = true;
            }
            globalChanged = Redact(ledger.SetupSecretHash is not null, ref ledger.PayloadRedacted);
            ledger.SetupSecretHash = null;
        }
        if (enrollmentRead is { Value: var enrollment })
        {
            var receipt = receiptRead?.Value;
            var expiry = PayloadClock.After(enrollment.ExpiresAt, Retention);
            var terminal = PayloadClock.After(enrollment.FirstTerminalAt, Retention)
                ?? (receipt is null ? null : PayloadClock.After(receipt.RevokedAt, Retention));
            var enrollmentDue = expiry is null || enrollment.State == "revoked" && terminal is null
                ? null : PayloadClock.Earlier(expiry, terminal);
            if (enrollmentDue is null || enrollmentDue <= now)
            {
                changed |= Redact(enrollment.ControllerSecretHash is not null, ref enrollment.PayloadRedacted);
                enrollment.ControllerSecretHash = null;
                var ended = PayloadClock.Earlier(PayloadClock.Read(enrollment.ExpiresAt), receipt is null ? null : PayloadClock.Read(receipt.RevokedAt));
                if (enrollment.FirstTerminalAt is null && ended is { } first)
                {
                    enrollment.FirstTerminalAt = first;
                    changed = true;
                }
            }
        }
        var resolution = pending is null ? PayloadClock.Read(ledger.Deadline)
            : PayloadClock.Earlier(PayloadClock.Read(pending.FirstTerminalAt), PayloadClock.Read(ledger.Deadline));
        if (PayloadClock.Add(resolution, Audit) is { } auditAt && auditAt <= now)
        {
            if (pending is not null)
            {
                changed |= pending.NextWorkAt is not null || pending.WorkStartedAt is not null || pending.ObservationReservedAt is not null
                    || pending.ObservationJwtIssuedAt is not null || pending.ObservationJwtExpiresAt is not null;
                pending.NextWorkAt = pending.WorkStartedAt = pending.ObservationReservedAt = pending.ObservationJwtIssuedAt = pending.ObservationJwtExpiresAt = null;
            }
            if (due)
            {
                globalChanged |= ledger.BodyHash is not null;
                ledger.BodyHash = null;
            }
        }
        if (!changed && !globalChanged) return;
        gate.Value.Sequence = checked(Number(gate.Value.Sequence) + 1);
        var transaction = new StateTransaction();
        gate.Write(transaction);
        if (pending is not null)
        {
            if (freshPending) transaction.Add(pendingKey, pending);
            else transaction.Replace(pendingKey, pending, pendingRead!.Version);
        }
        if (enrollmentRead is not null) transaction.Replace(enrollmentKey, enrollmentRead.Value, enrollmentRead.Version);
        if (receiptRead is not null) transaction.Replace(receiptKey, receiptRead.Value, receiptRead.Version);
        // Fence all repository observations before changing the global recovery copy.
        await state.CommitAsync(transaction, ct);
        if (globalChanged) await state.CommitAsync(new StateTransaction().Replace(EnrollmentLedger.Key(idempotencyId), ledger, read.Version), ct);
    }

    public async Task RedactIssuanceAsync(string operationId, string generation, CancellationToken ct)
    {
        var read = await LedgerAsync(RetainedLedgers.IssuanceAsync(state, operationId, generation, ct), ct);
        if (read is null) return;
        var gate = await GateAsync(Text(read.Value.RepositoryId), read.Value.Epoch, ct);
        read = await LedgerAsync(RetainedLedgers.IssuanceAsync(state, operationId, generation, ct), ct);
        if (read is null || read.Value.RetirementState is not null) return;
        var ledger = read.Value;
        if (Text(gate.Value.RepositoryId) != Text(ledger.RepositoryId)) throw new IssuanceDenied();
        var attemptKey = IssuanceAttempt.Key(Text(ledger.RepositoryId), operationId);
        var stored = await state.ReadAsync(attemptKey, ct);
        if (stored is { Value: var current })
        {
            if (gate.Version is null) throw new IssuanceDenied();
            Bind(RetainedAllocation.Of(ledger), RetainedAllocation.Of(current), true);
            if (Text(ledger.OperationId) != Text(current.OperationId) || Text(ledger.Purpose) != Text(current.Purpose)
                || Number(ledger.GlobalSlot) != Number(current.GlobalSlot)) throw new IssuanceDenied();
        }
        var now = clock.GetUtcNow();
        var deadline = PayloadClock.Fallback(ledger.PayloadPurgeAt, ledger.ReservedAt, Retention);
        var attempt = stored?.Value;
        if (attempt is not null)
        {
            // Invalid fallback on the actual payload also fails closed, even with a valid ledger.
            var fallback = PayloadClock.Fallback(attempt.PayloadPurgeAt, attempt.ReservedAt, Retention);
            deadline = deadline is null || fallback is null ? null : PayloadClock.Earlier(deadline, fallback);
            if (deadline is not null)
            {
                deadline = PayloadClock.Earlier(deadline, PayloadClock.After(attempt.FirstTerminalAt, Retention));
                if (attempt.ScopeValid == true) deadline = PayloadClock.Earlier(deadline, PayloadClock.After(attempt.ExpiresAt, Retention));
            }
        }
        var fresh = attempt is null;
        var changed = false;
        if (deadline is null || deadline <= now)
        {
            attempt ??= RetainedRows.Tombstone<IssuanceAttempt>(ledger);
            changed = Redact(attempt.ProtectedToken is not null, ref attempt.PayloadRedacted) || fresh;
            attempt.ProtectedToken = null;
        }
        // Released/known/sending authority is not resolved by the passage of time.
        if (attempt is not null && attempt.State == "settled"
            && PayloadClock.After(attempt.SettledAt, Audit) is { } auditAt && auditAt <= now)
        {
            changed |= attempt.SendClaimedAt is not null || attempt.ReleasedAt is not null;
            attempt.SendClaimedAt = attempt.ReleasedAt = null;
        }
        if (!changed) return;
        gate.Value.Sequence = checked(Number(gate.Value.Sequence) + 1);
        var transaction = new StateTransaction();
        gate.Write(transaction);
        if (fresh) transaction.Add(attemptKey, attempt!);
        else transaction.Replace(attemptKey, attempt!, stored!.Version);
        await state.CommitAsync(transaction, ct);
    }

    // The ledger, once both global quotas and the candidate repository check out.
    private async Task<Versioned<T>?> LedgerAsync<T>(Task<Versioned<T>?> ledger, CancellationToken ct) where T : BrokerRecord
    {
        var read = await ledger;
        if (read is null) return null;
        _ = await capacity.SchemaAsync(ct);
        _ = await capacity.CandidateAsync(Text(RetainedAllocation.Of(read.Value).RepositoryId), ct) ?? throw new IssuanceDenied();
        return read;
    }

    // The repository gate. Old-epoch payloads remain due: an existing gate's epoch, owner and counters are never reset,
    // and a repository that has never had one gets a vacant gate, which only an Add can write.
    private async Task<Tracked<RepositoryGate>> GateAsync(string repository, string? epoch, CancellationToken ct)
    {
        var key = RepositoryGate.Key(repository);
        if (await state.ReadAsync(key, ct) is { } read)
        {
            if (Text(read.Value.RepositoryId) != repository) throw new IssuanceDenied();
            _ = Number(read.Value.Sequence);
            return new(key, read.Value, read.Version);
        }
        return new(key, new() { RepositoryId = repository, Epoch = Text(epoch), State = "vacant", Sequence = 0, OutstandingTokenCount = 0, PendingCount = 0 }, null);
    }

    // Redaction's binding is identity only: the same repository, enrollment and epoch, and for a row of the allocation
    // itself the same generation. The approval contract is reclamation's to check.
    private static void Bind(RetainedAllocation ledger, RetainedAllocation row, bool generation)
    {
        if (Text(row.RepositoryId) != Text(ledger.RepositoryId) || Text(row.EnrollmentId) != Text(ledger.EnrollmentId)
            || Text(row.Epoch) != Text(ledger.Epoch)) throw new IssuanceDenied();
        if (generation && Text(row.Generation) != Text(ledger.Generation)) throw new IssuanceDenied();
    }

    // Marks the record's payload redacted. Changed when a payload field was present or the mark was not yet set.
    private static bool Redact(bool payloadPresent, ref bool? payloadRedacted)
    {
        if (payloadRedacted == true) return payloadPresent;
        payloadRedacted = true;
        return true;
    }
}
