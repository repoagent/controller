using System.Text.Json.Nodes;
using Xunit;

namespace RepoAgentController.Tests;

/// <summary>
/// The bridge's JSON, mapped from the Discussion records, is byte for byte what ec1ccf7 produced by copying GitHub's nodes.
/// <c>DiscussionJsonGolden.json</c> holds the GraphQL nodes of two observations of one conversation, and what ec1ccf7's real
/// entrypoint made of them: the first observation's agent input and checkpoints, and the second's checkpoints and waiting
/// input. The conversation covers GitHub's field order, absent and <c>null</c> fields, a <c>null</c> author and reaction user,
/// the App's own reply and reaction, escaped text, and between the observations a close with an answer, a lock, an edit, a new
/// comment, a deleted reply and reaction changes. The bridge does not run here: these cases pin what the mapper gives it, and the
/// saved changes it makes of that. BridgeInputTests runs the same conversation through the bridge and compares the agent input.
/// </summary>
public sealed class DiscussionJsonGoldenTests
{
    const string Root = "D_kwDOGolden7";
    static readonly AppIdentity App = new("test-app");
    internal static readonly JsonNode Golden = Load();

    [Theory]
    [InlineData("first")]
    [InlineData("second")]
    public void Checkpoints_are_the_bytes_ec1ccf7_saved_so_an_upgraded_controller_observes_no_change(string observation)
    {
        var saved = Golden["ec1ccf7"]![observation]!["sources"]!.AsObject();

        Assert.Equal(saved.Select(source => (source.Key, source.Value!.ToJsonString())),
            Checkpoints(Observe(observation)).Select(source => (source.Key, source.Value!.ToJsonString())));
    }

    [Fact]
    public void A_waiting_input_saves_the_Discussion_and_receipts_as_ec1ccf7_did_and_reads_each_receipt_back()
    {
        var waiting = Golden["ec1ccf7"]!["second"]!["waiting"]!;
        var conversation = Observe("second");

        Assert.Equal(waiting["discussion"]!.ToJsonString(), DiscussionJson.Public(conversation.Discussion).ToJsonString());
        var receipts = waiting["inputs"]!.AsArray().Select(input => input!["receipt"]).OfType<JsonNode>().ToArray();
        Assert.Equal(5, receipts.Length); // A deletion has no receipt.
        foreach (var receipt in receipts)
            Assert.Equal(receipt.ToJsonString(),
                ControllerStateFiles.Receipt(EyesReceipt.Of(conversation.Messages.Single(source => source.Message.Id == receipt["id"]!.GetValue<string>()).Message)).ToJsonString());
        // Dispatch reads a saved receipt back to decide whether the App's EYES is still to add; only the first comment has it.
        Assert.Equal([false, true, false, false, false],
            receipts.Select(receipt => DiscussionMessage.HasEyes(ControllerStateFiles.ReadReceipt(receipt.AsObject()).ReactionGroups)));
    }

    [Fact]
    public void The_waiting_changes_carry_the_mapped_subjects_bodies_state_and_reactions_ec1ccf7_saved()
    {
        var saved = Golden["ec1ccf7"]!["second"]!["waiting"]!["inputs"]!.AsArray().Select(input => input!["change"]!.ToJsonString());
        var before = Checkpoints(Observe("first"));
        var conversation = Observe("second");
        var after = Checkpoints(conversation);
        JsonObject Changed(string id, string field, params string[] names)
        {
            var subject = Subject(conversation, id);
            foreach (var name in names) subject[name] = after[id]![field]![name]!.DeepClone();
            return subject;
        }

        Assert.Equal(
        [
            Change("discussion.stateChanged", Changed(Root, "state", "closed", "locked", "answer"), null),
            Change("reaction.removed", Subject(conversation, CommentId(101)), CommentId(101), before[CommentId(101)]!["reactions"]![ReactionId(4)]),
            Change("comment.edited", Changed(CommentId(102), "content", "body"), CommentId(102)),
            Change("comment.created", Changed(CommentId(105), "content", "body"), CommentId(105)),
            Change("reaction.added", Subject(conversation, CommentId(104)), CommentId(101), after[CommentId(104)]!["reactions"]![ReactionId(5)]),
            // A deletion is attributed and routed from the reply's checkpoint, as nothing else is left of it.
            Change("comment.deleted", new JsonObject
            {
                ["id"] = CommentId(103), ["author"] = before[CommentId(103)]!["content"]!["author"]!.DeepClone(),
                ["replyTo"] = new JsonObject { ["id"] = before[CommentId(103)]!["replyTo"]!.DeepClone() }
            }, CommentId(101))
        ], saved);
    }

    // The records GitHubDiscussions reads from the observation's nodes: the Discussion, its top-level comments in order, then
    // the replies to each, each with its reactions. No mention user is configured.
    internal static DiscussionConversation Observe(string observation)
    {
        var world = Golden["github"]![observation]!;
        IReadOnlyList<Reaction> Reactions(string id) => [.. world["reactions"]![id]?.AsArray().Select(node => DiscussionJson.ReadReaction(node!)) ?? []];
        var comments = world["comments"]!.AsArray().Select(node => node!).ToArray();
        var replies = comments.SelectMany(root => world["replies"]![root["id"]!.GetValue<string>()]?.AsArray().Select(node => node!) ?? []);
        var discussion = DiscussionJson.ReadDiscussion(world["discussion"]!, null);
        return new(discussion, Reactions(discussion.Id),
            [.. comments.Concat(replies).Select(node => DiscussionJson.ReadComment(node, Reactions(node["id"]!.GetValue<string>()), null))]);
    }

    // Each message's checkpoint by ID, in the conversation's order, as the bridge saves them.
    static JsonObject Checkpoints(DiscussionConversation conversation) => new(conversation.Messages.Select(source =>
        KeyValuePair.Create(source.Message.Id, (JsonNode?)ControllerStateFiles.Checkpoint(SourceCheckpoint.Of(source.Message, source.Reactions, App)))));

    static JsonObject Subject(DiscussionConversation conversation, string id) =>
        DiscussionJson.Subject(conversation.Messages.Single(source => source.Message.Id == id).Message);

    static string Change(string kind, JsonObject subject, string? replyToId, JsonNode? reaction = null)
    {
        var change = new JsonObject { ["kind"] = kind, ["subject"] = subject, ["replyTarget"] = new JsonObject { ["replyToId"] = replyToId } };
        if (reaction is not null) change["reaction"] = reaction.DeepClone();
        return change.ToJsonString();
    }

    static string CommentId(int number) => "DC_kwDOGolden" + number;
    static string ReactionId(int number) => "REA_kwDOGolden" + number;

    static JsonNode Load()
    {
        using var golden = typeof(DiscussionJsonGoldenTests).Assembly.GetManifestResourceStream("DiscussionJsonGolden.json")!;
        return JsonNode.Parse(golden)!;
    }
}
