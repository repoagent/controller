using System.Globalization;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.DataProtection;
using static RepoAgent.Auth.IssuancePolicy;

namespace RepoAgent.Auth;

// One Table-specific module owns the two-part conservative reservation and repository EGTs.
// Capacity release is owned by BrokerCapacityReclamation after repository settlement.
public sealed class IssuanceStore(TableClient table, BrokerOptions options, IDataProtectionProvider protection, TimeProvider clock, IssuanceLimits? configuredLimits = null)
{
    private readonly RetainedState retained = new(table, options);
    private readonly IssuanceLimits limits = configuredLimits ?? new();

    public async Task<GrantSnapshot> AuthenticateAsync(string deviceId, string? secret, CancellationToken ct)
    {
        var hash = DeviceSecret.Hash(secret);
        if (!DeviceSecret.IsDeviceId(deviceId) || hash is null) throw new IssuanceDenied(401);
        var locator = await ReadAsync("locator:" + deviceId, "locator", ct) ?? throw new IssuanceDenied(401);
        if (Text(locator, "DeviceId") != deviceId) throw new IssuanceDenied(401);
        var repository = Text(locator, "RepositoryId");
        _ = Id(repository);
        var grant = await SnapshotAsync("repository:" + repository, deviceId, ct);
        if (grant.RepositoryId != repository || !DeviceSecret.Matches(hash, Text(grant.Device, "SecretHash"))) throw new IssuanceDenied(401);
        grant.RequireActive(clock.GetUtcNow());
        return grant;
    }

    public async Task<GrantSnapshot> SnapshotAsync(string partition, string deviceId, CancellationToken ct)
    {
        var gate = await ReadAsync(partition, "gate", ct) ?? throw new IssuanceDenied(401);
        var device = await ReadAsync(partition, "device:" + deviceId, ct) ?? throw new IssuanceDenied(401);
        var snapshot = new GrantSnapshot(gate, device);
        if (snapshot.DeviceId != deviceId || partition != "repository:" + snapshot.RepositoryId) throw new IssuanceDenied();
        // Retained old devices remain readable after owner recovery, but are no longer grants.
        if (gate.GetString("OwnerDeviceId") != deviceId) throw new IssuanceDenied(401);
        snapshot.RequireBinding(options);
        return snapshot;
    }

    public async Task<TableEntity?> VerifierAsync(GrantSnapshot grant, CancellationToken ct)
    {
        if (!grant.Gate.TryGetValue("VerifierAttempt", out var value)) return null;
        if (value is not string id || !Guid.TryParseExact(id, "N", out _)) throw new IssuanceDenied();
        var attempt = await ReadAsync(grant.Partition, "attempt:" + id, ct) ?? throw new IssuanceDenied();
        BindAttempt(grant, attempt, "verification");
        if (Text(attempt, "State") != "known" || attempt.GetBoolean("ScopeValid") != true) throw new IssuanceDenied();
        // An unresolved acquisition blocks subsequent acquisitions indefinitely. Only a known,
        // exact-scope token may be replaced near its actual expiry; both remain accounted.
        if (Date(attempt, "ExpiresAt") <= clock.GetUtcNow().AddMinutes(5)) return null;
        return attempt;
    }

    public string Recover(GrantSnapshot grant, TableEntity attempt)
    {
        PayloadClock.RequireAttempt(attempt);
        BindAttempt(grant, attempt, Text(attempt, "Purpose"));
        var token = Protector(grant, attempt).Unprotect(Text(attempt, "ProtectedToken"));
        if (!IsBearer(token)) throw new IssuanceDenied();
        return token;
    }

    public async Task<(GrantSnapshot Grant, TableEntity Attempt)> ReserveAsync(GrantSnapshot grant, string purpose, IssuanceWork work)
    {
        if (purpose is not ("verification" or "runtime")) throw new IssuanceDenied();
        var ct = work.Cancellation;
        var profile = purpose == "verification" ? grant.Approval.VerifierProfile : grant.Profile;
        var binding = grant.Marker;
        var secretHash = Text(grant.Device, "SecretHash");
        var gateState = Text(grant.Gate, "State");
        var deviceState = Text(grant.Device, "State");
        var acknowledged = grant.Device.GetBoolean("Acknowledged");
        var expires = Date(grant.Device, "ExpiresAt");
        var verifierOperation = grant.Gate.GetString("VerifierAttempt");
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
                grant = await SnapshotAsync(grant.Partition, grant.DeviceId, ct);
                if (grant.Marker != binding || Text(grant.Device, "SecretHash") != secretHash
                    || Text(grant.Gate, "State") != gateState || Text(grant.Device, "State") != deviceState
                    || grant.Device.GetBoolean("Acknowledged") != acknowledged || Date(grant.Device, "ExpiresAt") != expires
                    || grant.Gate.GetString("VerifierAttempt") != verifierOperation
                    || await ReadAsync(grant.Partition, "attempt:" + operation, ct) is not null)
                    throw new IssuanceDenied();
            }
            if (purpose == "runtime") grant.RequireActive(clock.GetUtcNow());
            TableEntity? verifier = null;
            if (purpose == "verification" && grant.Gate.TryGetValue("VerifierAttempt", out var previous))
            {
                verifier = await ReadAsync(grant.Partition, "attempt:" + (string)previous, ct) ?? throw new IssuanceDenied();
                BindAttempt(grant, verifier, "verification");
                if (Text(verifier, "State") != "known" || verifier.GetBoolean("ScopeValid") != true
                    || Date(verifier, "ExpiresAt") > clock.GetUtcNow().AddMinutes(5)) throw new IssuanceDenied();
            }
            else if (purpose == "runtime")
            {
                verifier = await VerifierAsync(grant, ct) ?? throw new IssuanceDenied();
            }
            try
            {
                // Definite global conflicts have no committed charge; reevaluate with the same operation.
                // Once charged, reuse that reservation across known repository conflicts.
                // A timeout or unknown response never reaches this retry loop.
                slot ??= await ChargeGlobalAsync(operation, generation, grant.RepositoryId, grant.DeviceId, purpose, reads, now, grant.Approval, ct);
                await retained.RequireIssuanceAsync(operation, generation, grant.RepositoryId, grant.DeviceId,
                    purpose, grant.Epoch, slot.Value, ct);
                var quota = await ReadAsync(grant.Partition, "quota", ct);
                var freshQuota = quota is null;
                quota ??= NewQuota(grant.Partition);
                Charge(quota, now, reads, limits.RepositoryAdmissions, limits.RepositoryReads, limits.RepositorySigns, limits.RepositoryMints);
                if (Number(grant.Gate, "OutstandingTokenCount") >= limits.RepositoryAttempts) throw new IssuanceDenied(429);
                grant.Gate["OutstandingTokenCount"] = Number(grant.Gate, "OutstandingTokenCount") + 1;
                if (purpose == "verification") grant.Gate["VerifierAttempt"] = operation;
                else grant.Device["OutstandingTokenCount"] = checked(Number(grant.Device, "OutstandingTokenCount") + 1);
                var attempt = new TableEntity(grant.Partition, "attempt:" + operation)
                {
                    ["OperationId"] = operation, ["Generation"] = generation, ["RepositoryId"] = grant.RepositoryId,
                    ["DeviceId"] = grant.DeviceId, ["Epoch"] = grant.Epoch, ["Purpose"] = purpose,
                    ["Profile"] = profile, ["InstallationId"] = grant.InstallationId,
                    ["RepositoryCharged"] = true, ["State"] = "reserved", ["ReservedAt"] = now, ["PayloadPurgeAt"] = now.AddHours(24),
                    ["JwtIssuedAt"] = now.AddSeconds(-60), ["JwtExpiresAt"] = now.AddSeconds(120), ["GlobalSlot"] = slot.Value
                };
                grant.Approval.Write(attempt);
                var extra = new List<TableTransactionAction>
                {
                    new(freshQuota ? TableTransactionActionType.Add : TableTransactionActionType.UpdateReplace, quota, quota.ETag),
                    new(TableTransactionActionType.Add, attempt)
                };
                if (verifier is not null) extra.Add(Update(verifier));
                await CommitAsync(grant, extra, ct);
                return (await SnapshotAsync(grant.Partition, grant.DeviceId, ct), await RequiredAttemptAsync(grant, operation, ct));
            }
            catch (TableTransactionFailedException ex) when (ex.Status is 409 or 412)
            {
                retry = true;
                continue;
            }
        }
    }

    private async Task<long> ChargeGlobalAsync(string operation, string generation, string repository, string device, string purpose, int reads, DateTimeOffset now, ApprovalContract approval, CancellationToken ct)
    {
        const string partition = "global:issuance-v1";
        var quota = (await retained.SchemaAsync(ct)).Issuance;
        _ = await retained.DirectoryAsync(repository, ct) ?? throw new IssuanceDenied();
        // One fixed partition and exactly 128 possible slot keys. No attacker-controlled quota keys.
        var occupancy = Number(quota, "Slots");
        if (occupancy >= limits.GlobalAttempts) throw new IssuanceDenied(429);
        Charge(quota, now, reads, limits.GlobalAdmissions, limits.GlobalReads, limits.GlobalSigns, limits.GlobalMints);
        var slot = await retained.FreeSlotAsync(limits.GlobalAttempts, ct);
        quota["Slots"] = occupancy + 1;
        var reservation = new TableEntity(partition, "slot:" + slot.ToString("D3", CultureInfo.InvariantCulture))
        {
            ["OperationId"] = operation, ["Generation"] = generation, ["RepositoryId"] = repository, ["Purpose"] = purpose,
            ["Epoch"] = options.DeploymentEpoch, ["ReservedAt"] = now,
            ["PayloadPurgeAt"] = now.AddHours(24), ["GlobalSlot"] = slot
        };
        approval.Write(reservation);
        var ledger = retained.ChargeIssuance(quota, reservation, device);
        await BrokerTable.SubmitAsync(table, [
            new(TableTransactionActionType.UpdateReplace, quota, quota.ETag),
            new(TableTransactionActionType.Add, ledger),
            new(TableTransactionActionType.Add, reservation)
        ], ct);
        // Never compensate on an ambiguous or failed repository commit.
        return slot;
    }

    public async Task<TableEntity> ClaimSendAsync(GrantSnapshot grant, TableEntity attempt, CancellationToken ct)
    {
        PayloadClock.RequireAttempt(attempt);
        BindAttempt(grant, attempt, Text(attempt, "Purpose"));
        if (Text(attempt, "State") != "reserved" || Date(attempt, "JwtExpiresAt") <= clock.GetUtcNow()) throw new IssuanceDenied();
        if (Text(attempt, "Purpose") == "runtime") grant.RequireActive(clock.GetUtcNow());
        attempt["State"] = "sending";
        attempt["SendClaimedAt"] = clock.GetUtcNow();
        await CommitAsync(grant, [Update(attempt)], ct);
        return await RequiredAttemptAsync(grant, Text(attempt, "OperationId"), ct);
    }

    public async Task<TableEntity> RecordKnownAsync(GrantSnapshot original, TableEntity attempt, IssuedToken token, CancellationToken ct)
    {
        PayloadClock.RequireAttempt(attempt);
        var grant = await SnapshotAsync(original.Partition, original.DeviceId, ct);
        BindAttempt(grant, attempt, Text(attempt, "Purpose"));
        if (Text(attempt, "State") != "sending") throw new IssuanceDenied();
        attempt["ProtectedToken"] = Protector(grant, attempt).Protect(token.Token);
        attempt["ExpiresAt"] = token.ExpiresAt;
        attempt["ScopeValid"] = token.ScopeValid;
        attempt["State"] = "known";
        await CommitAsync(grant, [Update(attempt)], ct);
        return await RequiredAttemptAsync(grant, Text(attempt, "OperationId"), ct);
    }

    public async Task ReleaseAsync(GrantSnapshot grant, TableEntity attempt, TableEntity verifier, CancellationToken ct)
    {
        PayloadClock.RequireAttempt(attempt);
        PayloadClock.RequireAttempt(verifier);
        grant.RequireActive(clock.GetUtcNow());
        BindAttempt(grant, attempt, "runtime");
        BindAttempt(grant, verifier, "verification");
        if (Text(attempt, "State") != "known" || attempt.GetBoolean("ScopeValid") != true
            || Date(attempt, "ExpiresAt") <= clock.GetUtcNow().AddMinutes(5)
            || Text(verifier, "State") != "known" || verifier.GetBoolean("ScopeValid") != true
            || Date(verifier, "ExpiresAt") <= clock.GetUtcNow().AddMinutes(5)
            || Text(grant.Gate, "VerifierAttempt") != Text(verifier, "OperationId")) throw new IssuanceDenied();
        attempt["State"] = "released";
        PayloadClock.FirstTerminal(attempt, clock.GetUtcNow());
        if (!attempt.ContainsKey("ReleasedAt")) attempt["ReleasedAt"] = clock.GetUtcNow();
        grant.Device["LastActivityAt"] = clock.GetUtcNow();
        grant.Device["ExpiresAt"] = clock.GetUtcNow().AddDays(90);
        await CommitAsync(grant, [Update(attempt), Update(verifier)], ct);
    }

    public async Task CloseChangedApprovalAsync(GrantSnapshot grant, CancellationToken ct)
    {
        if (Text(grant.Gate, "State") != "active" || Text(grant.Device, "State") != "active") return;
        grant.Gate["State"] = "closed";
        grant.Device["State"] = "revoked";
        PayloadClock.FirstTerminal(grant.Device, clock.GetUtcNow());
        var receipt = new TableEntity(grant.Partition, "revocation:" + grant.DeviceId)
        {
            ["RepositoryId"] = grant.RepositoryId, ["DeviceId"] = grant.DeviceId, ["Epoch"] = grant.Epoch,
            ["RevokedAt"] = clock.GetUtcNow()
        };
        await CommitAsync(grant, [new(TableTransactionActionType.Add, receipt)], ct);
    }

    public async Task MarkRevokedAsync(GrantSnapshot original, TableEntity attempt, CancellationToken ct)
    {
        PayloadClock.RequireAttempt(attempt);
        var grant = await SnapshotAsync(original.Partition, original.DeviceId, ct);
        BindAttempt(grant, attempt, Text(attempt, "Purpose"));
        attempt["State"] = "settled";
        PayloadClock.FirstTerminal(attempt, clock.GetUtcNow());
        if (!attempt.ContainsKey("SettledAt")) attempt["SettledAt"] = clock.GetUtcNow();
        attempt.Remove("ProtectedToken");
        await CommitAsync(grant, [Update(attempt)], ct);
    }

    public Task<TableEntity?> ReadAsync(string partition, string row, CancellationToken ct) =>
        BrokerTable.ReadAsync(table, partition, row, ct);
    private async Task<TableEntity> RequiredAttemptAsync(GrantSnapshot grant, string operation, CancellationToken ct) =>
        await ReadAsync(grant.Partition, "attempt:" + operation, ct) ?? throw new IssuanceDenied();
    private static TableTransactionAction Update(TableEntity e) => new(TableTransactionActionType.UpdateReplace, e, e.ETag);
    private async Task CommitAsync(GrantSnapshot grant, IEnumerable<TableTransactionAction> extra, CancellationToken ct)
    {
        grant.Gate["Sequence"] = checked(Number(grant.Gate, "Sequence") + 1);
        var actions = new List<TableTransactionAction> { Update(grant.Gate), Update(grant.Device) };
        actions.AddRange(extra);
        await BrokerTable.SubmitAsync(table, actions, ct);
        // Post-observation conflicts invalidate the decision and deny; only admission owns a bounded reevaluation loop.
    }
    private IDataProtector Protector(GrantSnapshot grant, TableEntity attempt) => grant.Approval.TokenProtector(
        protection, grant.RepositoryId, grant.Epoch, Text(attempt, "OperationId"), Text(attempt, "Purpose"));
    private static void BindAttempt(GrantSnapshot grant, TableEntity attempt, string purpose)
    {
        grant.Approval.Require(attempt);
        if (Text(attempt, "RepositoryId") != grant.RepositoryId || Text(attempt, "Epoch") != grant.Epoch
            || Text(attempt, "DeviceId") != grant.DeviceId || Text(attempt, "Purpose") != purpose
            || Number(attempt, "InstallationId") != grant.InstallationId
            || Text(attempt, "Profile") != (purpose == "verification" ? grant.Approval.VerifierProfile : grant.Profile)) throw new IssuanceDenied();
    }
    private static TableEntity NewQuota(string partition) => new(partition, "quota")
    {
        ["Window"] = 0L, ["Admissions"] = 0L, ["Reads"] = 0L, ["Signs"] = 0L, ["Mints"] = 0L, ["Slots"] = 0L
    };
    private static void Charge(TableEntity quota, DateTimeOffset now, int reads, long admissions, long readLimit, long signLimit, long mintLimit)
    {
        var window = now.ToUnixTimeSeconds() / 60;
        if (Number(quota, "Window") > window) throw new IssuanceDenied();
        if (Number(quota, "Window") != window)
        {
            quota["Window"] = window;
            foreach (var field in new[] { "Admissions", "Reads", "Signs", "Mints" }) quota[field] = 0L;
        }
        if (Number(quota, "Admissions") + 1 > admissions || Number(quota, "Reads") + reads > readLimit
            || Number(quota, "Signs") + 1 > signLimit || Number(quota, "Mints") + 1 > mintLimit) throw new IssuanceDenied(429);
        quota["Admissions"] = Number(quota, "Admissions") + 1;
        quota["Reads"] = Number(quota, "Reads") + reads;
        quota["Signs"] = Number(quota, "Signs") + 1;
        quota["Mints"] = Number(quota, "Mints") + 1;
    }
}
