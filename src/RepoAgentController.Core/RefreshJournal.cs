namespace RepoAgentController;

/// <summary>
/// Where the managed checkout's refresh journal is kept. Reading returns null when no journal exists yet, and throws when one
/// exists but cannot be used: it is incomplete, unsafe to read, or not in the form its writer leaves.
/// </summary>
public interface IRefreshJournal
{
    RefreshJournal? Read();
    void Write(RefreshJournal journal);
}

/// <summary>
/// The refresh journal: the checkout it is bound to, the refresh generations requested and completed, the agent work admitted
/// while the checkout is excluded, whether refresh and admission are blocked, how many admitted turns were running, why the
/// journal last held refresh back, and the refresh being applied. Keys this controller does not write are kept as read.
/// </summary>
public sealed class RefreshJournal(string root, string owner, long repositoryId)
{
    public const string Unusable = "Refresh journal binding or format differs; manual recovery required";

    string? reason;

    public string Root => root;
    public string Owner => owner;
    public long RepositoryId => repositoryId;
    public long Requested { get; set; }
    public long Completed { get; set; }

    /// <summary>Each unresolved admission by its ID, in the order admitted.</summary>
    public OrderedDictionary<string, RefreshAdmission> Active { get; } = [];
    public bool Blocked { get; set; }
    public int Running { get; set; }

    /// <summary>Why refresh is held back or blocked; setting it replaces a reason read in a form no writer leaves.</summary>
    public string? Reason
    {
        get => reason;
        set { reason = value; ReasonAsRead = null; }
    }

    /// <summary>The reason as read, as JSON, when it is not text.</summary>
    public string? ReasonAsRead { get; set; }

    public RefreshApplying? Applying { get; set; }
    public IReadOnlyList<UnknownKey> Unknown { get; init; } = [];
}

/// <summary>
/// Agent work admitted while the checkout is excluded: the session it went to and the turn the agent acknowledged, empty until
/// known; its place in the order of admissions and lifecycle events; whether its turn holds the input; and whether the agent
/// may hold the input unacknowledged. An admission read in a form no writer leaves is <see cref="AsRead"/> and written back as
/// read; it keeps the session and turn it names as text, and is confirmed or unconfirmed only where it says true.
/// </summary>
public sealed class RefreshAdmission(long since)
{
    public string Thread { get; set; } = "";
    public string Turn { get; set; } = "";
    public long Since => since;
    public bool Confirmed { get; set; }
    public bool Unconfirmed { get; set; }
    public IReadOnlyList<UnknownKey> Unknown { get; init; } = [];
    public string? AsRead { get; init; }
}

/// <summary>A refresh from the previous commit to the target, for a requested generation, recorded before it is applied. A
/// recorded one is <see cref="AsRead"/>, written back as read.</summary>
public sealed record RefreshApplying(string Previous, string Target, long Generation)
{
    public string? AsRead { get; init; }
}

/// <summary>The managed checkout the journal is bound to: its root, its owner, the repository and its default branch's ref.</summary>
public sealed record CheckoutBinding(string Root, string Owner, long RepositoryId, string DefaultRef);

/// <summary>The managed checkout's refresh to the authoritative default-branch commit.</summary>
public interface IRepositoryCheckout
{
    /// <summary>
    /// Brings the checkout to the default branch's authoritative commit, calling <paramref name="beforeApply"/> with the
    /// previous and target commits just before it changes anything. True when it changed the checkout. Throws
    /// <see cref="CheckoutDeferred"/> when the refresh can safely wait, and any other exception when it is unsafe.
    /// </summary>
    Task<bool> Refresh(Action<string, string> beforeApply, CancellationToken ct);
}

/// <summary>A refresh that refused safely: the recorded commit stays, and the refresh is retried.</summary>
public sealed class CheckoutDeferred(string reason) : IOException(reason);
