using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>
/// Durably retains controller-owned GitHub operations, EYES receipts and dashboard updates, until each succeeds or
/// fails permanently. Delivery is independent of native input acceptance: nothing here resends agent input, and
/// agent-authored replies stay on the agent's direct gh.
/// </summary>
sealed class GitHubOutbox : IAsyncDisposable
{
    static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(15), LongestRetry = TimeSpan.FromMinutes(15),
        UnsignalledRateLimit = TimeSpan.FromMinutes(1);
    readonly string path;
    readonly GitHub github;
    readonly TimeProvider clock;
    readonly object sync = new();
    readonly JsonObject state;
    readonly CancellationTokenSource stop = new();
    TaskCompletionSource wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    Task running = Task.CompletedTask;
    // The owned dashboard found or created during this lifetime; only the delivering attempt touches these.
    (string Id, string Url)? dashboard;
    string? dashboardCategory;
    bool dashboardRejected;

    public GitHubOutbox(string path, GitHub github, TimeProvider clock)
    {
        this.path = path;
        this.github = github;
        this.clock = clock;
        state = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject
        {
            ["version"] = 1, ["repository"] = github.Repository, ["pausedUntil"] = null, ["operations"] = new JsonArray()
        };
        if (state["version"]?.GetValue<int>() != 1 || !string.Equals(state.Text("repository"), github.Repository, StringComparison.OrdinalIgnoreCase) ||
            state["operations"] is not JsonArray)
            throw new InvalidOperationException("GitHub outbox belongs to a different repository or unsupported version; preserve it for manual inspection");
        try
        {
            // Read every retained field once, as delivery will, so a damaged file stops startup instead of the loop.
            _ = Time(state["pausedUntil"]);
            foreach (var operation in Operations.Select(item => item!.AsObject()))
                _ = (operation.Text("key")[0], operation.Text("kind"), operation.Text("subjectId"), operation.Text("url"),
                    operation.Text("category"), operation.Text("title"), operation.Text("body"), operation["attempts"]!.GetValue<int>(), operation["uncertain"]!.GetValue<bool>(), Time(operation["dueAt"]));
        }
        catch (Exception error) { throw new InvalidOperationException("GitHub outbox contains an unreadable operation; preserve it for manual inspection", error); }
    }

    JsonArray Operations => state["operations"]!.AsArray();

    /// <summary>Retains an EYES receipt for accepted input unless the App already reacted to that source.</summary>
    public void Receipt(JsonNode source)
    {
        if (GitHub.HasViewerReceipt(source))
        {
            Log.Write("receipt-reused", source.Text("url"));
            return;
        }
        Enqueue(new JsonObject
        {
            ["key"] = "receipt:" + source.Text("id"), ["kind"] = "receipt", ["subjectId"] = source.Text("id"), ["url"] = source.Text("url")
        });
    }

    /// <summary>
    /// Retains the dashboard's complete title and body. A newer publication supersedes a pending one, so after an outage
    /// only the latest status and heartbeat are written. After a permanent failure this lifetime publishes no more.
    /// </summary>
    public void Dashboard(string category, string title, string body)
    {
        lock (sync) if (dashboardRejected) return;
        Enqueue(new JsonObject
        {
            ["key"] = "dashboard", ["kind"] = "dashboard", ["subjectId"] = "", ["url"] = "", ["category"] = category, ["title"] = title, ["body"] = body
        });
    }

    /// <summary>
    /// Retains an operation. A pending operation with the same key is superseded in place and keeps its retry
    /// schedule, so later status updates can replace stale ones; operations with other keys are never affected.
    /// </summary>
    void Enqueue(JsonObject operation)
    {
        lock (sync)
        {
            var existing = Find(operation.Text("key"));
            foreach (var field in new[] { "attempts", "dueAt", "uncertain", "lastError" }) operation[field] = existing?[field]?.DeepClone();
            operation["attempts"] ??= 0;
            operation["dueAt"] ??= clock.GetUtcNow().ToString("O");
            operation["uncertain"] ??= false;
            if (existing is null) Operations.Add(operation);
            else Operations[Operations.IndexOf(existing)] = operation;
            Save();
            wake.TrySetResult();
        }
    }

    /// <summary>Delivers due operations in the background until disposal; retained work survives restarts.</summary>
    public void Start(CancellationToken ct) => running = Run(CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token));

    /// <summary>Attempts each due operation once. True only when nothing failed and nothing remains retained.</summary>
    public async Task<bool> Flush(CancellationToken ct)
    {
        var delivered = await Deliver(ct);
        lock (sync) return delivered && Operations.Count == 0;
    }

    /// <summary>Stops background delivery, then attempts each due operation once more within <paramref name="ct"/>.</summary>
    public async Task<bool> Finish(CancellationToken ct)
    {
        await stop.CancelAsync();
        await running;
        return await Flush(ct);
    }

    async Task Run(CancellationTokenSource lifetime)
    {
        var ct = lifetime.Token;
        try
        {
            while (true)
            {
                Task signal;
                lock (sync)
                {
                    if (wake.Task.IsCompleted) wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    signal = wake.Task;
                }
                try { await Deliver(ct); }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    // A failed save leaves the in-memory schedule current; keep delivering and retry the save later.
                    Log.Write("outbox-error", $"{error.Message}; retained operations stay queued");
                }
                TimeSpan delay;
                lock (sync) delay = NextDue() is { } due ? TimeSpan.FromTicks(Math.Max(0, (due - clock.GetUtcNow()).Ticks)) : Timeout.InfiniteTimeSpan;
                using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await Task.WhenAny(signal, Task.Delay(delay, clock, timer.Token));
                await timer.CancelAsync();
                ct.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { lifetime.Dispose(); }
    }

    async Task<bool> Deliver(CancellationToken ct)
    {
        var delivered = true;
        while (true)
        {
            JsonObject? operation;
            lock (sync)
            {
                var now = clock.GetUtcNow();
                operation = Time(state["pausedUntil"]) > now ? null : Operations.OfType<JsonObject>().FirstOrDefault(o => Time(o["dueAt"]) <= now);
            }
            if (operation is null) return delivered;
            delivered &= await Attempt(operation, ct);
        }
    }

    async Task<bool> Attempt(JsonObject operation, CancellationToken ct)
    {
        string key, kind, subject, url;
        bool uncertain;
        int attempt;
        JsonObject request;
        lock (sync)
        {
            (key, kind, subject, url) = (operation.Text("key"), operation.Text("kind"), operation.Text("subjectId"), operation.Text("url"));
            uncertain = operation["uncertain"]!.GetValue<bool>();
            attempt = operation["attempts"]!.GetValue<int>() + 1;
            request = operation.DeepClone().AsObject();
        }
        if (kind == "dashboard") url = dashboard?.Url ?? $"dashboard category '{request.Text("category")}'";
        (string Kind, string Url)? outcome;
        try { outcome = await Execute(request, attempt, uncertain, ct); }
        catch (OperationCanceledException)
        {
            // An interrupted request may have reached GitHub: reconcile it before writing again.
            lock (sync)
            {
                if (Find(key) is { } current) current["uncertain"] = true;
                try { Save(); } catch (IOException) { }
            }
            throw;
        }
        catch (Exception error)
        {
            var failure = error as GitHubWriteException ?? new GitHubWriteException(GitHubFailure.Transient, error.Message, uncertain: true);
            var context = $"{url}{(subject.Length == 0 ? "" : " subject=" + subject)} attempt={attempt} {failure.Kind}: {failure.Message}";
            if (failure.Kind == GitHubFailure.Permanent)
            {
                lock (sync)
                {
                    if (Find(key) == operation) Operations.Remove(operation);
                    // Configuration or permission must change first; republishing every heartbeat would never succeed.
                    if (kind == "dashboard" && Find(key) is { } superseding) Operations.Remove(superseding);
                    dashboardRejected |= kind == "dashboard";
                    Save();
                }
                Log.Write(kind + "-error", context + (kind == "dashboard"
                    ? "; permanent failure removed from retry; dashboard updates stop until the controller restarts"
                    : "; permanent failure removed from retry; accepted input is not replayed"));
                return false;
            }
            var now = clock.GetUtcNow();
            var due = now + TimeSpan.FromTicks(Math.Min(LongestRetry.Ticks, FirstRetry.Ticks << Math.Min(attempt - 1, 16)));
            lock (sync)
            {
                if (failure.Kind == GitHubFailure.RateLimited)
                {
                    // Rate limits apply to the installation: pause every retained operation, not only this one.
                    var signalled = failure.NotBefore(now) ?? now + UnsignalledRateLimit;
                    if (signalled > due) due = signalled;
                    state["pausedUntil"] = due.ToString("O");
                }
                if (Find(key) is { } current)
                {
                    current["attempts"] = attempt;
                    current["dueAt"] = due.ToString("O");
                    current["uncertain"] = uncertain || failure.Uncertain;
                    current["lastError"] = failure.Message;
                }
                Save();
            }
            Log.Write(kind + "-error", context + $"; retained for retry at {due:O}");
            return false;
        }
        lock (sync)
        {
            // A superseding operation enqueued during this attempt still needs its own delivery.
            if (Find(key) == operation) Operations.Remove(operation);
            Save();
        }
        if (outcome is { } logged) Log.Write(logged.Kind, logged.Url);
        return true;
    }

    // Each kind owns how an uncertain earlier attempt is reconciled before it writes again. Returns what to log, if anything.
    async Task<(string Kind, string Url)?> Execute(JsonObject operation, int attempt, bool uncertain, CancellationToken ct)
    {
        switch (operation.Text("kind"))
        {
            case "receipt":
                // A lost response is resolved by reading the App's actual reaction, not by request identifiers.
                if (uncertain && await github.HasReceipt(operation.Text("subjectId"), ct)) return ("receipt-reconciled", operation.Text("url"));
                await github.AddReceipt(operation.Text("subjectId"), ct);
                return ("receipt", operation.Text("url"));
            case "dashboard":
                var (category, title, body) = (operation.Text("category"), operation.Text("title"), operation.Text("body"));
                if (dashboard is { } known && dashboardCategory == category)
                {
                    await github.UpdateDashboard(known.Id, title, body, ct);
                    return attempt > 1 ? ("dashboard", known.Url) : null; // Routine heartbeats are not logged; a recovery is.
                }
                var target = await github.DashboardCategory(category, ct);
                // Finding first also settles an uncertain earlier creation, so a lost response never adds a second dashboard.
                var found = await github.FindDashboard(target.Category, ct);
                if (found is { } existing) await github.UpdateDashboard(existing.Id, title, body, ct);
                var owned = found ?? await github.CreateDashboard(target, title, body, ct);
                (dashboard, dashboardCategory) = (owned, category);
                return (found is null ? "dashboard-created" : "dashboard", owned.Url);
            default:
                throw new GitHubWriteException(GitHubFailure.Permanent, $"unsupported outbox operation kind '{operation.Text("kind")}'");
        }
    }

    JsonObject? Find(string key) => Operations.OfType<JsonObject>().FirstOrDefault(o => o.Text("key") == key);

    DateTimeOffset? NextDue()
    {
        if (Operations.Count == 0) return null;
        var due = Operations.Min(o => Time(o!["dueAt"]));
        var paused = Time(state["pausedUntil"]);
        return paused > due ? paused : due;
    }

    static DateTimeOffset Time(JsonNode? value) => value is null ? DateTimeOffset.MinValue
        : DateTimeOffset.Parse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    void Save()
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    public async ValueTask DisposeAsync()
    {
        // Retained operations stay durable; shutdown never discards or acknowledges them.
        await stop.CancelAsync();
        await running;
        stop.Dispose();
    }
}
