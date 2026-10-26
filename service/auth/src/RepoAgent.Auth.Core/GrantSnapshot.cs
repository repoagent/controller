using System.Text.Json;
using static RepoAgent.Auth.IssuancePolicy;
using static RepoAgent.Auth.IssuanceValues;

namespace RepoAgent.Auth;

// Durable values a decision requires. A missing or out-of-range value denies the operation with 503.
internal static class IssuanceValues
{
    public static string Text(string? value) => value is { Length: > 0 and <= 8192 } text ? text : throw new IssuanceDenied();
    public static long Number(long? value) => value is { } number && number >= 0 ? number : throw new IssuanceDenied();
    public static DateTimeOffset Date(DateTimeOffset? value) => value ?? throw new IssuanceDenied();
}

// A controller enrollment's grant as read together: its repository gate and its controller enrollment, each with the
// version it was read at. Every issuance transaction replaces both at those versions, so a writer that came between
// fences it. The versions never advance: after a commit, a caller that needs the grant again reads a new snapshot.
public sealed class GrantSnapshot(string partitionRepositoryId, string enrollmentId,
    Versioned<RepositoryGate> gate, Versioned<ControllerEnrollment> enrollment)
{
    public RepositoryGate Gate { get; } = gate.Value;
    public ControllerEnrollment Enrollment { get; } = enrollment.Value;
    public StateVersion GateVersion { get; } = gate.Version;
    public StateVersion EnrollmentVersion { get; } = enrollment.Version;
    // The repository partition and enrollment the records were read under.
    public string PartitionRepositoryId { get; } = partitionRepositoryId;
    public StateKey<RepositoryGate> GateKey { get; } = RepositoryGate.Key(partitionRepositoryId);
    public StateKey<ControllerEnrollment> EnrollmentKey { get; } = ControllerEnrollment.Key(partitionRepositoryId, enrollmentId);

    public string RepositoryId => Text(Enrollment.RepositoryId);
    public string EnrollmentId => Text(Enrollment.EnrollmentId);
    public string Repository => Text(Enrollment.Repository);
    public string Profile => Text(Enrollment.Profile);
    public long InstallationId => Number(Enrollment.InstallationId);
    public ApprovalContract Approval => ApprovalContract.Stored(Enrollment.ApprovalKind, Enrollment.SetupVersion,
        Enrollment.PolicyVersion, Enrollment.Environment is not null || Enrollment.EnvironmentId is not null);
    public string Environment => Text(Enrollment.Environment);
    public long EnvironmentId => Number(Enrollment.EnvironmentId);
    public string Variable => Text(Enrollment.Variable);
    public string Marker => Text(Enrollment.Marker);
    public string Epoch => Text(Gate.Epoch);
    public RepositoryIdentity BoundRepository => new(Id(RepositoryId), Repository);

    // The approval GitHub must still show for this enrollment.
    public ControllerApproval BoundApproval() => Approval == ApprovalContract.Legacy
        ? new(Approval, BoundRepository, InstallationId, Profile, Variable, Marker, Environment, EnvironmentId)
        : new(Approval, BoundRepository, InstallationId, Profile, Variable, Marker);

    // 503 for a redacted enrollment; 401 when the grant is not active, not acknowledged or expired.
    public void RequireActive(DateTimeOffset now)
    {
        if (Enrollment.PayloadRedacted == true || Enrollment.State == "redacted") throw new IssuanceDenied();
        if (Text(Gate.State) != "active" || Text(Enrollment.State) != "active" || Enrollment.Acknowledged is not true
            || Date(Enrollment.ExpiresAt) <= now) throw new IssuanceDenied(401);
    }

    // The enrollment is bound to this deployment, the App, its repository and its approval marker.
    public void RequireBinding(string epoch)
    {
        _ = Id(RepositoryId);
        if (Text(Gate.RepositoryId) != RepositoryId
            || Text(Gate.OwnerEnrollmentId) != EnrollmentId
            || Epoch != epoch || Text(Enrollment.Epoch) != Epoch
            || Number(Enrollment.AppId) != AppId
            || Text(Enrollment.AppSlug) != AppSlug
            || Text(Enrollment.GitHubHost) != "github.com"
            || Text(Enrollment.PolicyVersion) != Approval.Policy
            || InstallationId <= 0 || !Approval.Allows(Profile)
            || !IsRepositoryName(Repository)
            || Variable.Length > 100 || !Variable.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new IssuanceDenied();
        var legacy = Approval == ApprovalContract.Legacy;
        if (legacy && (EnvironmentId <= 0 || Environment.Length > 255 || Environment.Any(char.IsControl)))
            throw new IssuanceDenied();
        _ = Number(Gate.Sequence);
        if (Marker != ApprovalMarker.Create(Approval, RepositoryId, Repository, InstallationId, EnrollmentId, Enrollment.Challenge,
                Profile, Epoch, Variable, legacy ? Environment : null, legacy ? EnvironmentId : null))
            throw new IssuanceDenied();
    }
}

// Canonical, bounded public encoding shared by enrollment/setup. The challenge is public;
// the independent controller secret is never included. Property order and compact JSON are v1.
public static class ApprovalMarker
{
    // A setup v1 (Legacy) marker names its approval environment; repository-variable markers never do.
    public static string Create(ApprovalContract approval, string repositoryId, string repository, long installationId,
        string enrollmentId, string? challenge, string profile, string epoch, string variable, string? environment, long? environmentId)
    {
        var text = Text(challenge);
        if (text.Length != 43 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new IssuanceDenied();
        // Agent (v2, v4) and Actions (v3) markers share one field order; version and kind differ.
        if (approval.IsRepositoryVariable)
            return JsonSerializer.Serialize(new
            {
                version = approval.SetupVersion, approvalKind = approval.Kind, githubHost = "github.com", appId = AppId, appSlug = AppSlug,
                repositoryId = Id(repositoryId), repository,
                installationId, deviceId = enrollmentId, challenge = text,
                profile, policyVersion = approval.Policy, epoch, variable
            });
        return JsonSerializer.Serialize(new
        {
            version = 1, githubHost = "github.com", appId = AppId, appSlug = AppSlug,
            repositoryId = Id(repositoryId), repository,
            installationId, deviceId = enrollmentId, challenge = text,
            profile, policyVersion = IssuancePolicy.Version, epoch,
            environment = Text(environment), environmentId = Number(environmentId), variable
        });
    }
}
