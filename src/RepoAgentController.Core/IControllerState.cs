namespace RepoAgentController;

/// <summary>What the controller keeps beside its state file across lifetimes.</summary>
public interface IControllerState
{
    /// <summary>The repository settings this controller last recorded, or null when none are recorded or the record is unreadable.</summary>
    RecordedSettings? ReadSettings();

    /// <summary>Records the observed repository settings, replacing the earlier record. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when they cannot be recorded.</summary>
    void WriteSettings(RecordedSettings settings);
}

/// <summary>Each repository setting's latest observation, by name: its state, effective value, since when and why it is unusable.</summary>
public sealed record RecordedSettings(IReadOnlyDictionary<string, SettingValue> Settings);
