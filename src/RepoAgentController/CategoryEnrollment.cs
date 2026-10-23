using System.Globalization;
using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>Selects new Discussions in operator-configured categories for enrollment without a mention.</summary>
/// <remarks>Each selected category records, in the state file, when this controller's settings refresh first observed it selected. A Discussion
/// created before then is preexisting and is not selected. Removing a category forgets its start, so selecting it again
/// starts afresh. The App's own Discussions, including the controller's status dashboard, are never selected.
/// Callers serialize access to the state store.</remarks>
sealed class CategoryEnrollment
{
    public const string Setting = "REPOAGENT_ENROLLMENT_CATEGORIES";
    readonly RepositorySettings settings;
    readonly GitHub github;
    readonly StateStore store;

    public CategoryEnrollment(RepositorySettings settings, GitHub github, StateStore store)
    {
        (this.settings, this.github, this.store) = (settings, github, store);
        Starts();
    }

    public bool Selects(JsonNode discussion)
    {
        var starts = Starts();
        if (github.IsAgentActor(discussion["author"]) || discussion["category"]?.Text("name") is not { } category ||
            starts.FirstOrDefault(start => string.Equals(start.Key, category, StringComparison.OrdinalIgnoreCase)).Value is not { } since) return false;
        return DateTimeOffset.TryParse(discussion.Text("createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created) &&
            created >= DateTimeOffset.Parse(since.GetValue<string>(), CultureInfo.InvariantCulture);
    }

    // Reconciles the recorded selection starts with the effective setting; a retained value keeps its starts.
    JsonObject Starts()
    {
        var setting = settings[Setting];
        var names = setting.Value?.Split(',', StringSplitOptions.TrimEntries) ?? [];
        if (store.State["enrollmentCategories"] is not JsonObject starts)
        {
            if (names.Length == 0) return new JsonObject();
            store.State["enrollmentCategories"] = starts = new JsonObject();
        }
        var removed = starts.Select(start => start.Key).Where(name => !names.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var name in removed) starts.Remove(name);
        var added = names.Where(name => !starts.Any(start => string.Equals(start.Key, name, StringComparison.OrdinalIgnoreCase))).ToList();
        // Not the observation time: a webhook may report a new Discussion long after the refresh read its category.
        // GitHub reports creation in whole seconds.
        var since = setting.Since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        foreach (var name in added) starts[name] = since;
        if (removed.Count + added.Count == 0) return starts;
        if (starts.Count == 0) store.State.Remove("enrollmentCategories");
        store.Save();
        return starts;
    }
}
