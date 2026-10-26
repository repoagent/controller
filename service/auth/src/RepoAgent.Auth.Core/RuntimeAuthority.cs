namespace RepoAgent.Auth;

// The permissions a GitHub token carries, fixed by its controller enrollment's approval contract and profile together:
// each permission as GitHub names it, with its level. The broker's verifier token carries its contract's verifier authority.
// Two authorities are equal when they carry the same permissions at the same levels.
public sealed class RuntimeAuthority : IEquatable<RuntimeAuthority>
{
    private RuntimeAuthority(IReadOnlyDictionary<string, string> permissions) => Permissions = permissions;

    public IReadOnlyDictionary<string, string> Permissions { get; }

    // A profile its contract does not allow has no authority, and the operation is denied. A repository-settings profile
    // adds exactly read of the variables store its contract's approval lives in, never the profile name's choice.
    public static RuntimeAuthority For(ApprovalContract contract, string profile)
    {
        if (!contract.Allows(profile)) throw new IssuanceDenied();
        if (!contract.IsSettings) return new(Frozen(profile));
        return new(new Dictionary<string, string>(Frozen(profile[..^"-settings".Length]))
        {
            [contract.IsAgent ? "agent_variables" : "actions_variables"] = "read"
        });
    }

    // The broker-internal verifier reads its contract's approval location and nothing else.
    public static RuntimeAuthority Verifier(ApprovalContract contract) => new(contract.VerifierProfile switch
    {
        "agent-variable-verification" => new Dictionary<string, string> { ["agent_variables"] = "read" },
        "actions-variable-verification" => new Dictionary<string, string> { ["actions_variables"] = "read" },
        "verification" => new Dictionary<string, string> { ["environments"] = "read", ["actions"] = "read" },
        _ => throw new IssuanceDenied()
    });

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

    // The frozen profiles never grant variables access.
    private static Dictionary<string, string> Frozen(string profile) => profile switch
    {
        "polling-clone" => new() { ["discussions"] = "write", ["contents"] = "read" },
        "webhook-clone" => new() { ["discussions"] = "write", ["contents"] = "read", ["repository_hooks"] = "write" },
        "polling" => new() { ["discussions"] = "write" },
        "webhook" => new() { ["discussions"] = "write", ["repository_hooks"] = "write" },
        _ => throw new IssuanceDenied()
    };
}
