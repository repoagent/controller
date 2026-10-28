using System.Diagnostics;

namespace RepoAgentController;

/// <summary>Observes immediately, then after each completed interval, or flushes output and exits after one observation.</summary>
internal sealed class ControllerPolling(Bridge bridge, GitHubOutbox outbox, TimeProvider clock, IControllerLog log, TimeSpan interval)
{
    /// <summary>Stops scheduling on <paramref name="stop"/> while entered observations and output use <paramref name="activity"/>.</summary>
    public async Task<int> Run(bool once, CancellationToken stop, CancellationToken activity)
    {
        do
        {
            stop.ThrowIfCancellationRequested();
            var pollStarted = Stopwatch.GetTimestamp();
            var succeeded = await bridge.Poll(activity);
            log.Write("poll-completed", $"success={succeeded} elapsed-ms={Stopwatch.GetElapsedTime(pollStarted).TotalMilliseconds:F0}");
            if (once) return await outbox.Flush(activity) && succeeded ? 0 : 1;
            await Task.Delay(interval, clock, stop);
        } while (!stop.IsCancellationRequested);
        return 0;
    }
}
