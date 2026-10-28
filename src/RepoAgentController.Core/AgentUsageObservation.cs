namespace RepoAgentController;

/// <summary>Whether the runtime could supply an account usage snapshot, independently of authentication.</summary>
public enum AgentUsageAvailability { Available, Unsupported, Unavailable }

/// <summary>A consumed percentage and its provider-labelled window; a missing reset or duration is unknown.</summary>
public sealed record AgentUsageWindow(string Label, decimal UsedPercent, long? DurationMinutes, DateTimeOffset? ResetsAt)
{
    public bool IsSpendControl { get; init; }
    public decimal RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

/// <summary>A separately metered account/model allowance; labels are untrusted display text.</summary>
public sealed record AgentUsageBucket(string Id, string? Name, string? Model, string? Plan, IReadOnlyList<AgentUsageWindow> Windows)
{
    public bool AdditionalUsageAvailable { get; init; }
    public bool? SpendControlReached { get; init; }
    public string? ReachedType { get; init; }
    public bool Exhausted => SpendControlReached == true || Windows.Any(window => window.RemainingPercent == 0 && (window.IsSpendControl ? SpendControlReached != false : !AdditionalUsageAvailable));
}

/// <summary>An account snapshot from the owned runtime. AccountKey preserves private provider scope and is never published.</summary>
public sealed record AgentUsageObservation(AgentUsageAvailability Availability, IReadOnlyList<AgentUsageBucket> Buckets)
{
    public string? AccountKey { get; init; }
    public bool? OrdinaryUsageAllowed { get; init; }
}

/// <summary>The runtime's account or process changed; observations from its previous context are no longer current.</summary>
public sealed record AgentUsageContextChanged(int Epoch) : AgentRuntimeEvent(Epoch);
