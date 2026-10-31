namespace RepoAgent.Auth;

// The store requests a cleanup run may still make. The adapter counts the requests it dispatches, and refuses (and
// marks exceeded) one that would pass the budget. It also names the transport it reviewed the counts for, and the most
// sends one counted dispatch may make below them, which the summary reports.
public interface ICleanupBudget
{
    int Dispatches { get; }
    int BatchDispatches { get; }
    bool Exceeded { get; }
    bool CanStart(int dispatches, int batches);
    string EnvelopeScope { get; }
    int MaxSendsPerDispatch { get; }
}

// One bounded cleanup run over the retained ledgers, for at most 60 seconds. Two lanes, payload (redaction) and audit
// (reclamation and retirement), each walk both families of ledgers (pending enrollments, then issuance operations) one
// ledger per turn, from a durable cursor. A failed turn keeps its lane and family where they were, and the run goes on
// with the others. Traversal owns no lifecycle authority: every decision is redaction's or reclamation's.
public sealed class BrokerCleanup(IBrokerState state, string deploymentEpoch, ICleanupBudget budget, TimeProvider clock,
    Action<Exception>? observeFailure = null)
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private static readonly StateRange<EnrollmentLedger> Enrollments = new(IssuanceCapacity.EnrollmentScope, "pending:", "pending;");
    private static readonly StateRange<IssuanceLedger> Operations = new(IssuanceCapacity.Scope, "operation:", "operation;");
    private readonly PayloadRedaction redaction = new(state, clock);
    private readonly CapacityReclamation reclamation = new(state, deploymentEpoch, clock);
    private readonly IssuanceCapacity capacity = new(state, retainedIssuanceLimit: 0);
    private readonly bool[,] unavailable = new bool[2, 2];
    private readonly bool[,] completed = new bool[2, 2];
    private readonly Tracked<CleanupCursor>?[] cursors = new Tracked<CleanupCursor>?[2];
    private readonly long started = clock.GetTimestamp();
    private int pages, processed, deleted, removed, unsettled, failures, conflicts, unknownDeadlines;
    private DateTimeOffset? oldest;
    private string stopReason = "completed";

    // 0 when every turn succeeded, 1 when any failed.
    public async Task<int> RunAsync(CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(Deadline, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var token = linked.Token;
        try
        {
            _ = await capacity.SchemaAsync(token);
            for (var lane = 0; lane < 2; lane++) cursors[lane] = await ReadCursorAsync(lane, true, token);
            while (unavailable.Cast<bool>().Any(value => !value))
            {
                token.ThrowIfCancellationRequested();
                // Reserve the entire allocation, including observations and cursor transactions.
                if (!budget.CanStart(40, 8)) { stopReason = "request-budget"; break; }
                if (pages >= 16) { stopReason = "page-budget"; break; }
                for (var index = 0; index < 2; index++) cursors[index] = await ReadCursorAsync(index, false, token);
                var (lane, family) = Select();
                if (lane < 0) break;
                try
                {
                    await TurnAsync(lane, family, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    cursors[lane] = null; // The checkpoint may not have committed; do not report candidate ages.
                    throw;
                }
                catch (Exception ex)
                {
                    ObserveFailure(ex);
                    failures++;
                    if (ex is StateConflict) conflicts++;
                    unavailable[lane, family] = true;
                    cursors[lane] = null; // A failed checkpoint is not durable progress for age reporting.
                    stopReason = budget.Exceeded ? "budget-invariant-failed" : "stream-failed";
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { stopReason = "time-budget"; }
        catch (Exception ex)
        {
            ObserveFailure(ex);
            failures++;
            if (ex is StateConflict) conflicts++;
            stopReason = budget.Exceeded ? "budget-invariant-failed" : "storage-or-state-failed";
        }
        return failures == 0 ? 0 : 1;
    }

    private void ObserveFailure(Exception exception)
    {
        try { observeFailure?.Invoke(exception); }
        catch (Exception) { } // Optional observations cannot change cleanup authority or its exit code.
    }

    // The run's summary, as the cleanup command prints it and monitoring reads it.
    public object Summary()
    {
        var now = clock.GetUtcNow();
        var ages = new List<object>();
        for (var lane = 0; lane < 2; lane++)
            for (var family = 0; family < 2; family++)
            {
                var position = cursors[lane]?.Value.Family(family);
                ages.Add(new
                {
                    lane = lane == 0 ? "payload" : "audit",
                    family = family == 0 ? "enrollment" : "issuance",
                    cursorAgeSeconds = Age(position?.ProgressAt, now),
                    cycleAgeSeconds = Age(position?.StartedAt, now)
                });
            }
        return new
        {
            processedAllocations = processed,
            deletedRowsObserved = deleted,
            payloadFieldsRemovedObserved = removed,
            unsettledAllocationsObserved = unsettled,
            failures,
            conflicts,
            incompleteCoverage = completed.Cast<bool>().Any(value => !value),
            stopReason,
            logicalQueryPages = pages,
            sdkTableDispatches = budget.Dispatches,
            sdkBatchDispatches = budget.BatchDispatches,
            // Reference-scope calculation, not measured traffic or runtime-version detection.
            reviewedHttp11Envelope = new
            {
                scope = budget.EnvelopeScope,
                maxSendsPerSdkDispatch = budget.MaxSendsPerDispatch,
                tableRequestUpperBound = budget.MaxSendsPerDispatch * budget.Dispatches,
                batchRequestUpperBound = budget.MaxSendsPerDispatch * budget.BatchDispatches
            },
            elapsedMilliseconds = (long)clock.GetElapsedTime(started).TotalMilliseconds,
            oldestObservedOverduePayloadAt = oldest,
            oldestObservedOverduePayloadAgeSeconds = Age(oldest, now),
            unknownDeadlineCount = unknownDeadlines,
            cursorAndCycleAges = ages
        };
    }

    private (int Lane, int Family) Select()
    {
        var next = (int)cursors[0]!.Value.NextLane!.Value;
        for (var l = 0; l < 2; l++)
        {
            var lane = (next + l) % 2;
            var family = (int)cursors[lane]!.Value.NextFamily!.Value;
            for (var f = 0; f < 2; f++)
                if (!unavailable[lane, (family + f) % 2]) return (lane, (family + f) % 2);
        }
        return (-1, -1);
    }

    private async Task TurnAsync(int lane, int family, CancellationToken ct)
    {
        var cursor = cursors[lane]!;
        // Rotation is durable before work, but is never a claim of completed work.
        cursors[0]!.Value.NextLane = 1 - lane;
        cursor.Value.NextFamily = 1 - family;
        await SaveAsync(cursors[0]!, ct);
        if (lane != 0) await SaveAsync(cursor, ct);
        var position = cursor.Value.Family(family);
        pages++;
        var (ledger, continuation) = family == 0 ? await FirstAsync(Enrollments, position, ct) : await FirstAsync(Operations, position, ct);
        if (ledger is null)
        {
            if (continuation is not null)
            {
                position.Continuation = continuation;
                await SaveAsync(cursor, ct);
                return;
            }
            position.Continuation = null;
            position.LastKey = "";
            position.Cycle = checked(position.Cycle!.Value + 1);
            position.CompletedAt = clock.GetUtcNow();
            position.StartedAt = clock.GetUtcNow();
            position.ProgressAt = clock.GetUtcNow();
            await SaveAsync(cursor, ct);
            completed[lane, family] = unavailable[lane, family] = true;
            return;
        }
        var id = ledger.Id;
        var generation = RetainedAllocation.Of(ledger.Value).Generation ?? throw new InvalidDataException("Missing generation.");
        if (!Guid.TryParseExact(id, "N", out _) || !Guid.TryParseExact(generation, "N", out _))
            throw new InvalidDataException("Invalid allocation identity.");
        var before = await ObserveAsync(ledger, false, ct);
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
        var after = await ObserveAsync(ledger, true, ct);
        CountObservations(before, after);
        position.Continuation = null;
        position.LastKey = ledger.Key;
        position.ProgressAt = clock.GetUtcNow();
        await SaveAsync(cursor, ct);
    }

    // A ledger a cleanup page listed: its stored name, its idempotency or operation ID, and the ledger.
    private sealed record ListedLedger(string Key, string Id, BrokerRecord Value);

    // A single allocation per turn keeps the other lane independent of prefix length: the first ledger after the
    // position, or none and the empty page's continuation (null when the family's cycle is complete). A page the query
    // could not have answered proves nothing: at most 32 records, each of the range's scope, inside the range and after
    // the position, and a continuation the cursor can store.
    private async Task<(ListedLedger? Ledger, string? Continuation)> FirstAsync<T>(StateRange<T> range, CleanupPosition position,
        CancellationToken ct) where T : BrokerRecord, new()
    {
        var after = position.LastKey!;
        var page = await state.ListAsync(range, after, position.Continuation, ct);
        if (page.Records.Count > 32 || page.Continuation is { Length: > 8192 }) throw new InvalidDataException("Cleanup page exceeds its bound.");
        foreach (var record in page.Records)
            if (record.Key.Scope != range.Scope
                || string.CompareOrdinal(record.Key.Name, range.From) < 0
                || string.CompareOrdinal(record.Key.Name, range.To) >= 0
                || string.CompareOrdinal(record.Key.Name, after) <= 0) throw new InvalidDataException("Invalid cleanup page.");
        if (page.Records.Count == 0) return (null, page.Continuation);
        var first = page.Records[0];
        return (new(first.Key.Name, first.Key.Name[range.From.Length..], first.Read().Value), null);
    }

    // The allocation's ledger, its repository row and, for an enrollment, its controller enrollment. Before the turn the
    // ledger is the one the page listed; after it, the ledger as it is now.
    private async Task<BrokerRecord?[]> ObserveAsync(ListedLedger listed, bool refresh, CancellationToken ct)
    {
        var allocation = RetainedAllocation.Of(listed.Value);
        var repository = allocation.RepositoryId ?? throw new InvalidDataException("Missing repository.");
        _ = IssuancePolicy.Id(repository);
        if (listed.Value is IssuanceLedger operation)
        {
            if (operation.AttemptRowKey != "attempt:" + listed.Id) throw new InvalidDataException("Invalid target.");
            var current = refresh ? (await state.ReadAsync(IssuanceLedger.Key(listed.Id), ct))?.Value : operation;
            return [current, (await state.ReadAsync(IssuanceAttempt.Key(repository, listed.Id), ct))?.Value, null];
        }
        var ledger = refresh ? (await state.ReadAsync(EnrollmentLedger.Key(listed.Id), ct))?.Value : listed.Value;
        var pending = (await state.ReadAsync(PendingControllerEnrollment.Key(repository, listed.Id), ct))?.Value;
        if (!Guid.TryParseExact(allocation.EnrollmentId, "N", out _)) throw new InvalidDataException("Invalid controller enrollment.");
        var enrollment = (await state.ReadAsync(ControllerEnrollment.Key(repository, allocation.EnrollmentId), ct))?.Value;
        return [ledger, pending, enrollment];
    }

    // Counts what the turn deleted and redacted, and the oldest payload that was already due before it.
    private void CountObservations(BrokerRecord?[] before, BrokerRecord?[] after)
    {
        for (var i = 0; i < before.Length; i++)
        {
            if (before[i] is not { } record) continue;
            if (after[i] is null) deleted++;
            var payload = Payload(record);
            var remaining = after[i] is { } next ? Payload(next) : null;
            for (var field = 0; field < payload.Length; field++)
                if (payload[field] && remaining?[field] != true) removed++;
            if (!payload.Contains(true)) continue;
            if (Due(record) is not { } due) unknownDeadlines++;
            else if (due <= clock.GetUtcNow() && (oldest is null || due < oldest)) oldest = due;
        }
        if (after[0] is { } ledger && (ledger is EnrollmentLedger enrollment
                ? enrollment.PendingCapacityReleased != true : ((IssuanceLedger)ledger).CapacityState != "released")) unsettled++;
    }

    // Which payload fields the record holds: the setup secret's hash and the protected completion, the protected
    // GitHub token, or the controller secret's hash.
    private static bool[] Payload(BrokerRecord record) => record switch
    {
        EnrollmentLedger l => [l.SetupSecretHash is not null],
        PendingControllerEnrollment p => [p.SetupSecretHash is not null, p.ProtectedCompletion is not null],
        IssuanceLedger => [],
        IssuanceAttempt a => [a.ProtectedToken is not null],
        ControllerEnrollment e => [e.ControllerSecretHash is not null],
        _ => throw new InvalidOperationException("Cleanup observes only an allocation's records.")
    };

    // When the record's payload is due, as redaction decides it; null when unknown.
    private static DateTimeOffset? Due(BrokerRecord record)
    {
        switch (record)
        {
            case ControllerEnrollment e:
                var expiry = PayloadClock.After(e.ExpiresAt, Retention);
                var terminal = PayloadClock.After(e.FirstTerminalAt, Retention);
                return expiry is null || e.State == "revoked" && terminal is null ? null : PayloadClock.Earlier(expiry, terminal);
            case EnrollmentLedger l:
                return PayloadClock.Fallback(l.PayloadPurgeAt, l.Deadline, Retention);
            case PendingControllerEnrollment p:
                return PayloadClock.Fallback(p.PayloadPurgeAt, p.Deadline, Retention) is { } pendingDue
                    ? PayloadClock.Earlier(pendingDue, PayloadClock.After(p.FirstTerminalAt, Retention)) : null;
            case IssuanceAttempt a:
                if (PayloadClock.Fallback(a.PayloadPurgeAt, a.ReservedAt, Retention) is not { } attemptDue) return null;
                attemptDue = PayloadClock.Earlier(attemptDue, PayloadClock.After(a.FirstTerminalAt, Retention))!.Value;
                return a.ScopeValid == true ? PayloadClock.Earlier(attemptDue, PayloadClock.After(a.ExpiresAt, Retention)) : attemptDue;
            default:
                throw new InvalidOperationException("Cleanup observes only an allocation's payload records.");
        }
    }

    private async Task<Tracked<CleanupCursor>> ReadCursorAsync(int lane, bool initialize, CancellationToken ct)
    {
        var key = lane == 0 ? CleanupCursor.Payload : CleanupCursor.Audit;
        var read = await state.ReadAsync(key, ct);
        Tracked<CleanupCursor> tracked;
        if (read is null)
        {
            if (!initialize) throw new InvalidDataException("Missing cleanup cursor.");
            var created = new CleanupCursor { SchemaVersion = 1, NextFamily = 0, NextLane = lane == 0 ? 0 : null };
            foreach (var start in new[] { created.Enrollment, created.Issuance })
            {
                start.LastKey = "";
                start.Cycle = 0;
                start.StartedAt = clock.GetUtcNow();
                start.ProgressAt = clock.GetUtcNow();
            }
            tracked = new(key, created, null);
            await SaveAsync(tracked, ct);
        }
        else tracked = new(key, read.Value, read.Version);
        var cursor = tracked.Value;
        if (cursor.SchemaVersion != 1 || cursor.NextFamily is not (0 or 1)
            || lane == 0 && cursor.NextLane is not (0 or 1)) throw new InvalidDataException("Invalid cleanup cursor.");
        for (var family = 0; family < 2; family++)
        {
            var position = cursor.Family(family);
            var last = position.LastKey;
            if (position.Continuation is { Length: > 8192 }) throw new InvalidDataException("Oversized continuation.");
            var prefix = family == 0 ? Enrollments.From : Operations.From;
            if (last is null || last.Length != 0 && (!last.StartsWith(prefix, StringComparison.Ordinal)
                    || !Guid.TryParseExact(last[prefix.Length..], "N", out _))
                || position.Cycle is not >= 0
                || PayloadClock.Read(position.StartedAt) is null || PayloadClock.Read(position.ProgressAt) is null)
                throw new InvalidDataException("Invalid cleanup continuation.");
        }
        return tracked;
    }

    // Writes the cursor at the version it was read or last written at, or adds it.
    private async Task SaveAsync(Tracked<CleanupCursor> cursor, CancellationToken ct)
    {
        var transaction = new StateTransaction();
        cursor.Write(transaction);
        cursor.Advance((await state.CommitAsync(transaction, ct))[0] ?? throw new InvalidDataException("Missing cursor version."));
    }

    private static double? Age(DateTimeOffset? value, DateTimeOffset now) => value is null ? null : Math.Max(0, (now - value.Value).TotalSeconds);
}
