using Azure.Data.Tables;
using static RepoAgent.Auth.IssuancePolicy;
using static RepoAgent.Auth.TableValues;

namespace RepoAgent.Auth;

public sealed partial class IssuanceStore
{
    public async Task<TableEntity?> PublishedRuntimeAsync(GrantSnapshot grant, CancellationToken ct)
    {
        grant.RequireActive(clock.GetUtcNow());
        if (!grant.Device.ContainsKey("RuntimeAttempt") && !grant.Device.ContainsKey("RuntimeExpiresAt")) return null;
        var operation = Text(grant.Device, "RuntimeAttempt");
        if (!Guid.TryParseExact(operation, "N", out _)) throw new IssuanceDenied();
        if (Date(grant.Device, "RuntimeExpiresAt") <= clock.GetUtcNow().AddMinutes(5)) return null;
        var attempt = await RequiredAttemptAsync(grant, operation, ct);
        PayloadClock.RequireAttempt(attempt);
        BindAttempt(grant, attempt, "runtime");
        if (Text(attempt, "OperationId") != operation || Date(attempt, "ExpiresAt") != Date(grant.Device, "RuntimeExpiresAt"))
            throw new IssuanceDenied();
        _ = Date(attempt, "ReleasedAt");
        if (Text(attempt, "State") == "settled")
        {
            _ = Date(attempt, "SettledAt");
            return null;
        }
        RequirePublishedRuntime(grant, attempt);
        return attempt;
    }

    public async Task<(GrantSnapshot Grant, TableEntity Attempt, TableEntity Jwt)> ReservePublishedObservationAsync(
        GrantSnapshot grant, TableEntity attempt, IssuanceWork work)
    {
        RequirePublishedRuntime(grant, attempt);
        var ct = work.Cancellation;
        var now = clock.GetUtcNow();
        var observation = Guid.NewGuid().ToString("N");
        var binding = grant.Marker;
        var secretHash = Text(grant.Device, "SecretHash");
        var expires = Date(grant.Device, "ExpiresAt");
        var verifier = grant.Gate.GetString("VerifierAttempt");
        var operation = Text(attempt, "OperationId");
        var generation = Text(attempt, "Generation");
        var leaseExpiry = Date(attempt, "ExpiresAt");
        var globalCharged = false;
        var retry = false;
        while (true)
        {
            work.EvaluateAdmission();
            if (retry)
            {
                grant = await SnapshotAsync(grant.Partition, grant.DeviceId, ct);
                if (grant.Marker != binding || Text(grant.Device, "SecretHash") != secretHash
                    || Date(grant.Device, "ExpiresAt") != expires || grant.Gate.GetString("VerifierAttempt") != verifier)
                    throw new IssuanceDenied();
                attempt = await RequiredAttemptAsync(grant, operation, ct);
                if (Text(attempt, "Generation") != generation || Date(attempt, "ExpiresAt") != leaseExpiry) throw new IssuanceDenied();
            }
            RequirePublishedRuntime(grant, attempt);
            await retained.RequireIssuanceAsync(operation, generation, grant.RepositoryId, grant.DeviceId,
                "runtime", grant.Epoch, Number(attempt, "GlobalSlot"), ct);
            try
            {
                if (!globalCharged)
                {
                    var global = (await retained.SchemaAsync(ct)).Issuance;
                    Charge(global, now, 8, limits.GlobalAdmissions, limits.GlobalReads, limits.GlobalSigns, limits.GlobalMints, mint: false);
                    await BrokerTable.SubmitAsync(table, [Update(global)], ct);
                    globalCharged = true;
                }
                var quota = await ReadAsync(grant.Partition, "quota", ct);
                var freshQuota = quota is null;
                quota ??= NewQuota(grant.Partition);
                Charge(quota, now, 8, limits.RepositoryAdmissions, limits.RepositoryReads, limits.RepositorySigns, limits.RepositoryMints, mint: false);
                // Overwrite bounded observation metadata, never the original capability clocks.
                grant.Device["RuntimeObservation"] = observation;
                grant.Device["RuntimeObservationReservedAt"] = now;
                grant.Device["RuntimeObservationJwtIssuedAt"] = now.AddSeconds(-60);
                grant.Device["RuntimeObservationJwtExpiresAt"] = now.AddSeconds(120);
                await CommitAsync(grant, [new(freshQuota ? TableTransactionActionType.Add : TableTransactionActionType.UpdateReplace, quota, quota.ETag), Update(attempt)], ct);
                break;
            }
            catch (TableTransactionFailedException ex) when (ex.Status is 409 or 412) { retry = true; }
        }
        grant = await SnapshotAsync(grant.Partition, grant.DeviceId, ct);
        if (Text(grant.Device, "RuntimeObservation") != observation) throw new IssuanceDenied();
        attempt = await RequiredAttemptAsync(grant, Text(attempt, "OperationId"), ct);
        RequirePublishedRuntime(grant, attempt);
        var jwt = new TableEntity(grant.Partition, observation)
        {
            ["State"] = "reserved", ["ReservedAt"] = Date(grant.Device, "RuntimeObservationReservedAt"),
            ["JwtIssuedAt"] = Date(grant.Device, "RuntimeObservationJwtIssuedAt"),
            ["JwtExpiresAt"] = Date(grant.Device, "RuntimeObservationJwtExpiresAt")
        };
        return (grant, attempt, jwt);
    }

    public async Task RepublishAsync(GrantSnapshot grant, TableEntity attempt, TableEntity verifier, string observation, CancellationToken ct)
    {
        RequirePublishedRuntime(grant, attempt);
        PayloadClock.RequireAttempt(verifier);
        BindAttempt(grant, verifier, "verification");
        if (Text(grant.Device, "RuntimeObservation") != observation
            || Text(verifier, "State") != "known" || verifier.GetBoolean("ScopeValid") != true
            || Date(verifier, "ExpiresAt") <= clock.GetUtcNow().AddMinutes(5)
            || grant.Gate.GetString("VerifierAttempt") != Text(verifier, "OperationId")) throw new IssuanceDenied();
        grant.Device["LastActivityAt"] = clock.GetUtcNow();
        grant.Device["ExpiresAt"] = clock.GetUtcNow().AddDays(90);
        // The original release, expiry, payload clocks and outstanding charges remain intact.
        await CommitAsync(grant, [Update(attempt), Update(verifier)], ct);
    }

    public async Task<TableEntity> FenceUnpublishedRuntimeAsync(GrantSnapshot original, TableEntity attempt, CancellationToken ct)
    {
        var grant = await SnapshotAsync(original.Partition, original.DeviceId, ct);
        var current = await RequiredAttemptAsync(grant, Text(attempt, "OperationId"), ct);
        PayloadClock.RequireAttempt(current);
        BindAttempt(grant, current, "runtime");
        if (current.GetString("Generation") != attempt.GetString("Generation")
            || Text(current, "State") is not ("known" or "sending") || current.ContainsKey("ReleasedAt"))
            throw new IssuanceDenied();
        // A conditional touch fences an in-flight release before provider DELETE. If release
        // already committed, never revoke the token another authenticated request may reuse.
        await CommitAsync(grant, [Update(current)], ct);
        return await RequiredAttemptAsync(grant, Text(current, "OperationId"), ct);
    }

    private void RequirePublishedRuntime(GrantSnapshot grant, TableEntity attempt)
    {
        grant.RequireActive(clock.GetUtcNow());
        PayloadClock.RequireAttempt(attempt);
        BindAttempt(grant, attempt, "runtime");
        if (Text(attempt, "State") != "released" || attempt.GetBoolean("ScopeValid") != true
            || attempt.GetBoolean("RepositoryCharged") != true || attempt.GetBoolean("CapacityReleased") == true
            || Number(grant.Device, "OutstandingTokenCount") < 1
            || Number(grant.Gate, "OutstandingTokenCount") <= Number(grant.Device, "OutstandingTokenCount")
            || Text(grant.Device, "RuntimeAttempt") != Text(attempt, "OperationId")
            || Date(grant.Device, "RuntimeExpiresAt") != Date(attempt, "ExpiresAt")
            || Date(attempt, "ExpiresAt") <= clock.GetUtcNow().AddMinutes(5)) throw new IssuanceDenied();
        _ = Date(attempt, "ReleasedAt");
    }
}
