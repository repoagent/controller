using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace RepoAgent.Auth;

// Typed reads of broker Table row values. A missing or out-of-range value denies the operation.
internal static class TableValues
{
    public static string Text(TableEntity e, string key) => e.TryGetValue(key, out var v) && v is string s && s.Length is > 0 and <= 8192 ? s : throw new IssuanceDenied();
    public static long Number(TableEntity e, string key) => e.TryGetValue(key, out var v) && v is long n && n >= 0 ? n : throw new IssuanceDenied();
    public static DateTimeOffset Date(TableEntity e, string key) => e.TryGetValue(key, out var v) && v is DateTimeOffset d ? d : throw new IssuanceDenied();
}

public sealed record GrantSnapshot(TableEntity Gate, TableEntity Device)
{
    public string RepositoryId => TableValues.Text(Device, "RepositoryId");
    public string DeviceId => TableValues.Text(Device, "DeviceId");
    public string Repository => TableValues.Text(Device, "Repository");
    public string Profile => TableValues.Text(Device, "Profile");
    public long InstallationId => TableValues.Number(Device, "InstallationId");
    internal ApprovalContract Approval => ApprovalContract.Read(Device);
    public string Environment => TableValues.Text(Device, "Environment");
    public long EnvironmentId => TableValues.Number(Device, "EnvironmentId");
    public string Variable => TableValues.Text(Device, "Variable");
    public string Marker => TableValues.Text(Device, "Marker");
    public string Partition => Gate.PartitionKey;
    public string Epoch => TableValues.Text(Gate, "Epoch");
    public void RequireActive(DateTimeOffset now)
    {
        PayloadClock.RequirePayload(Device);
        if (TableValues.Text(Gate, "State") != "active" || TableValues.Text(Device, "State") != "active"
            || !Device.TryGetValue("Acknowledged", out var ack) || ack is not true
            || TableValues.Date(Device, "ExpiresAt") <= now) throw new IssuanceDenied(401);
    }
    public void RequireBinding(BrokerOptions options)
    {
        _ = IssuancePolicy.Id(RepositoryId);
        if (TableValues.Text(Gate, "RepositoryId") != RepositoryId
            || TableValues.Text(Gate, "OwnerDeviceId") != DeviceId
            || Epoch != options.DeploymentEpoch || TableValues.Text(Device, "Epoch") != Epoch
            || TableValues.Number(Device, "AppId") != IssuancePolicy.AppId
            || TableValues.Text(Device, "AppSlug") != IssuancePolicy.AppSlug
            || TableValues.Text(Device, "GitHubHost") != "github.com"
            || TableValues.Text(Device, "PolicyVersion") != Approval.Policy
            || InstallationId <= 0 || !Approval.Allows(Profile)
            || Repository.Split('/') is not [var owner, var name] || !Segment(owner) || !Segment(name)
            || Variable.Length > 100 || !Variable.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new IssuanceDenied();
        if (Approval == ApprovalContract.Legacy && (EnvironmentId <= 0 || Environment.Length > 255 || Environment.Any(char.IsControl)))
            throw new IssuanceDenied();
        _ = TableValues.Number(Gate, "Sequence");
        if (Marker != ApprovalMarker.Create(this)) throw new IssuanceDenied();
    }
    private static bool Segment(string s) => s.Length is > 0 and <= 100 && s is not ("." or "..")
        && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

public sealed record IssuedToken(string Token, DateTimeOffset ExpiresAt, bool ScopeValid);
public sealed record IssuanceResult(int Status, object? Body = null);
