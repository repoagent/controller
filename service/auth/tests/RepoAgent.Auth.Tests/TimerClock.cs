namespace RepoAgent.Auth.Tests;

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
