using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace RepoAgentController.Tests;

// The one golden round trip per persisted format (owner decision): a recorded file is read and written back unchanged, except
// that a settings entry of a state this controller does not know keeps its value and reads as pending. Status reports this record under
// "settings" verbatim, so an unchanged file also keeps that part of status unchanged. An outbox operation of a kind this controller
// does not deliver, or a receipt or dashboard in a form the controller never writes, is also written back unchanged. The state
// file keeps every key and value it was read with.
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
        // An operation of a kind a later controller might retain, which delivery drops as unsupported (GitHubOutboxDeliveryTests).
        const string Later = """
            {
              "version": 1,
              "repository": "owner/repository",
              "pausedUntil": null,
              "operations": [
                {
                  "key": "label:D_kwDOAbc",
                  "kind": "label",
                  "subjectId": "D_kwDOAbc",
                  "url": "https://github.com/owner/repository/discussions/2",
                  "category": "Agents",
                  "body": "triage",
                  "attempts": 0,
                  "dueAt": "2026-10-24T11:00:03.0000000\u002B00:00",
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
        Assert.Equal(new ReceiptOperation("D_kwDOAbc", "https://github.com/owner/repository/discussions/2")
            { Attempts = 1, DueAt = new(2026, 10, 24, 11, 0, 15, TimeSpan.Zero), LastError = "HTTP 401: Bad credentials" }, usual.Operations[0]);
        Assert.Equal(new DashboardOperation("Controller status", "RepoAgent controller status", "<!-- repoagent:controller-dashboard -->\nHeartbeat\n")
            { DueAt = new(2026, 10, 24, 11, 0, 3, TimeSpan.Zero) }, usual.Operations[1]);

        File.WriteAllText(ControllerStateFiles.OutboxPath(statePath), Paused);
        var outbox = files.ReadOutbox()!;
        files.WriteOutbox(outbox);

        var paused = new DateTimeOffset(2026, 10, 24, 10, 1, 1, 500, TimeSpan.Zero);
        Assert.Equal(new OutboxState("owner/repository", paused, outbox.Operations), outbox);
        Assert.Equal(new ReceiptOperation("DC_kwDOAbc", "https://github.com/owner/repository/discussions/1#discussioncomment-7")
            { Attempts = 2, DueAt = paused, Uncertain = true, LastError = "HTTP 502: <html>Bad gateway</html> & retry's \"later\"" }, outbox.Operations[0]);
        var publication = Assert.IsType<DashboardOperation>(outbox.Operations[1]);
        Assert.Equal(("Controller status", "Attention: RepoAgent controller authentication failed", 0, new DateTimeOffset(2026, 10, 24, 10, 0, 30, 250, TimeSpan.Zero), false, (string?)null),
            (publication.Category, publication.Title, publication.Attempts, publication.DueAt, publication.Uncertain, publication.LastError));
        Assert.EndsWith("1 inputs waiting \u2014 caf\u00e9\n", publication.Body);
        Assert.Equal(Paused, File.ReadAllText(ControllerStateFiles.OutboxPath(statePath)));

        File.WriteAllText(ControllerStateFiles.OutboxPath(statePath), Later);
        files.WriteOutbox(files.ReadOutbox()!);
        Assert.Equal(Later, File.ReadAllText(ControllerStateFiles.OutboxPath(statePath)));

        // A receipt or dashboard under another key, subject or set of fields than GitHubOutbox writes for its kind. One its record
        // holds keeps its key and is delivered as before; the rest are kept as read, as Later is, and delivery drops them.
        const string Retry = "\"attempts\":0,\"dueAt\":\"2026-10-24T11:00:03.0000000+00:00\",\"uncertain\":false,\"lastError\":null";
        (string Operation, Type Read)[] unwritten =
        [
            ($$"""{"key":"receipt:D1","kind":"receipt","subjectId":"D2","url":"u",{{Retry}}}""", typeof(ReceiptOperation)),
            ($$"""{"key":"receipt:D1","kind":"receipt","subjectId":"D1","url":"u","body":"b",{{Retry}}}""", typeof(UnsupportedOperation)),
            ($$"""{"key":"dashboard:2","kind":"dashboard","subjectId":"","url":"","category":"c","title":"t","body":"b",{{Retry}}}""", typeof(DashboardOperation)),
            ($$"""{"key":"dashboard","kind":"dashboard","subjectId":"D1","url":"","category":"c","title":"t","body":"b",{{Retry}}}""", typeof(UnsupportedOperation)),
            ($$"""{"key":"dashboard","kind":"dashboard","subjectId":"","url":"u","category":"c","title":"t","body":"b",{{Retry}}}""", typeof(UnsupportedOperation)),
            ($$"""{"key":"dashboard","kind":"dashboard","subjectId":"","url":"","title":"t","body":"b",{{Retry}}}""", typeof(UnsupportedOperation)),
            ($$"""{"key":"dashboard","kind":"dashboard","subjectId":"","url":"","category":"c","title":"t",{{Retry}}}""", typeof(UnsupportedOperation))
        ];
        foreach (var (operation, read) in unwritten)
        {
            // Indented and escaped as the writer's encoder writes it.
            var file = JsonNode.Parse($$"""{"version":1,"repository":"owner/repository","pausedUntil":null,"operations":[{{operation}}]}""")!
                .ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ControllerStateFiles.OutboxPath(statePath), file);
            var retained = Assert.Single(files.ReadOutbox()!.Operations);
            files.WriteOutbox(new("owner/repository", null, [retained]));
            Assert.Equal((read, JsonNode.Parse(operation)!["key"]!.GetValue<string>(), file),
                (retained.GetType(), retained.Key, File.ReadAllText(ControllerStateFiles.OutboxPath(statePath))));
        }
    }

    [Fact]
    public void The_state_file_round_trips_every_key_its_writers_produce_and_keeps_keys_this_controller_does_not_write()
    {
        // As the bridge, the identity check and category enrollment wrote it before the Discussion records were typed. Each record's
        // keys are in the order its history added them, and every object a key of its own could be added to carries one ("later").
        // Agent input the bridge keeps (a change, the Discussion, the bootstrap context) is the text it sends. D_kwDOAbc4's session ID
        // is not text, as no writer leaves it.
        const string Recorded = """
            {
              "version": 1,
              "repository": "owner/repository",
              "discussions": {
                "D_kwDOAbc1": {
                  "url": "https://github.com/owner/repository/discussions/1",
                  "sources": {
                    "D_kwDOAbc1": {
                      "content": { "body": "@test-app[bot] the deploy is flaky — café <b>", "author": { "login": "participant" } },
                      "state": { "title": "Flaky deploy", "closed": false, "locked": false, "answer": null, "category": { "id": "DIC_kwDOAgents", "name": "Agents" } },
                      "reactions": {
                        "REA_kwDOAbc1": { "id": "REA_kwDOAbc1", "content": "HEART", "user": { "__typename": "User", "login": "participant" }, "createdAt": "2026-10-23T08:20:00Z" },
                        "REA_kwDOAbc2": { "id": "REA_kwDOAbc2", "content": "ROCKET", "user": null, "createdAt": "2026-10-23T08:22:00Z" }
                      },
                      "later": "checkpoint"
                    },
                    "DC_kwDOAbc11": { "content": { "body": "Posted from a deleted account.", "author": null }, "state": {}, "reactions": {} },
                    "DC_kwDOAbc12": { "content": { "body": "A reply.", "author": { "login": "reviewer" } }, "state": {}, "reactions": {}, "replyTo": "DC_kwDOAbc11" }
                  },
                  "labels": [ "model:gpt-5.5", "reasoning:high" ],
                  "sessionId": "S1",
                  "running": { "turn": "T1", "since": "2026-10-24T09:30:00.0000000+00:00", "later": "running" },
                  "later": "record"
                },
                "D_kwDOAbc2": {
                  "url": "https://github.com/owner/repository/discussions/2",
                  "sources": {
                    "D_kwDOAbc2": {
                      "content": { "body": "Second question", "author": { "login": "participant" } },
                      "state": { "title": "Question", "closed": true, "locked": true, "answer": { "id": "DC_kwDOAbc21" }, "category": { "id": "DIC_kwDOQA", "name": "Q&A" } },
                      "reactions": {}
                    },
                    "DC_kwDOAbc21": { "content": { "body": "The answer.", "author": { "login": "reviewer" } }, "state": {}, "reactions": {} }
                  },
                  "waiting": {
                    "inputs": [
                      {
                        "observedAt": "2026-10-24T09:58:00.0000000+00:00",
                        "change": { "kind": "discussion.created", "subject": { "id": "D_kwDOAbc2", "url": "https://github.com/owner/repository/discussions/2", "author": { "__typename": "User", "login": "participant" } }, "replyTarget": { "replyToId": null } },
                        "receipt": {
                          "id": "D_kwDOAbc2",
                          "url": "https://github.com/owner/repository/discussions/2",
                          "reactionGroups": [ { "content": "THUMBS_UP", "viewerHasReacted": false }, { "content": "EYES", "viewerHasReacted": true } ],
                          "later": "receipt"
                        },
                        "redelivery": true,
                        "later": "input"
                      },
                      {
                        "observedAt": "2026-10-24T09:59:30.2500000+00:00",
                        "change": { "kind": "comment.deleted", "subject": { "id": "DC_kwDOAbc22", "author": { "login": "reviewer" }, "replyTo": { "id": "DC_kwDOAbc21" } }, "replyTarget": { "replyToId": "DC_kwDOAbc21" } }
                      }
                    ],
                    "discussion": { "id": "D_kwDOAbc2", "url": "https://github.com/owner/repository/discussions/2", "body": "Second question", "createdAt": "2026-10-24T09:00:00Z", "updatedAt": "2026-10-24T09:00:00Z", "author": { "__typename": "User", "login": "participant" }, "number": 2, "title": "Question", "closed": true, "locked": true, "answer": { "id": "DC_kwDOAbc21" }, "category": { "id": "DIC_kwDOQA", "name": "Q&A" } },
                    "context": {
                      "existingComments": [ { "id": "DC_kwDOAbc21", "url": "https://github.com/owner/repository/discussions/2#discussioncomment-21", "body": "The answer.", "createdAt": "2026-10-24T09:10:00Z", "updatedAt": "2026-10-24T09:10:00Z", "author": { "__typename": "User", "login": "reviewer" }, "replyTo": null, "reactions": [] } ],
                      "laterComments": 2
                    },
                    "retry": { "attempts": 2, "after": "2026-10-24T10:05:00.0000000+00:00", "later": "retry" },
                    "later": "waiting"
                  },
                  "pending": { "operation": "create-and-send", "since": "2026-10-24T10:04:00.0000000+00:00", "sourceIds": [ "D_kwDOAbc2" ], "inputs": 1, "later": "pending" }
                },
                "D_kwDOAbc3": {
                  "url": "https://github.com/owner/repository/discussions/3",
                  "sources": { "D_kwDOAbc3": { "content": { "body": "Third", "author": { "login": "participant" } }, "state": { "title": "Third", "closed": false, "locked": false, "answer": null, "category": null }, "reactions": {} } },
                  "sessionId": "S3",
                  "running": { "turn": "T3", "since": "2026-10-24T09:40:00.0000000+00:00" },
                  "waiting": {
                    "inputs": [ { "observedAt": "2026-10-24T09:45:00.0000000+00:00", "change": { "kind": "discussion.changed", "subject": { "id": "D_kwDOAbc3", "url": "https://github.com/owner/repository/discussions/3", "author": { "__typename": "User", "login": "participant" }, "body": "Third, edited" }, "replyTarget": { "replyToId": null } }, "receipt": { "id": "D_kwDOAbc3", "url": "https://github.com/owner/repository/discussions/3", "reactionGroups": [] } } ],
                    "discussion": { "id": "D_kwDOAbc3", "url": "https://github.com/owner/repository/discussions/3", "body": "Third, edited", "createdAt": "2026-10-24T09:20:00Z", "updatedAt": "2026-10-24T09:44:00Z", "author": { "__typename": "User", "login": "participant" }, "number": 3, "title": "Third", "closed": false, "locked": false, "answer": null, "category": null }
                  }
                },
                "D_kwDOAbc4": { "url": "https://github.com/owner/repository/discussions/4", "sources": {}, "sessionId": 4 }
              },
              "agentLogin": "test-app[bot]",
              "enrollmentCategories": { "Agents": "2026-10-24T08:00:00Z", "Q&A": "2026-10-24T08:30:00Z" },
              "later": { "version": 2 }
            }
            """;
        var statePath = Path.Combine(directory, "bridge.json");
        File.WriteAllText(statePath, Recorded);

        using (var store = new StateStore(statePath, "owner/repository")) store.Save();

        Assert.Equal(Canonical(JsonNode.Parse(Recorded)), Canonical(JsonNode.Parse(File.ReadAllText(statePath))));
    }

    // Keys in ordinal order at every level, so two documents compare by their keys and values alone.
    static string Canonical(JsonNode? node) => node switch
    {
        JsonObject value => "{" + string.Join(",", value.OrderBy(field => field.Key, StringComparer.Ordinal)
            .Select(field => JsonValue.Create(field.Key).ToJsonString() + ":" + Canonical(field.Value))) + "}",
        JsonArray items => "[" + string.Join(",", items.Select(Canonical)) + "]",
        null => "null",
        _ => node.ToJsonString()
    };

    public void Dispose() => Directory.Delete(directory, true);
}
