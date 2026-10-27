using System.Globalization;
using static RepoAgent.Auth.IssuancePolicy;
using static RepoAgent.Auth.IssuanceValues;

namespace RepoAgent.Auth;

// The ceilings an operator configured for retained global capacity, at most the schema's ceilings.
public sealed record RetainedLimits(long CandidateRepositories, long Enrollments, long Issuance);

// A record of a pending enrollment's repository partition as read, or new when Version is null. Each commit that
// writes it advances its version, so one enrollment operation can write it again.
public sealed class Tracked<T>(StateKey<T> key, T value, StateVersion? version) : TrackedRecord where T : BrokerRecord, new()
{
    public StateKey<T> Key { get; } = key;
    public T Value { get; } = value;
    public StateVersion? Version { get; private set; } = version;

    internal override void Write(StateTransaction transaction)
    {
        if (Version is null) transaction.Add(Key, Value);
        else transaction.Replace(Key, Value, Version);
    }

    internal override void Advance(StateVersion? version) => Version = version;
}

public abstract class TrackedRecord
{
    internal abstract void Write(StateTransaction transaction);
    internal abstract void Advance(StateVersion? version);
}

// A pending controller enrollment as read together with its repository gate. Every repository transaction replaces both
// at their versions, and a commit advances the versions, so the operation's later transactions are fenced only by
// writers that came between.
public sealed class PendingSnapshot(string partitionRepositoryId, string idempotencyId,
    Versioned<RepositoryGate> gate, Versioned<PendingControllerEnrollment> pending)
{
    public RepositoryGate Gate { get; } = gate.Value;
    public PendingControllerEnrollment Pending { get; } = pending.Value;
    public StateVersion GateVersion { get; internal set; } = gate.Version;
    public StateVersion PendingVersion { get; internal set; } = pending.Version;
    // The repository partition the records were read under.
    public string PartitionRepositoryId { get; } = partitionRepositoryId;
    public StateKey<RepositoryGate> GateKey { get; } = RepositoryGate.Key(partitionRepositoryId);
    public StateKey<PendingControllerEnrollment> PendingKey { get; } = PendingControllerEnrollment.Key(partitionRepositoryId, idempotencyId);

    public string RepositoryId => Text(Pending.RepositoryId);
    public string Id => Text(Pending.PendingId);
    public string EnrollmentId => Text(Pending.EnrollmentId);
    public ApprovalContract Approval => ApprovalContract.Stored(Pending.ApprovalKind, Pending.SetupVersion, Pending.PolicyVersion,
        Pending.Environment is not null || Pending.EnvironmentId is not null);

    // What the pending enrollment asked for and has bound so far, for GitHub to confirm.
    public ApprovalRequest ApprovalRequest()
    {
        var p = Pending;
        return new(Approval, IssuancePolicy.Id(RepositoryId), Text(p.RequestedRepository), Text(p.Profile), Text(p.Variable),
            Approval == ApprovalContract.Legacy ? Text(p.Environment) : null,
            p.InstallationId is null ? null : Number(p.InstallationId),
            p.EnvironmentId is null ? null : Number(p.EnvironmentId),
            p.Repository is null ? null : Text(p.Repository),
            p.Marker is null ? null : Text(p.Marker));
    }

    // The approval marker the pending enrollment's binding encodes.
    public string CreateMarker()
    {
        var legacy = !Approval.IsRepositoryVariable;
        return ApprovalMarker.Create(Approval, RepositoryId, Text(Pending.Repository), Number(Pending.InstallationId), EnrollmentId,
            Pending.Challenge, Text(Pending.Profile), Text(Gate.Epoch), Text(Pending.Variable),
            legacy ? Text(Pending.Environment) : null, legacy ? Number(Pending.EnvironmentId) : null);
    }
}

// One claimed unit of enrollment work: the App authorization window it reserved and, for a mint, the verifier attempt.
public sealed record WorkReservation(AppAuthorizationWindow Window, Tracked<IssuanceAttempt>? Attempt);

// Enrollment's durable decisions over the broker state: admission (a global charge, then the repository transaction),
// the work claims and verifier reservation each GitHub interaction needs, and every repository transaction. Global
// reservations are conservative and never rolled back across scopes, and nothing is compensated after an unknown outcome.
public sealed class EnrollmentStore(IBrokerState state, string epoch, RetainedLimits retained, ISecretProtection secrets,
    TimeProvider clock, IssuanceLimits? configuredLimits = null)
{
    private readonly IssuanceCapacity capacity = new(state, retained.Issuance);
    private readonly IssuanceLimits limits = configuredLimits ?? new();

    public string Epoch => epoch;

    internal async Task<PendingSnapshot> AdmitAsync(EnrollmentInput request, string hash, CancellationToken ct)
    {
        _ = await capacity.QuotasAsync(ct);
        var repository = request.RepositoryId.ToString(CultureInfo.InvariantCulture);
        var ledgerKey = EnrollmentLedger.Key(request.IdempotencyId);
        EnrollmentLedger? reservation = null;
        for (var retry = 0; retry < 4; retry++)
        {
            reservation = (await state.ReadAsync(ledgerKey, ct))?.Value;
            if (reservation is not null) break;
            RequireReady((await state.ReadAsync(RepositoryGate.Key(repository), ct))?.Value, repository);
            var quota = (await capacity.QuotasAsync(ct)).Enrollment;
            var directory = await capacity.CandidateAsync(repository, ct);
            var candidate = directory?.Value ?? new() { RepositoryId = repository, PendingCount = 0 };
            IssuanceCapacity.ChargeEnrollment(quota.Value, candidate, directory is null, retained.Enrollments, retained.CandidateRepositories);
            var now = clock.GetUtcNow();
            reservation = new()
            {
                RepositoryId = repository,
                PendingId = repository + "_" + request.IdempotencyId,
                SetupSecretHash = hash,
                BodyHash = request.BodyHash,
                Epoch = epoch,
                CreatedAt = now,
                Deadline = now.Add(PendingLifetime),
                PayloadPurgeAt = now.Add(PendingLifetime).AddHours(24),
                Generation = Guid.NewGuid().ToString("N"),
                RowCredits = 8,
                ReclamationVersion = 1,
                EnrollmentId = Guid.NewGuid().ToString("N"),
                Challenge = EnrollmentSecrets.Generate(),
                ApprovalKind = request.Approval.Kind,
                SetupVersion = request.Approval.SetupVersion,
                PolicyVersion = request.Approval.Policy
            };
            var candidateKey = CandidateRepository.Key(repository);
            var admission = new StateTransaction().Replace(EnrollmentQuota.Key, quota.Value, quota.Version);
            if (directory is null) admission.Add(candidateKey, candidate);
            else admission.Replace(candidateKey, candidate, directory.Version);
            try
            {
                await state.CommitAsync(admission.Add(ledgerKey, reservation), ct);
                break;
            }
            catch (StateConflict) { reservation = null; }
        }
        if (reservation is null) throw new IssuanceDenied();
        if (Text(reservation.SetupSecretHash) != hash || Text(reservation.BodyHash) != request.BodyHash
            || Text(reservation.RepositoryId) != repository) throw new IssuanceDenied(409);
        Require(request.Approval, reservation);
        RequireLive(reservation.PayloadRedacted, null, reservation.Epoch, reservation.Deadline);
        for (var retry = 0; retry < 4; retry++)
        {
            var gate = await state.ReadAsync(RepositoryGate.Key(repository), ct);
            var current = (await state.ReadAsync(ledgerKey, ct) ?? throw new IssuanceDenied()).Value;
            if (Text(current.Generation) != Text(reservation.Generation)
                || current.PendingCapacityReleased == true || current.RetirementState is not null
                || Text(current.RepositoryId) != repository || Text(current.SetupSecretHash) != hash
                || Text(current.BodyHash) != request.BodyHash) throw new IssuanceDenied();
            Require(request.Approval, current);
            RequireLive(current.PayloadRedacted, null, current.Epoch, current.Deadline);
            reservation = current;
            var pendingKey = PendingControllerEnrollment.Key(repository, request.IdempotencyId);
            if (await state.ReadAsync(pendingKey, ct) is not null)
                return await SnapshotAsync(Text(reservation.PendingId), hash, ct, request.Approval.SetupVersion);
            var record = gate?.Value ?? RepositoryGate.Vacant(repository, epoch);
            RequireReady(record, repository);
            var count = record.PendingCount ?? 0;
            if (count >= RepositoryPending) throw new IssuanceDenied(429);
            record.PendingCount = count + 1;
            record.Sequence = Number(record.Sequence) + 1;
            var pending = Copy(reservation);
            pending.ApprovalKind = request.Approval.Kind;
            pending.SetupVersion = request.Approval.SetupVersion;
            pending.PolicyVersion = request.Approval.Policy;
            pending.RequestedRepository = request.Repository;
            pending.Repository = request.Repository;
            if (request.Approval == ApprovalContract.Legacy) pending.Environment = request.Environment!;
            pending.Variable = request.Variable.ToUpperInvariant();
            pending.Profile = request.Profile;
            pending.State = "discovering";
            pending.RepositoryCharged = true;
            pending.NextWorkAt = DateTimeOffset.UnixEpoch;
            pending.PositiveApproval = false;
            var gateKey = RepositoryGate.Key(repository);
            var publication = gate is null ? new StateTransaction().Add(gateKey, record) : new StateTransaction().Replace(gateKey, record, gate.Version);
            try
            {
                await state.CommitAsync(publication.Add(pendingKey, pending), ct);
                return await SnapshotAsync(Text(reservation.PendingId), hash, ct, request.Approval.SetupVersion);
            }
            catch (StateConflict) { }
        }
        throw new IssuanceDenied();
    }

    // The pending enrollment starts as a copy of its ledger: every property admission or reclamation may have set on it.
    // Retirement is not copied; admission denies a ledger that has started it.
    private static PendingControllerEnrollment Copy(EnrollmentLedger ledger) => new()
    {
        RepositoryId = ledger.RepositoryId,
        PendingId = ledger.PendingId,
        SetupSecretHash = ledger.SetupSecretHash,
        BodyHash = ledger.BodyHash,
        Epoch = ledger.Epoch,
        CreatedAt = ledger.CreatedAt,
        Deadline = ledger.Deadline,
        PayloadPurgeAt = ledger.PayloadPurgeAt,
        Generation = ledger.Generation,
        RowCredits = ledger.RowCredits,
        ReclamationVersion = ledger.ReclamationVersion,
        EnrollmentId = ledger.EnrollmentId,
        Challenge = ledger.Challenge,
        ApprovalKind = ledger.ApprovalKind,
        SetupVersion = ledger.SetupVersion,
        PolicyVersion = ledger.PolicyVersion,
        PendingCapacityReleased = ledger.PendingCapacityReleased,
        PayloadRedacted = ledger.PayloadRedacted
    };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068", Justification = "Preserve existing positional callers; this token is distinct from the optional configuration that follows it.")]
    public async Task<PendingSnapshot> SnapshotAsync(string id, string? setupSecretHash, CancellationToken ct, long setupVersion)
    {
        var parts = id.Split('_');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[1], "N", out _)) throw new IssuanceDenied(401);
        _ = Id(parts[0]);
        var pending = await state.ReadAsync(PendingControllerEnrollment.Key(parts[0], parts[1]), ct) ?? throw new IssuanceDenied(401);
        var gate = await state.ReadAsync(RepositoryGate.Key(parts[0]), ct) ?? throw new IssuanceDenied(401);
        var (p, g) = (pending.Value, gate.Value);
        if (Text(p.PendingId) != id || Text(p.RepositoryId) != parts[0]
            || Text(g.RepositoryId) != parts[0] || Text(g.Epoch) != epoch
            || Text(p.Epoch) != epoch) throw new IssuanceDenied(401);
        if (setupSecretHash is not null && (p.SetupSecretHash is not { } hash
            || !DeviceSecret.Matches(Convert.FromHexString(setupSecretHash), hash))) throw new IssuanceDenied(401);
        var snapshot = new PendingSnapshot(parts[0], parts[1], gate, pending);
        if (!snapshot.Approval.Allows(Text(p.Profile))) throw new IssuanceDenied();
        if (snapshot.Approval.SetupVersion != setupVersion) throw new IssuanceDenied(409);
        if (snapshot.Approval == ApprovalContract.Legacy)
        {
            var environment = Text(p.Environment);
            if (environment.Length > 255 || environment.Any(char.IsControl)) throw new IssuanceDenied();
            if (p.Marker is not null && Number(p.EnvironmentId) <= 0) throw new IssuanceDenied();
        }
        if (p.Marker is not null && Text(p.Marker) != snapshot.CreateMarker()) throw new IssuanceDenied();
        return snapshot;
    }

    // 503 for a redacted pending enrollment; 410 once it is closed, active, past its deadline or of another epoch.
    public void RequireLive(PendingControllerEnrollment pending) =>
        RequireLive(pending.PayloadRedacted, pending.State, pending.Epoch, pending.Deadline);

    private void RequireLive(bool? payloadRedacted, string? pendingState, string? recordEpoch, DateTimeOffset? deadline)
    {
        RequirePayload(payloadRedacted, pendingState);
        if (Text(recordEpoch) != epoch || Date(deadline) <= clock.GetUtcNow()) throw new IssuanceDenied(410);
        if (pendingState is "closed" or "active") throw new IssuanceDenied(410);
    }

    private static void RequirePayload(bool? payloadRedacted, string? recordState)
    {
        if (payloadRedacted == true || recordState == "redacted") throw new IssuanceDenied();
    }

    // Claims one durable unit of work and charges it, globally and on the repository. A mint also reserves a global
    // slot, a retained ledger and the verifier attempt; an observation's App authorization window is kept on the
    // pending enrollment instead, never in an attempt of its own.
    public async Task<WorkReservation> ReserveWorkAsync(PendingSnapshot s, bool mint, IssuanceWork work)
    {
        var ct = work.Cancellation;
        RequireLive(s.Pending);
        if (mint) RequireReady(s.Gate, s.RepositoryId);
        _ = await capacity.QuotasAsync(ct);
        _ = await capacity.CandidateAsync(s.RepositoryId, ct) ?? throw new IssuanceDenied();
        var now = clock.GetUtcNow();
        if (Date(s.Pending.NextWorkAt) > now) throw new IssuanceDenied(429);
        if (s.Pending.Attempt is not null && mint) throw new IssuanceDenied();
        work.EvaluateAdmission();
        // Claim one durable operation before global/provider admission; all endpoints share this field.
        s.Pending.NextWorkAt = now.AddSeconds(15);
        s.Pending.WorkStartedAt = now;
        if (mint) s.Pending.MintAdmissionStarted = true;
        await CommitAsync(s, [], ct);
        var global = await capacity.SchemaAsync(ct);
        global.Value.Charge(now, 8, limits.GlobalAdmissions, limits.GlobalReads, limits.GlobalSigns, limits.GlobalMints, mint);
        var operation = mint ? Guid.NewGuid().ToString("N") : "observation";
        var generation = Guid.NewGuid().ToString("N");
        long? slot = null;
        var charge = new StateTransaction();
        if (mint)
        {
            var occupancy = Number(global.Value.Slots);
            if (occupancy >= limits.GlobalAttempts) throw new IssuanceDenied(429);
            slot = await capacity.FreeSlotAsync(limits.GlobalAttempts, ct);
            global.Value.Slots = occupancy + 1;
            var slotReservation = new IssuanceSlot
            {
                OperationId = operation,
                Generation = generation,
                RepositoryId = s.RepositoryId,
                Purpose = "verification",
                Epoch = epoch,
                ReservedAt = now,
                PayloadPurgeAt = now.AddHours(24),
                GlobalSlot = slot.Value,
                ApprovalKind = s.Approval.Kind,
                SetupVersion = s.Approval.SetupVersion,
                PolicyVersion = s.Approval.Policy
            };
            charge.Add(IssuanceLedger.Key(operation), capacity.ChargeIssuance(global.Value, slotReservation, s.EnrollmentId, s.Id));
            charge.Add(IssuanceSlot.Key(slot.Value), slotReservation);
        }
        await state.CommitAsync(charge.Replace(IssuanceQuota.Key, global.Value, global.Version), ct);
        if (mint) await capacity.RequireIssuanceAsync(operation, generation, s.RepositoryId, s.EnrollmentId,
            "verification", epoch, slot!.Value, ct);
        var quotaKey = RepositoryQuota.Key(s.PartitionRepositoryId);
        var quota = await state.ReadAsync(quotaKey, ct);
        var charged = new Tracked<RepositoryQuota>(quotaKey, quota?.Value ?? RepositoryQuota.New(), quota?.Version);
        charged.Value.Charge(now, 8, limits.RepositoryAdmissions, limits.RepositoryReads, limits.RepositorySigns, limits.RepositoryMints, mint);
        var extra = new List<TrackedRecord> { charged };
        if (mint)
        {
            if (Number(s.Gate.OutstandingTokenCount) >= limits.RepositoryAttempts) throw new IssuanceDenied(429);
            s.Gate.OutstandingTokenCount = Number(s.Gate.OutstandingTokenCount) + 1;
            s.Pending.Attempt = operation;
            extra.Add(new Tracked<IssuanceAttempt>(IssuanceAttempt.Key(s.PartitionRepositoryId, operation), new()
            {
                OperationId = operation,
                Generation = generation,
                RepositoryId = s.RepositoryId,
                EnrollmentId = s.EnrollmentId,
                PendingId = s.Id,
                Epoch = epoch,
                Purpose = "verification",
                Profile = s.Approval.VerifierProfile,
                RepositoryCharged = true,
                State = "reserved",
                ReservedAt = now,
                PayloadPurgeAt = now.AddHours(24),
                JwtIssuedAt = now.AddSeconds(-60),
                JwtExpiresAt = now.AddSeconds(120),
                GlobalSlot = slot!.Value,
                ApprovalKind = s.Approval.Kind,
                SetupVersion = s.Approval.SetupVersion,
                PolicyVersion = s.Approval.Policy
            }, null));
        }
        // Observation JWT timestamps stay in this bounded pending row, not an append-only operation log.
        s.Pending.ObservationReservedAt = now;
        s.Pending.ObservationJwtIssuedAt = now.AddSeconds(-60);
        s.Pending.ObservationJwtExpiresAt = now.AddSeconds(120);
        await CommitAsync(s, extra, ct);
        if (!mint) return new(AppAuthorizationWindow.Reserved(now, now.AddSeconds(-60), now.AddSeconds(120)), null);
        var attempt = await AttemptAsync(s, ct);
        return new(IssuanceStore.ReservedWindow(attempt.Value), attempt);
    }

    // The verifier attempt the pending enrollment minted, bound to it.
    public async Task<Tracked<IssuanceAttempt>> AttemptAsync(PendingSnapshot s, CancellationToken ct)
    {
        var key = IssuanceAttempt.Key(s.PartitionRepositoryId, Text(s.Pending.Attempt));
        var read = await state.ReadAsync(key, ct) ?? throw new IssuanceDenied();
        var a = read.Value;
        if (Text(a.PendingId) != s.Id || Text(a.RepositoryId) != s.RepositoryId || Text(a.EnrollmentId) != s.EnrollmentId
            || Text(a.Epoch) != epoch || Text(a.Purpose) != "verification" || Text(a.Profile) != s.Approval.VerifierProfile) throw new IssuanceDenied();
        if (ApprovalContract.Stored(a.ApprovalKind, a.SetupVersion, a.PolicyVersion, false) != s.Approval) throw new IssuanceDenied();
        return new(key, a, read.Version);
    }

    // A verifier attempt that reclamation settled as definitely unsent frees the pending enrollment for a new one.
    public async Task ReleaseUnsentAttemptAsync(PendingSnapshot s, Tracked<IssuanceAttempt> stored, CancellationToken ct)
    {
        var attempt = stored.Value;
        RequireLive(s.Pending);
        if (Text(attempt.State) != "settled" || attempt.CapacityReleased != true
            || Text(attempt.SettlementReason) != "unsent" || attempt.SendClaimedAt is not null
            || Date(attempt.SettledAt) < Date(attempt.ReservedAt) || Date(attempt.SettledAt) > clock.GetUtcNow())
            throw new IssuanceDenied();
        // Keep the charged audit and original pending clocks. The gate/pending/attempt
        // versions fence stale senders before a separately charged replacement is admitted.
        s.Pending.Attempt = null;
        await CommitAsync(s, [stored], ct);
    }

    // The verifier's GitHub token, while it is known, in scope, of the bound installation and not about to expire.
    public GitHubToken Recover(PendingSnapshot s, Tracked<IssuanceAttempt> stored)
    {
        var attempt = stored.Value;
        IssuanceStore.RequireAttemptPayload(attempt);
        if (Text(attempt.State) != "known" || attempt.ScopeValid != true
            || Number(attempt.InstallationId) != Number(s.Pending.InstallationId)
            || Date(attempt.ExpiresAt) <= clock.GetUtcNow().AddMinutes(5)) throw new IssuanceDenied();
        return new(secrets.Unprotect(TokenPurpose(s, attempt), Text(attempt.ProtectedToken)), Date(attempt.ExpiresAt));
    }

    public string ProtectToken(PendingSnapshot s, IssuanceAttempt attempt, string token) => secrets.Protect(TokenPurpose(s, attempt), token);
    public string ProtectCompletion(PendingSnapshot s, string completion) => secrets.Protect(CompletionPurpose(s), completion);
    public string UnprotectCompletion(PendingSnapshot s, string protectedCompletion) => secrets.Unprotect(CompletionPurpose(s), protectedCompletion);
    private SecretPurpose TokenPurpose(PendingSnapshot s, IssuanceAttempt attempt) => s.Approval.TokenPurpose(
        s.RepositoryId, epoch, Text(attempt.OperationId), "verification");
    private SecretPurpose CompletionPurpose(PendingSnapshot s) => s.Approval.CompletionPurpose(s.RepositoryId, epoch, s.Id);

    // Every repository transaction replaces the gate, with the next sequence, and the pending enrollment at the versions
    // the operation last read or wrote, and advances those versions and the other records' when it commits.
    public async Task CommitAsync(PendingSnapshot s, IReadOnlyList<TrackedRecord> extra, CancellationToken ct)
    {
        RequirePayload(s.Pending.PayloadRedacted, s.Pending.State);
        s.Gate.Sequence = checked(Number(s.Gate.Sequence) + 1);
        var transaction = new StateTransaction()
            .Replace(s.GateKey, s.Gate, s.GateVersion)
            .Replace(s.PendingKey, s.Pending, s.PendingVersion);
        foreach (var record in extra) record.Write(transaction);
        var versions = await state.CommitAsync(transaction, ct);
        s.GateVersion = versions[0] ?? throw new IssuanceDenied();
        s.PendingVersion = versions[1] ?? throw new IssuanceDenied();
        for (var i = 0; i < extra.Count; i++) extra[i].Advance(versions[i + 2]);
    }

    // Ends the operation's work claim: the next one may start five seconds after this one started.
    public async Task FinishAsync(PendingSnapshot s, IReadOnlyList<TrackedRecord> extra, CancellationToken ct)
    {
        s.Pending.NextWorkAt = Date(s.Pending.WorkStartedAt).AddSeconds(5);
        await CommitAsync(s, extra, ct);
    }

    // Routes the enrollment ID to its repository. An existing locator must already route it to this enrollment.
    public async Task PublishLocatorAsync(PendingSnapshot s, CancellationToken ct)
    {
        var key = EnrollmentLocator.Key(s.EnrollmentId);
        try { await state.CommitAsync(new StateTransaction().Add(key, new EnrollmentLocator { RepositoryId = s.RepositoryId, EnrollmentId = s.EnrollmentId, PendingId = s.Id }), ct); }
        catch (StateConflict)
        {
            var current = (await state.ReadAsync(key, ct) ?? throw new IssuanceDenied()).Value;
            if (Text(current.RepositoryId) != s.RepositoryId || Text(current.EnrollmentId) != s.EnrollmentId || Text(current.PendingId) != s.Id)
                throw new IssuanceDenied();
        }
    }

    public async Task<EnrollmentLocator?> LocatorAsync(string enrollmentId, CancellationToken ct) =>
        (await state.ReadAsync(EnrollmentLocator.Key(enrollmentId), ct))?.Value;

    public async Task<Tracked<ControllerEnrollment>?> EnrollmentAsync(PendingSnapshot s, CancellationToken ct)
    {
        var key = ControllerEnrollment.Key(s.PartitionRepositoryId, s.EnrollmentId);
        return await state.ReadAsync(key, ct) is { } read ? new(key, read.Value, read.Version) : null;
    }

    // The pending enrollment's grant as issuance reads it, to check its binding and activity.
    public static GrantSnapshot Grant(PendingSnapshot s, Tracked<ControllerEnrollment> enrollment) => new(s.PartitionRepositoryId, s.EnrollmentId,
        new(s.Gate, s.GateVersion), new(enrollment.Value, enrollment.Version ?? throw new IssuanceDenied()));

    // A resumed reservation already counts its own verifier. Its attempt binding was checked by AttemptAsync.
    public void RequireUnsentReady(PendingSnapshot s) => RequireReady(s.Gate, s.RepositoryId, ownAttempt: true);

    // Readiness never settles authority. Only reclamation may publish vacancy after durable owner release.
    private void RequireReady(RepositoryGate? gate, string repository, bool ownAttempt = false)
    {
        if (gate is null) return;
        if (Text(gate.RepositoryId) != repository || Text(gate.Epoch) != epoch) throw new IssuanceDenied();
        _ = Number(gate.Sequence);
        _ = Number(gate.PendingCount);
        var outstanding = Number(gate.OutstandingTokenCount);
        var state = Text(gate.State);
        if (gate.OwnerEnrollmentId is not null)
        {
            _ = Text(gate.OwnerEnrollmentId);
            if (state is not ("active" or "unacknowledged" or "closed")) throw new IssuanceDenied();
            throw new EnrollmentNotReady();
        }
        if (state != "vacant" || gate.VerifierAttempt is not null || ownAttempt && outstanding == 0)
            throw new IssuanceDenied();
        if (outstanding != (ownAttempt ? 1 : 0)) throw new EnrollmentNotReady();
    }

    // A record's approval contract must be the request's. Ledgers never carry an environment.
    private static void Require(ApprovalContract approval, EnrollmentLedger ledger)
    {
        if (ApprovalContract.Stored(ledger.ApprovalKind, ledger.SetupVersion, ledger.PolicyVersion, false) != approval) throw new IssuanceDenied();
    }
}

// Kept separate from request-binding conflicts so a caller never treats a mismatched recovery as a safe recheck.
internal sealed class EnrollmentNotReady : Exception;
