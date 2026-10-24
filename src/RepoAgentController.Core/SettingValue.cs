namespace RepoAgentController;

/// <summary>The effective value after the latest observation; Value is null when nothing applies.</summary>
public sealed record SettingValue(SettingState State, string? Value, string? Reason = null)
{
    /// <summary>When this controller first observed the current effective value; a retained value keeps its time.</summary>
    public DateTimeOffset Since { get; init; }
}

/// <summary>A repository setting's state after the latest observation. Absence, an invalid value and a failed read are distinct.</summary>
public enum SettingState
{
    /// <summary>Not read yet in this lifetime.</summary>
    Pending,
    /// <summary>Never read: the controller runs without repository settings.</summary>
    Disabled,
    /// <summary>GitHub confirmed that the variable does not exist.</summary>
    Absent,
    Configured,
    Invalid,
    ReadFailed
}

public static class SettingStates
{
    /// <summary>The state's name in logs, status and the recorded settings.</summary>
    public static string Name(this SettingState state) => state switch
    {
        SettingState.Pending => "pending",
        SettingState.Disabled => "disabled",
        SettingState.Absent => "absent",
        SettingState.Configured => "configured",
        SettingState.Invalid => "invalid",
        SettingState.ReadFailed => "read-failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    /// <summary>The state with this <see cref="Name"/>, or null for any other text.</summary>
    public static SettingState? Parse(string? name) => Enum.GetValues<SettingState>().Cast<SettingState?>().FirstOrDefault(state => state!.Value.Name() == name);
}
