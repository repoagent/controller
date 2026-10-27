using System.Globalization;
using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>
/// Maps Discussions between GitHub's GraphQL nodes, the core's records, and the JSON the bridge sends as agent input. The JSON
/// keeps GitHub's field order and shapes: an absent author is <c>null</c>, a comment's <c>replyTo</c> is <c>null</c> or an
/// object, and timestamps are <c>yyyy-MM-ddTHH:mm:ssZ</c>. Checkpoints keep the same shapes in the state file.
/// </summary>
static class DiscussionJson
{
    static readonly IReadOnlySet<string> NoMentions = new HashSet<string>();

    // GitHub's nodes are external input, read leniently: a missing or mistyped field reads as empty, zero, false or null.

    /// <summary>A Discussion node of the GraphQL Discussion fields. Mentions are read from its rendered body only for a mention user.</summary>
    public static Discussion ReadDiscussion(JsonNode node, MentionUser? mentionUser) => new(Text(node["id"]), Text(node["url"]), Text(node["body"]),
        ReadActor(node["author"]), ReadTime(node["createdAt"]), ReadTime(node["updatedAt"]), ReadReactionGroups(node["reactionGroups"]),
        Mentions(node, mentionUser), node["number"] is JsonValue number && number.TryGetValue<int>(out var value) ? value : 0, Text(node["title"]),
        Flag(node["closed"]), Flag(node["locked"]), (node["answer"] as JsonObject)?["id"].OptionalText(),
        node["category"] is JsonObject category ? new(Text(category["id"]), Text(category["name"])) : null);

    /// <summary>A comment or reply node, with the reactions read for it.</summary>
    public static DiscussionComment ReadComment(JsonNode node, IReadOnlyList<Reaction> reactions, MentionUser? mentionUser) =>
        new(Text(node["id"]), Text(node["url"]), Text(node["body"]), ReadActor(node["author"]), ReadTime(node["createdAt"]), ReadTime(node["updatedAt"]),
            ReadReactionGroups(node["reactionGroups"]), Mentions(node, mentionUser), (node["replyTo"] as JsonObject)?["id"].OptionalText(), reactions);

    public static Reaction ReadReaction(JsonNode node) => new(Text(node["id"]), Text(node["content"]), ReadActor(node["user"]), ReadTime(node["createdAt"]));

    /// <summary>An author, sender or reaction user as GitHub reports it; null when GitHub reports none.</summary>
    public static Actor? ReadActor(JsonNode? actor) => actor is JsonObject account ? new(Text(account["login"]), Text(account["__typename"])) : null;

    /// <summary>Reaction groups as GitHub reports them, and as a saved receipt keeps them.</summary>
    public static IReadOnlyList<ReactionGroup> ReadReactionGroups(JsonNode? groups) => groups is JsonArray array
        ? [.. array.OfType<JsonObject>().Select(group => new ReactionGroup(Text(group["content"]), Flag(group["viewerHasReacted"])))] : [];

    static IReadOnlySet<string> Mentions(JsonNode node, MentionUser? mentionUser) =>
        mentionUser is null ? NoMentions : RenderedMentions.Users(Text(node["bodyHTML"]));

    static string Text(JsonNode? node) => node.OptionalText() ?? "";
    static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    static DateTimeOffset ReadTime(JsonNode? node) =>
        DateTimeOffset.TryParse(node.OptionalText(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at : DateTimeOffset.MinValue;

    // The bridge's JSON, as ec1ccf7 copied it from GitHub's nodes.

    /// <summary>The Discussion as agent input carries it: its GraphQL fields without reaction groups and rendered body.</summary>
    public static JsonObject Public(Discussion discussion) => new()
    {
        ["id"] = discussion.Id,
        ["url"] = discussion.Url,
        ["body"] = discussion.Body,
        ["createdAt"] = Time(discussion.CreatedAt),
        ["updatedAt"] = Time(discussion.UpdatedAt),
        ["author"] = Author(discussion.Author),
        ["number"] = discussion.Number,
        ["title"] = discussion.Title,
        ["closed"] = discussion.Closed,
        ["locked"] = discussion.Locked,
        ["answer"] = Answer(discussion.AnswerId),
        ["category"] = Category(discussion.Category)
    };

    /// <summary>A comment as bootstrap context carries it, with its reactions.</summary>
    public static JsonObject Public(DiscussionComment comment) => new()
    {
        ["id"] = comment.Id,
        ["url"] = comment.Url,
        ["body"] = comment.Body,
        ["createdAt"] = Time(comment.CreatedAt),
        ["updatedAt"] = Time(comment.UpdatedAt),
        ["author"] = Author(comment.Author),
        ["replyTo"] = comment.ReplyToId is { } parent ? new JsonObject { ["id"] = parent } : null,
        ["reactions"] = new JsonArray([.. comment.Reactions.Select(reaction => (JsonNode)Public(reaction))])
    };

    /// <summary>A change subject's identity and attribution, with a reply's thread root.</summary>
    public static JsonObject Subject(DiscussionMessage source)
    {
        var subject = new JsonObject { ["id"] = source.Id, ["url"] = source.Url, ["author"] = Author(source.Author) };
        if (source is DiscussionComment { ReplyToId: { } parent }) subject["replyTo"] = new JsonObject { ["id"] = parent };
        return subject;
    }

    /// <summary>A reaction as agent input and checkpoints carry it.</summary>
    public static JsonObject Public(Reaction reaction) => new()
    {
        ["id"] = reaction.Id,
        ["content"] = reaction.Content,
        ["user"] = Author(reaction.User),
        ["createdAt"] = Time(reaction.CreatedAt)
    };

    /// <summary>A Discussion's answer and category as agent input and checkpoints carry them, or null.</summary>
    public static JsonObject? Answer(string? answerId) => answerId is { } id ? new JsonObject { ["id"] = id } : null;
    public static JsonObject? Category(DiscussionCategory? category) =>
        category is null ? null : new JsonObject { ["id"] = category.Id, ["name"] = category.Name };

    static JsonObject? Author(Actor? actor) => actor is null ? null : new JsonObject { ["__typename"] = actor.Type, ["login"] = actor.Login };
    static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
