namespace RepoAgentController;

/// <summary>
/// Category enrollment's starts: for each category the REPOAGENT_ENROLLMENT_CATEGORIES setting selects, when this controller's
/// settings refresh first observed it selected. A Discussion created before its category's start is preexisting and never
/// enrolls by category.
/// </summary>
public static class CategoryEnrollmentStarts
{
    /// <summary>
    /// The changes that reconcile the categories with recorded starts, <paramref name="started"/>, with the effective setting, or
    /// null when there are none. A newly selected category starts when the controller first observed the setting's current value;
    /// a removed category is forgotten, so selecting it again starts afresh. Names compare case-insensitively.
    /// </summary>
    /// <remarks>Without any known value, a failed or invalid read changes nothing: only a confirmed absence or disabled settings
    /// remove a selection. A retained value keeps its starts.</remarks>
    public static CategoryStartChanges? Reconcile(IReadOnlyCollection<string> started, SettingValue setting)
    {
        if (setting is { Value: null, State: not (SettingState.Absent or SettingState.Disabled) }) return null;
        var names = setting.Value?.Split(',', StringSplitOptions.TrimEntries) ?? [];
        var forgotten = started.Where(name => !names.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
        var added = names.Where(name => !started.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();
        // Not the observation time: a webhook may report a new Discussion long after the refresh read its category.
        // GitHub reports creation in whole seconds.
        var since = new DateTimeOffset(setting.Since.UtcTicks - setting.Since.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        return forgotten.Count + added.Count == 0 ? null : new(forgotten, added, since);
    }
}

/// <summary>The recorded starts to forget, and the categories that start at <see cref="Since"/>.</summary>
public sealed record CategoryStartChanges(IReadOnlyList<string> Forgotten, IReadOnlyList<string> Started, DateTimeOffset Since);
