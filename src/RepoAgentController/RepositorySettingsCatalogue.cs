using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RepoAgentController;

/// <summary>The supported repository settings, from the repository-settings.json catalogue embedded in the controller.</summary>
static class RepositorySettingsCatalogue
{
    public static IReadOnlyList<RepositorySetting> Settings { get; } = Load();

    static List<RepositorySetting> Load()
    {
        using var stream = typeof(RepositorySettingsCatalogue).Assembly.GetManifestResourceStream("repository-settings.json")
            ?? throw new InvalidOperationException("Missing repository settings catalogue");
        var catalogue = JsonNode.Parse(stream) ?? throw new InvalidOperationException("Empty repository settings catalogue");
        if (catalogue["schemaVersion"]?.GetValue<int>() != 1 || catalogue["settings"] is not JsonArray entries ||
            catalogue["reserved"] is not JsonArray reservedNames)
            throw new InvalidOperationException("Unsupported repository settings catalogue");
        // Reserved REPOAGENT_ variables belong to other owners (the approval marker); they are never settings.
        var reserved = reservedNames.Select(name => name?.GetValue<string>() is { } text && Regex.IsMatch(text, @"\AREPOAGENT_[A-Z0-9_]{1,90}\z")
            ? text : throw new InvalidOperationException("Invalid reserved repository variable")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var settings = entries.Select(entry => entry is JsonObject fields &&
            Regex.IsMatch(fields.Text("name"), @"\AREPOAGENT_[A-Z0-9_]{1,90}\z") && fields.Text("description").Length is > 0 and <= 200
            ? fields.Text("type") switch
            {
                "integer" or "category-names" when fields.Count == 5 && fields["minimum"]!.GetValue<long>() is >= 0 and var minimum &&
                    fields["maximum"]!.GetValue<long>() is var maximum && maximum >= minimum => new RepositorySetting(fields.Text("name"), fields.Text("type"), minimum, maximum),
                "token" when fields.Count == 3 => new RepositorySetting(fields.Text("name"), "token", 0, 0),
                _ => throw new InvalidOperationException("Invalid repository settings catalogue entry")
            }
            : throw new InvalidOperationException("Invalid repository settings catalogue entry")).ToList();
        if (settings.DistinctBy(setting => setting.Name).Count() != settings.Count) throw new InvalidOperationException("Duplicate repository setting");
        if (settings.Any(setting => reserved.Contains(setting.Name))) throw new InvalidOperationException("Reserved repository variable used as a setting");
        return settings;
    }
}
