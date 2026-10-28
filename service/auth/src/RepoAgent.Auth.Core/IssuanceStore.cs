using static RepoAgent.Auth.IssuancePolicy;
using static RepoAgent.Auth.IssuanceValues;

namespace RepoAgent.Auth;

// An issuance attempt as read, with the key it was read under and the version it had.
public sealed record StoredAttempt(StateKey<IssuanceAttempt> Key, IssuanceAttempt Value, StateVersion Version);

// The two-part conservative reservation (a global charge, then the repository transaction) and every repository
// transaction of GitHub token issuance. A global charge is never compensated, and no transaction is compensated after an
// unknown outcome. Capacity release is owned by CapacityReclamation after repository settlement.
public sealed partial class IssuanceStore(IBrokerState state, string epoch, long retainedIssuanceLimit, ISecretProtection secrets,
    TimeProvider clock, IssuanceLimits? configuredLimits = null)
{
    private readonly IssuanceCapacity capacity = new(state, retainedIssuanceLimit);
    private readonly IssuanceLimits limits = configuredLimits ?? new();

    public async Task<GrantSnapshot> AuthenticateAsync(string deviceId, string? secret, CancellationToken ct)
    {
        var hash = DeviceSecret.Hash(secret);
        if (!DeviceSecret.IsDeviceId(deviceId) || hash is null) throw new IssuanceDenied(401);
        var locator = (await state.ReadAsync(EnrollmentLocator.Key(deviceId), ct) ?? throw new IssuanceDenied(401)).Value;
        if (Text(locator.EnrollmentId) != deviceId) throw new IssuanceDenied(401);
        var repository = Text(locator.RepositoryId);
        _ = Id(repository);
        var grant = await SnapshotAsync(repository, deviceId, ct);
        if (grant.RepositoryId != repository || !DeviceSecret.Matches(hash, Text(grant.Enrollment.ControllerSecretHash)))
            throw new IssuanceDenied(401);
        grant.RequireActive(clock.GetUtcNow());
        return grant;
    }

    public async Task<GrantSnapshot> SnapshotAsync(string repositoryId, string deviceId, CancellationToken ct)
    {
        var gate = await state.ReadAsync(RepositoryGate.Key(repositoryId), ct) ?? throw new IssuanceDenied(401);
        var enrollment = await state.ReadAsync(ControllerEnrollment.Key(repositoryId, deviceId), ct) ?? throw new IssuanceDenied(401);
        var snapshot = new GrantSnapshot(repositoryId, deviceId, gate, enrollment);
        if (snapshot.EnrollmentId != deviceId || repositoryId != snapshot.RepositoryId) throw new IssuanceDenied();
        // Retained old enrollments remain readable after owner recovery, but are no longer grants.
        if (gate.Value.OwnerEnrollmentId != deviceId) throw new IssuanceDenied(401);
        snapshot.RequireBinding(epoch);
        return snapshot;
    }

    public async Task<StoredAttempt?> VerifierAsync(GrantSnapshot grant, CancellationToken ct)
    {
        if (grant.Gate.VerifierAttempt is not { } id) return null;
        if (!Guid.TryParseExact(id, "N", out _)) throw new IssuanceDenied();
        var attempt = await AttemptAsync(grant, id, ct) ?? throw new IssuanceDenied();
        BindAttempt(grant, attempt.Value, "verification");
        if (Text(attempt.Value.State) != "known" || attempt.Value.ScopeValid != true) throw new IssuanceDenied();
        // An unresolved acquisition blocks subsequent acquisitions indefinitely. Only a known,
        // exact-scope token may be replaced near its actual expiry; both remain accounted.
        if (Date(attempt.Value.ExpiresAt) <= clock.GetUtcNow().AddMinutes(5)) return null;
        return attempt;
    }

    public GitHubToken Recover(GrantSnapshot grant, StoredAttempt stored)
    {
        var attempt = stored.Value;
        RequireAttemptPayload(attempt);
        BindAttempt(grant, attempt, Text(attempt.Purpose));
        var token = secrets.Unprotect(TokenPurpose(grant, attempt), Text(attempt.ProtectedToken));
        if (!IsBearer(token)) throw new IssuanceDenied();
        return new(token, Date(attempt.ExpiresAt));
    }

    public async Task<(GrantSnapshot Grant, StoredAttempt Attempt)> ReserveAsync(GrantSnapshot grant, string purpose, IssuanceWork work)
    {
        if (purpose is not ("verification" or "runtime")) throw new IssuanceDenied();
        var ct = work.Cancellation;
        var profile = purpose == "verification" ? grant.Approval.VerifierProfile : grant.Profile;
        var binding = grant.Marker;
        var secretHash = Text(grant.Enrollment.ControllerSecretHash);
        var gateState = Text(grant.Gate.State);
        var enrollmentState = Text(grant.Enrollment.State);
        var acknowledged = grant.Enrollment.Acknowledged;
        var expires = Date(grant.Enrollment.ExpiresAt);
        var verifierOperation = grant.Gate.VerifierAttempt;
        var now = clock.GetUtcNow();
        var operation = Guid.NewGuid().ToString("N");
        var generation = Guid.NewGuid().ToString("N");
        var reads = purpose == "runtime" ? 8 : 0;
        long? slot = null;
        var retry = false;
        while (true)
        {
            // The shared operation budget includes both verifier and runtime admission decisions.
            // Only a definite pre-observation transaction conflict permits another evaluation.
            work.EvaluateAdmission();
            if (retry)
            {
                grant = await SnapshotAsync(grant.PartitionRepositoryId, grant.EnrollmentId, ct);
                if (grant.Marker != binding || Text(grant.Enrollment.ControllerSecretHash) != secretHash
                    || Text(grant.Gate.State) != gateState || Text(grant.Enrollment.State) != enrollmentState
                    || grant.Enrollment.Acknowledged != acknowledged || Date(grant.Enrollment.ExpiresAt) != expires
                    || grant.Gate.VerifierAttempt != verifierOperation
                    || await AttemptAsync(grant, operation, ct) is not null)
                    throw new IssuanceDenied();
            }
            if (purpose == "runtime") grant.RequireActive(clock.GetUtcNow());
            StoredAttempt? verifier = null;
            if (purpose == "verification" && grant.Gate.VerifierAttempt is { } previous)
            {
                verifier = await AttemptAsync(grant, previous, ct) ?? throw new IssuanceDenied();
                BindAttempt(grant, verifier.Value, "verification");
                if (Text(verifier.Value.State) != "known" || verifier.Value.ScopeValid != true
                    || Date(verifier.Value.ExpiresAt) > clock.GetUtcNow().AddMinutes(5)) throw new IssuanceDenied();
            }
            else if (purpose == "runtime")
            {
                verifier = await VerifierAsync(grant, ct) ?? throw new IssuanceDenied();
            }
            try
            {
                // Definite global conflicts have no committed charge; reevaluate with the same operation.
                // Once charged, reuse that reservation across known repository conflicts.
                // An unknown outcome never reaches this retry loop.
                slot ??= await ChargeGlobalAsync(operation, generation, grant.RepositoryId, grant.EnrollmentId, purpose, reads, now, grant.Approval, ct);
                await capacity.RequireIssuanceAsync(operation, generation, grant.RepositoryId, grant.EnrollmentId,
                    purpose, grant.Epoch, slot.Value, ct);
                var quotaKey = RepositoryQuota.Key(grant.PartitionRepositoryId);
                var quota = await state.ReadAsync(quotaKey, ct);
                var charged = quota?.Value ?? RepositoryQuota.New();
                charged.Charge(now, reads, limits.RepositoryAdmissions, limits.RepositoryReads, limits.RepositorySigns, limits.RepositoryMints);
                if (Number(grant.Gate.OutstandingTokenCount) >= limits.RepositoryAttempts) throw new IssuanceDenied(429, BrokerRejectionReason.RepositoryAttempts);
                grant.Gate.OutstandingTokenCount = Number(grant.Gate.OutstandingTokenCount) + 1;
                if (purpose == "verification") grant.Gate.VerifierAttempt = operation;
                else grant.Enrollment.OutstandingTokenCount = checked(Number(grant.Enrollment.OutstandingTokenCount) + 1);
                var approval = grant.Approval;
                var attempt = new IssuanceAttempt
                {
                    OperationId = operation,
                    Generation = generation,
                    RepositoryId = grant.RepositoryId,
                    EnrollmentId = grant.EnrollmentId,
                    Epoch = grant.Epoch,
                    Purpose = purpose,
                    Profile = profile,
                    InstallationId = grant.InstallationId,
                    RepositoryCharged = true,
                    State = "reserved",
                    ReservedAt = now,
                    PayloadPurgeAt = now.AddHours(24),
                    JwtIssuedAt = now.AddSeconds(-60),
                    JwtExpiresAt = now.AddSeconds(120),
                    GlobalSlot = slot.Value,
                    ApprovalKind = approval.Kind,
                    SetupVersion = approval.SetupVersion,
                    PolicyVersion = approval.Policy
                };
                await CommitAsync(grant, transaction =>
                {
                    if (quota is null) transaction.Add(quotaKey, charged);
                    else transaction.Replace(quotaKey, charged, quota.Version);
                    transaction.Add(IssuanceAttempt.Key(grant.PartitionRepositoryId, operation), attempt);
                    if (verifier is not null) transaction.Replace(verifier.Key, verifier.Value, verifier.Version);
                }, ct);
                return (await SnapshotAsync(grant.PartitionRepositoryId, grant.EnrollmentId, ct), await RequiredAttemptAsync(grant, operation, ct));
            }
            catch (StateConflict)
            {
                retry = true;
            }
        }
    }

    private async Task<long> ChargeGlobalAsync(string operation, string generation, string repository, string enrollmentId,
        string purpose, int reads, DateTimeOffset now, ApprovalContract approval, CancellationToken ct)
    {
        var quota = await capacity.SchemaAsync(ct);
        await capacity.RequireCandidateAsync(repository, ct);
        // One fixed partition and exactly 128 possible slot keys. No attacker-controlled quota keys.
        var occupancy = Number(quota.Value.Slots);
        if (occupancy >= limits.GlobalAttempts) throw new IssuanceDenied(429, BrokerRejectionReason.GlobalAttempts);
        quota.Value.Charge(now, reads, limits.GlobalAdmissions, limits.GlobalReads, limits.GlobalSigns, limits.GlobalMints);
        var slot = await capacity.FreeSlotAsync(limits.GlobalAttempts, ct);
        quota.Value.Slots = occupancy + 1;
        var reservation = new IssuanceSlot
        {
            OperationId = operation,
            Generation = generation,
            RepositoryId = repository,
            Purpose = purpose,
            Epoch = epoch,
            ReservedAt = now,
            PayloadPurgeAt = now.AddHours(24),
            GlobalSlot = slot,
            ApprovalKind = approval.Kind,
            SetupVersion = approval.SetupVersion,
            PolicyVersion = approval.Policy
        };
        var ledger = capacity.ChargeIssuance(quota.Value, reservation, enrollmentId);
        await state.CommitAsync(new StateTransaction()
            .Replace(IssuanceQuota.Key, quota.Value, quota.Version)
            .Add(IssuanceLedger.Key(operation), ledger)
            .Add(IssuanceSlot.Key(slot), reservation), ct);
        // Never compensate on an ambiguous or failed repository commit.
        return slot;
    }

    public async Task<StoredAttempt> ClaimSendAsync(GrantSnapshot grant, StoredAttempt stored, CancellationToken ct)
    {
        var attempt = stored.Value;
        RequireAttemptPayload(attempt);
        BindAttempt(grant, attempt, Text(attempt.Purpose));
        if (Text(attempt.State) != "reserved" || Date(attempt.JwtExpiresAt) <= clock.GetUtcNow()) throw new IssuanceDenied();
        if (Text(attempt.Purpose) == "runtime") grant.RequireActive(clock.GetUtcNow());
        attempt.State = "sending";
        attempt.SendClaimedAt = clock.GetUtcNow();
        await CommitAsync(grant, transaction => transaction.Replace(stored.Key, attempt, stored.Version), ct);
        return await RequiredAttemptAsync(grant, Text(attempt.OperationId), ct);
    }

    public async Task<StoredAttempt> RecordKnownAsync(GrantSnapshot original, StoredAttempt stored, KnownGitHubToken token, CancellationToken ct)
    {
        var attempt = stored.Value;
        RequireAttemptPayload(attempt);
        var grant = await SnapshotAsync(original.PartitionRepositoryId, original.EnrollmentId, ct);
        BindAttempt(grant, attempt, Text(attempt.Purpose));
        if (Text(attempt.State) != "sending") throw new IssuanceDenied();
        attempt.ProtectedToken = secrets.Protect(TokenPurpose(grant, attempt), token.Token.Value);
        attempt.ExpiresAt = token.Token.ExpiresAt;
        attempt.ScopeValid = token.ScopeValid;
        attempt.State = "known";
        await CommitAsync(grant, transaction => transaction.Replace(stored.Key, attempt, stored.Version), ct);
        return await RequiredAttemptAsync(grant, Text(attempt.OperationId), ct);
    }

    public async Task ReleaseAsync(GrantSnapshot grant, StoredAttempt runtime, StoredAttempt verifierRead, CancellationToken ct)
    {
        var (attempt, verifier) = (runtime.Value, verifierRead.Value);
        RequireAttemptPayload(attempt);
        RequireAttemptPayload(verifier);
        grant.RequireActive(clock.GetUtcNow());
        BindAttempt(grant, attempt, "runtime");
        BindAttempt(grant, verifier, "verification");
        if (Text(attempt.State) != "known" || attempt.ScopeValid != true
            || Date(attempt.ExpiresAt) <= clock.GetUtcNow().AddMinutes(5)
            || Text(verifier.State) != "known" || verifier.ScopeValid != true
            || Date(verifier.ExpiresAt) <= clock.GetUtcNow().AddMinutes(5)
            || Text(grant.Gate.VerifierAttempt) != Text(verifier.OperationId)) throw new IssuanceDenied();
        attempt.State = "released";
        attempt.FirstTerminalAt ??= clock.GetUtcNow();
        attempt.ReleasedAt ??= clock.GetUtcNow();
        grant.Enrollment.RuntimeAttempt = Text(attempt.OperationId);
        grant.Enrollment.RuntimeExpiresAt = Date(attempt.ExpiresAt);
        grant.Enrollment.LastActivityAt = clock.GetUtcNow();
        grant.Enrollment.ExpiresAt = clock.GetUtcNow().AddDays(90);
        await CommitAsync(grant, transaction => transaction
            .Replace(runtime.Key, attempt, runtime.Version)
            .Replace(verifierRead.Key, verifier, verifierRead.Version), ct);
    }

    public async Task CloseChangedApprovalAsync(GrantSnapshot grant, CancellationToken ct)
    {
        if (Text(grant.Gate.State) != "active" || Text(grant.Enrollment.State) != "active") return;
        grant.Gate.State = "closed";
        grant.Enrollment.State = "revoked";
        grant.Enrollment.FirstTerminalAt ??= clock.GetUtcNow();
        var receipt = new RevocationReceipt
        {
            RepositoryId = grant.RepositoryId,
            EnrollmentId = grant.EnrollmentId,
            Epoch = grant.Epoch,
            RevokedAt = clock.GetUtcNow()
        };
        await CommitAsync(grant, transaction => transaction.Add(RevocationReceipt.Key(grant.PartitionRepositoryId, grant.EnrollmentId), receipt), ct);
    }

    public async Task MarkRevokedAsync(GrantSnapshot original, StoredAttempt stored, CancellationToken ct)
    {
        var attempt = stored.Value;
        RequireAttemptPayload(attempt);
        var grant = await SnapshotAsync(original.PartitionRepositoryId, original.EnrollmentId, ct);
        BindAttempt(grant, attempt, Text(attempt.Purpose));
        attempt.State = "settled";
        attempt.FirstTerminalAt ??= clock.GetUtcNow();
        attempt.SettledAt ??= clock.GetUtcNow();
        attempt.ProtectedToken = null;
        await CommitAsync(grant, transaction => transaction.Replace(stored.Key, attempt, stored.Version), ct);
    }

    // The App authorization window a still-reserved attempt reserved. Only such an attempt may be signed.
    public static AppAuthorizationWindow ReservedWindow(IssuanceAttempt attempt) => Text(attempt.State) == "reserved"
        ? AppAuthorizationWindow.Reserved(Date(attempt.ReservedAt), Date(attempt.JwtIssuedAt), Date(attempt.JwtExpiresAt))
        : throw new IssuanceDenied();

    private async Task<StoredAttempt?> AttemptAsync(GrantSnapshot grant, string operation, CancellationToken ct)
    {
        var key = IssuanceAttempt.Key(grant.PartitionRepositoryId, operation);
        return await state.ReadAsync(key, ct) is { } read ? new(key, read.Value, read.Version) : null;
    }
    private async Task<StoredAttempt> RequiredAttemptAsync(GrantSnapshot grant, string operation, CancellationToken ct) =>
        await AttemptAsync(grant, operation, ct) ?? throw new IssuanceDenied();

    // Every repository transaction replaces the gate, with the next sequence, and the enrollment at the versions the grant
    // was read at. Post-observation conflicts invalidate the decision and deny; only admission owns a bounded
    // reevaluation loop.
    private async Task CommitAsync(GrantSnapshot grant, Action<StateTransaction> extra, CancellationToken ct)
    {
        grant.Gate.Sequence = checked(Number(grant.Gate.Sequence) + 1);
        var transaction = new StateTransaction()
            .Replace(grant.GateKey, grant.Gate, grant.GateVersion)
            .Replace(grant.EnrollmentKey, grant.Enrollment, grant.EnrollmentVersion);
        extra(transaction);
        await state.CommitAsync(transaction, ct);
    }

    private static SecretPurpose TokenPurpose(GrantSnapshot grant, IssuanceAttempt attempt) => grant.Approval.TokenPurpose(
        grant.RepositoryId, grant.Epoch, Text(attempt.OperationId), Text(attempt.Purpose));

    private static void BindAttempt(GrantSnapshot grant, IssuanceAttempt attempt, string purpose)
    {
        if (ApprovalContract.Stored(attempt.ApprovalKind, attempt.SetupVersion, attempt.PolicyVersion, false) != grant.Approval)
            throw new IssuanceDenied();
        if (Text(attempt.RepositoryId) != grant.RepositoryId || Text(attempt.Epoch) != grant.Epoch
            || Text(attempt.EnrollmentId) != grant.EnrollmentId || Text(attempt.Purpose) != purpose
            || Number(attempt.InstallationId) != grant.InstallationId
            || Text(attempt.Profile) != (purpose == "verification" ? grant.Approval.VerifierProfile : grant.Profile)) throw new IssuanceDenied();
    }

    // A redacted attempt's payload is gone. Retention needs a purge time, or a reservation time to fall back to.
    internal static void RequireAttemptPayload(IssuanceAttempt attempt)
    {
        if (attempt.PayloadRedacted == true || attempt.State == "redacted") throw new IssuanceDenied();
        if (Usable(attempt.PayloadPurgeAt) is null
            && (Usable(attempt.ReservedAt) is not { } reserved || reserved > DateTimeOffset.MaxValue - TimeSpan.FromHours(24)))
            throw new IssuanceDenied();
    }

    private static DateTimeOffset? Usable(DateTimeOffset? date) => date is { } value && value != DateTimeOffset.MaxValue ? value : null;
}
