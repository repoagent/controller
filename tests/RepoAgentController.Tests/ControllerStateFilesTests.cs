using Xunit;

namespace RepoAgentController.Tests;

// The one golden round trip per persisted format (owner decision): a recorded file is read and written back unchanged, except
// that a settings entry of a state this controller does not know keeps its value and reads as pending. Status reports this record under
// "settings" verbatim, so an unchanged file also keeps that part of status unchanged.
public sealed class ControllerStateFilesTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "controller-state-files-" + Guid.NewGuid().ToString("N"));

    public ControllerStateFilesTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void Recorded_settings_round_trip_unchanged_and_a_value_of_an_unknown_state_is_kept()
    {
        // As RepositorySettings recorded it before the core owned the decisions; its JSON encoder escapes '&' and '+'.
        const string Recorded = """{"version":1,"settings":{"REPOAGENT_MAX_ACTIVE_SESSIONS":{"state":"invalid","value":"6","since":"2026-10-24T09:58:00.0000000\u002B00:00","reason":"must be an integer from 1 to 64"},"REPOAGENT_ENROLLMENT_CATEGORIES":{"state":"configured","value":"Agents, Q\u0026A","since":"2026-10-24T09:59:30.2500000\u002B00:00"},"REPOAGENT_MODEL":{"state":"read-failed","reason":"HTTP 503"},"REPOAGENT_REASONING":{"state":"absent"},"REPOAGENT_LATER_SETTING":{"state":"retired","value":"on","since":"2026-10-23T08:00:00.0000000\u002B00:00"}}}""";
        var statePath = Path.Combine(directory, "bridge.json");
        File.WriteAllText(ControllerStateFiles.SettingsPath(statePath), Recorded);
        var files = new ControllerStateFiles(statePath);

        var settings = files.ReadSettings()!;
        files.WriteSettings(settings);

        Assert.Equal(new SettingValue(SettingState.Configured, "Agents, Q&A") { Since = new(2026, 10, 24, 9, 59, 30, 250, TimeSpan.Zero) },
            settings.Settings["REPOAGENT_ENROLLMENT_CATEGORIES"]);
        // An earlier or later controller's state does not lose the value it was recorded with.
        Assert.Equal(new SettingValue(SettingState.Pending, "on") { Since = new(2026, 10, 23, 8, 0, 0, TimeSpan.Zero) },
            settings.Settings["REPOAGENT_LATER_SETTING"]);
        Assert.Equal(Recorded.Replace("\"state\":\"retired\"", "\"state\":\"pending\"", StringComparison.Ordinal),
            File.ReadAllText(ControllerStateFiles.SettingsPath(statePath)));
    }

    [Fact]
    public void Retained_outbox_operations_round_trip_unchanged()
    {
        // As GitHubOutbox wrote them before the core owned it: indented, and its JSON encoder escapes HTML-sensitive and non-ASCII
        // text. The usual file has no rate-limit pause: a receipt waits to retry a certain failure, and a dashboard publication is due.
        const string Usual = """
            {
              "version": 1,
              "repository": "owner/repository",
              "pausedUntil": null,
              "operations": [
                {
                  "key": "receipt:D_kwDOAbc",
                  "kind": "receipt",
                  "subjectId": "D_kwDOAbc",
                  "url": "https://github.com/owner/repository/discussions/2",
                  "attempts": 1,
                  "dueAt": "2026-10-24T11:00:15.0000000\u002B00:00",
                  "uncertain": false,
                  "lastError": "HTTP 401: Bad credentials"
                },
                {
                  "key": "dashboard",
                  "kind": "dashboard",
                  "subjectId": "",
                  "url": "",
                  "category": "Controller status",
                  "title": "RepoAgent controller status",
                  "body": "\u003C!-- repoagent:controller-dashboard --\u003E\nHeartbeat\n",
                  "attempts": 0,
                  "dueAt": "2026-10-24T11:00:03.0000000\u002B00:00",
                  "uncertain": false,
                  "lastError": null
                }
              ]
            }
            """;
        // A rate-limited, uncertain receipt and a dashboard publication with text the encoder escapes.
        const string Paused = """
            {
              "version": 1,
              "repository": "owner/repository",
              "pausedUntil": "2026-10-24T10:01:01.5000000\u002B00:00",
              "operations": [
                {
                  "key": "receipt:DC_kwDOAbc",
                  "kind": "receipt",
                  "subjectId": "DC_kwDOAbc",
                  "url": "https://github.com/owner/repository/discussions/1#discussioncomment-7",
                  "attempts": 2,
                  "dueAt": "2026-10-24T10:01:01.5000000\u002B00:00",
                  "uncertain": true,
                  "lastError": "HTTP 502: \u003Chtml\u003EBad gateway\u003C/html\u003E \u0026 retry\u0027s \u0022later\u0022"
                },
                {
                  "key": "dashboard",
                  "kind": "dashboard",
                  "subjectId": "",
                  "url": "",
                  "category": "Controller status",
                  "title": "Attention: RepoAgent controller authentication failed",
                  "body": "\u003C!-- repoagent:controller-dashboard --\u003E\nOperational status of this repository\u0027s RepoAgent controller.\n\n- **Authentication:** failed at 2026-10-24T10:00:00Z: \u0060HTTP 401 \u002B retry\u0060. Check the controller\u0027s approval \u0026 credentials.\n- [Discussion #1](https://github.com/owner/repository/discussions/1): 1 inputs waiting \u2014 caf\u00E9\n",
                  "attempts": 0,
                  "dueAt": "2026-10-24T10:00:30.2500000\u002B00:00",
                  "uncertain": false,
                  "lastError": null
                }
              ]
            }
            """;
        var statePath = Path.Combine(directory, "bridge.json");
        var files = new ControllerStateFiles(statePath);

        File.WriteAllText(ControllerStateFiles.OutboxPath(statePath), Usual);
        var usual = files.ReadOutbox()!;
        files.WriteOutbox(usual);
        Assert.Equal(Usual, File.ReadAllText(ControllerStateFiles.OutboxPath(statePath)));
        Assert.Equal(("owner/repository", (DateTimeOffset?)null), (usual.Repository, usual.PausedUntil));
        Assert.Equal(new OutboxOperation("receipt:D_kwDOAbc", "receipt", "D_kwDOAbc", "https://github.com/owner/repository/discussions/2", null, null, null, 1,
            new DateTimeOffset(2026, 10, 24, 11, 0, 15, TimeSpan.Zero), false, "HTTP 401: Bad credentials"), usual.Operations[0]);
        Assert.Equal(new OutboxOperation("dashboard", "dashboard", "", "", "Controller status", "RepoAgent controller status", "<!-- repoagent:controller-dashboard -->\nHeartbeat\n", 0,
            new DateTimeOffset(2026, 10, 24, 11, 0, 3, TimeSpan.Zero), false, null), usual.Operations[1]);

        File.WriteAllText(ControllerStateFiles.OutboxPath(statePath), Paused);
        var outbox = files.ReadOutbox()!;
        files.WriteOutbox(outbox);

        var paused = new DateTimeOffset(2026, 10, 24, 10, 1, 1, 500, TimeSpan.Zero);
        Assert.Equal(new OutboxState("owner/repository", paused, outbox.Operations), outbox);
        Assert.Equal(new OutboxOperation("receipt:DC_kwDOAbc", "receipt", "DC_kwDOAbc", "https://github.com/owner/repository/discussions/1#discussioncomment-7",
            null, null, null, 2, paused, true, "HTTP 502: <html>Bad gateway</html> & retry's \"later\""), outbox.Operations[0]);
        Assert.Equal(("dashboard", "Controller status", "Attention: RepoAgent controller authentication failed", 0, new DateTimeOffset(2026, 10, 24, 10, 0, 30, 250, TimeSpan.Zero), false, (string?)null),
            (outbox.Operations[1].Kind, outbox.Operations[1].Category, outbox.Operations[1].Title, outbox.Operations[1].Attempts, outbox.Operations[1].DueAt, outbox.Operations[1].Uncertain, outbox.Operations[1].LastError));
        Assert.EndsWith("1 inputs waiting \u2014 caf\u00e9\n", outbox.Operations[1].Body);
        Assert.Equal(Paused, File.ReadAllText(ControllerStateFiles.OutboxPath(statePath)));
    }

    public void Dispose() => Directory.Delete(directory, true);
}
