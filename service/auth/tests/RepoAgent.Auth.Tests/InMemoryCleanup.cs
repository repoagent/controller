namespace RepoAgent.Auth.Tests;

// The cleanup command's request budget over a broker state, as the Table adapter's counting transport keeps it: every
// read, listing page and transaction is one dispatch and every transaction is also a batch. A request that would pass
// the budget is refused, the budget is marked exceeded, and the core sees the store as unavailable. Its envelope is its
// own, not the Table transport's, so the summary shows it reports the budget's.
internal sealed class CleanupBudget(IBrokerState inner, int dispatches = 128, int batches = 32) : IBrokerState, ICleanupBudget
{
    private readonly Lock sync = new();
    public int Dispatches { get; private set; }
    public int BatchDispatches { get; private set; }
    public bool Exceeded { get; private set; }
    public bool CanStart(int requests, int batchRequests) => Dispatches + requests <= dispatches && BatchDispatches + batchRequests <= batches;
    public string EnvelopeScope => "in-memory broker state";
    public int MaxSendsPerDispatch => 3;

    public Task<Versioned<T>?> ReadAsync<T>(StateKey<T> key, CancellationToken ct) where T : BrokerRecord, new()
    {
        Dispatch(false);
        return inner.ReadAsync(key, ct);
    }

    public Task<IReadOnlyList<StateVersion?>> CommitAsync(StateTransaction transaction, CancellationToken ct)
    {
        Dispatch(true);
        return inner.CommitAsync(transaction, ct);
    }

    public Task<StatePage<T>> ListAsync<T>(StateRange<T> range, string after, string? continuation, CancellationToken ct)
        where T : BrokerRecord, new()
    {
        Dispatch(false);
        return inner.ListAsync(range, after, continuation, ct);
    }

    private void Dispatch(bool batch)
    {
        lock (sync)
        {
            if (!CanStart(1, batch ? 1 : 0))
            {
                Exceeded = true;
                throw new BrokerStateUnavailable("Cleanup request budget invariant exceeded.");
            }
            Dispatches++;
            if (batch) BatchDispatches++;
        }
    }
}

// What one cleanup run returned: its exit code, the summary the command prints, and the requests it made.
internal sealed record CleanupRun(int Exit, System.Text.Json.JsonElement Summary, CleanupBudget Budget)
{
    public string StopReason => Summary.GetProperty("stopReason").GetString()!;
    public int Int(string property) => Summary.GetProperty(property).GetInt32();
}

// Holds the first transaction that matches until the test releases it; every other transaction passes. It runs after
// the state's earlier BeforeCommit hook.
internal sealed class CommitPause
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CommitPause(InMemoryBrokerState state, Func<StateTransaction, bool> matches)
    {
        var previous = state.BeforeCommit;
        var paused = 0;
        state.BeforeCommit = async transaction =>
        {
            if (previous is not null) await previous(transaction);
            if (!matches(transaction) || Interlocked.CompareExchange(ref paused, 1, 0) != 0) return;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(12), Xunit.TestContext.Current.CancellationToken);
        };
    }
}

internal static class StateTransactions
{
    // Whether the transaction writes a record with this stored property value.
    public static bool Writes(this StateTransaction transaction, string property, object value) =>
        transaction.Writes.Any(write => write.Row is { } row && row.TryGetValue(property, out var stored) && Equals(stored, value));

    // Whether the transaction writes a record that stores this property, with any value.
    public static bool Stores(this StateTransaction transaction, string property) =>
        transaction.Writes.Any(write => write.Row is { } row && row.ContainsKey(property));

    // Whether the transaction writes (adds, replaces or deletes) the record of this name.
    public static bool Touches(this StateTransaction transaction, string name) =>
        transaction.Writes.Any(write => write.Key.Name == name);

    public static bool InScope(this StateTransaction transaction, string scope) =>
        transaction.Writes.Count > 0 && transaction.Writes[0].Key.Scope == scope;
}

// A manual clock whose timers fire only when the test fires them, for the cleanup run's 60-second deadline. Time is the
// wrapped clock's.
internal sealed class TimerClock(TimeProvider clock) : TimeProvider
{
    private readonly List<ManualTimer> timers = [];

    public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

    // The due times of the timers that are still live.
    public IReadOnlyList<TimeSpan> Due
    {
        get { lock (timers) return [.. timers.Select(timer => timer.DueTime)]; }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, dueTime);
        lock (timers) timers.Add(timer);
        return timer;
    }

    // Fires every live timer once.
    public void Fire()
    {
        ManualTimer[] due;
        lock (timers) due = [.. timers];
        foreach (var timer in due) timer.Callback(timer.State);
    }

    private sealed class ManualTimer(TimerClock owner, TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public TimeSpan DueTime { get; private set; } = dueTime;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            return true;
        }

        public void Dispose()
        {
            lock (owner.timers) owner.timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
