using static RepoAgent.Auth.IssuanceValues;

namespace RepoAgent.Auth;

// One reuse of a published runtime GitHub token: the observation ID and the App authorization window it reserved on
// the enrollment.
public sealed record PublishedObservation(string Id, DateTimeOffset ReservedAt, DateTimeOffset JwtIssuedAt, DateTimeOffset JwtExpiresAt);

public sealed partial class IssuanceStore
{
    public async Task<StoredAttempt?> PublishedRuntimeAsync(GrantSnapshot grant, CancellationToken ct)
    {
        grant.RequireActive(clock.GetUtcNow());
        if (grant.Enrollment.RuntimeAttempt is null && grant.Enrollment.RuntimeExpiresAt is null) return null;
        var operation = Text(grant.Enrollment.RuntimeAttempt);
        if (!Guid.TryParseExact(operation, "N", out _)) throw new IssuanceDenied();
        if (Date(grant.Enrollment.RuntimeExpiresAt) <= clock.GetUtcNow().AddMinutes(5)) return null;
        var stored = await RequiredAttemptAsync(grant, operation, ct);
        var attempt = stored.Value;
        RequireAttemptPayload(attempt);
        BindAttempt(grant, attempt, "runtime");
        if (Text(attempt.OperationId) != operation || Date(attempt.ExpiresAt) != Date(grant.Enrollment.RuntimeExpiresAt))
            throw new IssuanceDenied();
        _ = Date(attempt.ReleasedAt);
        if (Text(attempt.State) == "settled")
        {
            _ = Date(attempt.SettledAt);
            return null;
        }
        RequirePublishedRuntime(grant, attempt);
        return stored;
    }

    public async Task<(GrantSnapshot Grant, StoredAttempt Attempt, PublishedObservation Observation)> ReservePublishedObservationAsync(
        GrantSnapshot grant, StoredAttempt attempt, IssuanceWork work)
    {
        RequirePublishedRuntime(grant, attempt.Value);
        var ct = work.Cancellation;
        var now = clock.GetUtcNow();
        var observation = Guid.NewGuid().ToString("N");
        var binding = grant.Marker;
        var secretHash = Text(grant.Enrollment.ControllerSecretHash);
        var expires = Date(grant.Enrollment.ExpiresAt);
        var verifier = grant.Gate.VerifierAttempt;
        var operation = Text(attempt.Value.OperationId);
        var generation = Text(attempt.Value.Generation);
        var leaseExpiry = Date(attempt.Value.ExpiresAt);
        var globalCharged = false;
        var retry = false;
        while (true)
        {
            work.EvaluateAdmission();
            if (retry)
            {
                grant = await SnapshotAsync(grant.PartitionRepositoryId, grant.EnrollmentId, ct);
                if (grant.Marker != binding || Text(grant.Enrollment.ControllerSecretHash) != secretHash
                    || Date(grant.Enrollment.ExpiresAt) != expires || grant.Gate.VerifierAttempt != verifier)
                    throw new IssuanceDenied();
                attempt = await RequiredAttemptAsync(grant, operation, ct);
                if (Text(attempt.Value.Generation) != generation || Date(attempt.Value.ExpiresAt) != leaseExpiry) throw new IssuanceDenied();
            }
            RequirePublishedRuntime(grant, attempt.Value);
            await capacity.RequireIssuanceAsync(operation, generation, grant.RepositoryId, grant.EnrollmentId,
                "runtime", grant.Epoch, Number(attempt.Value.GlobalSlot), ct);
            try
            {
                if (!globalCharged)
                {
                    var global = await capacity.SchemaAsync(ct);
                    global.Value.Charge(now, 8, limits.GlobalAdmissions, limits.GlobalReads, limits.GlobalSigns, limits.GlobalMints, mint: false);
                    await state.CommitAsync(new StateTransaction().Replace(IssuanceQuota.Key, global.Value, global.Version), ct);
                    globalCharged = true;
                }
                var quotaKey = RepositoryQuota.Key(grant.PartitionRepositoryId);
                var quota = await state.ReadAsync(quotaKey, ct);
                var charged = quota?.Value ?? RepositoryQuota.New();
                charged.Charge(now, 8, limits.RepositoryAdmissions, limits.RepositoryReads, limits.RepositorySigns, limits.RepositoryMints, mint: false);
                // Overwrite bounded observation metadata, never the original capability clocks.
                grant.Enrollment.RuntimeObservation = observation;
                grant.Enrollment.RuntimeObservationReservedAt = now;
                grant.Enrollment.RuntimeObservationJwtIssuedAt = now.AddSeconds(-60);
                grant.Enrollment.RuntimeObservationJwtExpiresAt = now.AddSeconds(120);
                var published = attempt;
                await CommitAsync(grant, transaction =>
                {
                    if (quota is null) transaction.Add(quotaKey, charged);
                    else transaction.Replace(quotaKey, charged, quota.Version);
                    transaction.Replace(published.Key, published.Value, published.Version);
                }, ct);
                break;
            }
            catch (StateConflict) { retry = true; }
        }
        grant = await SnapshotAsync(grant.PartitionRepositoryId, grant.EnrollmentId, ct);
        if (Text(grant.Enrollment.RuntimeObservation) != observation) throw new IssuanceDenied();
        attempt = await RequiredAttemptAsync(grant, Text(attempt.Value.OperationId), ct);
        RequirePublishedRuntime(grant, attempt.Value);
        return (grant, attempt, new(observation, Date(grant.Enrollment.RuntimeObservationReservedAt),
            Date(grant.Enrollment.RuntimeObservationJwtIssuedAt), Date(grant.Enrollment.RuntimeObservationJwtExpiresAt)));
    }

    public async Task RepublishAsync(GrantSnapshot grant, StoredAttempt attempt, StoredAttempt verifierRead, string observation, CancellationToken ct)
    {
        RequirePublishedRuntime(grant, attempt.Value);
        var verifier = verifierRead.Value;
        RequireAttemptPayload(verifier);
        BindAttempt(grant, verifier, "verification");
        if (Text(grant.Enrollment.RuntimeObservation) != observation
            || Text(verifier.State) != "known" || verifier.ScopeValid != true
            || Date(verifier.ExpiresAt) <= clock.GetUtcNow().AddMinutes(5)
            || grant.Gate.VerifierAttempt != Text(verifier.OperationId)) throw new IssuanceDenied();
        grant.Enrollment.LastActivityAt = clock.GetUtcNow();
        grant.Enrollment.ExpiresAt = clock.GetUtcNow().AddDays(90);
        // The original release, expiry, payload clocks and outstanding charges remain intact.
        await CommitAsync(grant, transaction => transaction
            .Replace(attempt.Key, attempt.Value, attempt.Version)
            .Replace(verifierRead.Key, verifier, verifierRead.Version), ct);
    }

    public async Task<StoredAttempt> FenceUnpublishedRuntimeAsync(GrantSnapshot original, StoredAttempt attempt, CancellationToken ct)
    {
        var grant = await SnapshotAsync(original.PartitionRepositoryId, original.EnrollmentId, ct);
        var current = await RequiredAttemptAsync(grant, Text(attempt.Value.OperationId), ct);
        RequireAttemptPayload(current.Value);
        BindAttempt(grant, current.Value, "runtime");
        if (current.Value.Generation != attempt.Value.Generation
            || Text(current.Value.State) is not ("known" or "sending") || current.Value.ReleasedAt is not null)
            throw new IssuanceDenied();
        // A conditional touch fences an in-flight release before provider DELETE. If release
        // already committed, never revoke the token another authenticated request may reuse.
        await CommitAsync(grant, transaction => transaction.Replace(current.Key, current.Value, current.Version), ct);
        return await RequiredAttemptAsync(grant, Text(current.Value.OperationId), ct);
    }

    private void RequirePublishedRuntime(GrantSnapshot grant, IssuanceAttempt attempt)
    {
        grant.RequireActive(clock.GetUtcNow());
        RequireAttemptPayload(attempt);
        BindAttempt(grant, attempt, "runtime");
        if (Text(attempt.State) != "released" || attempt.ScopeValid != true
            || attempt.RepositoryCharged != true || attempt.CapacityReleased == true
            || Number(grant.Enrollment.OutstandingTokenCount) < 1
            || Number(grant.Gate.OutstandingTokenCount) <= Number(grant.Enrollment.OutstandingTokenCount)
            || Text(grant.Enrollment.RuntimeAttempt) != Text(attempt.OperationId)
            || Date(grant.Enrollment.RuntimeExpiresAt) != Date(attempt.ExpiresAt)
            || Date(attempt.ExpiresAt) <= clock.GetUtcNow().AddMinutes(5)) throw new IssuanceDenied();
        _ = Date(attempt.ReleasedAt);
    }
}
