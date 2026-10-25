using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace RepoAgentController.Tests;

// What the bridge makes of observed Discussions as agent input, and when that input and its EYES receipts go out, in-process
// (BridgeWorld). Each assertion reads the input text a turn request carried. The agent runtime adds its skill prefix.
public sealed class BridgeInputTests
{
    const string DiscussionBody = "Please review the fixture.";
    static readonly Actor Alice = new("alice", "User"), Bob = new("bob", "User"), Dave = new("dave", "User"), Erin = new("erin", "User");

    // The enrolled Discussion of the input cases, which names no one, so each lifetime enrolls it explicitly.
    static BridgeWorld Enrolled()
    {
        var world = new BridgeWorld();
        world.GitHub.Open(1, DiscussionBody);
        return world;
    }

    // Kind, subject, who acted (the reaction's user, else the subject's author) and reply target of each change.
    static (string, string, string, string?)[] Changes(JsonNode envelope) => [.. envelope["changes"]!.AsArray().Select(change => (
        change!["kind"]!.GetValue<string>(), change["subject"]!["id"]!.GetValue<string>(),
        (change["reaction"]?["user"] ?? change["subject"]!["author"])!["login"]!.GetValue<string>(), change["replyTarget"]!["replyToId"]?.GetValue<string>()))];

    static (string, string, string, string?, string?)[] Deletions(JsonNode envelope) => [.. envelope["changes"]!.AsArray().Select(change => (
        change!["kind"]!.GetValue<string>(), change["subject"]!["id"]!.GetValue<string>(), change["subject"]!["author"]!["login"]!.GetValue<string>(),
        change["subject"]!["replyTo"]?["id"]?.GetValue<string>(), change["replyTarget"]!["replyToId"]?.GetValue<string>()))];

    [Fact]
    public async Task Agent_input_and_checkpoints_are_the_bytes_ec1ccf7_sent_and_saved_for_the_golden_conversation()
    {
        await using var world = new BridgeWorld();
        var golden = DiscussionJsonGoldenTests.Golden["ec1ccf7"]!;
        JsonNode Saved() => JsonNode.Parse(File.ReadAllText(world.StatePath))!["discussions"]!["D_kwDOGolden7"]!;
        world.GitHub.Put(DiscussionJsonGoldenTests.Observe("first"));
        await world.Begin();
        Assert.True(await world.Poll()); // Enrolled by the mention in its body.

        Assert.Equal(golden["first"]!["agentInput"]!.GetValue<string>(), Assert.Single(world.Turns).Input);
        Assert.Equal(golden["first"]!["sources"]!.ToJsonString(), Saved()["sources"]!.ToJsonString());

        // The second observation waits behind the running turn, saved as ec1ccf7 saved it.
        world.GitHub.Put(DiscussionJsonGoldenTests.Observe("second"));
        Assert.True(await world.Poll());
        var waiting = Saved()["waiting"]!;
        Assert.Equal(golden["second"]!["sources"]!.ToJsonString(), Saved()["sources"]!.ToJsonString());
        Assert.Equal(golden["second"]!["waiting"]!["discussion"]!.ToJsonString(), waiting["discussion"]!.ToJsonString());
        Assert.Equal(golden["second"]!["waiting"]!["inputs"]!.AsArray().Select(input => input!.ToJsonString()),
            waiting["inputs"]!.AsArray().Select(input => new JsonObject { ["change"] = input!["change"]!.DeepClone(), ["receipt"] = input["receipt"]?.DeepClone() })
                .Select(input => { if (input["receipt"] is null) input.Remove("receipt"); return input.ToJsonString(); }));
    }

    [Fact]
    public async Task New_reply_in_an_existing_session_carries_only_its_content_attribution_and_routing()
    {
        await using var world = Enrolled();
        world.GitHub.Comment(1, "C1", "Which release should we test first?", Alice);
        Assert.True(await world.RunOnce(1));

        world.GitHub.Comment(1, "R1", "Release 2.4, please.", Bob, replyTo: "C1");
        Assert.True(await world.RunOnce(1));

        var text = world.Turns[1].Input;
        Assert.DoesNotContain(DiscussionBody, text);
        Assert.DoesNotContain("Which release should we test first?", text);
        Assert.DoesNotContain("\"Fixture\"", text); // The unchanged title.
        Assert.Single(Regex.Matches(text, "\"D1\"")); // The shared Discussion identity appears once per batch.
        var envelope = JsonNode.Parse(text)!;
        Assert.Equal(("continue", "D1"), (envelope["_bridge"]!["mode"]!.GetValue<string>(), envelope["discussion"]!["id"]!.GetValue<string>()));
        Assert.Equal([("comment.created", "R1", "bob", "C1")], Changes(envelope));
        Assert.Equal("Release 2.4, please.", envelope["changes"]![0]!["subject"]!["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task Bootstrap_supplies_the_initial_conversation_once_with_attribution_and_thread_routing()
    {
        await using var world = Enrolled();
        world.GitHub.Comment(1, "C1", "Which release should we test first?", Alice);
        world.GitHub.Comment(1, "R1", "Release 2.4, please.", Bob, replyTo: "C1");
        world.GitHub.React(1, "C1", "RE1", "HEART", "carol");
        Assert.True(await world.RunOnce(1));

        var text = Assert.Single(world.Turns).Input;
        foreach (var body in new[] { DiscussionBody, "Which release should we test first?", "Release 2.4, please." })
            Assert.Single(Regex.Matches(text, Regex.Escape(body)));
        var envelope = JsonNode.Parse(text)!;
        Assert.Equal("bootstrap", envelope["_bridge"]!["mode"]!.GetValue<string>());
        var discussion = envelope["discussion"]!;
        Assert.Equal(("D1", "Fixture", DiscussionBody, "participant"), (discussion["id"]!.GetValue<string>(), discussion["title"]!.GetValue<string>(),
            discussion["body"]!.GetValue<string>(), discussion["author"]!["login"]!.GetValue<string>()));
        Assert.Equal([("C1", "alice", "Which release should we test first?", null), ("R1", "bob", "Release 2.4, please.", "C1")],
            envelope["context"]!["existingComments"]!.AsArray().Select(comment => (comment!["id"]!.GetValue<string>(), comment["author"]!["login"]!.GetValue<string>(),
                comment["body"]!.GetValue<string>(), comment["replyTo"]?["id"]?.GetValue<string>())));
        Assert.Equal([("discussion.created", "D1", "participant", null), ("comment.created", "C1", "alice", "C1"), ("reaction.added", "C1", "carol", "C1"),
            ("comment.created", "R1", "bob", "C1")], Changes(envelope));
    }

    [Fact]
    public async Task A_long_enrolled_conversation_bootstraps_in_bounded_inputs_that_keep_every_comment_in_order()
    {
        await using var world = Enrolled();
        // GitHub bodies hold at most 65,536 characters; twenty long comments exceed the native input limit of 1,048,576.
        var bodies = Enumerable.Range(1, 20).Select(index => $"comment {index:D2} " + new string((char)('a' + index), 65_000)).ToArray();
        for (var index = 0; index < bodies.Length; index++) world.GitHub.Comment(1, $"C{index + 1}", bodies[index], Alice);
        for (var turns = -1; turns != world.Turns.Count;)
        {
            turns = world.Turns.Count;
            await world.RunOnce(1);
            Assert.True(world.Turns.Count < 30, "The conversation never finished dispatching");
        }

        var inputs = world.Turns.Select(turn => (Text: turn.Input, Envelope: JsonNode.Parse(turn.Input)!)).ToArray();
        // Each input stays within the 256 KiB batch bound, far below the native limit, so none is refused for its size.
        Assert.All(inputs, input => Assert.InRange(input.Text.Length, 1, 256 * 1024));
        Assert.Equal(["bootstrap", .. Enumerable.Repeat("continue", inputs.Length - 1)], inputs.Select(input => input.Envelope["_bridge"]!["mode"]!.GetValue<string>()));
        // Every comment arrives exactly once and in order: in the bootstrap context or, after it, in its change.
        Assert.Equal(bodies, inputs.SelectMany(input =>
            (input.Envelope["context"]?["existingComments"]?.AsArray() ?? []).Select(comment => comment!["body"]!.GetValue<string>())
            .Concat(input.Envelope["changes"]!.AsArray().Select(change => change!["subject"]!["body"]?.GetValue<string>()).OfType<string>())));
        Assert.Equal(Enumerable.Range(1, 20).Select(index => $"C{index}"), inputs.SelectMany(input => input.Envelope["changes"]!.AsArray())
            .Where(change => change!["kind"]!.GetValue<string>() == "comment.created").Select(change => change!["subject"]!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Mixed_batch_of_edits_reactions_answer_and_comments_keeps_identity_attribution_and_thread_routing()
    {
        await using var world = Enrolled();
        world.GitHub.Comment(1, "C1", "Which release should we test first?", Alice);
        world.GitHub.Comment(1, "R1", "Release 2.4, please.", Bob, replyTo: "C1");
        Assert.True(await world.RunOnce(1));

        world.GitHub.Answer(1, "C1");
        world.GitHub.Comment(1, "C1", "Edited: test 2.5 before 2.4.");
        world.GitHub.Comment(1, "C2", "Also check the installer.", Dave);
        world.GitHub.React(1, "R1", "RE1", "THUMBS_UP", "carol");
        Assert.True(await world.RunOnce(1));

        var text = world.Turns[1].Input;
        Assert.DoesNotContain(DiscussionBody, text);
        Assert.DoesNotContain("Which release should we test first?", text); // The superseded version of the edited comment.
        Assert.DoesNotContain("Release 2.4, please.", text); // The reaction's unchanged subject.
        var envelope = JsonNode.Parse(text)!;
        var changes = envelope["changes"]!.AsArray();
        // Only a change about the Discussion itself names it; comment changes rely on the batch's shared identity.
        Assert.All(changes.Skip(1), change => Assert.DoesNotContain("\"D1\"", change!.ToJsonString()));
        Assert.Equal([("discussion.stateChanged", "D1", "participant", null), ("comment.edited", "C1", "alice", "C1"), ("comment.created", "C2", "dave", "C2"),
            ("reaction.added", "R1", "carol", "C1")], Changes(envelope));
        Assert.Equal("C1", changes[0]!["subject"]!["answer"]!["id"]!.GetValue<string>());
        Assert.Equal("Edited: test 2.5 before 2.4.", changes[1]!["subject"]!["body"]!.GetValue<string>());
        Assert.Equal("Also check the installer.", changes[2]!["subject"]!["body"]!.GetValue<string>());
        Assert.Equal(("RE1", "THUMBS_UP", "bob", "C1"), (changes[3]!["reaction"]!["id"]!.GetValue<string>(), changes[3]!["reaction"]!["content"]!.GetValue<string>(),
            changes[3]!["subject"]!["author"]!["login"]!.GetValue<string>(), changes[3]!["subject"]!["replyTo"]!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Confirmed_deletions_reach_the_existing_session_once_with_attribution_and_surviving_thread_routing()
    {
        await using var world = Enrolled();
        world.GitHub.Comment(1, "C1", "Which release should we test first?", Alice);
        world.GitHub.Comment(1, "C2", "Ship the installer now.", Dave);
        world.GitHub.Comment(1, "R1", "Release 2.4, please.", Bob, replyTo: "C1");
        world.GitHub.Comment(1, "R2", "Agreed, ship it.", Erin, replyTo: "C2");
        Assert.True(await world.RunOnce(1));
        int Receipts() => world.GitHub.Writes("R1") + world.GitHub.Writes("R2") + world.GitHub.Writes("C2");
        var receipts = Receipts();

        world.GitHub.Delete(1, "R1"); // A reply whose thread remains.
        world.GitHub.Delete(1, "R2"); // A whole thread: its root and reply.
        world.GitHub.Delete(1, "C2");
        Assert.True(await world.RunOnce(1));

        Assert.Equal(2, world.Turns.Count);
        var text = world.Turns[1].Input;
        foreach (var body in new[] { "Release 2.4, please.", "Ship the installer now.", "Agreed, ship it." }) Assert.DoesNotContain(body, text);
        var envelope = JsonNode.Parse(text)!;
        Assert.Equal("continue", envelope["_bridge"]!["mode"]!.GetValue<string>());
        Assert.Equal(
        [
            ("comment.deleted", "C2", "dave", null, null),
            ("comment.deleted", "R1", "bob", "C1", "C1"),
            ("comment.deleted", "R2", "erin", "C2", null) // Its thread root is gone too, so a response goes top-level.
        ], Deletions(envelope));
        Assert.Equal(receipts, Receipts()); // A deleted source cannot carry an EYES receipt.

        Assert.True(await world.RunOnce(1));
        Assert.Equal(2, world.Turns.Count); // The confirmed deletion is not reported again.
    }

    [Fact]
    public async Task Rejected_batch_with_a_deletion_is_redelivered_in_the_compact_shape_with_the_deletion_intact()
    {
        await using var world = Enrolled();
        world.GitHub.Comment(1, "C1", "Which release should we test first?", Alice);
        world.GitHub.Comment(1, "R1", "Release 2.4, please.", Bob, replyTo: "C1");
        Assert.True(await world.RunOnce(1));

        world.GitHub.Delete(1, "R1");
        world.GitHub.Comment(1, "C2", "Also check the installer.", Dave);
        world.Answer(_ => throw new AgentRequestRejected("fixture rejection"));
        Assert.True(await world.RunOnce(1)); // A failed delivery waits for its retry; the observation itself succeeded.
        Assert.Contains(world.Logged("delivery-retry"), line => line.Contains("redelivery=true") && line.Contains("sources=C2 "));
        Assert.Equal(0, world.GitHub.Writes("C2")); // No EYES before a delivery of it is acknowledged.

        world.Clock.Advance(TimeSpan.FromMinutes(1)); // Past the first backoff.
        Assert.True(await world.RunOnce(1));

        var inputs = world.Turns.Select(turn => turn.Input).ToArray();
        Assert.Equal(3, inputs.Length);
        Assert.Equal(inputs[1], inputs[2].Replace(",\"redelivery\":true", "")); // The same compact input, marked once.
        Assert.DoesNotContain(DiscussionBody, inputs[2]);
        Assert.DoesNotContain("Release 2.4, please.", inputs[2]);
        var envelope = JsonNode.Parse(inputs[2])!;
        Assert.Equal(("continue", true), (envelope["_bridge"]!["mode"]!.GetValue<string>(), envelope["_bridge"]!["redelivery"]!.GetValue<bool>()));
        Assert.Equal(["id", "number", "url"], envelope["discussion"]!.AsObject().Select(property => property.Key));
        Assert.Equal([("comment.created", "C2", "dave", null, "C2"), ("comment.deleted", "R1", "bob", "C1", "C1")], Deletions(envelope));
        Assert.Equal(1, world.GitHub.Writes("C2"));

        Assert.True(await world.RunOnce(1));
        Assert.Equal(3, world.Turns.Count); // Delivered once acknowledged; nothing is sent again.
    }

    // A read that fails on any page throws in the adapter; the bridge then keeps the checkpoint it has.
    [Fact]
    public async Task A_failed_read_preserves_the_checkpoint_and_infers_no_deletion_until_a_complete_read_confirms_it()
    {
        await using var world = Enrolled();
        world.GitHub.Comment(1, "C1", "Which release should we test first?", Alice);
        world.GitHub.Comment(1, "C2", "Ship the installer now.", Dave);
        Assert.True(await world.RunOnce(1));

        world.GitHub.FailReads("Read", new IOException("HTTP 502"));
        Assert.False(await world.RunOnce(1));
        Assert.Single(world.Turns);

        world.GitHub.FailReads("Read", null);
        Assert.True(await world.RunOnce(1));
        Assert.Single(world.Turns); // C2 is neither deleted nor re-created: the checkpoint still holds it.

        world.GitHub.Delete(1, "C2");
        Assert.True(await world.RunOnce(1));
        Assert.Equal([("comment.deleted", "C2", "dave", null, null)], Deletions(world.Envelope(1)));
    }

    [Fact]
    public async Task Input_during_a_running_turn_waits_for_its_end_and_its_EYES_follow_the_acceptance_of_its_batch()
    {
        await using var world = Enrolled();
        await world.Begin(1);
        await world.Poll();
        await world.Deliver();
        Assert.True(world.GitHub.HasEyes("D1"));

        world.GitHub.Comment(1, "C1", "first while active", Alice);
        world.GitHub.Comment(1, "C2", "second while active", Bob);
        for (var pass = 0; pass < 2; pass++) // Observations and dispatch passes, not elapsed time, establish that the input waits.
        {
            Assert.True(await world.Poll());
            await world.Dispatch();
            await world.Deliver();
        }
        Assert.Single(world.Turns);
        Assert.Equal(1, world.GitHub.Calls.Count(call => call.StartsWith("addReaction ", StringComparison.Ordinal)));

        string[]? retained = null;
        world.Answer(request =>
        {
            retained = [.. new ControllerStateFiles(world.StatePath).ReadOutbox()!.Operations.Cast<ReceiptOperation>().Select(operation => operation.SubjectId)];
            return new("T2", false);
        });
        world.Finish(1);
        await world.Dispatch();
        Assert.Equal(["C1", "C2"], BridgeWorld.Subjects(world.Envelope(1)));
        Assert.Empty(retained!); // No receipt was retained before the agent accepted the batch carrying its source.
        await world.Deliver();
        Assert.True(world.GitHub.HasEyes("C1") && world.GitHub.HasEyes("C2"));
        Assert.Null(world.Record(1)!["waiting"]);
    }

    [Fact]
    public async Task Oversized_waiting_input_splits_into_ordered_batches_that_each_wait_for_the_previous_turn()
    {
        await using var world = Enrolled();
        await world.Begin(1);
        await world.Poll();
        await world.Deliver();
        // Five maximum-size GitHub comment bodies exceed one batch.
        var bodies = Enumerable.Range(1, 5).Select(number => number + ":" + new string('x', 65_534)).ToArray();
        for (var number = 1; number <= 5; number++) world.GitHub.Comment(1, "C" + number, bodies[number - 1], new("author" + number, "User"));
        await world.Poll();
        string[] sources = ["D1", "C1", "C2", "C3", "C4", "C5"];

        var delivered = new List<string>();
        List<string> received = ["D1"];
        while (delivered.Count < bodies.Length)
        {
            var turns = world.Turns.Count;
            await world.Dispatch();
            Assert.Equal(turns, world.Turns.Count); // The remainder waits while the previous batch's turn runs.
            world.Finish(1);
            await world.Dispatch();
            var turn = world.Turns[turns];
            Assert.InRange(turn.Input.Length, 1, 256 * 1024);
            var batch = JsonNode.Parse(turn.Input)!["changes"]!.AsArray().Select(change => change!["subject"]!["body"]!.GetValue<string>()).ToArray();
            Assert.NotEmpty(batch);
            delivered.AddRange(batch);
            // EYES follow each batch: exactly the sources accepted so far, none of those still waiting.
            received.AddRange(BridgeWorld.Subjects(world.Envelope(turns)));
            await world.Deliver();
            Assert.Equal(received, sources.Where(world.GitHub.HasEyes));
        }
        Assert.Equal(bodies, delivered);
        Assert.InRange(world.Turns.Count - 1, 2, 5);
        Assert.Null(world.Record(1)!["waiting"]);
    }

    [Fact]
    public async Task Rejected_input_is_retried_after_its_backoff_as_a_marked_redelivery_and_later_input_is_ordinary()
    {
        await using var world = Enrolled();
        await world.Begin(1);
        await world.Poll();
        world.Finish(1);
        world.GitHub.Comment(1, "C1", "please answer", Alice);
        world.Answer(_ => throw new AgentRequestRejected("fixture rejection"));
        await world.Poll();
        Assert.Null(world.Envelope(1)["_bridge"]!["redelivery"]);

        // Neither a blocked Discussion nor a retry on every pass: the first retry waits five seconds.
        Assert.True(await world.Poll());
        Assert.Equal(TimeSpan.FromSeconds(5), await world.Dispatch());
        Assert.Equal(2, world.Turns.Count);
        world.Clock.Advance(TimeSpan.FromSeconds(5));
        await world.Dispatch();
        var retried = world.Envelope(2);
        Assert.True(retried["_bridge"]!["redelivery"]!.GetValue<bool>());
        Assert.Equal(["C1"], BridgeWorld.Subjects(retried));
        await world.Deliver();
        Assert.True(world.GitHub.HasEyes("C1"));

        world.Finish(1);
        world.GitHub.Comment(1, "C2", "follow-up", Bob);
        await world.Poll();
        Assert.Null(world.Envelope(3)["_bridge"]!["redelivery"]);
        Assert.Equal(["C2"], BridgeWorld.Subjects(world.Envelope(3)));
        Assert.Null(world.Record(1)!["pending"]);
        Assert.Null(world.Record(1)!["waiting"]);
    }

    [Fact]
    public async Task A_renamed_Discussion_moved_to_another_category_sends_one_state_change_with_its_new_title_and_category()
    {
        await using var world = Enrolled();
        await world.Begin(1);
        await world.Poll();
        world.Finish(1);
        world.GitHub.Move(1, "Renamed", "Q&A");

        await world.Poll();

        var change = Assert.Single(world.Envelope(1)["changes"]!.AsArray())!;
        Assert.Equal("discussion.stateChanged", change["kind"]!.GetValue<string>());
        Assert.Equal("""{"id":"D1","url":"https://github.com/owner/repository/discussions/1","author":{"__typename":"User","login":"participant"},"title":"Renamed","category":{"id":"DIC_Q\u0026A","name":"Q\u0026A"}}""",
            change["subject"]!.ToJsonString());
    }

    [Fact]
    public async Task Reactions_are_told_apart_by_ID_so_one_removed_and_one_added_of_the_same_content_are_both_sent()
    {
        await using var world = Enrolled();
        world.GitHub.React(1, "D1", "R1", "HEART", "alice");
        await world.Begin(1);
        await world.Poll();
        world.Finish(1);
        world.GitHub.Unreact(1, "R1");
        world.GitHub.React(1, "D1", "R2", "HEART", "bob");

        await world.Poll();

        Assert.Equal([("reaction.added", "R2"), ("reaction.removed", "R1")], world.Envelope(1)["changes"]!.AsArray()
            .Select(change => (change!["kind"]!.GetValue<string>(), change["reaction"]!["id"]!.GetValue<string>())));
    }

    [Fact]
    public async Task The_App_s_own_reply_is_checkpointed_without_input_so_its_deletion_reaches_the_session()
    {
        await using var world = Enrolled();
        await world.Begin(1);
        await world.Poll();
        world.Finish(1);
        world.GitHub.Comment(1, "C9", "An answer.", InMemoryDiscussions.AppAccount);
        Assert.True(await world.Poll());
        Assert.Single(world.Turns);
        world.GitHub.Delete(1, "C9");

        await world.Poll();

        Assert.Equal("comment.deleted", world.Envelope(1)["changes"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal(["C9"], BridgeWorld.Subjects(world.Envelope(1)));
    }

    [Fact]
    public async Task A_bootstrap_waiting_for_its_retry_carries_the_Discussion_as_last_observed()
    {
        await using var world = Enrolled();
        await world.Begin(1);
        world.Answer(_ => throw new AgentRequestRejected("fixture rejection"));
        await world.Poll();
        world.GitHub.Edit(1, "Edited while the bootstrap waited");
        await world.Poll();

        world.Clock.Advance(TimeSpan.FromSeconds(5));
        await world.Dispatch();

        var retried = world.Envelope(1);
        Assert.Equal("bootstrap", retried["_bridge"]!["mode"]!.GetValue<string>());
        Assert.Equal("Edited while the bootstrap waited", retried["discussion"]!["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_retried_delivery_once_accepted_leaves_no_retry_on_the_input_still_waiting()
    {
        await using var world = Enrolled();
        await world.Begin(1);
        await world.Poll();
        world.Finish(1);
        world.GitHub.Comment(1, "C1", "please answer", Alice);
        world.Answer(_ => throw new AgentRequestRejected("fixture rejection"));
        await world.Poll();
        world.GitHub.Comment(1, "C2", "follow-up", Bob);
        await world.Poll();

        world.Clock.Advance(TimeSpan.FromSeconds(5));
        await world.Dispatch();

        Assert.Equal(["C1"], BridgeWorld.Subjects(world.Envelope(2)));
        var waiting = world.Record(1)!["waiting"]!;
        Assert.Equal(["C2"], waiting["inputs"]!.AsArray().Select(input => input!["change"]!["subject"]!["id"]!.GetValue<string>()));
        Assert.Null(waiting["retry"]);
    }

    [Fact]
    public async Task A_delivery_interrupted_by_a_restart_is_redelivered_and_once_accepted_never_resent_though_its_receipt_failed()
    {
        await using var world = Enrolled();
        await world.Begin(1);
        await world.Poll();
        await world.Deliver();
        world.Finish(1);
        world.GitHub.Comment(1, "C1", "please answer", Alice);
        world.Answer(_ =>
        {
            world.Interrupt(); // The controller stops before any acknowledgement arrives.
            throw new OperationCanceledException();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(world.Poll);
        Assert.Null(world.Envelope(1)["_bridge"]!["redelivery"]);

        // The next lifetime redelivers it at once, marked; GitHub then refuses its EYES.
        world.GitHub.Fail(new(GitHubFailure.Permanent, "FORBIDDEN: Resource not accessible by integration"));
        await world.Begin(1);
        await world.Poll();
        var redelivered = world.Envelope(2);
        Assert.True(redelivered["_bridge"]!["redelivery"]!.GetValue<bool>());
        Assert.Equal(["C1"], BridgeWorld.Subjects(redelivered));
        Assert.False(await world.Deliver());
        var failure = Assert.Single(world.Logged("receipt-error"));
        Assert.StartsWith(BridgeWorld.Url(1) + "#discussioncomment-C1 subject=C1 attempt=1 Permanent: FORBIDDEN: Resource not accessible by integration;", failure);
        Assert.Contains("permanent failure", failure);

        // Accepted input is never sent again: not in this lifetime, nor after a restart.
        world.Finish(1);
        world.Clock.Advance(TimeSpan.FromMinutes(30));
        for (var lifetime = 0; lifetime < 2; lifetime++)
        {
            Assert.True(await world.Poll());
            await world.Dispatch();
            Assert.True(await world.Deliver());
            await world.Begin(1);
        }
        Assert.Equal(3, world.Turns.Count);
        Assert.Equal(1, world.GitHub.Writes("C1"));
        Assert.False(world.GitHub.HasEyes("C1"));
        Assert.Null(world.Record(1)!["pending"]);
        Assert.Null(world.Record(1)!["waiting"]);
    }

    [Fact]
    public async Task A_delivery_a_controller_from_before_the_waiting_queue_left_pending_ends_at_the_next_start_and_its_input_is_observed_again()
    {
        await using var world = Enrolled();
        await world.Begin(1);
        await world.Poll();
        await world.Deliver();
        world.Finish(1);
        await world.End();
        // That controller kept no waiting input and no input count, and checkpointed a source only once its delivery was accepted.
        world.GitHub.Comment(1, "C1", "please answer", Alice);
        var state = JsonNode.Parse(File.ReadAllText(world.StatePath))!;
        state["discussions"]!["D1"]!["pending"] = new JsonObject { ["operation"] = "send", ["since"] = "2026-10-24T09:59:00.0000000+00:00", ["sourceIds"] = new JsonArray("C1") };
        File.WriteAllText(world.StatePath, state.ToJsonString());

        await world.Begin(1);
        Assert.Null(world.Record(1)!["pending"]);
        Assert.True(await world.Poll());

        var observed = world.Envelope(1);
        Assert.Null(observed["_bridge"]!["redelivery"]);
        Assert.Equal(["C1"], BridgeWorld.Subjects(observed));
    }

    [Fact]
    public async Task A_checkpoint_holding_a_key_no_writer_writes_is_kept_until_the_next_observation_rewrites_it_without_input()
    {
        await using var world = Enrolled();
        await world.Begin(1);
        await world.Poll();
        world.Finish(1);
        await world.End();
        var state = JsonNode.Parse(File.ReadAllText(world.StatePath))!;
        state["discussions"]!["D1"]!["sources"]!["D1"]!["later"] = "checkpoint";
        File.WriteAllText(world.StatePath, state.ToJsonString());

        // The next start saves as it ends the earlier lifetime's turn, keeping the key; the next observation finds no change.
        await world.Begin(1);
        Assert.Null(world.Record(1)!["running"]);
        Assert.Equal("checkpoint", world.Record(1)!["sources"]!["D1"]!["later"]!.GetValue<string>());
        Assert.True(await world.Poll());

        Assert.Single(world.Turns);
        Assert.Null(world.Record(1)!["sources"]!["D1"]!["later"]);
    }

    [Fact]
    public async Task A_receipt_superseded_by_new_input_during_a_rate_limit_is_written_once_without_dropping_the_others()
    {
        await using var world = Enrolled();
        world.GitHub.Comment(1, "C1", "First observation.");
        world.GitHub.Fail(new(GitHubFailure.RateLimited, "secondary rate limit", retryAfter: TimeSpan.FromSeconds(61)));
        Assert.False(await world.RunOnce(1)); // D1 is rate-limited; C1 waits behind the installation-wide pause.

        world.Clock.Advance(TimeSpan.FromSeconds(30));
        world.GitHub.Comment(1, "C1", "Edited observation.");
        Assert.False(await world.RunOnce(1));
        Assert.Equal(2, world.Turns.Count); // The edit is new input and supersedes C1's still-pending receipt.

        world.Clock.Advance(TimeSpan.FromSeconds(32));
        Assert.True(await world.RunOnce(1));
        Assert.True(world.GitHub.HasEyes("D1") && world.GitHub.HasEyes("C1"));
        Assert.Equal((2, 1), (world.GitHub.Writes("D1"), world.GitHub.Writes("C1")));
        Assert.Equal(2, world.Turns.Count);
    }
}
