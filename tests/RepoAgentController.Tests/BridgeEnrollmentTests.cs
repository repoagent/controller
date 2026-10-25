using System.Text.Json.Nodes;
using Xunit;

namespace RepoAgentController.Tests;

// Which Discussions the bridge enrolls and when it forgets one, in-process (BridgeWorld): category enrollment from the
// refreshed repository setting, the App's own dashboard, and a Discussion GitHub deleted while the bridge held its state.
// CategoryEnrollmentStartsTests owns how starts follow the setting; here the bridge applies them to observed Discussions.
public sealed class BridgeEnrollmentTests
{
    const string Setting = CategoryEnrollment.Setting;
    const string Mention = "@test-app[bot] please look.";

    // Discussion IDs in the order their input reached agent turns.
    static string[] Delivered(BridgeWorld world) => [.. world.Turns.Select(turn => System.Text.Json.Nodes.JsonNode.Parse(turn.Input)!["discussion"]!["id"]!.GetValue<string>())];

    static Discussion Agents(BridgeWorld world, int number, DateTimeOffset? created = null) =>
        world.GitHub.Open(number, $"Discussion {number} needs no mention.", "Agents", created: created);

    [Fact]
    public async Task A_new_Discussion_in_a_configured_category_enrolls_without_a_mention_and_others_do_not()
    {
        await using var world = new BridgeWorld();
        world.Variables.Set(Setting, "Agents");
        Assert.True(await world.RunOnce());

        world.Clock.Advance(TimeSpan.FromMinutes(1));
        Agents(world, 1);
        world.GitHub.Open(2, "Discussion 2 needs no mention.", "General");
        // The controller's own Discussions, such as its status dashboard, never enroll themselves.
        world.GitHub.Open(3, AppIdentity.DashboardMarker + "\nController status", "Agents", InMemoryDiscussions.AppAccount);
        Assert.True(await world.RunOnce());

        Assert.Equal(["D1"], Delivered(world));
        Assert.Equal(["D1"], world.WithSessions);
        Assert.True(world.GitHub.HasEyes("D1"));
        Assert.Equal(1, world.GitHub.Calls.Count(call => call.StartsWith("addReaction ", StringComparison.Ordinal)));
        Assert.Contains(BridgeWorld.Url(1) + " enrollment=category", world.Logged("discovered"));
    }

    [Fact]
    public async Task Preexisting_Discussions_in_a_newly_selected_category_wait_for_a_mention_or_explicit_URL()
    {
        await using var world = new BridgeWorld();
        var earlier = world.Clock.GetUtcNow().AddHours(-1);
        Agents(world, 1, earlier);
        world.GitHub.Open(2, "Please look, @test-app[bot].", "Agents", created: earlier);
        world.GitHub.Open(3, "Discussion 3 needs no mention.", created: earlier);
        world.GitHub.Open(4, "Discussion 4 needs no mention.", created: earlier);
        world.GitHub.Comment(4, "C4", "Asking @test-app[bot] in a comment.");
        world.Variables.Set(Setting, "Agents");

        Assert.True(await world.RunOnce(3));

        Assert.Equal(["D2", "D3", "D4"], Delivered(world).Order());
        Assert.Equal(["D2", "D3", "D4"], world.WithSessions);
        Assert.DoesNotContain(world.Logged("discovered"), line => line.Contains(" enrollment=category"));
    }

    [Fact]
    public async Task Operator_category_changes_apply_to_later_Discussions_while_enrolled_sessions_continue()
    {
        await using var world = new BridgeWorld();
        world.Variables.Set(Setting, "Agents");
        Assert.True(await world.RunOnce());
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        Agents(world, 1);
        Assert.True(await world.RunOnce());

        world.Variables.Set(Setting, "research, Q&A");
        Assert.True(await world.RunOnce());
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        Agents(world, 2);
        world.GitHub.Open(3, "Discussion 3 needs no mention.", "Research");
        world.GitHub.Comment(1, "C1", "A follow-up in the Discussion enrolled earlier.");
        Assert.True(await world.RunOnce());

        Assert.Equal(["D1", "D1", "D3"], Delivered(world));
        Assert.Equal(["D1", "D3"], world.WithSessions);

        // Selecting a category again starts afresh: Discussions created while it was unselected stay preexisting.
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        world.Variables.Set(Setting, "Research, Agents");
        Assert.True(await world.RunOnce());
        Assert.Equal(["D1", "D3"], world.WithSessions);
    }

    [Fact]
    public async Task A_failed_settings_read_after_a_restart_keeps_the_selection_and_its_recorded_start()
    {
        await using var world = new BridgeWorld();
        world.Variables.Set(Setting, "Agents");
        Assert.True(await world.RunOnce());

        // The next lifetime cannot read the variable; a Discussion created in the category meanwhile still enrolls.
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        world.Variables.Fail(Setting, new RepositorySettingRejectedException(503));
        Agents(world, 1);
        Assert.True(await world.RunOnce());
        Assert.Contains($"name={Setting} state=read-failed value=Agents reason=\"HTTP 503\"", world.Logged("repository-setting"));
        Assert.Equal(["D1"], Delivered(world));

        // A Discussion created during the outage is not preexisting once the read recovers: the first start still applies.
        Agents(world, 2);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        world.Variables.Set(Setting, "Agents");
        Assert.True(await world.RunOnce());
        Assert.Equal(["D1", "D2"], Delivered(world));
    }

    [Fact]
    public async Task A_running_controller_selects_from_its_settings_refresh_without_waiting_for_the_next_observation()
    {
        await using var world = new BridgeWorld();
        await world.Begin();
        Assert.True(await world.Poll());

        world.Variables.Set(Setting, "Agents");
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        await world.Refresh();
        Assert.Contains($"name={Setting} state=configured value=Agents", world.Logged("repository-setting"));

        // Created after the refresh read the selection and before the next observation, like a webhook-mode gap.
        Agents(world, 1, world.Clock.GetUtcNow().AddSeconds(30));
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(await world.Poll());
        Assert.Equal(["D1"], Delivered(world));
    }

    [Fact]
    public async Task A_category_removed_and_selected_again_between_observations_starts_afresh()
    {
        await using var world = new BridgeWorld();
        world.Variables.Set(Setting, "Agents");
        await world.Begin();
        Assert.True(await world.Poll());

        // No observation runs until the category is selected again.
        world.Variables.Set(Setting, null);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        await world.Refresh();
        Assert.Contains($"name={Setting} state=absent", world.Logged("repository-setting"));
        Agents(world, 1, world.Clock.GetUtcNow().AddSeconds(30)); // While the category is not selected.
        world.Variables.Set(Setting, "Agents");
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        await world.Refresh();

        Agents(world, 2, world.Clock.GetUtcNow().AddSeconds(10));
        world.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(await world.Poll());
        Assert.DoesNotContain(world.Logged("discovered"), line => line.StartsWith(BridgeWorld.Url(1), StringComparison.Ordinal));
        Assert.Equal(["D2"], Delivered(world));
    }

    [Fact]
    public async Task Category_starts_that_could_not_be_saved_stay_reconciled_and_are_saved_with_the_next_state_change()
    {
        await using var world = new BridgeWorld();
        world.Variables.Set(Setting, "Agents");
        await world.Begin();
        Assert.True(await world.Poll());
        Assert.Equal(["Agents"], world.State.Saved!.CategoryStarts.Keys);
        await world.End();
        await world.Begin(); // This lifetime holds the saved starts as it read them.

        world.State.FailWrites = true; // As a full disk fails the save.
        world.Variables.Set(Setting, null);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        await world.Refresh();
        Assert.Equal(["the category enrollment starts could not be saved: No space left on device"], world.Logged("repository-settings-error"));
        Assert.Equal(["Agents"], world.State.Saved!.CategoryStarts.Keys);

        world.State.FailWrites = false;
        world.GitHub.Open(1, Mention);
        Assert.True(await world.Poll());
        Assert.Empty(world.State.Saved!.CategoryStarts);
    }

    // Starts that are not an object of text, as only a hand edit leaves them, are replaced by the first start recorded.
    [Theory]
    [InlineData("5")]
    [InlineData("""{"Agents":"2026-10-24T08:00:00Z","Q&A":7}""")]
    public async Task Category_starts_no_writer_leaves_are_gone_once_their_replacement_is_forgotten(string starts)
    {
        await using var world = new BridgeWorld { Files = true }; // A state file a controller left.
        Directory.CreateDirectory(Path.GetDirectoryName(world.StatePath)!);
        File.WriteAllText(world.StatePath, """{"version":1,"repository":"owner/repository","discussions":{},"enrollmentCategories":""" + starts + "}");
        world.Variables.Set(Setting, "Agents");
        await world.Begin();

        world.Variables.Set(Setting, null);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        await world.Refresh();

        Assert.False(JsonNode.Parse(File.ReadAllText(world.StatePath))!.AsObject().ContainsKey("enrollmentCategories"));
    }

    [Fact]
    public async Task The_owned_dashboard_never_becomes_agent_input_however_it_is_enrolled_while_a_participant_copy_stays_ordinary()
    {
        await using var world = new BridgeWorld();
        var body = AppIdentity.DashboardMarker + "\nController status";
        world.GitHub.Open(3, body, "Controller status", InMemoryDiscussions.AppAccount);
        // A participant asks the App about the dashboard and copies its whole text into their own Discussion, which lists
        // first; the operator even names the dashboard for explicit enrollment.
        world.GitHub.Comment(3, "C9", "@test-app[bot] what does this status mean?");
        world.GitHub.Open(2, body + "\n\n@test-app[bot] please explain this copy.", "Controller status");

        Assert.True(await world.RunOnce(3));

        Assert.Equal(["D2"], Delivered(world));
        Assert.Equal(["D2"], world.WithSessions);
        Assert.True(world.GitHub.HasEyes("D2"));
        Assert.False(world.GitHub.HasEyes("D3") || world.GitHub.HasEyes("C9"));
    }

    [Fact]
    public async Task A_deleted_Discussion_is_forgotten_only_once_GitHub_confirms_it_and_unrelated_work_continues()
    {
        await using var world = new BridgeWorld();
        world.GitHub.Open(1, Mention);
        world.GitHub.Open(2, Mention);
        await world.Begin();
        await world.Poll();
        world.Finish(1);
        world.Finish(2);

        // A later lifetime, as when the deployed Discussion was deleted: a failed lookup is not a deletion and keeps its state.
        await world.Begin();
        var retained = world.Saved(1)!;
        var unrelated = world.Saved(2)!;
        world.GitHub.FailReads("Find", new GitHubRequestException(GitHubFailure.Permanent, "FORBIDDEN: Resource not accessible by integration"));
        Assert.False(await world.Observe(1));
        Assert.Equivalent(retained, world.Saved(1), strict: true);

        // GitHub confirms the deletion: the whole record goes, and the other Discussion's session and checkpoints stay.
        world.GitHub.FailReads("Find", null);
        world.GitHub.Remove(1);
        Assert.True(await world.Observe(1));
        Assert.Equal(BridgeWorld.Url(1) + " reason=not-found session=S1", Assert.Single(world.Logged("discussion-forgotten")));
        Assert.Null(world.Saved(1));
        Assert.Equivalent(unrelated, world.Saved(2), strict: true);

        // A duplicate deletion notification ahead of new live input is harmless.
        world.GitHub.Edit(2, Mention + " behind a stale notification");
        Assert.True(await world.Observe(1));
        Assert.True(await world.Observe(2));
        Assert.Equal(["S1", "S2", "S2"], world.Turns.Select(turn => turn.SessionId));
        Assert.Equal(["D2"], world.WithSessions);
        Assert.Single(world.Logged("discussion-forgotten")); // Nothing was left to forget.
    }

    [Fact]
    public async Task A_deleted_Discussion_with_waiting_input_and_a_running_turn_never_dispatches_and_its_turn_holds_its_slot_until_it_ends()
    {
        await using var world = new BridgeWorld();
        world.GitHub.Open(1, Mention);
        world.GitHub.Open(2, Mention);
        await world.Begin();
        await world.Poll();
        world.Finish(2);
        // T1 keeps D1's session active; a D1 edit waits in the controller behind it.
        world.GitHub.Edit(1, Mention + " while T1 runs");
        Assert.True(await world.Observe(1));
        Assert.Equal("T1", world.Saved(1)!.Running!.Turn);
        Assert.Single(world.Saved(1)!.Waiting!.Inputs);
        var unrelated = world.Saved(2)!;

        world.GitHub.Remove(1);
        Assert.True(await world.Observe(1));
        Assert.Equal(["D2"], world.WithSessions); // Its waiting input, running turn and checkpoints are forgotten.
        Assert.Equivalent(unrelated, world.Saved(2), strict: true);
        Assert.Equal(1, world.Status()["work"]!["running"]!.GetValue<int>()); // Counted while the agent still runs it.

        // The agent ends the turn: its slot frees and the forgotten input is never sent.
        world.Finish("S1", "T1");
        await world.Dispatch();
        Assert.Equal(0, world.Status()["work"]!["running"]!.GetValue<int>());
        world.GitHub.Edit(2, Mention + " after the deletion");
        Assert.True(await world.Observe(2));
        Assert.Equal(["S1", "S2", "S2"], world.Turns.Select(turn => turn.SessionId));
    }

    [Fact]
    public async Task Model_labels_that_cannot_be_read_hold_delivery_until_observation_replaces_them()
    {
        await using var world = new BridgeWorld();
        await Damage(world, "labels", "[4]", waiting: true);

        await world.Begin();
        await world.Dispatch(); // Before any observation.
        Assert.StartsWith(BridgeWorld.Url(1) + ": ", Assert.Single(world.Logged("dispatch-error")));
        Assert.Equal(2, world.Turns.Count);
        Assert.True(await world.Poll());

        Assert.Equal(["S1", "S2", "S1"], world.Turns.Select(turn => turn.SessionId));
    }

    // Discussion 1 has a session that took its first input and is idle, or with waiting input while that turn ran; a hand edit
    // then changes its record at this dotted path: the JSON text replaces the value there, or null removes it.
    static async Task Damage(BridgeWorld world, string path, string? json, bool waiting = false)
    {
        world.Files = true;
        world.GitHub.Open(1, Mention);
        world.GitHub.React(1, "D1", "R1", "HEART", "octocat");
        world.GitHub.Open(2, Mention);
        await world.Begin();
        Assert.True(await world.Poll());
        if (waiting)
        {
            world.GitHub.Comment(1, "C0", "Queued while the turn runs.");
            Assert.True(await world.Poll());
        }
        else
        {
            world.Finish(1);
            await world.Dispatch();
        }
        await world.End();
        var state = JsonNode.Parse(File.ReadAllText(world.StatePath))!;
        var keys = path.Split('.');
        var parent = keys[..^1].Aggregate(state["discussions"]!["D1"]!, (node, key) => node is JsonArray array ? array[int.Parse(key)]! : node[key]!);
        if (json is null) parent.AsObject().Remove(keys[^1]);
        else if (parent is JsonArray array) array[int.Parse(keys[^1])] = "\u0001"; // Replaced as text, so the edit can repeat a key.
        else parent[keys[^1]] = "\u0001";
        File.WriteAllText(world.StatePath, state.ToJsonString().Replace("\"\\u0001\"", json));
    }

    [Theory]
    [InlineData("sessionId", "4")]
    [InlineData("url", "5")]
    [InlineData("sources", null)]
    [InlineData("sources", "5")]
    [InlineData("sources.D1", """{"content":{"body":"one"},"content":{"body":"two"},"state":{},"reactions":{}}""")]
    [InlineData("sources.D1.reactions", "5")]
    [InlineData("sessionId", "4", true)] // Its waiting input fails delivery too, and starts no session.
    [InlineData("waiting.inputs", "5", true)]
    public async Task A_Discussion_record_that_observation_cannot_read_is_kept_as_it_was_and_only_its_Discussion_fails_observation(string path, string? json,
        bool waiting = false)
    {
        await using var world = new BridgeWorld();
        await Damage(world, path, json, waiting);

        world.GitHub.Comment(1, "C1", "More context, please.");
        world.GitHub.Comment(2, "C2", "Still here.");
        await world.Begin();
        var damaged = world.Record(1)!.ToJsonString(); // Less the turn that ended with its lifetime.
        Assert.False(await world.Poll());
        world.Finish(2);
        await world.Dispatch();

        // Never observed as new: no session starts for it and its input is not sent again; the other Discussion continues.
        Assert.Equal(["S1", "S2", "S2"], world.Turns.Select(turn => turn.SessionId));
        Assert.StartsWith(BridgeWorld.Url(1) + ": ", Assert.Single(world.Logged("observation-error")));
        Assert.Equal(waiting, world.Logged("dispatch-error").Any(message => message.StartsWith(BridgeWorld.Url(1) + ": ")));
        Assert.Equal(damaged, world.Record(1)!.ToJsonString());
    }

    [Theory]
    [InlineData("labels", "[4]", "comment.created")]
    [InlineData("running", """{"turn":5}""", "comment.created")]
    [InlineData("pending", """{"operation":5}""", "comment.created")]
    [InlineData("pending", """{"operation":5,"inputs":1}""", "comment.created", true)] // Its input is redelivered alone.
    [InlineData("pending", """{"operation":"send","operation":"send"}""", "comment.created")] // Its keys cannot be read.
    [InlineData("pending", """{"inputs":1,"inputs":1}""", "comment.created,comment.created", true)] // It carries no inputs, so none is redelivered alone.
    [InlineData("url", null, "comment.created")]
    [InlineData("sources.D1", "null", "discussion.created,reaction.added,comment.created")]
    [InlineData("sources.D1.content.body", "5", "discussion.changed,comment.created")]
    [InlineData("sources.D1.content.extra", "1", "discussion.changed,comment.created")]
    [InlineData("sources.D1.state", null, "discussion.stateChanged(title closed locked category),comment.created")]
    [InlineData("sources.D1.state.closed", "\"yes\"", "discussion.stateChanged(closed),comment.created")]
    [InlineData("sources.D1.state.extra", "1", "discussion.stateChanged(),comment.created")]
    [InlineData("sources.D1.state.extra", "null", "discussion.stateChanged(),comment.created")]
    [InlineData("sources.D1.state.answer", null, "discussion.stateChanged(),comment.created")]
    [InlineData("sources.D1.reactions", null, "reaction.added,comment.created")]
    [InlineData("sources.D1.reactions.R1", "5", "comment.created")]
    [InlineData("waiting", "5", "comment.created", true)] // Replaced by what observation found, as it was.
    [InlineData("waiting.discussion", "5", "comment.created,comment.created", true)]
    [InlineData("waiting.context", "5", "comment.created,comment.created", true)]
    [InlineData("waiting.inputs.0.observedAt", "5", "comment.created,comment.created", true)]
    [InlineData("waiting.retry", """{"attempts":"x"}""", "comment.created,comment.created", true)]
    [InlineData("waiting.inputs.0.receipt.reactionGroups", "null", "comment.created,comment.created", true)] // As an earlier controller copied it.
    [InlineData("waiting.inputs.0.receipt.reactionGroups", """[{"content":"EYES"}]""", "comment.created,comment.created", true)]
    [InlineData("waiting.inputs.0.receipt.url", null, "comment.created,comment.created", true)]
    public async Task A_Discussion_record_key_that_observation_replaces_or_a_start_ends_is_replaced_and_its_Discussion_continues(string path, string? json, string kinds,
        bool waiting = false)
    {
        await using var world = new BridgeWorld();
        await Damage(world, path, json, waiting);

        world.GitHub.Comment(1, "C1", "More context, please.");
        await world.Begin();
        // Turns and deliveries end with their lifetime, so status reports neither.
        Assert.Null(world.Record(1)!["running"]);
        Assert.Null(world.Record(1)!["pending"]);
        Assert.True(await world.Poll());

        Assert.Empty(world.Logged("observation-error"));
        Assert.Equal("S1", world.Turns[^1].SessionId);
        // A state change also names the state fields it carries.
        Assert.Equal(kinds.Split(','), world.Envelope(world.Turns.Count - 1)["changes"]!.AsArray().Select(change => change!["kind"]!.GetValue<string>() is var kind &&
            kind == "discussion.stateChanged" ? $"{kind}({string.Join(' ', change["subject"]!.AsObject().Select(field => field.Key).Except(["id", "url", "author"]))})" : kind));
        // A damaged value is replaced or removed; a removed key has no damaged value to replace.
        var value = path.Split('.').Aggregate((JsonNode?)world.Record(1), (node, key) => node switch
        {
            JsonArray items => items.ElementAtOrDefault(int.Parse(key)),
            JsonObject item => item[key],
            _ => null
        });
        if (json is not null) Assert.NotEqual(json, value?.ToJsonString());
    }
}
