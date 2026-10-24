using System.Text.RegularExpressions;

namespace RepoAgentController;

/// <summary>The validated native model and reasoning effort for one Discussion's next turn.</summary>
public sealed record ModelChoice(string Model, string Effort, string Source);

/// <summary>One selectable entry of the native model/list catalogue for this Codex account.</summary>
public sealed record NativeModel(string Model, bool IsDefault, string DefaultEffort, IReadOnlyList<string> Efforts);

/// <summary>A selection that cannot be used as requested; the message says what to change.</summary>
public sealed class ModelSelectionException(string message) : Exception(message);

/// <summary>
/// Resolves a Discussion's repoagent:&lt;model&gt;:&lt;reasoning&gt; label, or else the defaults, to a model and effort the
/// account's native catalogue offers. Nothing is substituted: conflicting or malformed labels, an unavailable model and an
/// effort the model does not support all fail with a message naming where the value came from.
/// </summary>
public static class ModelSelection
{
    public const string LabelPrefix = "repoagent:";
    /// <summary>Repository settings holding the defaults for Discussions without a model label.</summary>
    public const string ModelSetting = "REPOAGENT_MODEL", ReasoningSetting = "REPOAGENT_REASONING";

    /// <summary>Every label in the repoagent: namespace is a model label and must follow its grammar.</summary>
    public static bool IsLabel(string name) => name.StartsWith(LabelPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The lowercase model or reasoning name, or null when it is not one.</summary>
    public static string? Normalize(string value)
    {
        var text = value.Trim().ToLowerInvariant();
        return Regex.IsMatch(text, @"\A[a-z0-9][a-z0-9._-]{0,63}\z") ? text : null;
    }

    /// <summary>The short label alias of a native model ID, such as sol6.1 for gpt-6.1-sol; null when it has none.</summary>
    public static string? Alias(string model) =>
        Regex.Match(model, @"\Agpt-(?<version>[0-9]+(?:\.[0-9]+)*)-(?<family>[a-z]+)\z") is { Success: true } match
            ? match.Groups["family"].Value + match.Groups["version"].Value : null;

    /// <summary>
    /// Without a model label each part falls back from the repository default to the native configuration, and then to
    /// the catalogue's default model and that model's default effort. A repository default that is set but unusable, with no
    /// earlier value, is not replaced by a native default.
    /// </summary>
    public static ModelChoice Resolve(IReadOnlyCollection<string> labels, SettingValue modelSetting, SettingValue reasoningSetting,
        string? nativeModel, string? nativeReasoning, IReadOnlyList<NativeModel> catalogue)
    {
        var selected = labels.Where(IsLabel).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        if (selected.Length > 1)
            throw new ModelSelectionException($"conflicting model labels {string.Join(", ", selected)}; keep one {LabelPrefix}<model>:<reasoning> label");
        if (selected.Length == 0)
            foreach (var (name, setting) in new[] { (ModelSetting, modelSetting), (ReasoningSetting, reasoningSetting) })
                if (setting is { Value: null, State: "invalid" or "read-failed" })
                    throw new ModelSelectionException(setting.State == "invalid"
                        ? $"{name} is invalid ({setting.Reason}) and no earlier value is known; correct or clear the variable"
                        : $"{name} could not be read ({setting.Reason}) and no earlier value is known; the input waits for a successful settings read");
        var (defaultModel, defaultReasoning) = (modelSetting.Value, reasoningSetting.Value);
        string? model, reasoning;
        string modelOrigin, reasoningOrigin, source;
        if (selected.Length == 1)
        {
            var parts = selected[0].Split(':');
            if (parts.Length != 3 || Normalize(parts[1]) is not { } labelModel || Normalize(parts[2]) is not { } labelReasoning)
                throw new ModelSelectionException($"label {selected[0]} must be {LabelPrefix}<model>:<reasoning>, such as {LabelPrefix}sol6.1:xhigh");
            (model, reasoning, source) = (labelModel, labelReasoning, "label");
            modelOrigin = reasoningOrigin = "label " + selected[0];
        }
        else
        {
            model = defaultModel ?? nativeModel ?? catalogue.FirstOrDefault(candidate => candidate.IsDefault)?.Model
                ?? throw new ModelSelectionException("the Codex model catalogue has no default model; set REPOAGENT_MODEL");
            modelOrigin = defaultModel is not null ? ModelSetting : nativeModel is not null ? "native configuration" : "the model catalogue";
            reasoning = defaultReasoning ?? nativeReasoning;
            reasoningOrigin = defaultReasoning is not null ? ReasoningSetting : "native configuration";
            source = "default";
        }
        var entry = catalogue.FirstOrDefault(candidate => string.Equals(candidate.Model, model, StringComparison.OrdinalIgnoreCase) ||
                Alias(candidate.Model) == model)
            ?? throw new ModelSelectionException($"model {model} from {modelOrigin} is not available to this Codex account; available: " +
                string.Join(", ", catalogue.Select(candidate => Alias(candidate.Model) is { } alias ? $"{alias} ({candidate.Model})" : candidate.Model)));
        if (reasoning is null) return new(entry.Model, entry.DefaultEffort, source);
        var effort = entry.Efforts.FirstOrDefault(candidate => string.Equals(candidate, reasoning, StringComparison.OrdinalIgnoreCase))
            ?? throw new ModelSelectionException($"reasoning {reasoning} from {reasoningOrigin} is not supported by {entry.Model}; supported: {string.Join(", ", entry.Efforts)}");
        return new(entry.Model, effort, source);
    }
}
