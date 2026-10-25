namespace RepoAgentController;

public static class RetryDelay
{
    /// <summary>Doubles from <paramref name="first"/> with each 1-based attempt, up to <paramref name="longest"/>.</summary>
    public static TimeSpan Doubling(TimeSpan first, TimeSpan longest, int attempt) =>
        TimeSpan.FromTicks(Math.Min(longest.Ticks, first.Ticks << Math.Min(attempt - 1, 16)));
}

/// <summary>
/// A background loop of passes. After each pass it sleeps for the delay the pass returns; when the pass allows it, a signal
/// ends that sleep early. A signal raised during a pass ends the following sleep at once.
/// </summary>
public sealed class WakeLoop(TimeProvider clock)
{
    readonly Lock sync = new();
    TaskCompletionSource wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Signal() { lock (sync) wake.TrySetResult(); }

    /// <summary>Runs passes until <paramref name="lifetime"/> is cancelled, then disposes it.</summary>
    public async Task Run(CancellationTokenSource lifetime, Func<CancellationToken, Task<(TimeSpan Sleep, bool Wakeable)>> pass)
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
                var (sleep, wakeable) = await pass(ct);
                using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delay = Task.Delay(sleep, clock, timer.Token);
                await (wakeable ? Task.WhenAny(signal, delay) : delay);
                await timer.CancelAsync();
                ct.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { lifetime.Dispose(); }
    }
}
