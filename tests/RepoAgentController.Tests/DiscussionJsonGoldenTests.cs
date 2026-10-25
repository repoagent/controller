using System.Text.Json.Nodes;
using Xunit;

namespace RepoAgentController.Tests;

/// <summary>
/// The bridge's JSON, mapped from the Discussion records, is byte for byte what ec1ccf7 produced by copying GitHub's nodes.
/// <c>DiscussionJsonGolden.json</c> holds the GraphQL nodes of two observations of one conversation, and what ec1ccf7's real
/// entrypoint made of them: the first observation's agent input and checkpoints, and the second's checkpoints and waiting
/// input. The conversation covers GitHub's field order, absent and <c>null</c> fields, a <c>null</c> author and reaction user,
/// the App's own reply and reaction, escaped text, and edits, deletions and state changes between the observations.
/// </summary>
public sealed class DiscussionJsonGoldenTests
{
    static readonly AppIdentity App = new("test-app");
    static readonly JsonNode Golden = Load();

    [Fact]
    public void Agent_input_carries_the_Discussion_its_comments_and_each_change_subject_as_ec1ccf7_sent_them()
    {
        var text = Golden["ec1ccf7"]!["first"]!["agentInput"]!.GetValue<string>();
        var sent = JsonNode.Parse(text)!;
        Assert.Equal(text, sent.ToJsonString()); // Reserializing is exact, so each part below compares the bytes sent.
        var (conversation, sources) = Observe("first");

        Assert.Equal(sent["discussion"]!.ToJsonString(), DiscussionJson.Public(conversation.Discussion).ToJsonString());
        Assert.Equal(sent["context"]!["existingComments"]!.AsArray().Select(comment => comment!.ToJsonString()),
            conversation.Comments.Select(comment => DiscussionJson.Public(comment).ToJsonString()));
        var changes = sent["changes"]!.AsArray().Select(change => change!).ToArray();
        Assert.Equal(7, changes.Length);
        foreach (var change in changes)
        {
            var (source, reactions) = sources.Single(candidate => candidate.Source.Id == change["subject"]!["id"]!.GetValue<string>());
            Assert.Equal(change["subject"]!.ToJsonString(), DiscussionJson.Subject(source).ToJsonString());
            if (change["reaction"] is { } reaction)
                Assert.Equal(reaction.ToJsonString(), DiscussionJson.Checkpoint(source, reactions, App)["reactions"]![reaction["id"]!.GetValue<string>()]!.ToJsonString());
        }
    }

    [Theory]
    [InlineData("first")]
    [InlineData("second")]
    public void Checkpoints_are_the_bytes_ec1ccf7_saved_so_an_upgraded_controller_observes_no_change(string observation)
    {
        var saved = Golden["ec1ccf7"]![observation]!["sources"]!.AsObject();
        var (_, sources) = Observe(observation);

        Assert.Equal(saved.Select(source => (source.Key, source.Value!.ToJsonString())),
            sources.Select(source => (source.Source.Id, DiscussionJson.Checkpoint(source.Source, source.Reactions, App).ToJsonString())));
    }

    [Fact]
    public void A_waiting_input_saves_the_Discussion_and_receipts_as_ec1ccf7_did_and_reads_each_receipt_back()
    {
        var waiting = Golden["ec1ccf7"]!["second"]!["waiting"]!;
        var (conversation, sources) = Observe("second");

        Assert.Equal(waiting["discussion"]!.ToJsonString(), DiscussionJson.Public(conversation.Discussion).ToJsonString());
        var receipts = waiting["receipts"]!.AsArray().Select(receipt => receipt!).ToArray();
        foreach (var receipt in receipts)
            Assert.Equal(receipt.ToJsonString(),
                DiscussionJson.Receipt(sources.Single(source => source.Source.Id == receipt["id"]!.GetValue<string>()).Source).ToJsonString());
        // Dispatch reads a saved receipt back to decide whether the App's EYES is still to add; only the first comment has it.
        Assert.Equal([false, true, false, false, false],
            receipts.Select(receipt => DiscussionMessage.HasEyes(DiscussionJson.ReadReactionGroups(receipt["reactionGroups"]))));
    }

    // The records GitHubDiscussions reads from the observation's nodes: the Discussion, then its top-level comments in order,
    // then the replies to each, each with its reactions. No mention user is configured.
    static (DiscussionConversation Conversation, (DiscussionMessage Source, IReadOnlyList<Reaction> Reactions)[] Sources) Observe(string observation)
    {
        var world = Golden["github"]![observation]!;
        IReadOnlyList<Reaction> Reactions(string id) => [.. world["reactions"]![id]?.AsArray().Select(node => DiscussionJson.ReadReaction(node!)) ?? []];
        var comments = world["comments"]!.AsArray().Select(node => node!).ToArray();
        var replies = comments.SelectMany(root => world["replies"]![root["id"]!.GetValue<string>()]?.AsArray().Select(node => node!) ?? []);
        var discussion = DiscussionJson.ReadDiscussion(world["discussion"]!, null);
        var conversation = new DiscussionConversation(discussion, Reactions(discussion.Id),
            [.. comments.Concat(replies).Select(node => DiscussionJson.ReadComment(node, Reactions(node["id"]!.GetValue<string>()), null))]);
        return (conversation, [(discussion, conversation.Reactions), .. conversation.Comments.Select(comment => ((DiscussionMessage)comment, comment.Reactions))]);
    }

    static JsonNode Load()
    {
        using var golden = typeof(DiscussionJsonGoldenTests).Assembly.GetManifestResourceStream("DiscussionJsonGolden.json")!;
        return JsonNode.Parse(golden)!;
    }
}
