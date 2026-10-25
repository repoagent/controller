namespace RepoAgentController;

/// <summary>
/// The controller's record of its repository's Discussions, as its state file holds it: the App login it last ran as, the
/// category enrollment starts, and each observed Discussion's record by node ID, in the order each was first recorded. Its
/// holder changes it in place and saves it whole through <see cref="IControllerState.WriteDiscussions"/>.
/// </summary>
public sealed class DiscussionState(string repository)
{
    public string Repository { get; } = repository;

    /// <summary>The App login the controller last ran as; another login needs a fresh state file.</summary>
    public string? AgentLogin { get; set; }

    /// <summary>
    /// When each enrollment category started (<see cref="CategoryEnrollmentStarts"/>), by category name, as recorded
    /// (<c>yyyy-MM-ddTHH:mm:ssZ</c>), so a start a hand edit left unreadable fails selection as it always did.
    /// </summary>
    public OrderedDictionary<string, string> CategoryStarts { get; } = [];

    public OrderedDictionary<string, DiscussionRecord> Discussions { get; } = [];

    /// <summary>Keys this controller does not write, as read, so they are written back.</summary>
    public IReadOnlyList<UnknownKey> Unknown { get; init; } = [];

    /// <summary>
    /// Why the Discussion records cannot be read, when the state file's <c>discussions</c> is missing or not an object; it is
    /// then written back as read, and nothing reads <see cref="Discussions"/>.
    /// </summary>
    public string? Unreadable { get; init; }
}

/// <summary>A key a later controller or a hand edit added, with its value's JSON as read.</summary>
public sealed record UnknownKey(string Name, string Json);

/// <summary>
/// One observed Discussion: its checkpoints, model labels and agent session, the input waiting for that session, a delivery
/// whose outcome is not yet known, and the turn it runs. A key read in a form no writer leaves is in <see cref="UnreadableKeys"/>,
/// and a record that is not an object of keys is <see cref="Unreadable"/>: each is written back as read.
/// </summary>
public sealed class DiscussionRecord(string url)
{
    public string Url { get; } = url;

    /// <summary>What observation compares for work, by message ID, in observation order.</summary>
    public OrderedDictionary<string, SourceCheckpoint> Sources { get; set; } = [];

    /// <summary>The Discussion's model labels in ordinal order, or null when it has none.</summary>
    public IReadOnlyList<string>? Labels { get; set; }

    /// <summary>The Discussion's agent session; none while it is null or empty.</summary>
    public string? SessionId { get; set; }

    public WaitingInput? Waiting { get; set; }

    /// <summary>A delivery whose outcome is unknown; an interrupted lifetime leaves it for redelivery at the next start.</summary>
    public PendingDelivery? Pending { get; set; }

    public RunningTurn? Running { get; set; }

    public IReadOnlyList<UnknownKey> Unknown { get; init; } = [];

    /// <summary>
    /// Known keys whose value is in a form no writer leaves, by name, in the order read: the property each names is then null
    /// (<see cref="Url"/> empty), and the value is written back as read until the bridge replaces it.
    /// </summary>
    public OrderedDictionary<string, UnreadableJson> UnreadableKeys { get; } = [];

    /// <summary>The record as read, when it is not an object of keys; null otherwise.</summary>
    public UnreadableJson? Unreadable { get; init; }

    /// <summary>
    /// Why the bridge leaves the Discussion alone: the first unreadable key other than the model labels, which observation
    /// replaces, and the running turn, which every start ends; null when there is none. The reason is what reading the key
    /// reported.
    /// </summary>
    public string? Problem => Unreadable?.Reason ?? UnreadableKeys.Where(key => key.Key switch
    {
        "labels" or "running" => false,
        // Waiting input that is not an object: observation of a Discussion with a session replaces it with what it observes.
        "waiting" => string.IsNullOrEmpty(SessionId) || key.Value.Json.StartsWith('{'),
        _ => true
    }).Select(key => key.Value.Reason).FirstOrDefault();

    /// <summary>Whether the record may hold an agent session: a session ID, one that is not text, or a record that is not an object of keys.</summary>
    public bool HoldsSession => SessionId is { Length: > 0 } || UnreadableKeys.ContainsKey("sessionId") || Unreadable is not null;

    /// <summary>Whether the record may hold a delivery whose outcome is unknown.</summary>
    public bool HoldsPending => Pending is not null || Unreadable is not null;
}

/// <summary>JSON as read, in a form no writer leaves, and why reading it failed.</summary>
public sealed record UnreadableJson(string Json, string Reason);

/// <summary>
/// What observation compares of one message for work: its content, the Discussion's state fields (null for a comment), the
/// reactions of other accounts than the App, and a reply's thread root, so its deletion can still be attributed and routed.
/// </summary>
public sealed record SourceCheckpoint(CheckpointContent Content, CheckpointState? State, IReadOnlyList<Reaction> Reactions, string? ReplyTo)
{
    /// <summary>The checkpoint of <paramref name="source"/> observed with <paramref name="reactions"/>.</summary>
    public static SourceCheckpoint Of(DiscussionMessage source, IReadOnlyList<Reaction> reactions, AppIdentity identity) => new(
        new(source.Body, source.Author?.Login),
        source is Discussion discussion ? new(discussion.Title, discussion.Closed, discussion.Locked, discussion.AnswerId, discussion.Category) : null,
        [.. reactions.Where(reaction => !identity.IsAgentActor(reaction.User))], (source as DiscussionComment)?.ReplyToId);

    /// <summary>
    /// The checkpoint's JSON as read, when it holds keys or values no writer leaves; null otherwise. It is written back as read,
    /// and never equals a checkpoint observed since, so the next observation rewrites it. A value that cannot be read is then
    /// empty, false or none, and a reaction that cannot be read keeps only its ID.
    /// </summary>
    public string? AsRead { get; init; }

    /// <summary>What observation compares as changed, as its JSON compared before the records were typed, since it was read in
    /// a form no writer leaves: <c>content</c>, and the names of state keys.</summary>
    public IReadOnlyList<string> ReadAsChanged { get; init; } = [];

    /// <summary>The same content, state, reactions in any order, and thread root, neither kept as read.</summary>
    public bool Equals(SourceCheckpoint? other) => other is not null && AsRead == other.AsRead && Content == other.Content &&
        State == other.State && ReplyTo == other.ReplyTo && Reactions.Count == other.Reactions.Count && Reactions.All(other.Reactions.Contains);

    public override int GetHashCode() => HashCode.Combine(AsRead, Content, State, ReplyTo, Reactions.Count);
}

/// <summary>A message's body and its author's login, null when GitHub reports no account. The actor type is observation
/// metadata, so adding it never replays accepted content.</summary>
public sealed record CheckpointContent(string Body, string? Author);

/// <summary>The Discussion's state fields observation compares: its title, whether it is closed or locked, its answer comment's
/// node ID and its category.</summary>
public sealed record CheckpointState(string Title, bool Closed, bool Locked, string? AnswerId, DiscussionCategory? Category);

/// <summary>
/// Observed input waiting for the Discussion's session, oldest first, with the Discussion and, until the first delivery, the
/// bootstrap context, each as the agent input writer wrote it. <see cref="Retry"/> schedules the attempt after a failed one.
/// </summary>
public sealed class WaitingInput(string discussionJson)
{
    public List<ObservedInput> Inputs { get; } = [];

    /// <summary>The Discussion's JSON as agent input carries it.</summary>
    public string DiscussionJson { get; set; } = discussionJson;

    /// <summary>The bootstrap context's JSON as the first delivery to a new session carries it, or null.</summary>
    public string? ContextJson { get; set; }

    public DeliveryRetry? Retry { get; set; }

    public IReadOnlyList<UnknownKey> Unknown { get; init; } = [];
}

/// <summary>
/// One observed change waiting for delivery: when it was observed, its JSON as agent input carries it, the EYES receipt for its
/// source (none for a deletion), and whether an earlier delivery may already have carried it to the agent.
/// </summary>
public sealed record ObservedInput(DateTimeOffset ObservedAt, string ChangeJson, EyesReceipt? Receipt, bool Redelivery = false)
{
    public IReadOnlyList<UnknownKey> Unknown { get; init; } = [];

    /// <summary>The observation time's JSON as read, when it is not one a writer leaves; <see cref="ObservedAt"/> is then the
    /// time it reads as, or the earliest. It is written back as read.</summary>
    public string? ObservedAtAsRead { get; init; }
}

/// <summary>Where an EYES receipt for accepted input goes, and the source's reaction groups when it was observed, which tell
/// whether the App already reacted.</summary>
public sealed record EyesReceipt(string SubjectId, string Url, IReadOnlyList<ReactionGroup> ReactionGroups)
{
    public static EyesReceipt Of(DiscussionMessage source) => new(source.Id, source.Url, source.ReactionGroups);

    /// <summary>The receipt's JSON as read, when it holds keys or values no writer leaves; written back as read.</summary>
    public string? AsRead { get; init; }
}

/// <summary>A failed delivery's attempts so far, and when the next one is due.</summary>
public sealed record DeliveryRetry(int Attempts, DateTimeOffset After)
{
    public IReadOnlyList<UnknownKey> Unknown { get; init; } = [];

    /// <summary>The retry's JSON as read, when it is in a form no writer leaves; written back as read until the next attempt.</summary>
    public string? AsRead { get; init; }
}

/// <summary>
/// A delivery in flight: how it was being made ("create-and-send" or "send"), since when, the sources whose receipts it would
/// retain, and how many leading inputs it carries; none for a delivery a controller from before the waiting queue left.
/// </summary>
public sealed record PendingDelivery(string Operation, DateTimeOffset Since, IReadOnlyList<string> SourceIds, int? Inputs)
{
    public IReadOnlyList<UnknownKey> Unknown { get; init; } = [];

    /// <summary>
    /// The delivery's JSON as read, when it is in a form no writer leaves; null otherwise. It is written back as read; only
    /// <see cref="Inputs"/> is read from it, where it is a number, since every start ends a pending delivery.
    /// </summary>
    public string? AsRead { get; init; }
}

/// <summary>The agent turn the Discussion's last delivery started, and since when, until the agent reports it ended.</summary>
public sealed record RunningTurn(string Turn, DateTimeOffset Since)
{
    public IReadOnlyList<UnknownKey> Unknown { get; init; } = [];
}
