using System.Globalization;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace RepoAgent.Auth;

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
        "polling" => new Dictionary<string, string> { ["discussions"] = "write" },
        "webhook" => new Dictionary<string, string> { ["discussions"] = "write", ["repository_hooks"] = "write" },
        "verification" => new Dictionary<string, string> { ["environments"] = "read", ["actions"] = "read" },
        _ => throw new IssuanceDenied()
    };
    public static string Text(TableEntity e, string key) => e.TryGetValue(key, out var v) && v is string s && s.Length is > 0 and <= 8192 ? s : throw new IssuanceDenied();
    public static long Number(TableEntity e, string key) => e.TryGetValue(key, out var v) && v is long n && n >= 0 ? n : throw new IssuanceDenied();
    public static DateTimeOffset Date(TableEntity e, string key) => e.TryGetValue(key, out var v) && v is DateTimeOffset d ? d : throw new IssuanceDenied();
    public static long Id(string s) => long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 && n.ToString(CultureInfo.InvariantCulture) == s ? n : throw new IssuanceDenied();
    public static bool IsBearer(string s) => s.Length is > 0 and <= 8192 && s.All(c => c is >= '!' and <= '~');
}

public sealed record GrantSnapshot(TableEntity Gate, TableEntity Device)
{
    public string RepositoryId => IssuancePolicy.Text(Device, "RepositoryId");
    public string DeviceId => IssuancePolicy.Text(Device, "DeviceId");
    public string Repository => IssuancePolicy.Text(Device, "Repository");
    public string Profile => IssuancePolicy.Text(Device, "Profile");
    public long InstallationId => IssuancePolicy.Number(Device, "InstallationId");
    public string Environment => IssuancePolicy.Text(Device, "Environment");
    public long EnvironmentId => IssuancePolicy.Number(Device, "EnvironmentId");
    public string Variable => IssuancePolicy.Text(Device, "Variable");
    public string Marker => IssuancePolicy.Text(Device, "Marker");
    public string Partition => Gate.PartitionKey;
    public string Epoch => IssuancePolicy.Text(Gate, "Epoch");
    public void RequireActive(DateTimeOffset now)
    {
        if (IssuancePolicy.Text(Gate, "State") != "active" || IssuancePolicy.Text(Device, "State") != "active"
            || !Device.TryGetValue("Acknowledged", out var ack) || ack is not true
            || IssuancePolicy.Date(Device, "ExpiresAt") <= now) throw new IssuanceDenied(401);
    }
    public void RequireBinding(BrokerOptions options)
    {
        _ = IssuancePolicy.Id(RepositoryId);
        if (IssuancePolicy.Text(Gate, "RepositoryId") != RepositoryId
            || IssuancePolicy.Text(Gate, "OwnerDeviceId") != DeviceId
            || Epoch != options.DeploymentEpoch || IssuancePolicy.Text(Device, "Epoch") != Epoch
            || IssuancePolicy.Number(Device, "AppId") != IssuancePolicy.AppId
            || IssuancePolicy.Text(Device, "AppSlug") != IssuancePolicy.AppSlug
            || IssuancePolicy.Text(Device, "GitHubHost") != "github.com"
            || IssuancePolicy.Text(Device, "PolicyVersion") != IssuancePolicy.Version
            || InstallationId <= 0 || EnvironmentId <= 0 || Profile is not ("polling" or "webhook")
            || Repository.Split('/') is not [var owner, var name] || !Segment(owner) || !Segment(name)
            || Environment.Length > 255 || Environment.Any(char.IsControl)
            || Variable.Length > 100 || !Variable.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new IssuanceDenied();
        _ = IssuancePolicy.Number(Gate, "Sequence");
        if (Marker != ApprovalMarker.Create(this)) throw new IssuanceDenied();
    }
    private static bool Segment(string s) => s.Length is > 0 and <= 100 && s is not ("." or "..")
        && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

public sealed record IssuedToken(string Token, DateTimeOffset ExpiresAt, bool ScopeValid);
public sealed record IssuanceResult(int Status, object? Body = null);

// Shared work/deadline is owned by one operation; it is never refreshed by retries or caller disconnect.
public sealed class IssuanceWork(TimeProvider clock) : IDisposable
{
    private readonly DateTimeOffset started = clock.GetUtcNow();
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(15), clock);
    private int reads;
    private int admissionEvaluations;
    public CancellationToken Cancellation => lifetime.Token;
    public void Check()
    {
        Cancellation.ThrowIfCancellationRequested();
        if (clock.GetUtcNow() - started >= TimeSpan.FromSeconds(15)) throw new IssuanceDenied();
    }
    public void Read() { Check(); if (++reads > 8) throw new IssuanceDenied(); }
    public void EvaluateAdmission() { Check(); if (++admissionEvaluations > 4) throw new IssuanceDenied(); }
    public void Fresh(DateTimeOffset readStart)
    {
        Check();
        if (clock.GetUtcNow() - readStart > TimeSpan.FromSeconds(10) || readStart > clock.GetUtcNow()) throw new IssuanceDenied();
    }
    public void Dispose() => lifetime.Dispose();
}
