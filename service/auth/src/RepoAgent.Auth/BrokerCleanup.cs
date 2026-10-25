using System.Diagnostics;
using Azure;
using Azure.Data.Tables;

namespace RepoAgent.Auth;

// Traversal owns no lifecycle authority. All decisions remain in the two facades.
internal sealed class BrokerCleanup(TableClient table, BrokerOptions options, CleanupRequestBudget budget, TimeProvider clock)
{
    private const string Partition = RetainedState.Enrollment;
    private readonly BrokerPayloadRedaction redaction = new(table, clock);
    private readonly BrokerCapacityReclamation reclamation = new(table, options, clock);
    private readonly bool[,] unavailable = new bool[2, 2];
    private readonly bool[,] completed = new bool[2, 2];
    private readonly TableEntity?[] cursors = new TableEntity?[2];
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private int pages, processed, deleted, removed, unsettled, failures, conflicts, unknownDeadlines;
    private DateTimeOffset? oldest;
    private string stopReason = "completed";

    internal async Task<int> RunAsync(CancellationToken ct)
    {
        try
        {
            _ = await new RetainedState(table).SchemaAsync(ct);
            for (var lane = 0; lane < 2; lane++) cursors[lane] = await ReadCursorAsync(lane, true, ct);
            while (unavailable.Cast<bool>().Any(value => !value))
            {
                ct.ThrowIfCancellationRequested();
                // Reserve the entire allocation, including observations and cursor transactions.
                if (!budget.CanStart(40, 8)) { stopReason = "request-budget"; break; }
                if (pages >= 16) { stopReason = "page-budget"; break; }
                for (var index = 0; index < 2; index++) cursors[index] = await ReadCursorAsync(index, false, ct);
                var (lane, family) = Select();
                if (lane < 0) break;
                try
                {
                    await TurnAsync(lane, family, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cursors[lane] = null; // The checkpoint may not have committed; do not report candidate ages.
                    throw;
                }
                catch (Exception ex)
                {
                    failures++;
                    if (ex is RequestFailedException { Status: 409 or 412 }) conflicts++;
                    unavailable[lane, family] = true;
                    cursors[lane] = null; // A failed checkpoint is not durable progress for age reporting.
                    stopReason = budget.Exceeded ? "budget-invariant-failed" : "stream-failed";
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { stopReason = "time-budget"; }
        catch (Exception ex)
        {
            failures++;
            if (ex is RequestFailedException { Status: 409 or 412 }) conflicts++;
            stopReason = budget.Exceeded ? "budget-invariant-failed" : "storage-or-state-failed";
        }
        return failures == 0 ? 0 : 1;
    }

    internal object Summary()
    {
        var now = clock.GetUtcNow();
        var ages = new List<object>();
        for (var lane = 0; lane < 2; lane++)
            for (var family = 0; family < 2; family++)
            {
                var cursor = cursors[lane];
                var prefix = Field(family);
                ages.Add(new
                {
                    lane = lane == 0 ? "payload" : "audit", family = family == 0 ? "enrollment" : "issuance",
                    cursorAgeSeconds = Age(cursor?.GetDateTimeOffset(prefix + "ProgressAt"), now),
                    cycleAgeSeconds = Age(cursor?.GetDateTimeOffset(prefix + "StartedAt"), now)
                });
            }
        return new
        {
            processedAllocations = processed, deletedRowsObserved = deleted, payloadFieldsRemovedObserved = removed,
            unsettledAllocationsObserved = unsettled, failures, conflicts,
            incompleteCoverage = completed.Cast<bool>().Any(value => !value), stopReason,
            logicalQueryPages = pages, sdkTableDispatches = budget.SdkDispatches, sdkBatchDispatches = budget.SdkBatchDispatches,
            // Reference-scope calculation, not measured traffic or runtime-version detection.
            reviewedHttp11Envelope = new
            {
                scope = "Azure.Core1.62.0/.NET10.0.13/direct HTTP1.x/no handler authentication",
                maxSendsPerSdkDispatch = 4,
                tableRequestUpperBound = 4 * budget.SdkDispatches,
                batchRequestUpperBound = 4 * budget.SdkBatchDispatches
            },
            elapsedMilliseconds = elapsed.ElapsedMilliseconds,
            oldestObservedOverduePayloadAt = oldest, oldestObservedOverduePayloadAgeSeconds = Age(oldest, now),
            unknownDeadlineCount = unknownDeadlines, cursorAndCycleAges = ages
        };
    }

    private (int Lane, int Family) Select()
    {
        var next = (int)cursors[0]!.GetInt64("NextLane")!;
        for (var l = 0; l < 2; l++)
        {
            var lane = (next + l) % 2;
            var family = (int)cursors[lane]!.GetInt64("NextFamily")!;
            for (var f = 0; f < 2; f++)
                if (!unavailable[lane, (family + f) % 2]) return (lane, (family + f) % 2);
        }
        return (-1, -1);
    }

    private async Task TurnAsync(int lane, int family, CancellationToken ct)
    {
        var cursor = cursors[lane]!;
        // Rotation is durable before work, but is never a claim of completed work.
        cursors[0]!["NextLane"] = (long)(1 - lane);
        cursor["NextFamily"] = (long)(1 - family);
        await SaveAsync(cursors[0]!, ct);
        if (lane != 0) await SaveAsync(cursor, ct);
        var prefix = family == 0 ? "pending:" : "operation:";
        var end = family == 0 ? "pending;" : "operation;";
        var partition = family == 0 ? RetainedState.Enrollment : RetainedState.Issuance;
        var field = Field(family);
        var last = cursor.GetString(field + "LastKey")!;
        var filter = TableClient.CreateQueryFilter($"PartitionKey eq {partition} and RowKey ge {prefix} and RowKey lt {end} and RowKey gt {last}");
        await using var enumerator = table.QueryAsync<TableEntity>(filter, maxPerPage: 32, cancellationToken: ct)
            .AsPages(cursor.GetString(field + "Continuation"), pageSizeHint: 32).GetAsyncEnumerator(ct);
        pages++;
        var hasPage = await enumerator.MoveNextAsync();
        var values = hasPage ? enumerator.Current.Values : Array.Empty<TableEntity>();
        if (values.Count > 32) throw new InvalidDataException("Cleanup page exceeds its bound.");
        if (values.Count == 0)
        {
            if (hasPage && enumerator.Current.ContinuationToken is { } continuation)
            {
                if (continuation.Length > 8192) throw new InvalidDataException("Oversized continuation.");
                cursor[field + "Continuation"] = continuation;
                await SaveAsync(cursor, ct);
                return;
            }
            cursor.Remove(field + "Continuation");
            cursor[field + "LastKey"] = "";
            cursor[field + "Cycle"] = checked(cursor.GetInt64(field + "Cycle")!.Value + 1);
            cursor[field + "CompletedAt"] = clock.GetUtcNow();
            cursor[field + "StartedAt"] = clock.GetUtcNow();
            cursor[field + "ProgressAt"] = clock.GetUtcNow();
            await SaveAsync(cursor, ct);
            completed[lane, family] = unavailable[lane, family] = true;
            return;
        }
        // A single allocation per turn keeps the other lane independent of prefix length.
        var ledger = values[0];
        BrokerTable.Validate(ledger);
        if (ledger.PartitionKey != partition || !ledger.RowKey.StartsWith(prefix, StringComparison.Ordinal)
            || string.CompareOrdinal(ledger.RowKey, last) <= 0) throw new InvalidDataException("Invalid cleanup page.");
        var id = ledger.RowKey[prefix.Length..];
        var generation = ledger.GetString("Generation") ?? throw new InvalidDataException("Missing generation.");
        if (!Guid.TryParseExact(id, "N", out _) || !Guid.TryParseExact(generation, "N", out _))
            throw new InvalidDataException("Invalid allocation identity.");
        var before = await ObserveAsync(ledger, family, false, ct);
        if (lane == 0)
        {
            if (family == 0) await redaction.RedactEnrollmentAsync(id, generation, ct);
            else await redaction.RedactIssuanceAsync(id, generation, ct);
        }
        else
        {
            if (family == 0) await reclamation.ReclaimEnrollmentAsync(id, generation, ct);
            else await reclamation.ReclaimIssuanceAsync(id, generation, ct);
        }
        processed++;
        var after = await ObserveAsync(ledger, family, true, ct);
        CountObservations(before, after, family);
        cursor.Remove(field + "Continuation");
        cursor[field + "LastKey"] = ledger.RowKey;
        cursor[field + "ProgressAt"] = clock.GetUtcNow();
        await SaveAsync(cursor, ct);
    }

    private async Task<TableEntity?[]> ObserveAsync(TableEntity ledger, int family, bool refresh, CancellationToken ct)
    {
        var repository = ledger.GetString("RepositoryId") ?? throw new InvalidDataException("Missing repository.");
        _ = IssuancePolicy.Id(repository);
        var partition = "repository:" + repository;
        var target = family == 0 ? ledger.RowKey : ledger.GetString("AttemptRowKey");
        if (target is null || family == 1 && target != "attempt:" + ledger.RowKey["operation:".Length..])
            throw new InvalidDataException("Invalid target.");
        var current = refresh ? await BrokerTable.ReadAsync(table, ledger.PartitionKey, ledger.RowKey, ct) : ledger;
        var row = await BrokerTable.ReadAsync(table, partition, target, ct);
        TableEntity? device = null;
        if (family == 0)
        {
            var id = ledger.GetString("DeviceId");
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid device.");
            device = await BrokerTable.ReadAsync(table, partition, "device:" + id, ct);
        }
        return [current, row, device];
    }

    private void CountObservations(TableEntity?[] before, TableEntity?[] after, int family)
    {
        var fields = new[] { "RecoveryHash", "ProtectedCompletion", "ProtectedToken", "SecretHash" };
        for (var i = 0; i < before.Length; i++)
        {
            if (before[i] is not { } row) continue;
            if (after[i] is null) deleted++;
            foreach (var field in fields)
                if (row.ContainsKey(field) && (after[i] is null || !after[i]!.ContainsKey(field))) removed++;
            if (!fields.Any(row.ContainsKey)) continue;
            DateTimeOffset? due;
            if (i == 2)
            {
                due = PayloadClock.After(row, "ExpiresAt", TimeSpan.FromHours(24));
                var terminal = PayloadClock.After(row, "FirstTerminalAt", TimeSpan.FromHours(24));
                due = due is null || row.GetString("State") == "revoked" && terminal is null
                    ? null : PayloadClock.Earlier(due, terminal);
            }
            else
            {
                due = PayloadClock.Fallback(row, family == 0 ? "Deadline" : "ReservedAt", TimeSpan.FromHours(24));
                due = due is null ? null : PayloadClock.Earlier(due, PayloadClock.After(row, "FirstTerminalAt", TimeSpan.FromHours(24)));
                if (due is not null && family == 1 && row.GetBoolean("ScopeValid") == true)
                    due = PayloadClock.Earlier(due, PayloadClock.After(row, "ExpiresAt", TimeSpan.FromHours(24)));
            }
            if (due is null) unknownDeadlines++;
            else if (due <= clock.GetUtcNow() && (oldest is null || due < oldest)) oldest = due;
        }
        if (after[0] is { } ledger && (family == 0
                ? ledger.GetBoolean("PendingCapacityReleased") != true : ledger.GetString("CapacityState") != "released")) unsettled++;
    }

    private async Task<TableEntity> ReadCursorAsync(int lane, bool initialize, CancellationToken ct)
    {
        var key = lane == 0 ? "cleanup:payload-v1" : "cleanup:audit-v1";
        var row = await BrokerTable.ReadAsync(table, Partition, key, ct);
        if (row is null)
        {
            if (!initialize) throw new InvalidDataException("Missing cleanup cursor.");
            row = new TableEntity(Partition, key) { ["SchemaVersion"] = 1L, ["NextFamily"] = 0L };
            if (lane == 0) row["NextLane"] = 0L;
            foreach (var prefix in new[] { "Enrollment", "Issuance" })
            {
                row[prefix + "LastKey"] = "";
                row[prefix + "Cycle"] = 0L;
                row[prefix + "StartedAt"] = clock.GetUtcNow();
                row[prefix + "ProgressAt"] = clock.GetUtcNow();
            }
            var response = await BrokerTable.AddAsync(table, row, ct);
            row.ETag = response.Headers.ETag ?? throw new InvalidDataException("Missing cursor ETag.");
        }
        if (row.GetInt64("SchemaVersion") != 1 || row.GetInt64("NextFamily") is not (0 or 1)
            || lane == 0 && row.GetInt64("NextLane") is not (0 or 1)) throw new InvalidDataException("Invalid cleanup cursor.");
        for (var family = 0; family < 2; family++)
        {
            var prefix = Field(family);
            var last = row.GetString(prefix + "LastKey");
            if (row.GetString(prefix + "Continuation") is { Length: > 8192 })
                throw new InvalidDataException("Oversized continuation.");
            var keyPrefix = family == 0 ? "pending:" : "operation:";
            if (last is null || last.Length != 0 && (!last.StartsWith(keyPrefix, StringComparison.Ordinal)
                    || !Guid.TryParseExact(last[keyPrefix.Length..], "N", out _))
                || row.GetInt64(prefix + "Cycle") is not >= 0
                || PayloadClock.Read(row, prefix + "StartedAt") is null || PayloadClock.Read(row, prefix + "ProgressAt") is null)
                throw new InvalidDataException("Invalid cleanup continuation.");
        }
        return row;
    }

    private async Task SaveAsync(TableEntity row, CancellationToken ct)
    {
        var response = await BrokerTable.SubmitAsync(table,
            [new(TableTransactionActionType.UpdateReplace, row, row.ETag)], ct);
        row.ETag = response.Value[0].Headers.ETag ?? throw new InvalidDataException("Missing cursor ETag.");
    }
    private static string Field(int family) => family == 0 ? "Enrollment" : "Issuance";
    private static double? Age(DateTimeOffset? value, DateTimeOffset now) => value is null ? null : Math.Max(0, (now - value.Value).TotalSeconds);
}
