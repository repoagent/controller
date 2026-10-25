using System.Diagnostics;

namespace RepoAgentController;

/// <summary>
/// Durably retains controller-owned GitHub operations, EYES receipts and dashboard updates, until each succeeds or
/// fails permanently. Delivery is independent of native input acceptance: nothing here resends agent input, and
/// agent-authored replies stay on the agent's direct gh.
/// </summary>
public sealed class GitHubOutbox : IAsyncDisposable
{
    static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(15), LongestRetry = TimeSpan.FromMinutes(15),
        UnsignalledRateLimit = TimeSpan.FromMinutes(1);
    readonly IDiscussions discussions;
    readonly AppIdentity identity;
    readonly IControllerState store;
    readonly TimeProvider clock;
    readonly IControllerLog log;
    readonly object sync = new();
    readonly string repository;
    readonly List<OutboxOperation> operations;
    DateTimeOffset? pausedUntil;
    readonly CancellationTokenSource stop = new();
    readonly WakeLoop loop;
    Task running = Task.CompletedTask;
    // The owned dashboard found or created during this lifetime; only the delivering attempt touches these.
    DashboardDiscussion? dashboard;
    string? dashboardCategory;
    bool dashboardRejected;

    /// <summary>Resumes the operations an earlier lifetime retained for <paramref name="repository"/>, whose App is <paramref name="identity"/>.</summary>
    public GitHubOutbox(string repository, IDiscussions discussions, AppIdentity identity, IControllerState store, TimeProvider clock, IControllerLog log)
    {
        loop = new(clock);
        (this.discussions, this.identity, this.store, this.clock, this.log) = (discussions, identity, store, clock, log);
        var retained = store.ReadOutbox();
        if (retained is not null && !string.Equals(retained.Repository, repository, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("GitHub outbox belongs to a different repository or unsupported version; preserve it for manual inspection");
        this.repository = retained?.Repository ?? repository;
        pausedUntil = retained?.PausedUntil;
        operations = [.. retained?.Operations ?? []];
    }

    /// <summary>Retains an EYES receipt for accepted input unless the App already reacted to that source.</summary>
    public void Receipt(string subjectId, string url, bool hasEyesReceipt)
    {
        if (hasEyesReceipt)
        {
            log.Write("receipt-reused", url);
            return;
        }
        Enqueue(new ReceiptOperation(subjectId, url));
    }

    /// <summary>
    /// Retains the dashboard's complete title and body. A newer publication supersedes a pending one, so after an outage
    /// only the latest status and heartbeat are written. After a permanent failure this lifetime publishes no more.
    /// </summary>
    public void Dashboard(string category, string title, string body)
    {
        lock (sync) if (dashboardRejected) return;
        Enqueue(new DashboardOperation(category, title, body));
    }

    /// <summary>
    /// Retains an operation. A pending operation with the same key is superseded in place and keeps its retry
    /// schedule, so later status updates can replace stale ones; operations with other keys are never affected.
    /// </summary>
    void Enqueue(OutboxOperation operation)
    {
        lock (sync)
        {
            var index = operations.FindIndex(retained => retained.Key == operation.Key);
            var existing = index < 0 ? null : operations[index];
            operation = operation with
            {
                Attempts = existing?.Attempts ?? 0, DueAt = existing?.DueAt ?? clock.GetUtcNow(), Uncertain = existing?.Uncertain ?? false,
                LastError = existing?.LastError
            };
            if (existing is null) operations.Add(operation);
            else operations[index] = operation;
            Save();
            loop.Signal();
        }
    }

    /// <summary>Delivers due operations in the background until disposal; retained work survives restarts.</summary>
    public void Start(CancellationToken ct) => running = loop.Run(CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token), async token =>
    {
        try { await Deliver(token); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A failed save leaves the in-memory schedule current; keep delivering and retry the save later.
            log.Write("outbox-error", $"{error.Message}; retained operations stay queued");
        }
        lock (sync) return (NextDue() is { } due ? TimeSpan.FromTicks(Math.Max(0, (due - clock.GetUtcNow()).Ticks)) : Timeout.InfiniteTimeSpan, true);
    });

    /// <summary>Attempts each due operation once. True only when nothing failed and nothing remains retained.</summary>
    public async Task<bool> Flush(CancellationToken ct)
    {
        var delivered = await Deliver(ct);
        lock (sync) return delivered && operations.Count == 0;
    }

    /// <summary>Stops background delivery, then attempts each due operation once more within <paramref name="ct"/>.</summary>
    public async Task<bool> Finish(CancellationToken ct)
    {
        await stop.CancelAsync();
        await running;
        return await Flush(ct);
    }

    async Task<bool> Deliver(CancellationToken ct)
    {
        var delivered = true;
        while (true)
        {
            OutboxOperation? operation;
            lock (sync)
            {
                var now = clock.GetUtcNow();
                operation = pausedUntil > now ? null : operations.FirstOrDefault(retained => retained.DueAt <= now);
            }
            if (operation is null) return delivered;
            delivered &= await Attempt(operation, ct);
        }
    }

    async Task<bool> Attempt(OutboxOperation operation, CancellationToken ct)
    {
        var key = operation.Key;
        var attempt = operation.Attempts + 1;
        var publication = operation is DashboardOperation;
        // How the operation's log lines name its kind, target and subject.
        var (kind, url, subject) = operation switch
        {
            ReceiptOperation receipt => ("receipt", receipt.Url, receipt.SubjectId),
            DashboardOperation update => ("dashboard", dashboard?.Url ?? $"dashboard category '{update.Category}'", ""),
            UnsupportedOperation other => (other.Kind, other.Url, other.SubjectId),
            _ => throw new UnreachableException()
        };
        (string Kind, string Url)? outcome;
        try { outcome = await Execute(operation, kind, attempt, operation.Uncertain, ct); }
        catch (OperationCanceledException)
        {
            // An interrupted request may have reached GitHub: reconcile it before writing again.
            lock (sync)
            {
                Update(key, current => current with { Uncertain = true });
                try { Save(); } catch (IOException) { }
            }
            throw;
        }
        catch (Exception error)
        {
            var failure = error as GitHubRequestException ?? new GitHubRequestException(GitHubFailure.Transient, error.Message, uncertain: true);
            var context = $"{url}{(subject.Length == 0 ? "" : " subject=" + subject)} attempt={attempt} {failure.Kind}: {failure.Message}";
            if (failure.Kind == GitHubFailure.Permanent)
            {
                lock (sync)
                {
                    if (ReferenceEquals(Find(key), operation)) operations.Remove(operation);
                    // Configuration or permission must change first; republishing every heartbeat would never succeed.
                    if (publication && Find(key) is { } superseding) operations.Remove(superseding);
                    dashboardRejected |= publication;
                    Save();
                }
                log.Write(kind + "-error", context + (publication
                    ? "; permanent failure removed from retry; dashboard updates stop until the controller restarts"
                    : "; permanent failure removed from retry; accepted input is not replayed"));
                return false;
            }
            var now = clock.GetUtcNow();
            var due = now + RetryDelay.Doubling(FirstRetry, LongestRetry, attempt);
            lock (sync)
            {
                if (failure.Kind == GitHubFailure.RateLimited)
                {
                    // Rate limits apply to the installation: pause every retained operation, not only this one.
                    var signalled = failure.NotBefore(now) ?? now + UnsignalledRateLimit;
                    if (signalled > due) due = signalled;
                    pausedUntil = due;
                }
                var uncertain = operation.Uncertain || failure.Uncertain;
                Update(key, current => current with { Attempts = attempt, DueAt = due, Uncertain = uncertain, LastError = failure.Message });
                Save();
            }
            log.Write(kind + "-error", context + $"; retained for retry at {due:O}");
            return false;
        }
        lock (sync)
        {
            // A superseding operation enqueued during this attempt still needs its own delivery.
            if (ReferenceEquals(Find(key), operation)) operations.Remove(operation);
            Save();
        }
        if (outcome is { } logged) log.Write(logged.Kind, logged.Url);
        return true;
    }

    // Each kind owns how an uncertain earlier attempt is reconciled before it writes again. Returns what to log, if anything.
    async Task<(string Kind, string Url)?> Execute(OutboxOperation operation, string kind, int attempt, bool uncertain, CancellationToken ct)
    {
        switch (operation)
        {
            case ReceiptOperation receipt:
                // A lost response is resolved by reading the App's actual reaction, not by request identifiers.
                if (uncertain && await discussions.HasEyesReceipt(receipt.SubjectId, ct)) return ("receipt-reconciled", receipt.Url);
                await discussions.AddEyesReceipt(receipt.SubjectId, ct);
                return ("receipt", receipt.Url);
            case DashboardOperation (var category, var title, var body):
                if (dashboard is { } known && dashboardCategory == category)
                {
                    await discussions.UpdateDashboard(known.Id, title, body, ct);
                    return attempt > 1 ? ("dashboard", known.Url) : null; // Routine heartbeats are not logged; a recovery is.
                }
                var target = await discussions.DashboardCategory(category, ct);
                // Finding first also settles an uncertain earlier creation, so a lost response never adds a second dashboard.
                var found = await FindDashboard(target, ct);
                if (found is { } existing) await discussions.UpdateDashboard(existing.Id, title, body, ct);
                var owned = found ?? await discussions.CreateDashboard(target, title, body, ct);
                (dashboard, dashboardCategory) = (owned, category);
                return (found is null ? "dashboard-created" : "dashboard", owned.Url);
            default:
                throw new GitHubRequestException(GitHubFailure.Permanent, $"unsupported outbox operation kind '{kind}'");
        }
    }

    // The oldest Discussion in the category that is the App's dashboard. Participants cannot author as the App, so a copy of
    // the marker in their Discussion is not it. Pages after the dashboard's are not read.
    async Task<DashboardDiscussion?> FindDashboard(DashboardLocation at, CancellationToken ct)
    {
        await foreach (var candidate in discussions.CategoryDiscussions(at, ct))
            if (identity.IsDashboard(candidate.Author, candidate.Body)) return new(candidate.Id, candidate.Url);
        return null;
    }

    OutboxOperation? Find(string key) => operations.FirstOrDefault(retained => retained.Key == key);

    // Applies a change to whichever operation holds the key now: the attempted one, or one that superseded it meanwhile.
    void Update(string key, Func<OutboxOperation, OutboxOperation> change)
    {
        var index = operations.FindIndex(retained => retained.Key == key);
        if (index >= 0) operations[index] = change(operations[index]);
    }

    DateTimeOffset? NextDue()
    {
        if (operations.Count == 0) return null;
        var due = operations.Min(retained => retained.DueAt);
        return pausedUntil > due ? pausedUntil : due;
    }

    void Save() => store.WriteOutbox(new(repository, pausedUntil, [.. operations]));

    public async ValueTask DisposeAsync()
    {
        // Retained operations stay durable; shutdown never discards or acknowledges them.
        await stop.CancelAsync();
        await running;
        stop.Dispose();
    }
}
