using static RepoAgent.Auth.IssuanceValues;

namespace RepoAgent.Auth;

// One charged allocation per call. Repository settlement precedes exact global release, and a later call retires
// eligible retained records using durable deletion proof. Every transaction is conditioned on the versions it read, so
// a writer that came between wins; nothing is compensated after an unknown outcome.
public sealed class CapacityReclamation(IBrokerState state, string deploymentEpoch, TimeProvider clock)
{
    // Reclamation checks the global quotas and candidates; it never charges.
    private readonly IssuanceCapacity capacity = new(state, retainedIssuanceLimit: 0);

    public async Task ReclaimIssuanceAsync(string operationId, string generation, CancellationToken ct)
    {
        var read = await RetainedLedgers.IssuanceAsync(state, operationId, generation, ct);
        if (read is null) return;
        var repository = Text(read.Value.RepositoryId);
        var gate = await GateAsync(repository, read.Value.Epoch, ct);
        read = await RetainedLedgers.IssuanceAsync(state, operationId, generation, ct);
        if (read is null) return;
        var ledger = read.Value;
        var binding = RetainedAllocation.Of(ledger);
        if (ledger.CapacityState == "released")
        {
            await RetireAsync(repository, new Tracked<IssuanceLedger>(IssuanceLedger.Key(operationId), ledger, read.Version),
                IssuanceAttempt.Key(repository, operationId), ReleaseProof.Issuance(repository, operationId), gate, ct);
            return;
        }
        if (ledger.RetirementState is not null) throw new IssuanceDenied();
        if (ledger.CapacityState is not null && ledger.CapacityState != "charged") throw new IssuanceDenied();
        var slot = await capacity.RequireIssuanceAsync(operationId, generation, Text(ledger.RepositoryId), Text(ledger.EnrollmentId),
            Text(ledger.Purpose), Text(ledger.Epoch), Number(ledger.GlobalSlot), ct);
        var attemptKey = IssuanceAttempt.Key(repository, operationId);
        var stored = await state.ReadAsync(attemptKey, ct);
        var releaseKey = ReleaseProof.Issuance(repository, operationId);
        var release = await state.ReadAsync(releaseKey, ct);
        if (release is null)
        {
            var fresh = stored is null;
            if (stored is { Value: var current })
            {
                binding.Bind(RetainedAllocation.Of(current), true);
                if (gate.Version is null || current.CapacityReleased == true) throw new IssuanceDenied();
                if (current.State == "redacted" && !RetainedRows.UnpublishedTombstone(ledger, current)) return;
            }
            var unpublished = fresh || RetainedRows.UnpublishedTombstone(ledger, stored!.Value);
            var reason = unpublished ? "unpublished" : Settlement(stored!.Value);
            if (reason is null) return;
            var extra = new List<TrackedRecord>();
            if (!unpublished)
            {
                gate.Value.OutstandingTokenCount = Decrement(gate.Value.OutstandingTokenCount);
                if (Text(ledger.Purpose) == "runtime")
                {
                    var enrollment = await EnrollmentAsync(repository, Text(ledger.EnrollmentId), ct) ?? throw new IssuanceDenied();
                    binding.Bind(RetainedAllocation.Of(enrollment.Value), false);
                    enrollment.Value.OutstandingTokenCount = Decrement(enrollment.Value.OutstandingTokenCount);
                    extra.Add(enrollment);
                }
            }
            var attempt = stored is null ? new Tracked<IssuanceAttempt>(attemptKey, RetainedRows.Copy<IssuanceAttempt>(ledger, binding.Approval), null)
                : new(attemptKey, stored.Value, stored.Version);
            attempt.Value.State = "settled";
            attempt.Value.CapacityReleased = true;
            attempt.Value.SettlementReason = reason;
            attempt.Value.FirstTerminalAt ??= clock.GetUtcNow();
            attempt.Value.SettledAt ??= clock.GetUtcNow();
            attempt.Value.ProtectedToken = null;
            if (gate.Value.VerifierAttempt == operationId) gate.Value.VerifierAttempt = null;
            var proof = RetainedRows.Copy<ReleaseProof>(ledger, binding.Approval);
            proof.SettlementReason = reason;
            proof.RepositorySettledAt = clock.GetUtcNow();
            extra.Add(attempt);
            extra.Add(new Tracked<ReleaseProof>(releaseKey, proof, null));
            await CommitAsync(gate, extra, [], ct);
        }
        else BindRelease(binding, ledger.RowCredits, release.Value);
        var quota = await capacity.SchemaAsync(ct);
        quota.Value.Slots = Decrement(quota.Value.Slots);
        ledger.CapacityState = "released";
        await state.CommitAsync(new StateTransaction()
            .Replace(IssuanceQuota.Key, quota.Value, quota.Version)
            .Replace(IssuanceLedger.Key(operationId), ledger, read.Version)
            .Delete(IssuanceSlot.Key(Number(ledger.GlobalSlot)), slot.Version), ct);
    }

    public async Task ReclaimEnrollmentAsync(string idempotencyId, string generation, CancellationToken ct)
    {
        var read = await RetainedLedgers.EnrollmentAsync(state, idempotencyId, generation, ct);
        if (read is null) return;
        var repository = Text(read.Value.RepositoryId);
        var gate = await GateAsync(repository, read.Value.Epoch, ct);
        read = await RetainedLedgers.EnrollmentAsync(state, idempotencyId, generation, ct);
        if (read is null) return;
        var ledger = read.Value;
        var binding = RetainedAllocation.Of(ledger);
        var pendingKey = PendingControllerEnrollment.Key(repository, idempotencyId);
        var releaseKey = ReleaseProof.Enrollment(repository, idempotencyId);
        if (ledger.PendingCapacityReleased == true)
        {
            var tracked = new Tracked<EnrollmentLedger>(EnrollmentLedger.Key(idempotencyId), ledger, read.Version);
            if (gate.Value.OwnerEnrollmentId == Text(ledger.EnrollmentId)) await ReleaseSettledOwnerAsync(repository, tracked, pendingKey, releaseKey, gate, ct);
            else await RetireAsync(repository, tracked, pendingKey, releaseKey, gate, ct);
            return;
        }
        if (ledger.RetirementState is not null) throw new IssuanceDenied();
        if (Text(ledger.PendingId) != Text(ledger.RepositoryId) + "_" + idempotencyId) throw new IssuanceDenied();
        var stored = await state.ReadAsync(pendingKey, ct);
        var release = await state.ReadAsync(releaseKey, ct);
        if (release is null)
        {
            var fresh = stored is null;
            var extra = new List<TrackedRecord>();
            if (stored is { Value: var current })
            {
                binding.Bind(RetainedAllocation.Of(current), true);
                if (gate.Version is null || current.PendingCapacityReleased == true) throw new IssuanceDenied();
                if (current.State == "redacted" && !RetainedRows.UnpublishedTombstone(ledger, current)) return;
            }
            var pendingState = stored?.Value.State;
            if (pendingState is not ("active" or "closed") && Date(ledger.Deadline) > clock.GetUtcNow()) return;
            if (pendingState == "active")
            {
                var enrollment = await EnrollmentAsync(repository, Text(ledger.EnrollmentId), ct) ?? throw new IssuanceDenied();
                binding.Bind(RetainedAllocation.Of(enrollment.Value), false);
                if (enrollment.Value.Acknowledged != true) throw new IssuanceDenied();
                extra.Add(enrollment);
            }
            if (!fresh && !RetainedRows.UnpublishedTombstone(ledger, stored!.Value)) gate.Value.PendingCount = Decrement(gate.Value.PendingCount);
            var pending = stored is null
                ? new Tracked<PendingControllerEnrollment>(pendingKey, RetainedRows.Copy<PendingControllerEnrollment>(ledger, binding.Approval), null)
                : new(pendingKey, stored.Value, stored.Version);
            if (pendingState != "active") pending.Value.State = "closed";
            pending.Value.PendingCapacityReleased = true;
            pending.Value.FirstTerminalAt ??= pendingState is "active" or "closed" ? clock.GetUtcNow() : Date(ledger.Deadline);
            var proof = RetainedRows.Copy<ReleaseProof>(ledger, binding.Approval);
            proof.RepositorySettledAt = clock.GetUtcNow();
            extra.Add(pending);
            extra.Add(new Tracked<ReleaseProof>(releaseKey, proof, null));
            await CommitAsync(gate, extra, [], ct);
        }
        else BindRelease(binding, ledger.RowCredits, release.Value);
        var quota = (await capacity.QuotasAsync(ct)).Enrollment;
        var candidate = await capacity.CandidateAsync(Text(ledger.RepositoryId), ct) ?? throw new IssuanceDenied();
        quota.Value.Count = Decrement(quota.Value.Count);
        candidate.Value.PendingCount = Decrement(candidate.Value.PendingCount);
        ledger.PendingCapacityReleased = true;
        await state.CommitAsync(new StateTransaction()
            .Replace(EnrollmentQuota.Key, quota.Value, quota.Version)
            .Replace(CandidateRepository.Key(Text(ledger.RepositoryId)), candidate.Value, candidate.Version)
            .Replace(EnrollmentLedger.Key(idempotencyId), ledger, read.Version), ct);
    }

    // A released pending enrollment whose controller enrollment still owns the gate: once the owner is settled (revoked
    // after acknowledgment, or expired unacknowledged), the gate becomes vacant and the receipt records the release.
    private async Task ReleaseSettledOwnerAsync(string repository, Tracked<EnrollmentLedger> tracked, StateKey<PendingControllerEnrollment> pendingKey,
        StateKey<ReleaseProof> releaseKey, Tracked<RepositoryGate> gate, CancellationToken ct)
    {
        var ledger = tracked.Value;
        var binding = RetainedAllocation.Of(ledger);
        var deadline = Date(ledger.Deadline);
        if (Text(ledger.Epoch) != deploymentEpoch || Text(gate.Value.Epoch) != deploymentEpoch
            || gate.Value.State is not ("unacknowledged" or "closed")
            || Number(gate.Value.OutstandingTokenCount) != 0 || gate.Value.VerifierAttempt is not null) return;
        if (ledger.RetirementState is not null || gate.Version is null) throw new IssuanceDenied();
        var pendingRead = await state.ReadAsync(pendingKey, ct) ?? throw new IssuanceDenied();
        var enrollment = await EnrollmentAsync(repository, Text(ledger.EnrollmentId), ct) ?? throw new IssuanceDenied();
        var pending = pendingRead.Value;
        var device = enrollment.Value;
        binding.Bind(RetainedAllocation.Of(pending), true);
        binding.Bind(RetainedAllocation.Of(device), false);
        var acknowledged = device.Acknowledged;
        if (acknowledged is null || acknowledged == false && deadline > clock.GetUtcNow()) return;
        new GrantSnapshot(Text(ledger.RepositoryId), Text(ledger.EnrollmentId), new(gate.Value, gate.Version), new(device, enrollment.Version!))
            .RequireBinding(deploymentEpoch);
        if (pending.PendingCapacityReleased != true || Date(pending.Deadline) != deadline
            || Text(device.PendingId) != Text(ledger.PendingId)
            || Text(pending.CompletionId) != Text(device.CompletionId)
            || Text(pending.Marker) != Text(device.Marker)
            || Text(pending.Repository) != Text(device.Repository)
            || Number(pending.InstallationId) != Number(device.InstallationId)
            || Text(pending.Profile) != Text(device.Profile)
            || Number(device.OutstandingTokenCount) != 0
            || (acknowledged.Value
                ? Text(pending.State) is not ("active" or "closed") || Text(gate.Value.State) != "closed" || Text(device.State) != "revoked"
                : Text(pending.State) != "closed" || Date(device.ExpiresAt) != deadline
                    || (Text(gate.Value.State), Text(device.State)) is not (("unacknowledged", "unacknowledged") or ("closed", "revoked"))))
            throw new IssuanceDenied();
        // Zero on the common gate is maintained only by positive issuance settlement.
        // Pending release has its own durable proof and is not token settlement.
        var release = await state.ReadAsync(releaseKey, ct) ?? throw new IssuanceDenied();
        BindRelease(binding, ledger.RowCredits, release.Value);
        var receiptKey = RevocationReceipt.Key(repository, Text(ledger.EnrollmentId));
        var stored = await state.ReadAsync(receiptKey, ct);
        // Acknowledged owners require an already committed closure, never expiry alone.
        if (acknowledged.Value && stored is null) throw new IssuanceDenied();
        if (stored is { Value: var existing })
        {
            if (Text(existing.RepositoryId) != Text(ledger.RepositoryId) || Text(existing.EnrollmentId) != Text(ledger.EnrollmentId)
                || Text(existing.Epoch) != Text(ledger.Epoch)) throw new IssuanceDenied();
            _ = Date(existing.RevokedAt);
        }
        var receipt = stored is null
            ? new Tracked<RevocationReceipt>(receiptKey, new()
            {
                RepositoryId = Text(ledger.RepositoryId), EnrollmentId = Text(ledger.EnrollmentId), Epoch = Text(ledger.Epoch), RevokedAt = clock.GetUtcNow()
            }, null)
            : new(receiptKey, stored.Value, stored.Version);
        receipt.Value.OwnerReleasedAt = clock.GetUtcNow();
        receipt.Value.Reason = acknowledged.Value ? "revoked-acknowledged" : "expired-unacknowledged";
        device.State = "revoked";
        device.FirstTerminalAt ??= acknowledged.Value ? Date(receipt.Value.RevokedAt) : deadline;
        gate.Value.OwnerEnrollmentId = null;
        gate.Value.State = "vacant";
        // Fence the authorization records. The generation-bound release proof is immutable
        // until gate-conditioned retirement, which cannot retire this completed enrollment.
        await CommitAsync(gate, [new Tracked<PendingControllerEnrollment>(pendingKey, pending, pendingRead.Version), enrollment, receipt], [], ct);
    }

    // Five bounded transactions: retiring, target deletion receipt, global proof,
    // receipt deletion, retained refund. No phase treats unexplained absence as proof.
    private async Task RetireAsync<TLedger, TTarget>(string repository, Tracked<TLedger> ledger, StateKey<TTarget> targetKey, StateKey<ReleaseProof> releaseKey,
        Tracked<RepositoryGate> gate, CancellationToken ct) where TLedger : BrokerRecord, IRetiredLedger, new() where TTarget : BrokerRecord, new()
    {
        var binding = RetainedAllocation.Of(ledger.Value);
        var enrollment = ledger.Value is EnrollmentLedger;
        if (gate.Version is null || Text(gate.Value.RepositoryId) != Text(binding.RepositoryId)) throw new IssuanceDenied();
        var phase = ledger.Value.RetirementState;
        if (phase is not (null or "retiring" or "repository-deleted")) throw new IssuanceDenied();
        var target = await state.ReadAsync(targetKey, ct);
        var release = await state.ReadAsync(releaseKey, ct);
        if (target is not null) binding.Bind(RetainedAllocation.Of(target.Value), true);
        if (release is not null) BindRelease(binding, RowCredits(ledger.Value), release.Value);
        if (phase != "repository-deleted")
        {
            if (release is null) throw new IssuanceDenied();
            if (release.Value.RetirementTargetDeleted == true)
            {
                if (phase != "retiring" || target is not null
                    || Date(release.Value.RetirementAuditAt) != Date(ledger.Value.RetirementAuditAt)) throw new IssuanceDenied();
            }
            else
            {
                if (target is null) throw new IssuanceDenied();
                if (target.Value is IssuanceAttempt settled && Text(release.Value.SettlementReason) != Text(settled.SettlementReason))
                    throw new IssuanceDenied();
                var extra = new List<TrackedRecord>();
                var resolved = (ledger.Value, target.Value) switch
                {
                    (EnrollmentLedger l, PendingControllerEnrollment p) => EnrollmentResolution(l, p),
                    (IssuanceLedger, IssuanceAttempt a) => IssuanceResolution(a),
                    _ => throw new InvalidOperationException("Retirement reads the ledger's own repository row.")
                };
                if (PayloadClock.Add(resolved, TimeSpan.FromDays(30)) is not { } audit || audit > clock.GetUtcNow()) return;
                if (enrollment)
                {
                    // No completed publisher can exist without these sticky/version proofs.
                    var id = Text(binding.EnrollmentId);
                    if (await state.ReadAsync(ControllerEnrollment.Key(repository, id), ct) is not null
                        || await state.ReadAsync(EnrollmentLocator.Key(id), ct) is not null
                        || await state.ReadAsync(RevocationReceipt.Key(repository, id), ct) is not null) return;
                }
                else if (binding.PendingId is { } pendingId)
                {
                    var prefix = Text(binding.RepositoryId) + "_";
                    if (!pendingId.StartsWith(prefix, StringComparison.Ordinal)
                        || !Guid.TryParseExact(pendingId[prefix.Length..], "N", out _)) throw new IssuanceDenied();
                    var pendingKey = PendingControllerEnrollment.Key(repository, pendingId[prefix.Length..]);
                    if (await state.ReadAsync(pendingKey, ct) is { Value: var pending } read)
                    {
                        binding.Bind(RetainedAllocation.Of(pending), false);
                        if (Text(pending.PendingId) != pendingId) throw new IssuanceDenied();
                        if (pending.Attempt == Text(binding.OperationId)
                            && pending.State is not ("closed" or "active")
                            && Date(pending.Deadline) > clock.GetUtcNow()) return;
                        extra.Add(new Tracked<PendingControllerEnrollment>(pendingKey, pending, read.Version));
                    }
                }
                if (phase is null)
                {
                    ledger.Value.RetirementState = "retiring";
                    ledger.Value.RetirementAuditAt = audit;
                    await CommitAsync(ledger, ct);
                }
                else if (Date(ledger.Value.RetirementAuditAt) != audit) throw new IssuanceDenied();
                if (!enrollment && gate.Value.VerifierAttempt == Text(binding.OperationId)) gate.Value.VerifierAttempt = null;
                release.Value.RetirementTargetDeleted = true;
                release.Value.RetirementAuditAt = audit;
                extra.Add(new Tracked<ReleaseProof>(releaseKey, release.Value, release.Version));
                await CommitAsync(gate, extra, [transaction => transaction.Delete(targetKey, target.Version)], ct);
            }
            // This update is authorized by a positively committed, generation-bound receipt.
            ledger.Value.RetirementState = "repository-deleted";
            await CommitAsync(ledger, ct);
        }
        if (Date(ledger.Value.RetirementAuditAt) > clock.GetUtcNow()) throw new IssuanceDenied();
        // Capture a new gate BEFORE validating current global proof, never adopt a new
        // gate under the earlier ledger decision. A concurrent phase worker wins versions.
        var gateRead = await state.ReadAsync(gate.Key, ct) ?? throw new IssuanceDenied();
        gate = new(gate.Key, gateRead.Value, gateRead.Version);
        var current = await state.ReadAsync(ledger.Key, ct) ?? throw new IssuanceDenied();
        binding.Bind(RetainedAllocation.Of(current.Value), true);
        if (current.Version != ledger.Version || Text(current.Value.RetirementState) != "repository-deleted"
            || Text(gate.Value.RepositoryId) != Text(binding.RepositoryId)) throw new IssuanceDenied();
        if (await state.ReadAsync(targetKey, ct) is not null) throw new IssuanceDenied();
        var proof = await state.ReadAsync(releaseKey, ct);
        var deletes = new List<Action<StateTransaction>>();
        if (proof is not null)
        {
            BindRelease(binding, RowCredits(ledger.Value), proof.Value);
            if (proof.Value.RetirementTargetDeleted != true
                || Date(proof.Value.RetirementAuditAt) != Date(ledger.Value.RetirementAuditAt)) throw new IssuanceDenied();
            deletes.Add(transaction => transaction.Delete(releaseKey, proof.Version));
        }
        await CommitAsync(gate, [], deletes, ct);
        var quotas = await capacity.QuotasAsync(ct);
        var refund = new StateTransaction();
        if (enrollment)
        {
            quotas.Enrollment.Value.RetainedEnrollmentCount = Decrement(quotas.Enrollment.Value.RetainedEnrollmentCount);
            refund.Replace(EnrollmentQuota.Key, quotas.Enrollment.Value, quotas.Enrollment.Version);
        }
        else
        {
            quotas.Issuance.Value.RetainedIssuanceCount = Decrement(quotas.Issuance.Value.RetainedIssuanceCount);
            refund.Replace(IssuanceQuota.Key, quotas.Issuance.Value, quotas.Issuance.Version);
        }
        await state.CommitAsync(refund.Delete(ledger.Key, ledger.Version!), ct);
    }

    private static long? RowCredits(BrokerRecord ledger) => ledger switch
    {
        EnrollmentLedger l => l.RowCredits, IssuanceLedger l => l.RowCredits, _ => null
    };

    private static void BindRelease(RetainedAllocation ledger, long? rowCredits, ReleaseProof release)
    {
        ledger.Bind(RetainedAllocation.Of(release), true);
        if (Number(release.RowCredits) != Number(rowCredits) || PayloadClock.Read(release.RepositorySettledAt) is null) throw new IssuanceDenied();
    }

    private static DateTimeOffset? EnrollmentResolution(EnrollmentLedger ledger, PendingControllerEnrollment pending)
    {
        if (ledger.ReclamationVersion != 1 || pending.ReclamationVersion != 1
            || pending.MintAdmissionStarted == true || pending.CompletionId is not null
            || pending.Attempt is not null || pending.State != "closed"
            || pending.PendingCapacityReleased != true) return null;
        return PayloadClock.Earlier(PayloadClock.Read(pending.FirstTerminalAt), PayloadClock.Read(ledger.Deadline));
    }

    private static DateTimeOffset? IssuanceResolution(IssuanceAttempt attempt)
    {
        if (attempt.CapacityReleased != true || attempt.State != "settled") return null;
        var reason = attempt.SettlementReason;
        if (reason == "expired")
        {
            var expiry = PayloadClock.Read(attempt.ExpiresAt);
            var reserved = PayloadClock.Read(attempt.ReservedAt);
            if (attempt.ScopeValid != true || expiry is null || reserved is null || expiry <= reserved) return null;
            return PayloadClock.Add(expiry, TimeSpan.FromMinutes(5));
        }
        if (reason == "unsent" && attempt.SendClaimedAt is not null
            || reason == "unpublished" && attempt.RepositoryCharged == true) return null;
        return reason is "revoked" or "unsent" or "unpublished" ? PayloadClock.Read(attempt.SettledAt) : null;
    }

    private string? Settlement(IssuanceAttempt attempt)
    {
        var current = Text(attempt.State);
        if (current == "reserved" && attempt.SendClaimedAt is null) return "unsent";
        // Existing settled rows are written only after a successful provider revocation.
        if (current == "settled" && PayloadClock.Read(attempt.SettledAt) is not null) return "revoked";
        if (current is not ("known" or "released") || attempt.ScopeValid != true) return null;
        var expiry = PayloadClock.Read(attempt.ExpiresAt);
        var reserved = PayloadClock.Read(attempt.ReservedAt);
        // Healthy synchronized UTC is required; the margin is not a clock-tampering defense.
        if (expiry is null || reserved is null || expiry <= reserved || expiry == DateTimeOffset.MinValue) return null;
        return PayloadClock.Add(expiry, TimeSpan.FromMinutes(5)) is { } due && clock.GetUtcNow() >= due ? "expired" : null;
    }

    // The repository gate, once both global quotas and the candidate repository check out.
    private async Task<Tracked<RepositoryGate>> GateAsync(string repository, string? epoch, CancellationToken ct)
    {
        _ = await capacity.SchemaAsync(ct);
        _ = await capacity.CandidateAsync(repository, ct) ?? throw new IssuanceDenied();
        return await RetainedGate.ReadAsync(state, repository, epoch, ct);
    }

    private async Task<Tracked<ControllerEnrollment>?> EnrollmentAsync(string repository, string enrollmentId, CancellationToken ct)
    {
        var key = ControllerEnrollment.Key(repository, enrollmentId);
        return await state.ReadAsync(key, ct) is { } read ? new(key, read.Value, read.Version) : null;
    }

    private static long Decrement(long? value)
    {
        var count = Number(value);
        if (count == 0) throw new IssuanceDenied();
        return count - 1;
    }

    private async Task CommitAsync(TrackedRecord record, CancellationToken ct)
    {
        var transaction = new StateTransaction();
        record.Write(transaction);
        record.Advance((await state.CommitAsync(transaction, ct))[0] ?? throw new IssuanceDenied());
    }

    // Every repository transaction writes the gate with the next sequence, as an Add when the repository had none.
    private async Task CommitAsync(Tracked<RepositoryGate> gate, IReadOnlyList<TrackedRecord> writes,
        IReadOnlyList<Action<StateTransaction>> deletes, CancellationToken ct)
    {
        gate.Value.Sequence = checked(Number(gate.Value.Sequence) + 1);
        var transaction = new StateTransaction();
        gate.Write(transaction);
        foreach (var write in writes) write.Write(transaction);
        foreach (var delete in deletes) delete(transaction);
        var versions = await state.CommitAsync(transaction, ct);
        gate.Advance(versions[0]);
        for (var i = 0; i < writes.Count; i++) writes[i].Advance(versions[i + 1]);
    }
}

// Retirement's phase on a retained ledger of either family.
internal interface IRetiredLedger
{
    string? RetirementState { get; set; }
    DateTimeOffset? RetirementAuditAt { get; set; }
}
