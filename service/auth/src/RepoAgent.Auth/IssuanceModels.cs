using Azure.Data.Tables;

namespace RepoAgent.Auth;

// Typed reads of broker Table row values. A missing or out-of-range value denies the operation.
internal static class TableValues
{
    public static string Text(TableEntity e, string key) => e.TryGetValue(key, out var v) && v is string s && s.Length is > 0 and <= 8192 ? s : throw new IssuanceDenied();
    public static long Number(TableEntity e, string key) => e.TryGetValue(key, out var v) && v is long n && n >= 0 ? n : throw new IssuanceDenied();
    public static DateTimeOffset Date(TableEntity e, string key) => e.TryGetValue(key, out var v) && v is DateTimeOffset d ? d : throw new IssuanceDenied();
}

// The App authorization window an attempt row reserved. Only a still-reserved attempt may be signed.
internal static class AppAuthorizationWindowRows
{
    extension(AppAuthorizationWindow)
    {
        public static AppAuthorizationWindow Read(TableEntity attempt) => TableValues.Text(attempt, "State") == "reserved"
            ? AppAuthorizationWindow.Reserved(TableValues.Date(attempt, "ReservedAt"), TableValues.Date(attempt, "JwtIssuedAt"), TableValues.Date(attempt, "JwtExpiresAt"))
            : throw new IssuanceDenied();
    }
}

// A grant read as Table rows, for the enrollment, reclamation and redaction paths that still use TableEntity. Issuance
// uses the core GrantSnapshot over IBrokerState; these checks duplicate its RequireActive and RequireBinding until
// slices 1c and 5 move their callers to the core. Device may be a pending enrollment row when only the marker is needed.
public sealed record GrantRows(TableEntity Gate, TableEntity Device)
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
            || !IssuancePolicy.IsRepositoryName(Repository)
            || Variable.Length > 100 || !Variable.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new IssuanceDenied();
        if (Approval == ApprovalContract.Legacy && (EnvironmentId <= 0 || Environment.Length > 255 || Environment.Any(char.IsControl)))
            throw new IssuanceDenied();
        _ = TableValues.Number(Gate, "Sequence");
        if (Marker != CreateMarker()) throw new IssuanceDenied();
    }

    // The approval marker these rows bind (ApprovalMarker in the core encodes it).
    public string CreateMarker()
    {
        var challenge = TableValues.Text(Device, "Challenge");
        var legacy = !Approval.IsRepositoryVariable;
        return ApprovalMarker.Create(Approval, RepositoryId, Repository, InstallationId, DeviceId, challenge, Profile, Epoch, Variable,
            legacy ? Environment : null, legacy ? EnvironmentId : null);
    }
}
