using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>Excludes checkout updates from all admitted native work, including uncertain work.</summary>
sealed class CheckoutRefresh : IAsyncDisposable
{
    readonly ManagedCheckout checkout;
    readonly string path;
    readonly object journalLock = new();
    readonly SemaphoreSlim exclusion = new(1), wake = new(0, 1);
    readonly CancellationTokenSource stop = new();
    readonly JsonObject journal, active;
    readonly Dictionary<(string Thread, string Turn), (bool Done, long Sequence)> turns = [];
    Task worker = Task.CompletedTask;
    Codex? codex;
    bool blocked, eligible;
    long sequence;
    int epoch = 1;

    public CheckoutRefresh(ManagedCheckout checkout, string statePath, bool priorSessions, bool pendingDiscussion)
    {
        this.checkout = checkout; path = statePath + ".refresh.json";
        ManagedCheckout.CheckPath(path);
        if (Path.Exists(path + ".tmp")) throw new IOException("Incomplete refresh journal; preserve it for manual recovery");
        var existed = File.Exists(path);
        journal = existed ? JsonNode.Parse(ManagedCheckout.ReadPrivate(path))!.AsObject() : Json.Object(
            ("version", 1), ("root", checkout.Root), ("owner", checkout.Owner), ("repositoryId", checkout.RepositoryId),
            ("requested", 0L), ("completed", 0L), ("active", new JsonObject()));
        if (journal["version"]?.GetValue<int>() != 1 || journal.Text("root") != checkout.Root ||
            journal.Text("owner") != checkout.Owner || journal["repositoryId"]?.GetValue<long>() != checkout.RepositoryId ||
            journal["active"] is not JsonObject entries || Generation("completed") < 0 || Generation("requested") < Generation("completed"))
            throw new IOException("Refresh journal binding or format differs; manual recovery required");
        active = entries;
        blocked = journal["blocked"]?.GetValue<bool>() == true || active.Count != 0 || journal["applying"] is not null ||
            (journal["running"]?.GetValue<int>() ?? 0) != 0 || pendingDiscussion || (!existed && priorSessions);
        if (blocked) journal["reason"] = "Native activity or checkout update is uncertain; inspect state before recovery";
        Save();
    }

    long Generation(string key) => journal[key]!.GetValue<long>();
    bool Busy => active.Count != 0 || turns.Values.Any(turn => !turn.Done);
    void Save()
    {
        journal["blocked"] = blocked;
        journal["running"] = turns.Values.Count(turn => !turn.Done);
        try { ManagedCheckout.SavePrivate(path, journal.ToJsonString()); }
        catch { blocked = true; throw; }
    }
    void CheckAdmission()
    {
        if (blocked || journal["applying"] is not null) throw new IOException("Checkout/native state uncertain; manual recovery required");
    }
    void Fault(string reason)
    {
        blocked = true; journal["reason"] = reason;
        Save();
    }
    void Signal() { if (wake.CurrentCount == 0) try { wake.Release(); } catch (SemaphoreFullException) { } }

    public long RepositoryId => checkout.RepositoryId;
    public string DefaultRef => checkout.DefaultRef;

    public void Request()
    {
        lock (journalLock)
        {
            journal["requested"] = checked(Generation("requested") + 1);
            eligible = true; Save();
        }
        Signal();
    }

    public async Task Start(Codex native, TimeSpan interval, CancellationToken ct)
    {
        codex = native;
        Request();
        await exclusion.WaitAsync(ct);
        try { await Reconcile(ct); }
        finally { exclusion.Release(); }
        worker = Run(interval, ct);
    }

    async Task Run(TimeSpan interval, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                if (!await wake.WaitAsync(interval, linked.Token)) Request();
                await exclusion.WaitAsync(linked.Token);
                try { await Reconcile(linked.Token); }
                finally { exclusion.Release(); }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (journalLock) { blocked = true; try { Fault(error.Message); } catch { } }
            Log.Write("refresh-blocked", error.Message);
        }
    }

    // Caller holds exclusion. Native callbacks only take journalLock; no RPC awaits under that lock.
    async Task Reconcile(CancellationToken ct)
    {
        long generation;
        lock (journalLock)
        {
            CheckAdmission();
            if (!eligible || Busy || Generation("requested") == Generation("completed")) return;
            generation = Generation("requested"); eligible = false;
        }
        try
        {
            var changed = await checkout.Refresh((previous, target) =>
            {
                lock (journalLock)
                {
                    CheckAdmission();
                    if (Busy) throw new IOException("Native work appeared while checkout was excluded");
                    journal["applying"] = Json.Object(("previous", previous), ("target", target), ("generation", generation));
                    Save();
                }
            }, ct);
            if (changed) await codex!.Reload(ct);
            lock (journalLock)
            {
                if (blocked) throw new IOException("Native refresh became uncertain");
                journal.Remove("applying"); journal.Remove("reason");
                journal["completed"] = generation; Save();
            }
        }
        catch (ManagedCheckout.DeferredRefresh error)
        {
            lock (journalLock)
            {
                if (journal["applying"] is not null) Fault(error.Message);
                else { journal["reason"] = error.Message; Save(); }
            }
            Log.Write("refresh-pending", error.Message);
        }
        catch (Exception error)
        {
            lock (journalLock) { blocked = true; try { Fault(error.Message); } catch { } }
            throw;
        }
    }

    public async Task<Admission> Admit(CancellationToken ct)
    {
        await exclusion.WaitAsync(ct);
        try
        {
            await Reconcile(ct); // Eligible refresh gets the next global idle boundary before new work.
            lock (journalLock)
            {
                CheckAdmission();
                if (active.Count >= 64) throw new IOException("Too many unresolved native admissions");
                var record = Json.Object(("thread", ""), ("turn", ""), ("since", ++sequence), ("confirmed", false));
                var id = Guid.NewGuid().ToString("N"); active[id] = record; Save();
                return new(this, id, record);
            }
        }
        catch { exclusion.Release(); throw; }
    }

    public sealed class Admission(CheckoutRefresh owner, string id, JsonObject record) : IDisposable
    {
        bool confirmed;
        public void Bind(string thread)
        {
            lock (owner.journalLock)
            {
                owner.CheckAdmission();
                if (!Valid(thread)) throw new IOException("Malformed native thread identity");
                record["thread"] = thread; owner.Save();
            }
        }
        public void Accepted(string thread, string turn)
        {
            lock (owner.journalLock)
            {
                owner.CheckAdmission();
                if (record.Text("thread") != thread || !Valid(turn)) throw new IOException("Native acknowledgement identity differs");
                var key = (thread, turn);
                if (owner.turns.TryGetValue(key, out var seen) && seen.Done && seen.Sequence <= record["since"]!.GetValue<long>())
                    throw new IOException("Native reused a completed turn identity");
                owner.Track(key, seen.Done, seen.Sequence);
                record["turn"] = turn; owner.Save();
            }
        }
        public void Confirm()
        {
            lock (owner.journalLock)
            {
                owner.CheckAdmission();
                if (record.Text("turn").Length == 0) throw new IOException("Native admission has no acknowledgement");
                record["confirmed"] = true; owner.Complete(); owner.Save(); confirmed = true;
            }
            owner.Signal();
        }
        public void Dispose()
        {
            try
            {
                if (!confirmed) lock (owner.journalLock) owner.Fault("Native admission interrupted; inspect pending operation " + id);
            }
            finally { owner.exclusion.Release(); }
        }
    }

    static bool Valid(string id) => id.Length is > 0 and <= 256 && !id.Any(char.IsControl);
    void Track((string Thread, string Turn) key, bool done, long observed)
    {
        if (!turns.ContainsKey(key) && turns.Count >= 256)
        {
            var old = turns.FirstOrDefault(pair => pair.Value.Done && !active.Any(operation =>
                operation.Value!.Text("thread") == pair.Key.Thread && (operation.Value!.Text("turn") == pair.Key.Turn || operation.Value!.Text("turn") == "")));
            if (old.Key == default) throw new IOException("Native correlation capacity exhausted; manual recovery required");
            turns.Remove(old.Key);
        }
        turns[key] = (done, observed);
    }
    void Complete()
    {
        foreach (var operation in active.ToArray())
            if (operation.Value!["confirmed"]?.GetValue<bool>() == true &&
                turns.TryGetValue((operation.Value!.Text("thread"), operation.Value!.Text("turn")), out var turn) && turn.Done)
                active.Remove(operation.Key);
    }
    public void NewEpoch(int value)
    {
        lock (journalLock)
        {
            if (Busy || blocked) throw new IOException("Cannot replace native transport with unresolved work");
            epoch = value; turns.Clear();
        }
    }
    public void Observe(int sourceEpoch, string method, JsonNode? parameters)
    {
        lock (journalLock)
        {
            if (sourceEpoch != epoch) return;
            try
            {
                if (method == "disconnect") { Fault("Native transport disconnected; completion unknown"); return; }
                if (method is not ("turn/started" or "turn/completed")) return;
                var thread = parameters?.Text("threadId") ?? "";
                var turn = parameters?["turn"]?.Text("id") ?? "";
                var key = (thread, turn);
                if (!Valid(thread) || !Valid(turn)) throw new IOException("Malformed native lifecycle identity");
                var known = turns.TryGetValue(key, out var previous);
                var awaiting = active.Any(item => item.Value!.Text("thread") == thread && item.Value!.Text("turn") == "");
                if (!known && !awaiting) throw new IOException("Unmatched native lifecycle event");
                if (method == "turn/completed" && known && previous.Done) return;
                if (method == "turn/started" && known && previous.Done) throw new IOException("Native restarted a completed turn identity");
                Track(key, method == "turn/completed" || previous.Done, ++sequence);
                Complete(); Save();
            }
            catch (Exception error) { blocked = true; try { Fault(error.Message); } catch { } }
        }
        Signal();
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync(); await worker; stop.Dispose();
    }
}
