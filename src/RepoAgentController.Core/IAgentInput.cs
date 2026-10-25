namespace RepoAgentController;

/// <summary>
/// Writes agent input as the bridge sends it: the Discussion, bootstrap context, each change and the envelope of a turn, as JSON
/// text whose bytes stay fixed until #197. The bridge decides what each carries; the writer only gives it its shape.
/// </summary>
public interface IAgentInput
{
    /// <summary>The Discussion as agent input carries it.</summary>
    string Discussion(Discussion discussion);

    /// <summary>A comment as bootstrap context carries it, whose length counts against the context budget.</summary>
    string Comment(DiscussionComment comment);

    /// <summary>The bootstrap context: these comments, and how many later ones carry their body in their own change.</summary>
    string Context(IReadOnlyList<DiscussionComment> comments, int later);

    /// <summary>An observed change, as the waiting queue keeps it and a turn's input carries it.</summary>
    string Change(AgentChange change);

    /// <summary>A confirmed deletion: only its identity, attribution and routing, since nothing of it is left.</summary>
    string Deletion(AgentDeletion deletion);

    /// <summary>A turn's input. Throws as reading the Discussion's identity throws when <see cref="AgentEnvelope.DiscussionJson"/> has none.</summary>
    string Envelope(AgentEnvelope envelope);

    /// <summary>The URL of a change's subject, or empty when it has none.</summary>
    string SubjectUrl(string change);
}

/// <summary>
/// One change for the agent about <paramref name="Subject"/>, routed to the thread root <paramref name="ReplyToId"/> (null for the
/// Discussion). It carries the body when <see cref="Body"/> is set, each state field in <see cref="Fields"/> from
/// <see cref="State"/>, and the reaction of a reaction change.
/// </summary>
public sealed record AgentChange(string Kind, DiscussionMessage Subject, string? ReplyToId)
{
    public string? Body { get; init; }
    public CheckpointState? State { get; init; }
    public IReadOnlyList<CheckpointPart> Fields { get; init; } = [];
    public Reaction? Reaction { get; init; }
}

/// <summary>A deleted message's ID, its author's login, its thread root, and the root to route to when that still exists.</summary>
public sealed record AgentDeletion(string Id, string? AuthorLogin, string? ReplyTo, string? ReplyToId);

/// <summary>
/// A turn's input for <paramref name="Repository"/> as <paramref name="Identity"/>: a bootstrap when it carries
/// <paramref name="ContextJson"/>, else a continuation that shares only the Discussion's identity; and its changes.
/// </summary>
public sealed record AgentEnvelope(string Repository, AppIdentity Identity, string DiscussionJson, string? ContextJson, bool Redelivery,
    IReadOnlyList<string> Changes);
