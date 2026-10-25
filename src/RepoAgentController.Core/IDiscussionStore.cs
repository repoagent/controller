namespace RepoAgentController;

/// <summary>Where the controller keeps its Discussion state across lifetimes.</summary>
public interface IDiscussionStore
{
    /// <summary>
    /// The Discussion state, or null when none was ever saved. A Discussion record's key in a form no writer leaves is read into
    /// <see cref="DiscussionRecord.UnreadableKeys"/>, a record that is not an object of keys as <see cref="DiscussionRecord.Unreadable"/>,
    /// and records that cannot be read at all as <see cref="DiscussionState.Unreadable"/>.
    /// Throws <see cref="InvalidOperationException"/> for a state of another version or an App login that is not text, and as
    /// its reader throws for a saved state that cannot be read as one.
    /// </summary>
    DiscussionState? ReadDiscussions();

    /// <summary>Replaces the Discussion state. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the
    /// save fails, leaving the state saved before it.</summary>
    void WriteDiscussions(DiscussionState state);
}
