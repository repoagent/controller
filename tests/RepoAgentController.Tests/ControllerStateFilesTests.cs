using Xunit;

namespace RepoAgentController.Tests;

// The one golden round trip per persisted format (owner decision): a recorded file is read and written back unchanged.
// Status reports this record under "settings" verbatim, so an unchanged file also keeps that part of status unchanged.
public sealed class ControllerStateFilesTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "controller-state-files-" + Guid.NewGuid().ToString("N"));

    public ControllerStateFilesTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void Recorded_settings_round_trip_unchanged()
    {
        // As RepositorySettings recorded it before the core owned the decisions; its JSON encoder escapes '&' and '+'.
        const string Recorded = """{"version":1,"settings":{"REPOAGENT_MAX_ACTIVE_SESSIONS":{"state":"invalid","value":"6","since":"2026-10-24T09:58:00.0000000\u002B00:00","reason":"must be an integer from 1 to 64"},"REPOAGENT_ENROLLMENT_CATEGORIES":{"state":"configured","value":"Agents, Q\u0026A","since":"2026-10-24T09:59:30.2500000\u002B00:00"},"REPOAGENT_MODEL":{"state":"read-failed","reason":"HTTP 503"},"REPOAGENT_REASONING":{"state":"absent"}}}""";
        var statePath = Path.Combine(directory, "bridge.json");
        File.WriteAllText(ControllerStateFiles.SettingsPath(statePath), Recorded);
        var files = new ControllerStateFiles(statePath);

        var settings = files.ReadSettings()!;
        files.WriteSettings(settings);

        Assert.Equal(new SettingValue(SettingState.Configured, "Agents, Q&A") { Since = new(2026, 10, 24, 9, 59, 30, 250, TimeSpan.Zero) },
            settings.Settings["REPOAGENT_ENROLLMENT_CATEGORIES"]);
        Assert.Equal(Recorded, File.ReadAllText(ControllerStateFiles.SettingsPath(statePath)));
    }

    public void Dispose() => Directory.Delete(directory, true);
}
