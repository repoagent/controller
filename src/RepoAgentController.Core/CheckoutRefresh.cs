namespace RepoAgentController;

/// <summary>Excludes checkout updates from all admitted native work, including uncertain work.</summary>
public sealed class CheckoutRefresh : IAgentTurnJournal, ICheckoutAdmission, IAsyncDisposable
{
    static readonly TimeSpan DeferredRetry = TimeSpan.FromSeconds(5);
    readonly IRepositoryCheckout checkout;
    readonly CheckoutBinding binding;
    readonly IRefreshJournal store;
    readonly TimeProvider clock;
    readonly IControllerLog log;
    readonly object journalLock = new();
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2213", Justification = "Native reader callbacks may still signal after the worker stops; these managed-only semaphores never create wait handles.")]
    readonly SemaphoreSlim exclusion = new(1), wake = new(0, 1);
    readonly CancellationTokenSource stop = new();
    readonly RefreshJournal journal;
    readonly OrderedDictionary<string, RefreshAdmission> active;
    readonly Dictionary<(string Thread, string Turn), (bool Done, long Sequence)> turns = [];
    Task worker = Task.CompletedTask;
    AgentSessions? sessions;
    bool blocked, eligible;
    long sequence;
    long? retryStarted;
    int epoch = 1;

    public CheckoutRefresh(IRepositoryCheckout checkout, CheckoutBinding binding, IRefreshJournal store, bool priorSessions, bool pendingDiscussion,
        TimeProvider clock, IControllerLog log)
    {
        this.checkout = checkout; this.binding = binding; this.store = store; this.clock = clock; this.log = log;
        var recorded = store.Read();
        journal = recorded ?? new RefreshJournal(binding.Root, binding.Owner, binding.RepositoryId);
        if (journal.Root != binding.Root || journal.Owner != binding.Owner || journal.RepositoryId != binding.RepositoryId ||
            journal.Completed < 0 || journal.Requested < journal.Completed)
            throw new IOException(RefreshJournal.Unusable);
        active = journal.Active;
        // The running count is read only when nothing before it blocks; one no writer leaves is then refused.
        blocked = journal.Blocked || active.Count != 0 || journal.Applying is not null ||
            (journal.RunningUnreadable ? throw new IOException(RefreshJournal.Unusable) : journal.Running != 0) || pendingDiscussion ||
            (recorded is null && priorSessions);
        if (blocked)
        {
            store.PreserveStartupEvidence();
            if (journal.ReasonAsRead is null)
                journal.Reason = "Native activity or checkout update is uncertain; inspect state before recovery";
        }
        Save();
    }

    bool Busy => active.Count != 0 || turns.Values.Any(turn => !turn.Done);
    void Save()
    {
        journal.Blocked = blocked;
        if (!blocked) journal.Running = turns.Values.Count(turn => !turn.Done);
        try { store.Write(journal); }
        catch { blocked = true; throw; }
    }
    void CheckAdmission()
    {
        if (blocked || journal.Applying is not null) throw new IOException("Checkout/native state uncertain; manual recovery required");
    }
    void Fault(string reason)
    {
        blocked = true; journal.Reason = reason;
        Save();
    }
    void Signal() { if (wake.CurrentCount == 0) try { wake.Release(); } catch (SemaphoreFullException) { } }

    public long RepositoryId => binding.RepositoryId;
    public string DefaultRef => binding.DefaultRef;
    public bool AdmissionBlocked { get { lock (journalLock) return blocked; } }

    public void Request()
    {
        lock (journalLock)
        {
            journal.Requested = checked(journal.Requested + 1);
            eligible = true; Save();
            retryStarted = null;
        }
        Signal();
    }

    public async Task Start(AgentSessions native, TimeSpan interval, CancellationToken ct)
    {
        sessions = native;
        lock (journalLock)
            if (blocked)
            {
                log.Write("refresh-blocked", "Checkout/native state uncertain; native admission is held. Stop the controller, runtime and ALL agent tools before using recover-checkout; applying or malformed evidence requires an isolated fresh instance.");
                return;
            }
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
                if (!await Woken(interval, linked.Token)) Request();
                await exclusion.WaitAsync(linked.Token);
                try { await Reconcile(linked.Token); }
                finally { exclusion.Release(); }
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (journalLock) { blocked = true; try { Fault(error.Message); } catch { } }
            log.Write("refresh-blocked", error.Message);
        }
    }

    /// <summary>True once signalled; false when <paramref name="interval"/> passes on the clock first.</summary>
    async Task<bool> Woken(TimeSpan interval, CancellationToken ct)
    {
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var signalled = wake.WaitAsync(waiting.Token);
        await Task.WhenAny(signalled, Task.Delay(interval, clock, waiting.Token));
        await waiting.CancelAsync();
        try { await signalled; return true; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    // Caller holds exclusion. Native callbacks only take journalLock; no RPC awaits under that lock.
    async Task Reconcile(CancellationToken ct)
    {
        long generation;
        lock (journalLock)
        {
            CheckAdmission();
            if (!eligible || Busy || journal.Requested == journal.Completed ||
                (retryStarted is { } started && clock.GetElapsedTime(started) < DeferredRetry)) return;
            generation = journal.Requested; eligible = false;
        }
        try
        {
            var changed = await checkout.Refresh((previous, target) =>
            {
                lock (journalLock)
                {
                    CheckAdmission();
                    if (Busy) throw new IOException("Native work appeared while checkout was excluded");
                    journal.Applying = new(previous, target, generation);
                    Save();
                }
            }, ct);
            if (changed) await sessions!.Reload(ct);
            lock (journalLock)
            {
                if (blocked) throw new IOException("Native refresh became uncertain");
                journal.Applying = null; journal.Reason = null;
                journal.Completed = generation; Save();
            }
        }
        catch (CheckoutDeferred error)
        {
            lock (journalLock)
            {
                if (journal.Applying is not null) Fault(error.Message);
                else { eligible = true; retryStarted = clock.GetTimestamp(); journal.Reason = error.Message; Save(); }
            }
            log.Write("refresh-pending", error.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown before applying leaves the checkout unchanged and the generation requested for the next lifetime.
            lock (journalLock) if (journal.Applying is not null) { blocked = true; try { Fault("Refresh interrupted while applying"); } catch { } }
            throw;
        }
        catch (Exception error)
        {
            lock (journalLock) { blocked = true; try { Fault(error.Message); } catch { } }
            throw;
        }
    }

    public async Task<ICheckoutTurn> Admit(CancellationToken ct)
    {
        await exclusion.WaitAsync(ct);
        try
        {
            await Reconcile(ct); // Eligible refresh gets the next global idle boundary before new work.
            lock (journalLock)
            {
                CheckAdmission();
                if (active.Count >= 64) throw new IOException("Too many unresolved native admissions");
                var record = new RefreshAdmission(++sequence);
                var id = Guid.NewGuid().ToString("N"); active[id] = record; Save();
                return new Admission(this, id, record);
            }
        }
        catch { exclusion.Release(); throw; }
    }

    public async Task<bool> CommitAtIdle(Func<CancellationToken, Task> commit, CancellationToken ct)
    {
        await exclusion.WaitAsync(ct);
        try
        {
            lock (journalLock) if (blocked || journal.Applying is not null) return false;
            await Reconcile(ct);
            lock (journalLock)
            {
                CheckAdmission();
                if (Busy || journal.Requested != journal.Completed) return false;
            }
            await commit(ct);
            lock (journalLock) { CheckAdmission(); if (Busy) throw new IOException("Native activity changed during idle commit"); }
            return true;
        }
        finally { exclusion.Release(); }
    }

    sealed class Admission(CheckoutRefresh owner, string id, RefreshAdmission record) : ICheckoutTurn
    {
        bool confirmed;
        public void Bind(string thread)
        {
            lock (owner.journalLock)
            {
                owner.CheckAdmission();
                if (!Valid(thread)) throw new IOException("Malformed native thread identity");
                record.Thread = thread; owner.Save();
            }
        }
        public void Accepted(string thread, string turn)
        {
            lock (owner.journalLock)
            {
                owner.CheckAdmission();
                if (record.Thread != thread || !Valid(turn)) throw new IOException("Native acknowledgement identity differs");
                var key = (thread, turn);
                if (owner.turns.TryGetValue(key, out var seen) && seen.Done && seen.Sequence <= record.Since)
                    throw new IOException("Native reused a completed turn identity");
                owner.Track(key, seen.Done, seen.Sequence);
                record.Turn = turn; owner.Save();
            }
        }
        /// <summary>Releases an admission that loaded or created a session but sent it no input.</summary>
        public void Abandon()
        {
            lock (owner.journalLock)
            {
                owner.CheckAdmission();
                if (record.Turn.Length != 0) throw new IOException("Accepted native admission cannot be abandoned");
                owner.active.Remove(id); owner.Save(); confirmed = true;
            }
            owner.Signal();
        }
        /// <summary>
        /// Releases an admission whose input the native may have accepted without acknowledgement. It stays recorded, so
        /// the checkout remains excluded and that thread's lifecycle events still match, until a later acknowledged turn on
        /// the same thread completes. Only the newest such admission per thread is kept, since it settles the same way.
        /// </summary>
        public void Unconfirmed()
        {
            lock (owner.journalLock)
            {
                owner.CheckAdmission();
                var thread = record.Thread;
                if (thread.Length == 0) owner.active.Remove(id); // No session, so no native turn can hold the input.
                else
                {
                    foreach (var older in owner.active.Where(operation => operation.Key != id && Unsettled(operation.Value, thread)).ToArray())
                        owner.active.Remove(older.Key);
                    record.Unconfirmed = true;
                }
                owner.Save(); confirmed = true;
            }
            owner.Signal();
        }
        public void Confirm()
        {
            lock (owner.journalLock)
            {
                owner.CheckAdmission();
                if (record.Turn.Length == 0) throw new IOException("Native admission has no acknowledgement");
                record.Confirmed = true; owner.Complete(); owner.Save(); confirmed = true;
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

    /// <summary>The native thread and turn a turn lifecycle event names, and whether it completed the turn; null for any other
    /// event. A missing turn ID maps to "", which no valid identity is.</summary>
    static (string Thread, string Turn, bool Completed)? Lifecycle(AgentRuntimeEvent change)
    {
        (string Thread, string Turn, bool Completed)? identity = change switch
        {
            AgentTurnStarted started => (started.SessionId, started.TurnId ?? "", false),
            AgentTurnCompleted ended => (ended.SessionId, ended.TurnId ?? "", true),
            _ => null
        };
        if (identity is (var thread, var turn, _) && (!Valid(thread) || !Valid(turn))) throw new IOException("Malformed native lifecycle identity");
        return identity;
    }

    void Track((string Thread, string Turn) key, bool done, long observed)
    {
        if (!turns.ContainsKey(key) && turns.Count >= 256)
        {
            var old = turns.FirstOrDefault(pair => pair.Value.Done && !active.Any(operation =>
                operation.Value.Thread == pair.Key.Thread && (operation.Value.Turn == pair.Key.Turn || operation.Value.Turn == "")));
            if (old.Key == default) throw new IOException("Native correlation capacity exhausted; manual recovery required");
            turns.Remove(old.Key);
        }
        turns[key] = (done, observed);
    }
    void Complete()
    {
        foreach (var operation in active.ToArray())
            if (operation.Value.Confirmed && turns.TryGetValue((operation.Value.Thread, operation.Value.Turn), out var turn) && turn.Done)
            {
                active.Remove(operation.Key);
                // A thread runs one turn at a time, so an earlier unacknowledged input to it cannot outlast this completion.
                var since = operation.Value.Since;
                foreach (var earlier in active.Where(item => Unsettled(item.Value, operation.Value.Thread) && item.Value.Since < since).ToArray())
                    active.Remove(earlier.Key);
            }
    }
    static bool Unsettled(RefreshAdmission operation, string thread) => operation.Unconfirmed && operation.Thread == thread;
    public void NewEpoch(int epoch)
    {
        lock (journalLock)
        {
            if (Busy || blocked) throw new IOException("Cannot replace native transport with unresolved work");
            this.epoch = epoch; turns.Clear();
        }
    }
    public void Observe(AgentRuntimeEvent change)
    {
        lock (journalLock)
        {
            if (change.Epoch != epoch) return;
            try
            {
                if (change is AgentRuntimeDisconnected) { Fault("Native transport disconnected; completion unknown"); return; }
                if (Lifecycle(change) is not (var thread, var turn, var completed)) return;
                var key = (thread, turn);
                var known = turns.TryGetValue(key, out var previous);
                var awaiting = active.Any(item => item.Value.Thread == thread && item.Value.Turn == "");
                if (!known && !awaiting) throw new IOException("Unmatched native lifecycle event");
                if (completed && known && previous.Done) return;
                if (!completed && known && previous.Done) throw new IOException("Native restarted a completed turn identity");
                Track(key, completed || previous.Done, ++sequence);
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
