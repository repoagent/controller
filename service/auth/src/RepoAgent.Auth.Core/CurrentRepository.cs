#pragma warning disable CA1051 // RecordMap uses ref fields for lossless stored-schema mapping.
using static RepoAgent.Auth.IssuanceValues;

namespace RepoAgent.Auth;

// Schema 2 uses a fresh Table. One bounded row is replaced throughout a repository's lifetime;
// closed rows retain only the current generation fence and rate counters, never credential history.
public sealed class CurrentRepository : BrokerRecord
{
    public const string Scope = "broker:current-v2";
    public long? SchemaVersion, RepositoryId, InstallationId, EnvironmentId, SetupVersion;
    public string? Epoch, State, EnrollmentId, PendingId, RequestHash, SetupSecretHash, Repository;
    public string? Profile, Variable, Environment, ApprovalKind, PolicyVersion, Challenge, Marker;
    public string? ControllerSecretHash, CompletionId, ProtectedCompletion;
    public DateTimeOffset? Deadline, ExpiresAt, NextCleanupAt;
    public bool? PositiveApproval;
    public long? Window, Admissions, Reads, Signs, Mints;

    public static StateKey<CurrentRepository> Key(long repository) => new(Scope, "repository:" + repository.ToString(System.Globalization.CultureInfo.InvariantCulture));
    public ApprovalContract Approval => ApprovalContract.Stored(ApprovalKind, SetupVersion, PolicyVersion,
        Environment is not null || EnvironmentId is not null);
    public RepositoryIdentity Identity => new(Number(RepositoryId), Text(Repository));
    public ApprovalRequest Request() => new(Approval, Number(RepositoryId), Text(Repository), Text(Profile), Text(Variable),
        Environment, InstallationId, EnvironmentId, Repository, Marker);
    public ControllerApproval BoundApproval() => new(Approval, Identity, Number(InstallationId), Text(Profile), Text(Variable),
        Text(Marker), Environment, EnvironmentId);

    public string CreateMarker() => ApprovalMarker.Create(Approval, Number(RepositoryId).ToString(System.Globalization.CultureInfo.InvariantCulture),
        Text(Repository), Number(InstallationId), Text(EnrollmentId), Challenge, Text(Profile), Text(Epoch), Text(Variable), Environment, EnvironmentId);

    public void Validate(long repository, string epoch)
    {
        if (SchemaVersion != 2 || RepositoryId != repository || repository <= 0 || !BrokerOptions.IsDeploymentEpoch(Text(Epoch))
            || State is not ("pending" or "completed" or "active" or "closed")) throw new IssuanceDenied();
        if (Epoch != epoch) throw new IssuanceDenied(401);
        foreach (var counter in new[] { Window, Admissions, Reads, Signs, Mints }) _ = Number(counter);
        if (State == "closed")
        {
            if (ControllerSecretHash is not null || SetupSecretHash is not null || ProtectedCompletion is not null || NextCleanupAt is not null)
                throw new IssuanceDenied();
            return;
        }
        if (NextCleanupAt != (State == "active" ? ExpiresAt : Deadline)) throw new IssuanceDenied();
        if ((!LegacyEnrollmentLocator.IsLegacyId(EnrollmentId) && CurrentRepositoryStore.RepositoryOf(Text(EnrollmentId)) != repository) || !Approval.Allows(Text(Profile))
            || !IssuancePolicy.IsRepositoryName(Repository) || !Approval.AcceptsVariable(Text(Variable))) throw new IssuanceDenied();
        if (Marker is not null && Marker != CreateMarker()) throw new IssuanceDenied();
        if (State is "pending" or "completed")
        {
            _ = Date(Deadline);
            if (CurrentRepositoryStore.RepositoryOf(Text(PendingId)) != repository || SetupSecretHash is null) throw new IssuanceDenied();
        }
        if (State is "active" or "completed")
        {
            _ = Date(ExpiresAt);
            if (ControllerSecretHash is null || CompletionId is null || Marker is null) throw new IssuanceDenied();
        }
    }

    // Called only on a conditional replacement. Keeping the random enrollment ID permanently rejects
    // old requests; replacing this fence for a new setup always creates a fresh ID and approval challenge.
    public void Close()
    {
        State = "closed";
        PendingId = RequestHash = SetupSecretHash = Repository = Profile = Variable = Environment = null;
        ApprovalKind = PolicyVersion = Challenge = Marker = ControllerSecretHash = CompletionId = ProtectedCompletion = null;
        InstallationId = EnvironmentId = SetupVersion = null;
        Deadline = ExpiresAt = NextCleanupAt = null;
        PositiveApproval = null;
    }

    protected override void Map(RecordMap map)
    {
        map.Number("SchemaVersion", ref SchemaVersion);
        map.Number("RepositoryId", ref RepositoryId);
        map.Number("InstallationId", ref InstallationId);
        map.Number("EnvironmentId", ref EnvironmentId);
        map.Number("SetupVersion", ref SetupVersion);
        map.Text("Epoch", ref Epoch);
        map.Text("State", ref State);
        map.Text("EnrollmentId", ref EnrollmentId);
        map.Text("PendingId", ref PendingId);
        map.Text("RequestHash", ref RequestHash);
        map.Text("SetupSecretHash", ref SetupSecretHash);
        map.Text("Repository", ref Repository);
        map.Text("Profile", ref Profile);
        map.Text("Variable", ref Variable);
        map.Text("Environment", ref Environment);
        map.Text("ApprovalKind", ref ApprovalKind);
        map.Text("PolicyVersion", ref PolicyVersion);
        map.Text("Challenge", ref Challenge);
        map.Text("Marker", ref Marker);
        map.Text("ControllerSecretHash", ref ControllerSecretHash);
        map.Text("CompletionId", ref CompletionId);
        map.Text("ProtectedCompletion", ref ProtectedCompletion);
        map.Time("Deadline", ref Deadline);
        map.Time("ExpiresAt", ref ExpiresAt);
        map.Time("NextCleanupAt", ref NextCleanupAt);
        map.Flag("PositiveApproval", ref PositiveApproval);
        map.Number("Window", ref Window);
        map.Number("Admissions", ref Admissions);
        map.Number("Reads", ref Reads);
        map.Number("Signs", ref Signs);
        map.Number("Mints", ref Mints);
    }
}

// Fixed global minute counters and the number of admitted repository rows. Initialized explicitly
// in a new Table, never inferred from absence in an existing/legacy Table.
public sealed class CurrentBrokerQuota : BrokerRecord
{
    public static readonly StateKey<CurrentBrokerQuota> Key = new(CurrentRepository.Scope, "quota");
    public long? SchemaVersion, Repositories, Window, Admissions, Reads, Signs, Mints;
    public static CurrentBrokerQuota Empty() => new() { SchemaVersion = 2, Repositories = 0, Window = 0, Admissions = 0, Reads = 0, Signs = 0, Mints = 0 };
    protected override void Map(RecordMap map)
    {
        map.Number("SchemaVersion", ref SchemaVersion);
        map.Number("Repositories", ref Repositories);
        map.Number("Window", ref Window);
        map.Number("Admissions", ref Admissions);
        map.Number("Reads", ref Reads);
        map.Number("Signs", ref Signs);
        map.Number("Mints", ref Mints);
    }
}

#pragma warning restore CA1051
