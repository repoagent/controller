namespace RepoAgent.Auth;

// The records a controller's self-revocation reads and writes. Each maps every property that any broker writer stores
// in its row shape (see the writer inventory in the #205 PR 4 report). Fields keep the stored names, except where the
// glossary renames a concept: an enrollment ID is stored as DeviceId, a controller secret's hash as SecretHash.

// Routes an enrollment ID to the repository partition that holds the enrollment. It proves nothing by itself: the
// records in that partition must bind the enrollment.
public sealed class EnrollmentLocator : BrokerRecord
{
    public string? RepositoryId, EnrollmentId, PendingId;

    public static StateKey<EnrollmentLocator> Key(string enrollmentId) => new("locator:" + enrollmentId, "locator");

    protected override void Map(RecordMap map)
    {
        map.Text("RepositoryId", ref RepositoryId);
        map.Text("DeviceId", ref EnrollmentId);
        map.Text("PendingId", ref PendingId);
    }
}

// The repository's common gate. Every repository transaction conditions on it, and each one increments Sequence.
public sealed class RepositoryGate : BrokerRecord
{
    public string? RepositoryId, Epoch, State, OwnerEnrollmentId, VerifierAttempt;
    public long? Sequence, OutstandingTokenCount, PendingCount;

    public static StateKey<RepositoryGate> Key(string repositoryId) => new("repository:" + repositoryId, "gate");

    // The gate of a repository that has never had one: vacant, of the epoch that creates it, with nothing counted. Only an
    // Add can write it.
    public static RepositoryGate Vacant(string repositoryId, string epoch) => new()
    {
        RepositoryId = repositoryId, Epoch = epoch, State = "vacant", Sequence = 0, OutstandingTokenCount = 0, PendingCount = 0
    };

    protected override void Map(RecordMap map)
    {
        map.Text("RepositoryId", ref RepositoryId);
        map.Text("Epoch", ref Epoch);
        map.Text("State", ref State);
        map.Number("Sequence", ref Sequence);
        map.Number("OutstandingTokenCount", ref OutstandingTokenCount);
        map.Number("PendingCount", ref PendingCount);
        map.Text("OwnerDeviceId", ref OwnerEnrollmentId);
        map.Text("VerifierAttempt", ref VerifierAttempt);
    }
}

// A controller enrollment: its binding, controller secret hash, lifecycle and published runtime GitHub token. Rows from
// before approval contracts carry only the binding, secret hash, expiry, state and token count.
public sealed class ControllerEnrollment : BrokerRecord
{
    // Binding and approval.
    public string? RepositoryId, Repository, EnrollmentId, Epoch, Variable, Profile, Challenge, Marker, Environment;
    public string? ApprovalKind, PolicyVersion, AppSlug, GitHubHost, PendingId, CompletionId;
    public long? InstallationId, EnvironmentId, SetupVersion, AppId;
    // Controller secret and lifecycle.
    public string? ControllerSecretHash, State;
    public bool? Acknowledged, PayloadRedacted;
    public DateTimeOffset? ExpiresAt, LastActivityAt, FirstTerminalAt;
    public long? OutstandingTokenCount;
    // The published runtime GitHub token and its last observation.
    public string? RuntimeAttempt, RuntimeObservation;
    public DateTimeOffset? RuntimeExpiresAt, RuntimeObservationReservedAt, RuntimeObservationJwtIssuedAt, RuntimeObservationJwtExpiresAt;

    public static StateKey<ControllerEnrollment> Key(string repositoryId, string enrollmentId) =>
        new("repository:" + repositoryId, "device:" + enrollmentId);

    protected override void Map(RecordMap map)
    {
        map.Text("RepositoryId", ref RepositoryId);
        map.Text("Repository", ref Repository);
        map.Text("DeviceId", ref EnrollmentId);
        map.Text("Epoch", ref Epoch);
        map.Number("InstallationId", ref InstallationId);
        map.Text("Variable", ref Variable);
        map.Text("Profile", ref Profile);
        map.Text("Challenge", ref Challenge);
        map.Text("Marker", ref Marker);
        map.Text("Environment", ref Environment);
        map.Number("EnvironmentId", ref EnvironmentId);
        map.Text("ApprovalKind", ref ApprovalKind);
        map.Number("SetupVersion", ref SetupVersion);
        map.Text("PolicyVersion", ref PolicyVersion);
        map.Number("AppId", ref AppId);
        map.Text("AppSlug", ref AppSlug);
        map.Text("GitHubHost", ref GitHubHost);
        map.Text("SecretHash", ref ControllerSecretHash);
        map.Flag("Acknowledged", ref Acknowledged);
        map.Text("State", ref State);
        map.Time("ExpiresAt", ref ExpiresAt);
        map.Number("OutstandingTokenCount", ref OutstandingTokenCount);
        map.Text("CompletionId", ref CompletionId);
        map.Text("PendingId", ref PendingId);
        map.Time("LastActivityAt", ref LastActivityAt);
        map.Text("RuntimeAttempt", ref RuntimeAttempt);
        map.Time("RuntimeExpiresAt", ref RuntimeExpiresAt);
        map.Text("RuntimeObservation", ref RuntimeObservation);
        map.Time("RuntimeObservationReservedAt", ref RuntimeObservationReservedAt);
        map.Time("RuntimeObservationJwtIssuedAt", ref RuntimeObservationJwtIssuedAt);
        map.Time("RuntimeObservationJwtExpiresAt", ref RuntimeObservationJwtExpiresAt);
        map.Time("FirstTerminalAt", ref FirstTerminalAt);
        map.Flag("PayloadRedacted", ref PayloadRedacted);
    }
}

// Proof that an enrollment was revoked, written in the same transaction that closed its gate. Owner release later adds
// when and why the gate was handed back.
public sealed class RevocationReceipt : BrokerRecord
{
    public string? RepositoryId, EnrollmentId, Epoch, Reason;
    public DateTimeOffset? RevokedAt, OwnerReleasedAt;

    public static StateKey<RevocationReceipt> Key(string repositoryId, string enrollmentId) =>
        new("repository:" + repositoryId, "revocation:" + enrollmentId);

    protected override void Map(RecordMap map)
    {
        map.Text("RepositoryId", ref RepositoryId);
        map.Text("DeviceId", ref EnrollmentId);
        map.Text("Epoch", ref Epoch);
        map.Time("RevokedAt", ref RevokedAt);
        map.Time("OwnerReleasedAt", ref OwnerReleasedAt);
        map.Text("Reason", ref Reason);
    }
}
