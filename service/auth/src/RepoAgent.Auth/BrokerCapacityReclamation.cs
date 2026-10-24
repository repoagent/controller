using Azure.Data.Tables;
using static RepoAgent.Auth.IssuancePolicy;
using static RepoAgent.Auth.TableValues;

namespace RepoAgent.Auth;

// One charged identity per call. Repository settlement precedes exact global release.
// A later invocation retires eligible retained rows using durable deletion proof.
public sealed class BrokerCapacityReclamation(TableClient table, BrokerOptions options, TimeProvider clock)
{
    private readonly RetainedState retained = new(table, options);

    public async Task ReclaimIssuanceAsync(string operationId, string generation, CancellationToken ct)
    {
        var ledger = await LedgerAsync(RetainedState.Issuance, "operation:" + operationId, operationId, generation, 4, ct);
        if (ledger is null) return;
        var gate = await GateAsync(ledger, ct);
        ledger = await LedgerAsync(ledger.PartitionKey, ledger.RowKey, operationId, generation, 4, ct);
        if (ledger is null) return;
        if (ledger.GetString("CapacityState") == "released") { await RetireAsync(ledger, gate, false, ct); return; }
        if (ledger.ContainsKey("RetirementState")) throw new IssuanceDenied();
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
                if (attempt.GetString("State") == "redacted" && !UnpublishedTombstone(ledger, attempt)) return;
            }
            var unpublished = fresh || UnpublishedTombstone(ledger, attempt!);
            var reason = unpublished ? "unpublished" : Settlement(attempt!);
            if (reason is null) return;
            var extra = new List<TableTransactionAction>();
            if (!unpublished)
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
        if (ledger is null) return;
        if (ledger.GetBoolean("PendingCapacityReleased") == true)
        {
            if (gate.GetString("OwnerDeviceId") == Text(ledger, "DeviceId"))
                await ReleaseSettledOwnerAsync(ledger, gate, ct);
            else await RetireAsync(ledger, gate, true, ct);
            return;
        }
        if (ledger.ContainsKey("RetirementState")) throw new IssuanceDenied();
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
                if (pending.GetString("State") == "redacted" && !UnpublishedTombstone(ledger, pending)) return;
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
            if (!fresh && !UnpublishedTombstone(ledger, pending!)) Decrement(gate, "PendingCount");
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

    private async Task ReleaseSettledOwnerAsync(TableEntity ledger, TableEntity gate, CancellationToken ct)
    {
        var deadline = Date(ledger, "Deadline");
        if (Text(ledger, "Epoch") != options.DeploymentEpoch
            || Text(gate, "Epoch") != options.DeploymentEpoch
            || gate.GetString("State") is not ("unacknowledged" or "closed")
            || Number(gate, "OutstandingTokenCount") != 0 || gate.ContainsKey("VerifierAttempt")) return;
        if (ledger.ContainsKey("RetirementState") || string.IsNullOrEmpty(gate.ETag.ToString())) throw new IssuanceDenied();
        var pending = await ReadAsync(gate.PartitionKey, ledger.RowKey, ct) ?? throw new IssuanceDenied();
        var device = await ReadAsync(gate.PartitionKey, "device:" + Text(ledger, "DeviceId"), ct) ?? throw new IssuanceDenied();
        Bind(ledger, pending, true);
        Bind(ledger, device, false);
        var acknowledged = device.GetBoolean("Acknowledged");
        if (acknowledged is null || acknowledged == false && deadline > clock.GetUtcNow()) return;
        new GrantSnapshot(gate, device).RequireBinding(options);
        if (pending.GetBoolean("PendingCapacityReleased") != true || Date(pending, "Deadline") != deadline
            || Text(device, "PendingId") != Text(ledger, "PendingId")
            || Text(pending, "CompletionId") != Text(device, "CompletionId")
            || Text(pending, "Marker") != Text(device, "Marker")
            || Text(pending, "Repository") != Text(device, "Repository")
            || Number(pending, "InstallationId") != Number(device, "InstallationId")
            || Text(pending, "Profile") != Text(device, "Profile")
            || Number(device, "OutstandingTokenCount") != 0
            || (acknowledged.Value
                ? Text(pending, "State") is not ("active" or "closed") || Text(gate, "State") != "closed" || Text(device, "State") != "revoked"
                : Text(pending, "State") != "closed" || Date(device, "ExpiresAt") != deadline
                    || (Text(gate, "State"), Text(device, "State")) is not (("unacknowledged", "unacknowledged") or ("closed", "revoked"))))
            throw new IssuanceDenied();
        // Zero on the common gate is maintained only by positive issuance settlement.
        // Pending release has its own durable proof and is not token settlement.
        var release = await ReadAsync(gate.PartitionKey, "release:" + ledger.RowKey, ct) ?? throw new IssuanceDenied();
        BindRelease(ledger, release);
        var receiptKey = "revocation:" + Text(ledger, "DeviceId");
        var receipt = await ReadAsync(gate.PartitionKey, receiptKey, ct);
        var fresh = receipt is null;
        // Acknowledged owners require an already committed closure, never expiry alone.
        if (acknowledged.Value && fresh) throw new IssuanceDenied();
        if (receipt is not null)
        {
            foreach (var field in new[] { "RepositoryId", "DeviceId", "Epoch" })
                if (Text(receipt, field) != Text(ledger, field)) throw new IssuanceDenied();
            _ = Date(receipt, "RevokedAt");
        }
        receipt ??= new(gate.PartitionKey, receiptKey)
        {
            ["RepositoryId"] = Text(ledger, "RepositoryId"), ["DeviceId"] = Text(ledger, "DeviceId"),
            ["Epoch"] = Text(ledger, "Epoch"), ["RevokedAt"] = clock.GetUtcNow()
        };
        receipt["OwnerReleasedAt"] = clock.GetUtcNow();
        receipt["Reason"] = acknowledged.Value ? "revoked-acknowledged" : "expired-unacknowledged";
        device["State"] = "revoked";
        PayloadClock.FirstTerminal(device, acknowledged.Value ? Date(receipt, "RevokedAt") : deadline);
        gate.Remove("OwnerDeviceId");
        gate["State"] = "vacant";
        // Fence the authorization records. The generation-bound release proof is immutable
        // until gate-conditioned retirement, which cannot retire this completed enrollment.
        await CommitAsync(gate, [Action(pending), Action(device), Action(receipt, fresh)], ct);
    }

    // Five bounded transactions: retiring, target deletion receipt, global proof,
    // receipt deletion, retained refund. No phase treats unexplained absence as proof.
    private async Task RetireAsync(TableEntity ledger, TableEntity gate, bool enrollment, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(gate.ETag.ToString()) || Text(gate, "RepositoryId") != Text(ledger, "RepositoryId")) throw new IssuanceDenied();
        var phase = ledger.GetString("RetirementState");
        if (phase is not (null or "retiring" or "repository-deleted")) throw new IssuanceDenied();
        var targetKey = enrollment ? ledger.RowKey : Text(ledger, "AttemptRowKey");
        var releaseKey = "release:" + (enrollment ? ledger.RowKey : Text(ledger, "OperationId"));
        var target = await ReadAsync(gate.PartitionKey, targetKey, ct);
        var release = await ReadAsync(gate.PartitionKey, releaseKey, ct);
        if (target is not null) Bind(ledger, target, true);
        if (release is not null) BindRelease(ledger, release);
        if (phase != "repository-deleted")
        {
            if (release is null) throw new IssuanceDenied();
            if (release.GetBoolean("RetirementTargetDeleted") == true)
            {
                if (phase != "retiring" || target is not null
                    || Date(release, "RetirementAuditAt") != Date(ledger, "RetirementAuditAt")) throw new IssuanceDenied();
            }
            else
            {
                if (target is null) throw new IssuanceDenied();
                if (!enrollment && Text(release, "SettlementReason") != Text(target, "SettlementReason")) throw new IssuanceDenied();
                var extra = new List<TableTransactionAction>();
                var resolved = enrollment ? EnrollmentResolution(ledger, target) : IssuanceResolution(target);
                if (PayloadClock.Add(resolved, TimeSpan.FromDays(30)) is not { } audit || audit > clock.GetUtcNow()) return;
                if (enrollment)
                {
                    // No completed publisher can exist without these sticky/version proofs.
                    if (await ReadAsync(gate.PartitionKey, "device:" + Text(ledger, "DeviceId"), ct) is not null
                        || await ReadAsync("locator:" + Text(ledger, "DeviceId"), "locator", ct) is not null
                        || await ReadAsync(gate.PartitionKey, "revocation:" + Text(ledger, "DeviceId"), ct) is not null) return;
                }
                else if (ledger.GetString("PendingId") is { } pendingId)
                {
                    var prefix = Text(ledger, "RepositoryId") + "_";
                    if (!pendingId.StartsWith(prefix, StringComparison.Ordinal)
                        || !Guid.TryParseExact(pendingId[prefix.Length..], "N", out _)) throw new IssuanceDenied();
                    var pending = await ReadAsync(gate.PartitionKey, "pending:" + pendingId[prefix.Length..], ct);
                    if (pending is not null)
                    {
                        Bind(ledger, pending, false);
                        if (Text(pending, "PendingId") != pendingId) throw new IssuanceDenied();
                        if (pending.GetString("Attempt") == Text(ledger, "OperationId")
                            && pending.GetString("State") is not ("closed" or "active")
                            && Date(pending, "Deadline") > clock.GetUtcNow()) return;
                        extra.Add(Action(pending));
                    }
                }
                if (phase is null)
                {
                    ledger["RetirementState"] = "retiring";
                    ledger["RetirementAuditAt"] = audit;
                    await ReplaceAsync(ledger, ct);
                }
                else if (Date(ledger, "RetirementAuditAt") != audit) throw new IssuanceDenied();
                if (!enrollment && gate.GetString("VerifierAttempt") == Text(ledger, "OperationId")) gate.Remove("VerifierAttempt");
                release["RetirementTargetDeleted"] = true;
                release["RetirementAuditAt"] = audit;
                extra.Add(Delete(target));
                extra.Add(Action(release));
                await CommitAsync(gate, extra, ct);
            }
            // This update is authorized by a positively committed, generation-bound receipt.
            ledger["RetirementState"] = "repository-deleted";
            await ReplaceAsync(ledger, ct);
        }
        if (Date(ledger, "RetirementAuditAt") > clock.GetUtcNow()) throw new IssuanceDenied();
        // Capture a new gate BEFORE validating current global proof, never adopt a new
        // gate under the earlier ledger decision. A concurrent phase worker wins ETags.
        gate = await ReadAsync(gate.PartitionKey, "gate", ct) ?? throw new IssuanceDenied();
        var current = await ReadAsync(ledger.PartitionKey, ledger.RowKey, ct) ?? throw new IssuanceDenied();
        Bind(ledger, current, true);
        if (current.ETag != ledger.ETag || Text(current, "RetirementState") != "repository-deleted"
            || Text(gate, "RepositoryId") != Text(ledger, "RepositoryId")) throw new IssuanceDenied();
        if (await ReadAsync(gate.PartitionKey, targetKey, ct) is not null) throw new IssuanceDenied();
        release = await ReadAsync(gate.PartitionKey, releaseKey, ct);
        var deletes = new List<TableTransactionAction>();
        if (release is not null)
        {
            BindRelease(ledger, release);
            if (release.GetBoolean("RetirementTargetDeleted") != true
                || Date(release, "RetirementAuditAt") != Date(ledger, "RetirementAuditAt")) throw new IssuanceDenied();
            deletes.Add(Delete(release));
        }
        await CommitAsync(gate, deletes, ct);
        var schema = await retained.SchemaAsync(ct);
        var quota = enrollment ? schema.Enrollment : schema.Issuance;
        Decrement(quota, enrollment ? "RetainedEnrollmentCount" : "RetainedIssuanceCount");
        await BrokerTable.SubmitAsync(table, [Action(quota), Delete(ledger)], ct);
    }

    private static void BindRelease(TableEntity ledger, TableEntity release)
    {
        Bind(ledger, release, true);
        if (Number(release, "RowCredits") != Number(ledger, "RowCredits")
            || PayloadClock.Read(release, "RepositorySettledAt") is null) throw new IssuanceDenied();
    }

    private static DateTimeOffset? EnrollmentResolution(TableEntity ledger, TableEntity pending)
    {
        if (ledger.GetInt64("ReclamationVersion") != 1 || pending.GetInt64("ReclamationVersion") != 1
            || pending.GetBoolean("MintAdmissionStarted") == true || pending.ContainsKey("CompletionId")
            || pending.ContainsKey("Attempt") || pending.GetString("State") != "closed"
            || pending.GetBoolean("PendingCapacityReleased") != true) return null;
        return PayloadClock.Earlier(PayloadClock.Read(pending, "FirstTerminalAt"), PayloadClock.Read(ledger, "Deadline"));
    }

    private static DateTimeOffset? IssuanceResolution(TableEntity attempt)
    {
        if (attempt.GetBoolean("CapacityReleased") != true || attempt.GetString("State") != "settled") return null;
        var reason = attempt.GetString("SettlementReason");
        if (reason == "expired")
        {
            var expiry = PayloadClock.Read(attempt, "ExpiresAt");
            var reserved = PayloadClock.Read(attempt, "ReservedAt");
            if (attempt.GetBoolean("ScopeValid") != true || expiry is null || reserved is null || expiry <= reserved) return null;
            return PayloadClock.Add(expiry, TimeSpan.FromMinutes(5));
        }
        if (reason == "unsent" && attempt.ContainsKey("SendClaimedAt")
            || reason == "unpublished" && attempt.GetBoolean("RepositoryCharged") == true) return null;
        return reason is "revoked" or "unsent" or "unpublished" ? PayloadClock.Read(attempt, "SettledAt") : null;
    }

    private async Task ReplaceAsync(TableEntity row, CancellationToken ct)
    {
        var response = await BrokerTable.SubmitAsync(table, [Action(row)], ct);
        row.ETag = response.Value[0].Headers.ETag ?? throw new IssuanceDenied();
    }
    private static TableTransactionAction Delete(TableEntity row) => new(TableTransactionActionType.Delete, row, row.ETag);

    // #78 creates this exact shape only when the repository target was absent.
    // Published rows retain their authority state and payload metadata during redaction.
    private static bool UnpublishedTombstone(TableEntity ledger, TableEntity row)
    {
        var expected = new Dictionary<string, object>
        {
            ["State"] = "redacted", ["PayloadRedacted"] = true
        };
        foreach (var field in new[] { "Generation", "RepositoryId", "DeviceId", "Epoch", "RowCredits", "PendingId", "OperationId",
            "Purpose", "GlobalSlot", "ReservedAt", "CreatedAt", "Deadline", "PayloadPurgeAt", "ApprovalKind", "SetupVersion", "PolicyVersion", "ReclamationVersion" })
            if (ledger.TryGetValue(field, out var value)) expected[field] = value;
        if (ledger.PartitionKey == RetainedState.Enrollment && PayloadClock.Read(ledger, "Deadline") is { } deadline)
            expected["FirstTerminalAt"] = deadline;
        var properties = row.Where(p => p.Key is not ("PartitionKey" or "RowKey" or "Timestamp" or "odata.etag")).ToArray();
        return properties.Length == expected.Count
            && properties.All(p => expected.TryGetValue(p.Key, out var value) && Equals(value, p.Value));
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
        if (credits == 8 && Text(row, "PendingId") != Text(row, "RepositoryId") + "_" + id) throw new IssuanceDenied();
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
            "GlobalSlot", "ReservedAt", "PayloadPurgeAt", "CreatedAt", "Deadline", "RowCredits", "ReclamationVersion" })
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
