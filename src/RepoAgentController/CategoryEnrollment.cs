using System.Globalization;

namespace RepoAgentController;

/// <summary>Category enrollment: enrolls new Discussions in operator-configured categories without a mention.</summary>
/// <remarks>Each selected category records, in the state file, when this controller's settings refresh first observed it selected. A Discussion
/// created before then is preexisting and is not selected. Removing a category forgets its start, so selecting it again
/// starts afresh; the bridge reconciles every refreshed change, so this holds without an observation in between. The App's
/// own Discussions, including the controller's status dashboard, are never selected. Callers serialize access to the state store.</remarks>
sealed class CategoryEnrollment
{
    public const string Setting = "REPOAGENT_ENROLLMENT_CATEGORIES";
    readonly RepositorySettings settings;
    readonly AppIdentity identity;
    readonly StateStore store;

    public CategoryEnrollment(RepositorySettings settings, AppIdentity identity, StateStore store)
    {
        (this.settings, this.identity, this.store) = (settings, identity, store);
        Starts();
    }

    public bool Selects(Discussion discussion)
    {
        var starts = Starts();
        if (identity.IsAgentActor(discussion.Author) || discussion.Category?.Name is not { } category ||
            starts.FirstOrDefault(start => string.Equals(start.Key, category, StringComparison.OrdinalIgnoreCase)).Value is not { } since) return false;
        return discussion.CreatedAt >= DateTimeOffset.Parse(since, CultureInfo.InvariantCulture);
    }

    /// <summary>Records starts for newly selected categories and forgets those of removed ones.</summary>
    public void Reconcile() => Starts();

    // Applies the core's reconciliation of the recorded starts with the effective setting, saving any change.
    OrderedDictionary<string, string> Starts()
    {
        var starts = store.State.CategoryStarts;
        if (CategoryEnrollmentStarts.Reconcile([.. starts.Keys], settings[Setting]) is not { } changes) return starts;
        foreach (var name in changes.Forgotten) starts.Remove(name);
        var since = changes.Since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        foreach (var name in changes.Started) starts[name] = since;
        store.Save();
        return starts;
    }
}
