namespace RepoAgent.Auth;

// The permissions a GitHub token carries, fixed by its controller enrollment's profile: each permission as GitHub
// names it, with its level. The broker's verifier token carries the authority of its contract's verifier profile.
public sealed class RuntimeAuthority
{
    private RuntimeAuthority(IReadOnlyDictionary<string, string> permissions) => Permissions = permissions;

    public IReadOnlyDictionary<string, string> Permissions { get; }

    // A profile the broker does not know has no authority, and the operation is denied.
    public static RuntimeAuthority For(string profile) => new(Profile(profile));

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
