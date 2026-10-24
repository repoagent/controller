using System.Globalization;

namespace RepoAgent.Auth;

// Denies the current broker operation. The status is what the caller sees; 503 means "try again later".
public sealed class IssuanceDenied(int status = 503) : Exception
{
    public int Status { get; } = status;
}

public static class IssuancePolicy
{
    public const long AppId = 5387625;
    public const string AppSlug = "repo-agent-connector";
    public const string Version = "repository-approval-v1";
    public const int RepositoryAttempts = 8, GlobalAttempts = 128;
    public const int RepositoryPending = 4, GlobalPending = 64;
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(15);
    public static IReadOnlyDictionary<string, string> Permissions(string profile) => profile switch
    {
        "polling-clone" => new Dictionary<string, string> { ["discussions"] = "write", ["contents"] = "read" },
        "webhook-clone" => new Dictionary<string, string> { ["discussions"] = "write", ["contents"] = "read", ["repository_hooks"] = "write" },
        "polling" => new Dictionary<string, string> { ["discussions"] = "write" },
        "webhook" => new Dictionary<string, string> { ["discussions"] = "write", ["repository_hooks"] = "write" },
        // Repository-settings profiles: the frozen profile above plus exactly Actions variables read.
        "polling-settings" or "webhook-settings" or "polling-clone-settings" or "webhook-clone-settings" =>
            new Dictionary<string, string>(Permissions(profile[..^"-settings".Length])) { ["actions_variables"] = "read" },
        "agent-variable-verification" => new Dictionary<string, string> { ["agent_variables"] = "read" },
        "actions-variable-verification" => new Dictionary<string, string> { ["actions_variables"] = "read" },
        "verification" => new Dictionary<string, string> { ["environments"] = "read", ["actions"] = "read" },
        _ => throw new IssuanceDenied()
    };
    public static long Id(string s) => long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 && n.ToString(CultureInfo.InvariantCulture) == s ? n : throw new IssuanceDenied();
    public static bool IsBearer(string s) => s.Length is > 0 and <= 8192 && s.All(c => c is >= '!' and <= '~');

    // A repository full name GitHub can hold: owner/name, each 1-100 ASCII letters, digits, '-', '_' or '.', never "." or "..".
    public static bool IsRepositoryName(string? repository) => repository?.Split('/') is [var owner, var name] && Segment(owner) && Segment(name);
    private static bool Segment(string value) => value.Length is > 0 and <= 100 && value is not ("." or "..")
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
