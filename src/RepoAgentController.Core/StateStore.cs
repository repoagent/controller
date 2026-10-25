namespace RepoAgentController;

/// <summary>The controller's Discussion state, held in memory by the one controller that leases it, and saved whole.</summary>
public sealed class StateStore
{
    readonly IDiscussionStore files;
    public DiscussionState State { get; }

    /// <summary>Throws as reading the untyped file's records threw when they are unreadable (<see cref="DiscussionState.Unreadable"/>).</summary>
    public OrderedDictionary<string, DiscussionRecord> Discussions =>
        State.Unreadable is { } problem ? throw new InvalidDataException(problem) : State.Discussions;

    /// <summary>Reads the state <paramref name="files"/> keep for <paramref name="repository"/>; the caller holds their lease.</summary>
    public StateStore(IDiscussionStore files, string repository) => (this.files, State) = (files, Read(files, repository));

    static DiscussionState Read(IDiscussionStore files, string repository)
    {
        var state = files.ReadDiscussions() ?? new DiscussionState(repository);
        if (!string.Equals(state.Repository, repository, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("State belongs to a different repository or unsupported version");
        return state;
    }

    public void VerifyIdentity(string login)
    {
        if (State.AgentLogin is { } recorded && !string.Equals(recorded, login, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("State belongs to a different GitHub identity; use a fresh state file when moving from a user login to a GitHub App");
        State.AgentLogin = login;
        Save();
    }

    /// <summary>Removes these Discussion records and saves. When the save fails they are restored before the error propagates,
    /// so memory still matches the state file and a retry removes and saves them again.</summary>
    public void Remove(IReadOnlyCollection<string> ids)
    {
        var removed = ids.Where(Discussions.ContainsKey).Select(id => (Id: id, Record: Discussions[id])).ToArray();
        foreach (var (id, _) in removed) Discussions.Remove(id);
        try { Save(); }
        catch
        {
            foreach (var (id, record) in removed) Discussions[id] = record;
            throw;
        }
    }

    public void Save() => files.WriteDiscussions(State);
}
