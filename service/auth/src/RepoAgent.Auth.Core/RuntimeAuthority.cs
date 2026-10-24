namespace RepoAgent.Auth;

// The permissions a GitHub token carries, fixed by its controller enrollment's profile: each permission as GitHub
// names it, with its level. The broker's verifier token carries the authority of its contract's verifier profile.
// Two authorities are equal when they carry the same permissions at the same levels.
public sealed class RuntimeAuthority : IEquatable<RuntimeAuthority>
{
    private RuntimeAuthority(IReadOnlyDictionary<string, string> permissions) => Permissions = permissions;

    public IReadOnlyDictionary<string, string> Permissions { get; }

    // A profile the broker does not know has no authority, and the operation is denied.
    public static RuntimeAuthority For(string profile) => new(Profile(profile));

    // An installation grants the authority when it grants every permission at its level or higher: write satisfies read.
    // A level GitHub did not give as a string grants nothing.
    public bool IsGrantedBy(IReadOnlyDictionary<string, string?> installation) =>
        Permissions.All(p => installation.TryGetValue(p.Key, out var granted) && (granted == p.Value || granted == "write" && p.Value == "read"));

    // A GitHub token carries exactly the authority: every permission at exactly its level, and nothing more but the
    // metadata read GitHub adds to every token.
    public bool MatchesExactly(IReadOnlyDictionary<string, string?> token) =>
        Permissions.All(p => token.TryGetValue(p.Key, out var level) && level == p.Value)
        && token.All(p => Permissions.TryGetValue(p.Key, out var level) && level == p.Value || p is { Key: "metadata", Value: "read" });

    public bool Equals(RuntimeAuthority? other) => other is not null && Permissions.Count == other.Permissions.Count
        && Permissions.All(p => other.Permissions.TryGetValue(p.Key, out var level) && level == p.Value);
    public override bool Equals(object? obj) => Equals(obj as RuntimeAuthority);
    public override int GetHashCode() => Permissions.Aggregate(0, (hash, p) => hash ^ HashCode.Combine(p.Key, p.Value));

    private static IReadOnlyDictionary<string, string> Profile(string profile) => profile switch
    {
        "polling-clone" => new Dictionary<string, string> { ["discussions"] = "write", ["contents"] = "read" },
        "webhook-clone" => new Dictionary<string, string> { ["discussions"] = "write", ["contents"] = "read", ["repository_hooks"] = "write" },
        "polling" => new Dictionary<string, string> { ["discussions"] = "write" },
        "webhook" => new Dictionary<string, string> { ["discussions"] = "write", ["repository_hooks"] = "write" },
        // Repository-settings profiles: the frozen profile above plus exactly Actions variables read.
        "polling-settings" or "webhook-settings" or "polling-clone-settings" or "webhook-clone-settings" =>
            new Dictionary<string, string>(Profile(profile[..^"-settings".Length])) { ["actions_variables"] = "read" },
        "agent-variable-verification" => new Dictionary<string, string> { ["agent_variables"] = "read" },
        "actions-variable-verification" => new Dictionary<string, string> { ["actions_variables"] = "read" },
        "verification" => new Dictionary<string, string> { ["environments"] = "read", ["actions"] = "read" },
        _ => throw new IssuanceDenied()
    };
}
