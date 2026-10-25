using System.Diagnostics;
using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>The <see cref="IAgentInput"/> adapter: agent input as the JSON ec1ccf7 sent, from <see cref="DiscussionJson"/>'s shapes.</summary>
sealed class AgentInputJson : IAgentInput
{
    public string Discussion(Discussion discussion) => DiscussionJson.Public(discussion).ToJsonString();

    public string Comment(DiscussionComment comment) => DiscussionJson.Public(comment).ToJsonString();

    public string Context(IReadOnlyList<DiscussionComment> comments, int later)
    {
        var context = new JsonObject { ["existingComments"] = new JsonArray([.. comments.Select(comment => (JsonNode)DiscussionJson.Public(comment))]) };
        if (later > 0) context["laterComments"] = later;
        return context.ToJsonString();
    }

    // The subject's identity and attribution, then the body and each state field the change carries.
    public string Change(AgentChange change)
    {
        var subject = DiscussionJson.Subject(change.Subject);
        if (change.Body is { } body) subject["body"] = body;
        foreach (var field in change.Fields) subject[Name(field)] = Value(field, change.State!);
        var item = new JsonObject { ["kind"] = change.Kind, ["subject"] = subject, ["replyTarget"] = new JsonObject { ["replyToId"] = change.ReplyToId } };
        if (change.Reaction is { } reaction) item["reaction"] = DiscussionJson.Public(reaction);
        return item.ToJsonString();
    }

    public string Deletion(AgentDeletion deletion)
    {
        var subject = new JsonObject { ["id"] = deletion.Id, ["author"] = deletion.AuthorLogin is { } login ? new JsonObject { ["login"] = login } : null };
        if (deletion.ReplyTo is { } root) subject["replyTo"] = new JsonObject { ["id"] = root };
        return new JsonObject { ["kind"] = "comment.deleted", ["subject"] = subject, ["replyTarget"] = new JsonObject { ["replyToId"] = deletion.ReplyToId } }
            .ToJsonString();
    }

    public string Envelope(AgentEnvelope envelope)
    {
        var bootstrap = envelope.ContextJson is not null;
        var discussion = JsonNode.Parse(envelope.DiscussionJson)!;
        var identity = envelope.Identity;
        var item = new JsonObject
        {
            ["_bridge"] = new JsonObject { ["channel"] = "github-discussions", ["mode"] = bootstrap ? "bootstrap" : "continue", ["repository"] = envelope.Repository, ["agentLogin"] = identity.AgentLogin, ["mentionLogin"] = identity.MentionUser?.Login },
            ["discussion"] = bootstrap ? discussion : new JsonObject { ["id"] = discussion.Text("id"), ["number"] = discussion["number"]?.Copy(), ["url"] = discussion.Text("url") }
        };
        if (envelope.Redelivery) item["_bridge"]!["redelivery"] = true;
        if (bootstrap) item["context"] = JsonNode.Parse(envelope.ContextJson!);
        item["changes"] = new JsonArray([.. envelope.Changes.Select(change => JsonNode.Parse(change))]);
        return item.ToJsonString();
    }

    public string SubjectUrl(string change) => JsonNode.Parse(change)!["subject"]!.Text("url");

    static string Name(CheckpointPart field) => field switch
    {
        CheckpointPart.Title => "title", CheckpointPart.Closed => "closed", CheckpointPart.Locked => "locked", CheckpointPart.Answer => "answer",
        CheckpointPart.Category => "category", _ => throw new UnreachableException()
    };

    static JsonNode? Value(CheckpointPart field, CheckpointState state) => field switch
    {
        CheckpointPart.Title => state.Title, CheckpointPart.Closed => state.Closed, CheckpointPart.Locked => state.Locked,
        CheckpointPart.Answer => DiscussionJson.Answer(state.AnswerId), CheckpointPart.Category => DiscussionJson.Category(state.Category),
        _ => throw new UnreachableException()
    };
}
