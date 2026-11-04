using static RepoAgent.Auth.IssuanceValues;

namespace RepoAgent.Auth;

// One-time conversion of current authority, never token state or retained history.
public static class LegacyEnrollmentImport
{
    public static CurrentRepository FromActive(RepositoryGate gate, ControllerEnrollment enrollment, RepositoryQuota quota,
        string epoch, DateTimeOffset now, RevocationReceipt? revocation = null)
    {
        var repository = IssuancePolicy.Id(Text(enrollment.RepositoryId));
        if (revocation is not null || gate.State != "active" || enrollment.State != "active" || enrollment.Acknowledged != true
            || enrollment.PayloadRedacted == true || Date(enrollment.ExpiresAt) <= now
            || gate.RepositoryId != enrollment.RepositoryId || gate.OwnerEnrollmentId != enrollment.EnrollmentId
            || gate.Epoch != epoch || enrollment.Epoch != epoch
            || enrollment.AppId != IssuancePolicy.AppId || enrollment.AppSlug != IssuancePolicy.AppSlug
            || enrollment.InstallationId is null or <= 0 || enrollment.GitHubHost != "github.com" || !LegacyEnrollmentLocator.IsLegacyId(enrollment.EnrollmentId))
            throw new IssuanceDenied();
        _ = Number(gate.Sequence);
        var hash = Text(enrollment.ControllerSecretHash);
        if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)) throw new IssuanceDenied();
        ValidateQuota(quota, now);
        var row = new CurrentRepository
        {
            SchemaVersion = 2,
            RepositoryId = repository,
            Epoch = epoch,
            State = "active",
            EnrollmentId = enrollment.EnrollmentId,
            Repository = enrollment.Repository,
            InstallationId = enrollment.InstallationId,
            Environment = enrollment.Environment,
            EnvironmentId = enrollment.EnvironmentId,
            Profile = enrollment.Profile,
            Variable = enrollment.Variable,
            ApprovalKind = enrollment.ApprovalKind,
            SetupVersion = enrollment.SetupVersion,
            PolicyVersion = enrollment.PolicyVersion,
            Challenge = enrollment.Challenge,
            Marker = enrollment.Marker,
            ControllerSecretHash = hash,
            CompletionId = enrollment.CompletionId,
            ExpiresAt = enrollment.ExpiresAt,
            NextCleanupAt = enrollment.ExpiresAt,
            Window = quota.Window,
            Admissions = quota.Admissions,
            Reads = quota.Reads,
            Signs = quota.Signs,
            Mints = quota.Mints
        };
        row.Validate(repository, epoch);
        if (row.Approval == ApprovalContract.Legacy && (row.EnvironmentId is null or <= 0
            || row.Environment is not { Length: > 0 and <= 255 } environment || environment.Any(char.IsControl)))
            throw new IssuanceDenied();
        if (row.Marker is null || row.Marker != row.CreateMarker()) throw new IssuanceDenied();
        return row;
    }

    public static CurrentBrokerQuota GlobalQuota(IssuanceQuota quota, int repositories, DateTimeOffset now)
    {
        if (repositories is < 0 or > 128 || quota.RetentionSchemaVersion != 1) throw new IssuanceDenied();
        ValidateQuota(quota, now);
        return new()
        {
            SchemaVersion = 2,
            Repositories = repositories,
            Window = quota.Window,
            Admissions = quota.Admissions,
            Reads = quota.Reads,
            Signs = quota.Signs,
            Mints = quota.Mints
        };
    }

    private static void ValidateQuota(ChargedQuota quota, DateTimeOffset now)
    {
        foreach (var value in new[] { quota.Window, quota.Admissions, quota.Reads, quota.Signs, quota.Mints }) _ = Number(value);
        if (quota.Window > now.ToUnixTimeSeconds() / 60) throw new IssuanceDenied();
    }
}

// A UUID-only frozen enrollment needs one current routing row. New IDs route themselves.
#pragma warning disable CA1051 // RecordMap maps the one stored field by reference.
public sealed class LegacyEnrollmentLocator : BrokerRecord
{
    public long? RepositoryId;
    public static bool IsLegacyId(string? id) => id is { Length: 32 } && Guid.TryParseExact(id, "N", out _)
        && id.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');
    public static StateKey<LegacyEnrollmentLocator> Key(string id) => IsLegacyId(id)
        ? new(CurrentRepository.Scope, "legacy:" + id) : throw new IssuanceDenied(401);
    protected override void Map(RecordMap map) => map.Number("RepositoryId", ref RepositoryId);
}
#pragma warning restore CA1051
