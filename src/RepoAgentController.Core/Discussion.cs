namespace RepoAgentController;

/// <summary>A Discussion category: its GitHub node ID and name.</summary>
public sealed record DiscussionCategory(string Id, string Name);

/// <summary>One reaction content on a message, and whether the App has reacted with it.</summary>
public sealed record ReactionGroup(string Content, bool ViewerHasReacted);

/// <summary>A participant's or the App's reaction to a message. <paramref name="User"/> is null when GitHub reports no account.</summary>
public sealed record Reaction(string Id, string Content, Actor? User, DateTimeOffset CreatedAt);

/// <summary>
/// A Discussion or one of its comments, as the App reads it. <paramref name="Author"/> is null when GitHub reports no account,
/// as for a deleted one. <paramref name="MentionedUsers"/> are the users its rendered body links as mentions; they are read only
/// when a mention user is configured, and are otherwise empty.
/// </summary>
public abstract record DiscussionMessage(string Id, string Url, string Body, Actor? Author, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<ReactionGroup> ReactionGroups, IReadOnlySet<string> MentionedUsers)
{
    /// <summary>Whether these reaction groups hold the App's EYES receipt for accepted input.</summary>
    public static bool HasEyes(IEnumerable<ReactionGroup> groups) => groups.Any(group => group.Content == "EYES" && group.ViewerHasReacted);
}

/// <summary>A Discussion, without its comments and reactions. <paramref name="AnswerId"/> is its answer comment's node ID, if any.</summary>
public sealed record Discussion(string Id, string Url, string Body, Actor? Author, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<ReactionGroup> ReactionGroups, IReadOnlySet<string> MentionedUsers, int Number, string Title, bool Closed, bool Locked,
    string? AnswerId, DiscussionCategory? Category)
    : DiscussionMessage(Id, Url, Body, Author, CreatedAt, UpdatedAt, ReactionGroups, MentionedUsers);

/// <summary>A comment, or a reply to the top-level comment <paramref name="ReplyToId"/>, with its reactions.</summary>
public sealed record DiscussionComment(string Id, string Url, string Body, Actor? Author, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    IReadOnlyList<ReactionGroup> ReactionGroups, IReadOnlySet<string> MentionedUsers, string? ReplyToId, IReadOnlyList<Reaction> Reactions)
    : DiscussionMessage(Id, Url, Body, Author, CreatedAt, UpdatedAt, ReactionGroups, MentionedUsers);

/// <summary>
/// A Discussion with every page of its reactions and comments: the top-level comments in order, then the replies to each in
/// that order.
/// </summary>
public sealed record DiscussionConversation(Discussion Discussion, IReadOnlyList<Reaction> Reactions, IReadOnlyList<DiscussionComment> Comments)
{
    /// <summary>The Discussion, then each comment in order, each with its reactions.</summary>
    public IReadOnlyList<(DiscussionMessage Message, IReadOnlyList<Reaction> Reactions)> Messages =>
        [(Discussion, Reactions), .. Comments.Select(comment => ((DiscussionMessage)comment, comment.Reactions))];
}

/// <summary>A Discussion in the dashboard's category: who wrote it and its body, which tell the App's dashboard from others.</summary>
public sealed record CategoryDiscussion(string Id, string Url, Actor? Author, string Body);
