using Azure.Data.Tables;
using static RepoAgent.Auth.IssuancePolicy;

namespace RepoAgent.Auth;

// One charged identity per call. Repository settlement precedes exact global release.
// Retained ledgers and release obligations survive; physical retirement belongs to #81.
public sealed class BrokerCapacityReclamation(TableClient table, BrokerOptions options, TimeProvider clock)
{
    private readonly RetainedState retained = new(table, options);

    public async Task ReclaimIssuanceAsync(string operationId, string generation, CancellationToken ct)
    {
        var ledger = await LedgerAsync(RetainedState.Issuance, "operation:" + operationId, operationId, generation, 4, ct);
        if (ledger is null) return;
        var gate = await GateAsync(ledger, ct);
        ledger = await LedgerAsync(ledger.PartitionKey, ledger.RowKey, operationId, generation, 4, ct);
        if (ledger is null || ledger.GetString("CapacityState") == "released") return;
        if (ledger.ContainsKey("CapacityState") && ledger.GetString("CapacityState") != "charged") throw new IssuanceDenied();
        var slot = await retained.RequireIssuanceAsync(operationId, generation, Text(ledger, "RepositoryId"),
            Text(ledger, "DeviceId"), Text(ledger, "Purpose"), Text(ledger, "Epoch"), Number(ledger, "GlobalSlot"), ct);
        var attempt = await ReadAsync(gate.PartitionKey, Text(ledger, "AttemptRowKey"), ct);
        var release = await ReadAsync(gate.PartitionKey, "release:" + operationId, ct);
        if (release is null)
        {
            var fresh = attempt is null;
            if (attempt is not null)
            {
                Bind(ledger, attempt, true);
                if (string.IsNullOrEmpty(gate.ETag.ToString()) || attempt.GetBoolean("CapacityReleased") == true) throw new IssuanceDenied();
                // A redaction tombstone alone is not a counter-settlement receipt.
                if (attempt.GetString("State") == "redacted") return;
            }
            var reason = fresh ? "unpublished" : Settlement(attempt!);
            if (reason is null) return;
            var extra = new List<TableTransactionAction>();
            if (!fresh)
            {
                Decrement(gate, "OutstandingTokenCount");
                if (Text(ledger, "Purpose") == "runtime")
                {
                    var device = await ReadAsync(gate.PartitionKey, "device:" + Text(ledger, "DeviceId"), ct) ?? throw new IssuanceDenied();
                    Bind(ledger, device, false);
                    Decrement(device, "OutstandingTokenCount");
                    extra.Add(Action(device));
                }
            }
            attempt ??= Copy(ledger, gate.PartitionKey, Text(ledger, "AttemptRowKey"));
            attempt["State"] = "settled";
            attempt["CapacityReleased"] = true;
            attempt["SettlementReason"] = reason;
            PayloadClock.FirstTerminal(attempt, clock.GetUtcNow());
            if (!attempt.ContainsKey("SettledAt")) attempt["SettledAt"] = clock.GetUtcNow();
            attempt.Remove("ProtectedToken");
            if (gate.GetString("VerifierAttempt") == operationId) gate.Remove("VerifierAttempt");
            release = Copy(ledger, gate.PartitionKey, "release:" + operationId);
            release["SettlementReason"] = reason;
            release["RepositorySettledAt"] = clock.GetUtcNow();
            extra.Add(Action(attempt, fresh));
            extra.Add(Action(release, true));
            await CommitAsync(gate, extra, ct);
        }
        else Bind(ledger, release, true);
        var quota = (await retained.SchemaAsync(ct)).Issuance;
        Decrement(quota, "Slots");
        ledger["CapacityState"] = "released";
        await BrokerTable.SubmitAsync(table, [Action(quota), Action(ledger),
            new(TableTransactionActionType.Delete, slot, slot.ETag)], ct);
    }

    public async Task ReclaimEnrollmentAsync(string idempotencyId, string generation, CancellationToken ct)
    {
        var ledger = await LedgerAsync(RetainedState.Enrollment, "pending:" + idempotencyId, idempotencyId, generation, 8, ct);
        if (ledger is null) return;
        var gate = await GateAsync(ledger, ct);
        ledger = await LedgerAsync(ledger.PartitionKey, ledger.RowKey, idempotencyId, generation, 8, ct);
        if (ledger is null || ledger.GetBoolean("PendingCapacityReleased") == true) return;
        if (Text(ledger, "PendingId") != Text(ledger, "RepositoryId") + "_" + idempotencyId) throw new IssuanceDenied();
        var pending = await ReadAsync(gate.PartitionKey, ledger.RowKey, ct);
        var release = await ReadAsync(gate.PartitionKey, "release:" + ledger.RowKey, ct);
        if (release is null)
        {
            var fresh = pending is null;
            var extra = new List<TableTransactionAction>();
            if (pending is not null)
            {
                Bind(ledger, pending, true);
                if (string.IsNullOrEmpty(gate.ETag.ToString()) || pending.GetBoolean("PendingCapacityReleased") == true) throw new IssuanceDenied();
                if (pending.GetString("State") == "redacted") return;
            }
            var state = pending?.GetString("State");
            if (state is not ("active" or "closed") && Date(ledger, "Deadline") > clock.GetUtcNow()) return;
            if (state == "active")
            {
                var device = await ReadAsync(gate.PartitionKey, "device:" + Text(ledger, "DeviceId"), ct) ?? throw new IssuanceDenied();
                Bind(ledger, device, false);
                if (device.GetBoolean("Acknowledged") != true) throw new IssuanceDenied();
                extra.Add(Action(device));
            }
            if (!fresh) Decrement(gate, "PendingCount");
            pending ??= Copy(ledger, gate.PartitionKey, ledger.RowKey);
            if (state != "active") pending["State"] = "closed";
            pending["PendingCapacityReleased"] = true;
            PayloadClock.FirstTerminal(pending, state is "active" or "closed" ? clock.GetUtcNow() : Date(ledger, "Deadline"));
            release = Copy(ledger, gate.PartitionKey, "release:" + ledger.RowKey);
            release["RepositorySettledAt"] = clock.GetUtcNow();
            extra.Add(Action(pending, fresh));
            extra.Add(Action(release, true));
            await CommitAsync(gate, extra, ct);
        }
        else Bind(ledger, release, true);
        var quota = (await retained.SchemaAsync(ct)).Enrollment;
        var directory = await retained.DirectoryAsync(Text(ledger, "RepositoryId"), ct) ?? throw new IssuanceDenied();
        Decrement(quota, "Count");
        Decrement(directory, "PendingCount");
        ledger["PendingCapacityReleased"] = true;
        await BrokerTable.SubmitAsync(table, [Action(quota), Action(directory), Action(ledger)], ct);
    }

    private string? Settlement(TableEntity attempt)
    {
        var state = Text(attempt, "State");
        if (state == "reserved" && !attempt.ContainsKey("SendClaimedAt")) return "unsent";
        // Existing settled rows are written only after a successful provider revocation.
        if (state == "settled" && PayloadClock.Read(attempt, "SettledAt") is not null) return "revoked";
        if (state is not ("known" or "released") || attempt.GetBoolean("ScopeValid") != true) return null;
        var expiry = PayloadClock.Read(attempt, "ExpiresAt");
        var reserved = PayloadClock.Read(attempt, "ReservedAt");
        // Healthy synchronized UTC is required; the margin is not a clock-tampering defense.
        if (expiry is null || reserved is null || expiry <= reserved || expiry == DateTimeOffset.MinValue) return null;
        return PayloadClock.Add(expiry, TimeSpan.FromMinutes(5)) is { } due && clock.GetUtcNow() >= due ? "expired" : null;
    }

    private async Task<TableEntity?> LedgerAsync(string partition, string key, string id, string generation, long credits, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(id, "N", out _) || !Guid.TryParseExact(generation, "N", out _)) throw new IssuanceDenied();
        var row = await ReadAsync(partition, key, ct);
        if (row is null) return null;
        if (Text(row, "Generation") != generation || Number(row, "RowCredits") != credits) throw new IssuanceDenied();
        _ = Id(Text(row, "RepositoryId"));
        if (!Guid.TryParseExact(Text(row, "DeviceId"), "N", out _)) throw new IssuanceDenied();
        _ = Text(row, "Epoch");
        _ = ApprovalContract.Read(row);
        if (credits == 4 && (Text(row, "OperationId") != id || Text(row, "AttemptRowKey") != "attempt:" + id
            || Text(row, "Purpose") is not ("verification" or "runtime"))) throw new IssuanceDenied();
        return row;
    }

    private async Task<TableEntity> GateAsync(TableEntity ledger, CancellationToken ct)
    {
        _ = await retained.SchemaAsync(ct);
        var repository = Text(ledger, "RepositoryId");
        _ = await retained.DirectoryAsync(repository, ct) ?? throw new IssuanceDenied();
        var gate = await ReadAsync("repository:" + repository, "gate", ct);
        if (gate is not null)
        {
            if (Text(gate, "RepositoryId") != repository) throw new IssuanceDenied();
            _ = Number(gate, "Sequence");
            return gate;
        }
        return new("repository:" + repository, "gate")
        {
            ["RepositoryId"] = repository, ["Epoch"] = Text(ledger, "Epoch"), ["State"] = "vacant",
            ["Sequence"] = 0L, ["OutstandingTokenCount"] = 0L, ["PendingCount"] = 0L
        };
    }

    private static void Bind(TableEntity ledger, TableEntity row, bool allocation)
    {
        foreach (var field in new[] { "RepositoryId", "DeviceId", "Epoch" })
            if (Text(ledger, field) != Text(row, field)) throw new IssuanceDenied();
        ApprovalContract.Read(ledger).Require(row);
        if (!allocation) return;
        if (Text(ledger, "Generation") != Text(row, "Generation")) throw new IssuanceDenied();
        foreach (var field in new[] { "OperationId", "Purpose", "PendingId" })
            if (ledger.ContainsKey(field) && Text(ledger, field) != Text(row, field)) throw new IssuanceDenied();
        if (ledger.ContainsKey("GlobalSlot") && Number(ledger, "GlobalSlot") != Number(row, "GlobalSlot")) throw new IssuanceDenied();
    }

    private static TableEntity Copy(TableEntity ledger, string partition, string key)
    {
        var row = new TableEntity(partition, key);
        foreach (var field in new[] { "RepositoryId", "DeviceId", "Epoch", "Generation", "OperationId", "Purpose", "PendingId",
            "GlobalSlot", "ReservedAt", "PayloadPurgeAt", "CreatedAt", "Deadline", "RowCredits" })
            if (ledger.TryGetValue(field, out var value)) row[field] = value;
        ApprovalContract.Read(ledger).Write(row);
        return row;
    }
    private static void Decrement(TableEntity row, string field)
    {
        var value = Number(row, field);
        if (value == 0) throw new IssuanceDenied();
        row[field] = value - 1;
    }
    private Task<TableEntity?> ReadAsync(string partition, string key, CancellationToken ct) => BrokerTable.ReadAsync(table, partition, key, ct);
    private static TableTransactionAction Action(TableEntity row, bool add = false) =>
        new(add ? TableTransactionActionType.Add : TableTransactionActionType.UpdateReplace, row, row.ETag);
    private Task CommitAsync(TableEntity gate, List<TableTransactionAction> actions, CancellationToken ct)
    {
        gate["Sequence"] = checked(Number(gate, "Sequence") + 1);
        actions.Insert(0, Action(gate, string.IsNullOrEmpty(gate.ETag.ToString())));
        return BrokerTable.SubmitAsync(table, actions, ct);
    }
}
