namespace RepoAgentController;

/// <summary>The effective value after the latest observation; Value is null when nothing applies.</summary>
public sealed record SettingValue(string State, string? Value, string? Reason = null)
{
    /// <summary>When this controller first observed the current effective value; a retained value keeps its time.</summary>
    public DateTimeOffset Since { get; init; }
}
