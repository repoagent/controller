namespace RepoAgentController;

/// <summary>Bounded observational reads of the owned runtime, independent of work admission and authentication.</summary>
sealed class AgentUsageCollection(IAgentRuntime runtime, TimeProvider clock) : IAsyncDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan FreshnessBudget = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ReadBudget = TimeSpan.FromSeconds(10);
    readonly Lock gate = new();
    readonly CancellationTokenSource stop = new();
    UsageSnapshot current = new(null, null, "Usage unavailable; no agent account observation has been collected.");
    Task worker = Task.CompletedTask;
    long context;
    int epoch;
    public event Action? Changed;
    public UsageSnapshot Current { get { lock (gate) return current; } }

    public void Start(CancellationToken ct)
    {
        runtime.Changed += Observe;
        worker = Run(ct);
    }

    void Observe(AgentRuntimeEvent change)
    {
        if (change is not (AgentRuntimeDisconnected or AgentUsageContextChanged)) return;
        lock (gate)
        {
            if (change.Epoch < epoch) return;
            epoch = change.Epoch;
            context++;
            current = current with { Problem = "Usage stale; the runtime or account changed. Values belong to the previous observation." };
        }
        Changed?.Invoke();
    }

    async Task Run(CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        while (!lifetime.IsCancellationRequested)
        {
            long reading;
            lock (gate) reading = context;
            try
            {
                using var deadline = new CancellationTokenSource(ReadBudget, clock);
                using var read = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, deadline.Token);
                var observation = await runtime.ReadUsage(read.Token).WaitAsync(read.Token);
                lock (gate) if (reading == context) current = observation.Availability != AgentUsageAvailability.Available && current.Observation?.Availability == AgentUsageAvailability.Available
                    ? current with { Problem = observation.Availability == AgentUsageAvailability.Unsupported ? "Usage unsupported after refresh; last-known values are stale." : "Usage unavailable after refresh; last-known values are stale." }
                    : new(observation, clock.GetUtcNow(), observation.Availability switch
                    {
                        AgentUsageAvailability.Unsupported => "Usage unsupported by this runtime or account.",
                        AgentUsageAvailability.Unavailable => "Usage unavailable from the runtime.",
                        _ => null
                    });
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            catch (Exception) { lock (gate) if (reading == context) current = current with { Problem = "Usage refresh failed; last-known values are stale." }; }
            Changed?.Invoke();
            try { await Task.Delay(RefreshInterval, clock, lifetime.Token); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        runtime.Changed -= Observe;
        await stop.CancelAsync();
        await worker;
        stop.Dispose();
    }
}

sealed record UsageSnapshot(AgentUsageObservation? Observation, DateTimeOffset? ObservedAt, string? Problem)
{
    public bool Fresh(DateTimeOffset now) => Problem is null && Observation?.Availability == AgentUsageAvailability.Available &&
        ObservedAt is { } at && now >= at && now - at < AgentUsageCollection.FreshnessBudget;
    public bool Exhausted => Observation is { Availability: AgentUsageAvailability.Available, OrdinaryUsageAllowed: not true } value &&
        value.Buckets.Any(bucket => bucket.Exhausted);
    public DateTimeOffset? Reset => Observation?.Buckets.Where(bucket => bucket.Exhausted).SelectMany(bucket => bucket.Windows)
        .Where(window => window.RemainingPercent == 0).Select(window => window.ResetsAt).Min();
}
