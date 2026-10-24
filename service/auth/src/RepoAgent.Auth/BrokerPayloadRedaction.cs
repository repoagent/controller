using Azure.Data.Tables;
using static RepoAgent.Auth.IssuancePolicy;
using static RepoAgent.Auth.TableValues;

namespace RepoAgent.Auth;

// One charged identity per call. No provider/key dependency, scans, refunds or settlement.
public sealed class BrokerPayloadRedaction(TableClient table, BrokerOptions options, TimeProvider clock)
{
    private readonly RetainedState retained = new(table, options);

    public async Task RedactEnrollmentAsync(string idempotencyId, string generation, CancellationToken ct)
    {
        var ledger = await LedgerAsync(RetainedState.Enrollment, "pending:", idempotencyId, generation, 8, ct);
        if (ledger is null) return;
        var repository = Text(ledger, "RepositoryId");
        var partition = "repository:" + repository;
        if (Text(ledger, "PendingId") != repository + "_" + idempotencyId) throw new IssuanceDenied();
        var gate = await GateAsync(ledger, ct);
        ledger = await LedgerAsync(RetainedState.Enrollment, "pending:", idempotencyId, generation, 8, ct);
        if (ledger is null || ledger.ContainsKey("RetirementState")) return;
        if (Text(gate, "RepositoryId") != Text(ledger, "RepositoryId")) throw new IssuanceDenied();
        var pending = await BrokerTable.ReadAsync(table, partition, ledger.RowKey, ct);
        var device = await BrokerTable.ReadAsync(table, partition, "device:" + Text(ledger, "DeviceId"), ct);
        var receipt = device is null ? null : await BrokerTable.ReadAsync(table, partition, "revocation:" + Text(ledger, "DeviceId"), ct);
        if (string.IsNullOrEmpty(gate.ETag.ToString()) && (pending is not null || device is not null)) throw new IssuanceDenied();
        if (pending is not null) Bind(ledger, pending, true);
        if (device is not null)
        {
            Bind(ledger, device, false);
            if (Text(device, "PendingId") != Text(ledger, "PendingId")) throw new IssuanceDenied();
        }
        if (receipt is not null) Bind(ledger, receipt, false);

        var now = clock.GetUtcNow();
        var deadline = PayloadClock.Earlier(PayloadClock.Fallback(ledger, "Deadline", TimeSpan.FromHours(24)),
            pending is null ? null : PayloadClock.After(pending, "FirstTerminalAt", TimeSpan.FromHours(24)));
        // A malformed repository copy cannot extend the immutable ledger deadline.
        var due = deadline is null || deadline <= now;
        var globalChanged = false;
        var changed = false;
        var freshPending = pending is null;
        if (due)
        {
            pending ??= Tombstone(ledger, partition, ledger.RowKey);
            changed = Redact(pending, "RecoveryHash", "ProtectedCompletion") || freshPending;
            if (!pending.ContainsKey("FirstTerminalAt") && PayloadClock.Read(ledger, "Deadline") is { } expired)
            {
                pending["FirstTerminalAt"] = expired;
                changed = true;
            }
            globalChanged = Redact(ledger, "RecoveryHash");
        }
        if (device is not null)
        {
            var expiry = PayloadClock.After(device, "ExpiresAt", TimeSpan.FromHours(24));
            var terminal = PayloadClock.After(device, "FirstTerminalAt", TimeSpan.FromHours(24))
                ?? (receipt is null ? null : PayloadClock.After(receipt, "RevokedAt", TimeSpan.FromHours(24)));
            var deviceDue = expiry is null || device.GetString("State") == "revoked" && terminal is null
                ? null : PayloadClock.Earlier(expiry, terminal);
            if (deviceDue is null || deviceDue <= now)
            {
                changed |= Redact(device, "SecretHash");
                var ended = PayloadClock.Earlier(PayloadClock.Read(device, "ExpiresAt"),
                    receipt is null ? null : PayloadClock.Read(receipt, "RevokedAt"));
                if (!device.ContainsKey("FirstTerminalAt") && ended is { } first)
                {
                    device["FirstTerminalAt"] = first;
                    changed = true;
                }
            }
        }
        var resolution = pending is null ? PayloadClock.Read(ledger, "Deadline")
            : PayloadClock.Earlier(PayloadClock.Read(pending, "FirstTerminalAt"), PayloadClock.Read(ledger, "Deadline"));
        if (PayloadClock.Add(resolution, TimeSpan.FromDays(30)) is { } auditAt && auditAt <= now)
        {
            if (pending is not null) changed |= Remove(pending, "NextWorkAt", "WorkStartedAt", "ObservationReservedAt", "ObservationJwtIssuedAt", "ObservationJwtExpiresAt");
            if (due) globalChanged |= Remove(ledger, "BodyHash");
        }
        if (!changed && !globalChanged) return;
        var actions = new List<TableTransactionAction> { GateAction(gate) };
        if (pending is not null) actions.Add(Action(pending, freshPending));
        if (device is not null) actions.Add(Action(device));
        if (receipt is not null) actions.Add(Action(receipt));
        // Fence all repository observations before changing the global recovery copy.
        await BrokerTable.SubmitAsync(table, actions, ct);
        if (globalChanged) await BrokerTable.SubmitAsync(table, [Action(ledger)], ct);
    }

    public async Task RedactIssuanceAsync(string operationId, string generation, CancellationToken ct)
    {
        var ledger = await LedgerAsync(RetainedState.Issuance, "operation:", operationId, generation, 4, ct);
        if (ledger is null) return;
        if (Text(ledger, "OperationId") != operationId || Text(ledger, "AttemptRowKey") != "attempt:" + operationId
            || Text(ledger, "Purpose") is not ("verification" or "runtime")) throw new IssuanceDenied();
        var gate = await GateAsync(ledger, ct);
        ledger = await LedgerAsync(RetainedState.Issuance, "operation:", operationId, generation, 4, ct);
        if (ledger is null || ledger.ContainsKey("RetirementState")) return;
        if (Text(gate, "RepositoryId") != Text(ledger, "RepositoryId")) throw new IssuanceDenied();
        var partition = "repository:" + Text(ledger, "RepositoryId");
        var attempt = await BrokerTable.ReadAsync(table, partition, Text(ledger, "AttemptRowKey"), ct);
        if (attempt is not null)
        {
            if (string.IsNullOrEmpty(gate.ETag.ToString())) throw new IssuanceDenied();
            Bind(ledger, attempt, true);
            foreach (var field in new[] { "OperationId", "Purpose" })
                if (Text(ledger, field) != Text(attempt, field)) throw new IssuanceDenied();
            if (Number(ledger, "GlobalSlot") != Number(attempt, "GlobalSlot")) throw new IssuanceDenied();
        }
        var now = clock.GetUtcNow();
        var deadline = PayloadClock.Fallback(ledger, "ReservedAt", TimeSpan.FromHours(24));
        if (attempt is not null)
        {
            // Invalid fallback on the actual payload also fails closed, even with a valid ledger.
            var fallback = PayloadClock.Fallback(attempt, "ReservedAt", TimeSpan.FromHours(24));
            deadline = deadline is null || fallback is null ? null : PayloadClock.Earlier(deadline, fallback);
            if (deadline is not null)
            {
                deadline = PayloadClock.Earlier(deadline, PayloadClock.After(attempt, "FirstTerminalAt", TimeSpan.FromHours(24)));
                if (attempt.GetBoolean("ScopeValid") == true)
                    deadline = PayloadClock.Earlier(deadline, PayloadClock.After(attempt, "ExpiresAt", TimeSpan.FromHours(24)));
            }
        }
        var fresh = attempt is null;
        var changed = false;
        if (deadline is null || deadline <= now)
        {
            attempt ??= Tombstone(ledger, partition, Text(ledger, "AttemptRowKey"));
            changed = Redact(attempt, "ProtectedToken") || fresh;
        }
        // Released/known/sending authority is not resolved by the passage of time.
        if (attempt is not null && attempt.GetString("State") == "settled"
            && PayloadClock.After(attempt, "SettledAt", TimeSpan.FromDays(30)) is { } auditAt && auditAt <= now)
            changed |= Remove(attempt, "SendClaimedAt", "ReleasedAt");
        if (changed) await BrokerTable.SubmitAsync(table, [GateAction(gate), Action(attempt!, fresh)], ct);
    }

    private async Task<TableEntity?> LedgerAsync(string partition, string prefix, string id, string generation, long credits, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Guid.TryParseExact(id, "N", out _) || !Guid.TryParseExact(generation, "N", out _)) throw new IssuanceDenied();
        var ledger = await BrokerTable.ReadAsync(table, partition, prefix + id, ct);
        if (ledger is null) return null;
        if (Text(ledger, "Generation") != generation || Number(ledger, "RowCredits") != credits) throw new IssuanceDenied();
        var repository = Text(ledger, "RepositoryId");
        _ = Id(repository);
        if (!Guid.TryParseExact(Text(ledger, "DeviceId"), "N", out _)) throw new IssuanceDenied();
        _ = Text(ledger, "Epoch");
        _ = ApprovalContract.Read(ledger);
        if (credits == 8 && Text(ledger, "PendingId") != repository + "_" + id
            || credits == 4 && (Text(ledger, "OperationId") != id || Text(ledger, "AttemptRowKey") != "attempt:" + id
                || Text(ledger, "Purpose") is not ("verification" or "runtime"))) throw new IssuanceDenied();
        _ = await retained.SchemaAsync(ct);
        _ = await retained.DirectoryAsync(repository, ct) ?? throw new IssuanceDenied();
        return ledger;
    }

    private async Task<TableEntity> GateAsync(TableEntity ledger, CancellationToken ct)
    {
        var repository = Text(ledger, "RepositoryId");
        var gate = await BrokerTable.ReadAsync(table, "repository:" + repository, "gate", ct);
        if (gate is not null)
        {
            if (Text(gate, "RepositoryId") != repository) throw new IssuanceDenied();
            // Old-epoch payloads remain due. Never reset an existing gate's epoch/owner/counters.
            _ = Number(gate, "Sequence");
            return gate;
        }
        return new("repository:" + repository, "gate")
        {
            ["RepositoryId"] = repository, ["Epoch"] = Text(ledger, "Epoch"), ["State"] = "vacant",
            ["Sequence"] = 0L, ["OutstandingTokenCount"] = 0L, ["PendingCount"] = 0L
        };
    }

    private static void Bind(TableEntity ledger, TableEntity row, bool generation)
    {
        foreach (var field in new[] { "RepositoryId", "DeviceId", "Epoch" })
            if (Text(row, field) != Text(ledger, field)) throw new IssuanceDenied();
        if (generation && Text(row, "Generation") != Text(ledger, "Generation")) throw new IssuanceDenied();
    }

    private static TableEntity Tombstone(TableEntity ledger, string partition, string key)
    {
        var row = new TableEntity(partition, key);
        foreach (var field in new[] { "Generation", "RepositoryId", "DeviceId", "Epoch", "RowCredits", "PendingId", "OperationId",
            "Purpose", "GlobalSlot", "ReservedAt", "CreatedAt", "Deadline", "PayloadPurgeAt", "ApprovalKind", "SetupVersion", "PolicyVersion", "ReclamationVersion" })
            if (ledger.TryGetValue(field, out var value)) row[field] = value;
        row["State"] = "redacted";
        return row;
    }

    private static bool Redact(TableEntity row, params string[] fields)
    {
        var changed = Remove(row, fields);
        if (row.GetBoolean("PayloadRedacted") != true) { row["PayloadRedacted"] = true; changed = true; }
        return changed;
    }
    private static bool Remove(TableEntity row, params string[] fields)
    {
        var changed = false;
        foreach (var field in fields) changed |= row.Remove(field);
        return changed;
    }
    private static TableTransactionAction GateAction(TableEntity gate)
    {
        gate["Sequence"] = checked(Number(gate, "Sequence") + 1);
        return Action(gate, string.IsNullOrEmpty(gate.ETag.ToString()));
    }
    private static TableTransactionAction Action(TableEntity row, bool add = false) =>
        new(add ? TableTransactionActionType.Add : TableTransactionActionType.UpdateReplace, row, row.ETag);
}

internal static class PayloadClock
{
    internal static DateTimeOffset? Read(TableEntity row, string field) =>
        row.TryGetValue(field, out var value) && value is DateTimeOffset date && date != DateTimeOffset.MaxValue ? date : null;
    internal static DateTimeOffset? Add(DateTimeOffset? date, TimeSpan age) =>
        date is { } value && value <= DateTimeOffset.MaxValue - age ? value + age : null;
    internal static DateTimeOffset? After(TableEntity row, string field, TimeSpan age) => Add(Read(row, field), age);
    internal static DateTimeOffset? Earlier(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null || first <= second ? first : second;
    internal static DateTimeOffset? Fallback(TableEntity row, string original, TimeSpan age) =>
        Earlier(Read(row, "PayloadPurgeAt"), After(row, original, age));
    internal static void FirstTerminal(TableEntity row, DateTimeOffset now)
    {
        if (!row.ContainsKey("FirstTerminalAt")) row["FirstTerminalAt"] = now;
    }
    internal static void RequirePayload(TableEntity row)
    {
        if (row.GetBoolean("PayloadRedacted") == true || row.GetString("State") == "redacted") throw new IssuanceDenied();
    }
    internal static void RequireAttempt(TableEntity row)
    {
        RequirePayload(row);
        if (Fallback(row, "ReservedAt", TimeSpan.FromHours(24)) is null) throw new IssuanceDenied();
    }
}
