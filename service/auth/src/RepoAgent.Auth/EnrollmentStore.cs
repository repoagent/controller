using System.Globalization;
using Azure;
using Azure.Data.Tables;
using Microsoft.AspNetCore.DataProtection;
using static RepoAgent.Auth.IssuancePolicy;

namespace RepoAgent.Auth;

// Concrete Table authority. Global reservations are conservative, never rolled back across partitions.
public sealed class EnrollmentStore(TableClient table, BrokerOptions options, IDataProtectionProvider protection,
    TimeProvider clock, IssuanceLimits limits, IssuanceStore reads)
{
    private const string Global = "global:enrollment-v1";
    private readonly RetainedState retained = new(table, options);
    internal async Task<EnrollmentSnapshot> AdmitAsync(EnrollmentInput request, string hash, CancellationToken ct)
    {
        _ = await retained.SchemaAsync(ct);
        var repository = request.RepositoryId.ToString(CultureInfo.InvariantCulture);
        var key = "pending:" + request.IdempotencyId;
        TableEntity? reservation = null;
        for (var retry = 0; retry < 4; retry++)
        {
            reservation = await reads.ReadAsync(Global, key, ct);
            if (reservation is not null) break;
            var quota = (await retained.SchemaAsync(ct)).Enrollment;
            var directory = await retained.DirectoryAsync(repository, ct);
            var freshDirectory = directory is null;
            directory ??= new(Global, "repository:" + repository) { ["RepositoryId"] = repository, ["PendingCount"] = 0L };
            retained.ChargeEnrollment(quota, directory, freshDirectory);
            var now = clock.GetUtcNow();
            reservation = new(Global, key)
            {
                ["RepositoryId"] = repository, ["PendingId"] = repository + "_" + request.IdempotencyId,
                ["RecoveryHash"] = hash, ["BodyHash"] = request.BodyHash, ["Epoch"] = options.DeploymentEpoch,
                ["CreatedAt"] = now, ["Deadline"] = now.Add(PendingLifetime),
                ["PayloadPurgeAt"] = now.Add(PendingLifetime).AddHours(24),
                ["Generation"] = Guid.NewGuid().ToString("N"), ["RowCredits"] = 8L,
                ["DeviceId"] = Guid.NewGuid().ToString("N"), ["Challenge"] = EnrollmentSecrets.Generate()
            };
            request.Approval.Write(reservation);
            try { await BrokerTable.SubmitAsync(table, [Action(quota), Action(directory, freshDirectory), Action(reservation, true)], ct); break; }
            catch (TableTransactionFailedException ex) when (ex.Status is 409 or 412) { reservation = null; }
        }
        if (reservation is null) throw new IssuanceDenied();
        if (Text(reservation, "RecoveryHash") != hash || Text(reservation, "BodyHash") != request.BodyHash
            || Text(reservation, "RepositoryId") != repository) throw new IssuanceDenied(409);
        request.Approval.Require(reservation);
        RequireLive(reservation);
        var partition = "repository:" + repository;
        for (var retry = 0; retry < 4; retry++)
        {
            var gate = await reads.ReadAsync(partition, "gate", ct);
            var current = await reads.ReadAsync(Global, key, ct) ?? throw new IssuanceDenied();
            if (Text(current, "Generation") != Text(reservation, "Generation")
                || current.GetBoolean("PendingCapacityReleased") == true
                || Text(current, "RepositoryId") != repository || Text(current, "RecoveryHash") != hash
                || Text(current, "BodyHash") != request.BodyHash) throw new IssuanceDenied();
            request.Approval.Require(current);
            RequireLive(current);
            reservation = current;
            var pending = await reads.ReadAsync(partition, key, ct);
            if (pending is not null) return await SnapshotAsync(Text(reservation, "PendingId"), hash, ct, request.Approval.SetupVersion);
            var fresh = gate is null;
            gate ??= new(partition, "gate")
            {
                ["RepositoryId"] = repository, ["Epoch"] = options.DeploymentEpoch, ["State"] = "vacant",
                ["Sequence"] = 0L, ["OutstandingTokenCount"] = 0L, ["PendingCount"] = 0L
            };
            if (Text(gate, "Epoch") != options.DeploymentEpoch || Text(gate, "RepositoryId") != repository) throw new IssuanceDenied();
            var count = gate.GetInt64("PendingCount") ?? 0;
            if (count >= RepositoryPending) throw new IssuanceDenied(429);
            gate["PendingCount"] = count + 1;
            gate["Sequence"] = Number(gate, "Sequence") + 1;
            pending = new(partition, key);
            foreach (var pair in reservation.Where(p => p.Key is not ("PartitionKey" or "RowKey" or "Timestamp" or "odata.etag"))) pending[pair.Key] = pair.Value;
            request.Approval.Write(pending);
            pending["RequestedRepository"] = request.Repository;
            pending["Repository"] = request.Repository;
            if (request.Approval == ApprovalContract.Legacy) pending["Environment"] = request.Environment!;
            pending["Variable"] = request.Variable.ToUpperInvariant();
            pending["Profile"] = request.Profile;
            pending["State"] = "discovering";
            pending["RepositoryCharged"] = true;
            pending["NextWorkAt"] = DateTimeOffset.UnixEpoch;
            pending["PositiveApproval"] = false;
            try
            {
                await BrokerTable.SubmitAsync(table, [Action(gate, fresh), Action(pending, true)], ct);
                return await SnapshotAsync(Text(reservation, "PendingId"), hash, ct, request.Approval.SetupVersion);
            }
            catch (TableTransactionFailedException ex) when (ex.Status is 409 or 412) { }
        }
        throw new IssuanceDenied();
    }

    internal async Task<EnrollmentSnapshot> SnapshotAsync(string id, string? recoveryHash, CancellationToken ct, long setupVersion)
    {
        var parts = id.Split('_');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[1], "N", out _)) throw new IssuanceDenied(401);
        _ = Id(parts[0]);
        var partition = "repository:" + parts[0];
        var pending = await reads.ReadAsync(partition, "pending:" + parts[1], ct) ?? throw new IssuanceDenied(401);
        var gate = await reads.ReadAsync(partition, "gate", ct) ?? throw new IssuanceDenied(401);
        if (Text(pending, "PendingId") != id || Text(pending, "RepositoryId") != parts[0]
            || Text(gate, "RepositoryId") != parts[0] || Text(gate, "Epoch") != options.DeploymentEpoch
            || Text(pending, "Epoch") != options.DeploymentEpoch) throw new IssuanceDenied(401);
        if (recoveryHash is not null && (pending.GetString("RecoveryHash") is not { } hash || !DeviceSecret.Matches(Convert.FromHexString(recoveryHash), hash))) throw new IssuanceDenied(401);
        var snapshot = new EnrollmentSnapshot(gate, pending);
        if (snapshot.Approval.SetupVersion != setupVersion) throw new IssuanceDenied(409);
        if (snapshot.Approval == ApprovalContract.Legacy)
        {
            var environment = Text(pending, "Environment");
            if (environment.Length > 255 || environment.Any(char.IsControl)) throw new IssuanceDenied();
            if (pending.ContainsKey("Marker") && Number(pending, "EnvironmentId") <= 0) throw new IssuanceDenied();
        }
        if (pending.ContainsKey("Marker") && Text(pending, "Marker") != ApprovalMarker.Create(new(gate, pending))) throw new IssuanceDenied();
        return snapshot;
    }

    internal void RequireLive(TableEntity pending)
    {
        PayloadClock.RequirePayload(pending);
        if (Text(pending, "Epoch") != options.DeploymentEpoch || Date(pending, "Deadline") <= clock.GetUtcNow()) throw new IssuanceDenied(410);
        if (pending.GetString("State") is "closed" or "active") throw new IssuanceDenied(410);
    }

    internal async Task<TableEntity> ReserveWorkAsync(EnrollmentSnapshot s, bool mint, IssuanceWork work)
    {
        RequireLive(s.Pending);
        _ = await retained.SchemaAsync(work.Cancellation);
        _ = await retained.DirectoryAsync(s.RepositoryId, work.Cancellation) ?? throw new IssuanceDenied();
        var now = clock.GetUtcNow();
        if (Date(s.Pending, "NextWorkAt") > now) throw new IssuanceDenied(429);
        if (s.Pending.GetString("Attempt") is not null && mint) throw new IssuanceDenied();
        work.EvaluateAdmission();
        // Claim one durable operation before global/provider admission; all endpoints share this field.
        s.Pending["NextWorkAt"] = now.AddSeconds(15);
        s.Pending["WorkStartedAt"] = now;
        await CommitAsync(s, [], work.Cancellation);
        var global = (await retained.SchemaAsync(work.Cancellation)).Issuance;
        Charge(global, now, limits.GlobalAdmissions, limits.GlobalReads, limits.GlobalSigns, limits.GlobalMints, mint);
        var operation = mint ? Guid.NewGuid().ToString("N") : "observation";
        var generation = Guid.NewGuid().ToString("N");
        long? slot = null;
        var actions = new List<TableTransactionAction>();
        if (mint)
        {
            var occupancy = Number(global, "Slots");
            if (occupancy >= limits.GlobalAttempts) throw new IssuanceDenied(429);
            slot = await retained.FreeSlotAsync(limits.GlobalAttempts, work.Cancellation);
            global["Slots"] = occupancy + 1;
            var slotReservation = new TableEntity("global:issuance-v1", "slot:" + slot.Value.ToString("D3", CultureInfo.InvariantCulture))
            {
                ["OperationId"] = operation, ["Generation"] = generation, ["RepositoryId"] = s.RepositoryId, ["Purpose"] = "verification",
                ["Epoch"] = options.DeploymentEpoch, ["ReservedAt"] = now,
                ["PayloadPurgeAt"] = now.AddHours(24), ["GlobalSlot"] = slot.Value
            };
            s.Approval.Write(slotReservation);
            actions.Add(Action(retained.ChargeIssuance(global, slotReservation, s.DeviceId, s.Id), true));
            actions.Add(Action(slotReservation, true));
        }
        actions.Add(Action(global));
        await BrokerTable.SubmitAsync(table, actions, work.Cancellation);
        if (mint) await retained.RequireIssuanceAsync(operation, generation, s.RepositoryId, s.DeviceId,
            "verification", options.DeploymentEpoch, slot!.Value, work.Cancellation);
        var quota = await reads.ReadAsync(s.Partition, "quota", work.Cancellation);
        var freshQuota = quota is null;
        quota ??= Quota(s.Partition);
        Charge(quota, now, limits.RepositoryAdmissions, limits.RepositoryReads, limits.RepositorySigns, limits.RepositoryMints, mint);
        var attempt = new TableEntity(s.Partition, mint ? "attempt:" + operation : "observation:" + s.Id)
        {
            ["OperationId"] = operation, ["Generation"] = generation, ["RepositoryId"] = s.RepositoryId, ["DeviceId"] = s.DeviceId,
            ["PendingId"] = s.Id, ["Epoch"] = options.DeploymentEpoch, ["Purpose"] = "verification", ["Profile"] = s.Approval.VerifierProfile,
            ["RepositoryCharged"] = true, ["State"] = "reserved", ["ReservedAt"] = now, ["PayloadPurgeAt"] = now.AddHours(24), ["JwtIssuedAt"] = now.AddSeconds(-60), ["JwtExpiresAt"] = now.AddSeconds(120)
        };
        s.Approval.Write(attempt);
        var extra = new List<TableTransactionAction> { Action(quota, freshQuota) };
        if (mint)
        {
            if (Number(s.Gate, "OutstandingTokenCount") >= limits.RepositoryAttempts) throw new IssuanceDenied(429);
            s.Gate["OutstandingTokenCount"] = Number(s.Gate, "OutstandingTokenCount") + 1;
            s.Pending["Attempt"] = operation;
            attempt["GlobalSlot"] = slot!.Value;
            extra.Add(Action(attempt, true));
        }
        // Observation JWT timestamps stay in this bounded pending row, not an append-only operation log.
        s.Pending["ObservationReservedAt"] = now;
        s.Pending["ObservationJwtIssuedAt"] = now.AddSeconds(-60);
        s.Pending["ObservationJwtExpiresAt"] = now.AddSeconds(120);
        await CommitAsync(s, extra, work.Cancellation);
        return mint ? await AttemptAsync(s, work.Cancellation) : attempt;
    }

    internal async Task<TableEntity> AttemptAsync(EnrollmentSnapshot s, CancellationToken ct)
    {
        var a = await reads.ReadAsync(s.Partition, "attempt:" + Text(s.Pending, "Attempt"), ct) ?? throw new IssuanceDenied();
        if (Text(a, "PendingId") != s.Id || Text(a, "RepositoryId") != s.RepositoryId || Text(a, "DeviceId") != s.DeviceId
            || Text(a, "Epoch") != options.DeploymentEpoch || Text(a, "Purpose") != "verification" || Text(a, "Profile") != s.Approval.VerifierProfile) throw new IssuanceDenied();
        s.Approval.Require(a);
        return a;
    }
    internal string Recover(EnrollmentSnapshot s, TableEntity attempt)
    {
        PayloadClock.RequireAttempt(attempt);
        if (Text(attempt, "State") != "known" || attempt.GetBoolean("ScopeValid") != true
            || Number(attempt, "InstallationId") != Number(s.Pending, "InstallationId")
            || Date(attempt, "ExpiresAt") <= clock.GetUtcNow().AddMinutes(5)) throw new IssuanceDenied();
        return TokenProtector(s, attempt).Unprotect(Text(attempt, "ProtectedToken"));
    }
    internal IDataProtector TokenProtector(EnrollmentSnapshot s, TableEntity attempt) => s.Approval.TokenProtector(
        protection, s.RepositoryId, options.DeploymentEpoch, Text(attempt, "OperationId"), "verification");
    internal IDataProtector CompletionProtector(EnrollmentSnapshot s) => s.Approval.CompletionProtector(protection, s.RepositoryId, options.DeploymentEpoch, s.Id);

    internal async Task CommitAsync(EnrollmentSnapshot s, IEnumerable<TableTransactionAction> extra, CancellationToken ct)
    {
        PayloadClock.RequirePayload(s.Pending);
        s.Gate["Sequence"] = checked(Number(s.Gate, "Sequence") + 1);
        var actions = new List<TableTransactionAction> { Action(s.Gate), Action(s.Pending) };
        actions.AddRange(extra);
        var response = await BrokerTable.SubmitAsync(table, actions, ct);
        for (var i = 0; i < actions.Count; i++)
            if (response.Value[i].Headers.ETag is { } etag) actions[i].Entity.ETag = etag;
    }
    internal async Task FinishAsync(EnrollmentSnapshot s, IEnumerable<TableTransactionAction> extra, CancellationToken ct)
    {
        s.Pending["NextWorkAt"] = Date(s.Pending, "WorkStartedAt").AddSeconds(5);
        await CommitAsync(s, extra, ct);
    }
    internal async Task PublishLocatorAsync(EnrollmentSnapshot s, CancellationToken ct)
    {
        var row = new TableEntity("locator:" + s.DeviceId, "locator") { ["RepositoryId"] = s.RepositoryId, ["DeviceId"] = s.DeviceId, ["PendingId"] = s.Id };
        try { await BrokerTable.AddAsync(table, row, ct); }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            var current = await reads.ReadAsync(row.PartitionKey, row.RowKey, ct) ?? throw new IssuanceDenied();
            if (Text(current, "RepositoryId") != s.RepositoryId || Text(current, "DeviceId") != s.DeviceId || Text(current, "PendingId") != s.Id) throw new IssuanceDenied();
        }
    }
    internal Task<TableEntity?> ReadAsync(string partition, string row, CancellationToken ct) => reads.ReadAsync(partition, row, ct);
    internal static TableTransactionAction Action(TableEntity entity, bool add = false) => new(add ? TableTransactionActionType.Add : TableTransactionActionType.UpdateReplace, entity, entity.ETag);
    private static TableEntity Quota(string partition) => new(partition, "quota")
    { ["Window"] = 0L, ["Admissions"] = 0L, ["Reads"] = 0L, ["Signs"] = 0L, ["Mints"] = 0L, ["Slots"] = 0L };
    private static void Charge(TableEntity q, DateTimeOffset now, int admissions, int reads, int signs, int mints, bool mint)
    {
        var window = now.ToUnixTimeSeconds() / 60;
        if (Number(q, "Window") > window) throw new IssuanceDenied();
        if (Number(q, "Window") != window)
        {
            q["Window"] = window;
            foreach (var field in new[] { "Admissions", "Reads", "Signs", "Mints" }) q[field] = 0L;
        }
        if (Number(q, "Admissions") + 1 > admissions || Number(q, "Reads") + 8 > reads
            || Number(q, "Signs") + 1 > signs || Number(q, "Mints") + (mint ? 1 : 0) > mints) throw new IssuanceDenied(429);
        q["Admissions"] = Number(q, "Admissions") + 1;
        q["Reads"] = Number(q, "Reads") + 8;
        q["Signs"] = Number(q, "Signs") + 1;
        q["Mints"] = Number(q, "Mints") + (mint ? 1 : 0);
    }
}
