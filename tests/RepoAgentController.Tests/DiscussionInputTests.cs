using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace RepoAgentController.Tests;

// The controller entrypoint, its state file and envelope serialization are real; GitHub reads and the native peer are
// controlled processes, and each assertion reads the input text the native process received. Not live-provider evidence.
[Collection("Managed checkout environment")]
public sealed class DiscussionInputTests
{
    const string DiscussionBody = "Please review the fixture.";

    [Fact]
    public async Task New_reply_in_an_existing_session_carries_only_its_content_attribution_and_routing()
    {
        using var controller = new GitHubOutboxTests.Controller();
        controller.Comment("C1", "Which release should we test first?", author: "alice");
        Assert.Equal(0, await controller.RunOnce());

        controller.Comment("R1", "Release 2.4, please.", author: "bob", replyTo: "C1");
        Assert.Equal(0, await controller.RunOnce());

        var text = controller.Inputs[1];
        Assert.DoesNotContain(DiscussionBody, text);
        Assert.DoesNotContain("Which release should we test first?", text);
        Assert.DoesNotContain("\"Fixture\"", text); // The unchanged title.
        Assert.Single(Regex.Matches(text, "\"D1\"")); // The shared Discussion identity appears once per batch.
        var envelope = JsonNode.Parse(text)!;
        Assert.Equal("continue", envelope["_bridge"]!["mode"]!.GetValue<string>());
        Assert.Equal("D1", envelope["discussion"]!["id"]!.GetValue<string>());
        var change = Assert.Single(envelope["changes"]!.AsArray())!;
        Assert.Equal("comment.created", change["kind"]!.GetValue<string>());
        Assert.Equal("R1", change["subject"]!["id"]!.GetValue<string>());
        Assert.Equal("bob", change["subject"]!["author"]!["login"]!.GetValue<string>());
        Assert.Equal("Release 2.4, please.", change["subject"]!["body"]!.GetValue<string>());
        Assert.Equal("C1", change["replyTarget"]!["replyToId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Bootstrap_supplies_the_initial_conversation_once_with_attribution_and_thread_routing()
    {
        using var controller = new GitHubOutboxTests.Controller();
        controller.Comment("C1", "Which release should we test first?", author: "alice");
        controller.Comment("R1", "Release 2.4, please.", author: "bob", replyTo: "C1");
        controller.React("C1", "RE1", "HEART", "carol");
        Assert.Equal(0, await controller.RunOnce());

        var text = Assert.Single(controller.Inputs);
        foreach (var body in new[] { DiscussionBody, "Which release should we test first?", "Release 2.4, please." })
            Assert.Single(Regex.Matches(text, Regex.Escape(body)));
        var envelope = JsonNode.Parse(text)!;
        Assert.Equal("bootstrap", envelope["_bridge"]!["mode"]!.GetValue<string>());
        var discussion = envelope["discussion"]!;
        Assert.Equal(("D1", "Fixture", DiscussionBody, "participant"), (discussion["id"]!.GetValue<string>(), discussion["title"]!.GetValue<string>(),
            discussion["body"]!.GetValue<string>(), discussion["author"]!["login"]!.GetValue<string>()));
        Assert.Equal(
        [
            ("C1", "alice", "Which release should we test first?", null),
            ("R1", "bob", "Release 2.4, please.", "C1")
        ], envelope["context"]!["existingComments"]!.AsArray().Select(comment => (
            comment!["id"]!.GetValue<string>(), comment["author"]!["login"]!.GetValue<string>(), comment["body"]!.GetValue<string>(),
            comment["replyTo"]?["id"]?.GetValue<string>())));
        Assert.Equal(
        [
            ("discussion.created", "D1", "participant", null),
            ("comment.created", "C1", "alice", "C1"),
            ("reaction.added", "C1", "carol", "C1"),
            ("comment.created", "R1", "bob", "C1")
        ], envelope["changes"]!.AsArray().Select(change => (
            change!["kind"]!.GetValue<string>(), change["subject"]!["id"]!.GetValue<string>(),
            (change["reaction"]?["user"] ?? change["subject"]!["author"])!["login"]!.GetValue<string>(),
            change["replyTarget"]!["replyToId"]?.GetValue<string>())));
    }

    [Fact]
    public async Task Mixed_batch_of_edits_reactions_answer_and_comments_keeps_identity_attribution_and_thread_routing()
    {
        using var controller = new GitHubOutboxTests.Controller();
        controller.Comment("C1", "Which release should we test first?", author: "alice");
        controller.Comment("R1", "Release 2.4, please.", author: "bob", replyTo: "C1");
        Assert.Equal(0, await controller.RunOnce());

        controller.Answer("C1");
        controller.Comment("C1", "Edited: test 2.5 before 2.4.");
        controller.Comment("C2", "Also check the installer.", author: "dave");
        controller.React("R1", "RE1", "THUMBS_UP", "carol");
        Assert.Equal(0, await controller.RunOnce());

        var text = controller.Inputs[1];
        Assert.DoesNotContain(DiscussionBody, text);
        Assert.DoesNotContain("Which release should we test first?", text); // The superseded version of the edited comment.
        Assert.DoesNotContain("Release 2.4, please.", text); // The reaction's unchanged subject.
        var changes = JsonNode.Parse(text)!["changes"]!.AsArray();
        // Only a change about the Discussion itself names it; comment changes rely on the batch's shared identity.
        Assert.All(changes.Skip(1), change => Assert.DoesNotContain("\"D1\"", change!.ToJsonString()));
        Assert.Equal(
        [
            ("discussion.stateChanged", "D1", "participant", null),
            ("comment.edited", "C1", "alice", "C1"),
            ("comment.created", "C2", "dave", "C2"),
            ("reaction.added", "R1", "carol", "C1")
        ], changes.Select(change => (
            change!["kind"]!.GetValue<string>(), change["subject"]!["id"]!.GetValue<string>(),
            (change["reaction"]?["user"] ?? change["subject"]!["author"])!["login"]!.GetValue<string>(),
            change["replyTarget"]!["replyToId"]?.GetValue<string>())));
        Assert.Equal("C1", changes[0]!["subject"]!["answer"]!["id"]!.GetValue<string>());
        Assert.Equal("Edited: test 2.5 before 2.4.", changes[1]!["subject"]!["body"]!.GetValue<string>());
        Assert.Equal("Also check the installer.", changes[2]!["subject"]!["body"]!.GetValue<string>());
        Assert.Equal(("RE1", "THUMBS_UP", "bob", "C1"), (changes[3]!["reaction"]!["id"]!.GetValue<string>(), changes[3]!["reaction"]!["content"]!.GetValue<string>(),
            changes[3]!["subject"]!["author"]!["login"]!.GetValue<string>(), changes[3]!["subject"]!["replyTo"]!["id"]!.GetValue<string>()));
    }
}
