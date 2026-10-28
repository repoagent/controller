using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace RepoAgent.Auth.Tests;

// One acknowledged setup v1 controller enrollment in broker state, as offline initialization and enrollment leave it:
// both global quotas, the candidate repository, and one earlier unknown runtime attempt ("one") that still holds global
// slot 0, a retained ledger and one outstanding GitHub token on the gate and the enrollment.
internal sealed class IssuanceFixture
{
    public const string Epoch = "test-epoch", RepositoryId = "123456";
    public InMemoryBrokerState State { get; } = new();
    public IssuanceClock Clock { get; } = new();
    public InMemoryGitHub GitHub { get; }
    public InMemorySecretProtection Secrets { get; } = new();
    public EnrolledDevice EnrollmentId { get; private set; } = null!;
    private IssuanceFixture() => GitHub = new(Clock);

    public IssuanceStore Store(IssuanceLimits? limits = null) => new(State, Epoch, 4096, Secrets, Clock, limits);
    public GitHubTokenIssuer Issuer(IssuanceLimits? limits = null) => new(Store(limits), GitHub, GitHub, GitHub, Clock);
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1068", Justification = "Preserve existing positional callers; this token is distinct from the optional configuration that follows it.")]
    public Task<IssuanceResult> IssueAsync(CancellationToken caller = default, IssuanceLimits? limits = null) =>
            Issuer(limits).IssueAsync(EnrollmentId.DeviceId, EnrollmentId.Secret, caller);
    public ControllerSelfRevocation Revocation() => new(State, Epoch, Clock);

    public static StateKey<RepositoryGate> GateKey => RepositoryGate.Key(RepositoryId);
    public StateKey<ControllerEnrollment> EnrollmentKey => ControllerEnrollment.Key(RepositoryId, EnrollmentId.DeviceId);
    public static StateKey<RepositoryQuota> QuotaKey => RepositoryQuota.Key(RepositoryId);
    public RepositoryGate Gate => State.Get(GateKey)!;
    public ControllerEnrollment Enrollment => State.Get(EnrollmentKey)!;
    public IssuanceQuota GlobalQuota => State.Get(IssuanceQuota.Key)!;
    public RepositoryQuota Quota => State.Get(QuotaKey)!;

    // The attempts issuance created; the offline unknown attempt "one" stays charged separately.
    public List<IssuanceAttempt> Attempts() => [.. State.Rows("repository:" + RepositoryId)
        .Where(row => row.Name.StartsWith("attempt:", StringComparison.Ordinal) && row.Name != "attempt:one")
        .Select(row => BrokerRecord.Read<IssuanceAttempt>(row.Row))];

    public IssuanceAttempt Attempt(string purpose) => Assert.Single(Attempts(), attempt => attempt.Purpose == purpose);

    // The global issuance rows: every slot has exactly one retained ledger, and the quota counts them.
    public (List<IssuanceSlot> Slots, List<IssuanceLedger> Ledgers) GlobalRows()
    {
        var rows = State.Rows(IssuanceCapacity.Scope);
        var slots = rows.Where(row => row.Name.StartsWith("slot:", StringComparison.Ordinal)).Select(row => BrokerRecord.Read<IssuanceSlot>(row.Row)).ToList();
        var ledgers = rows.Where(row => row.Name.StartsWith("operation:", StringComparison.Ordinal)).Select(row => BrokerRecord.Read<IssuanceLedger>(row.Row)).ToList();
        Assert.Equal(1 + slots.Count + ledgers.Count, rows.Count);
        Assert.Equal(slots.Count, ledgers.Count);
        Assert.Equal(ledgers.Count, GlobalQuota.RetainedIssuanceCount);
        foreach (var slot in slots)
        {
            var ledger = Assert.Single(ledgers, l => l.OperationId == slot.OperationId);
            Assert.Equal(slot.Generation, ledger.Generation);
            Assert.Equal(slot.ReservedAt, ledger.ReservedAt);
            Assert.Equal(slot.ReservedAt!.Value.AddHours(24), ledger.PayloadPurgeAt);
            Assert.Equal(4, ledger.RowCredits);
        }
        Assert.Equal("attempt:one", Assert.Single(ledgers, l => l.OperationId == "one").AttemptRowKey);
        return (slots, ledgers);
    }

    public static IssuanceFixture Create(TimeSpan? runtimeLifetime = null)
    {
        var f = new IssuanceFixture();
        if (runtimeLifetime is { } lifetime) f.GitHub.RuntimeLifetime = lifetime;
        f.EnrollmentId = f.Enroll(RepositoryId, "owner/project", outstanding: 1);
        f.GitHub.Marker = f.Enrollment.Marker!;
        return f;
    }

    // Enrolls the same controller in another repository with no outstanding GitHub token, and returns its grant.
    public async Task<GrantSnapshot> EnrollCompetitorAsync(string repositoryId, string repository, CancellationToken ct)
    {
        _ = Enroll(repositoryId, repository, outstanding: 0, EnrollmentId);
        return await Store().SnapshotAsync(repositoryId, EnrollmentId.DeviceId, ct);
    }

    private EnrolledDevice Enroll(string repositoryId, string repository, long outstanding, EnrolledDevice? existing = null)
    {
        var enrollmentId = existing?.DeviceId ?? Base64Url(RandomNumberGenerator.GetBytes(24));
        var secret = existing?.Secret ?? Base64Url(RandomNumberGenerator.GetBytes(32));
        var created = Clock.GetUtcNow();
        var enrollmentQuota = State.Get(EnrollmentQuota.Key) ?? new() { RetentionSchemaVersion = 1, CandidateCount = 0, RetainedEnrollmentCount = 0, Count = 0 };
        enrollmentQuota.CandidateCount++;
        enrollmentQuota.RetainedEnrollmentCount++;
        State.Seed(EnrollmentQuota.Key, enrollmentQuota);
        State.Seed(CandidateRepository.Key(repositoryId), new() { RepositoryId = repositoryId, PendingCount = 0 });
        if (existing is null)
        {
            var generation = Guid.NewGuid().ToString("N");
            State.Seed(IssuanceQuota.Key, new()
            {
                RetentionSchemaVersion = 1,
                RetainedIssuanceCount = 1,
                Window = 0,
                Admissions = 0,
                Reads = 0,
                Signs = 0,
                Mints = 0,
                Slots = 1
            });
            var ledger = new IssuanceLedger
            {
                OperationId = "one",
                Generation = generation,
                RepositoryId = repositoryId,
                EnrollmentId = enrollmentId,
                Purpose = "runtime",
                Epoch = Epoch,
                ReservedAt = created,
                PayloadPurgeAt = created.AddHours(24),
                GlobalSlot = 0,
                AttemptRowKey = "attempt:one",
                RowCredits = 4,
                ApprovalKind = "environment-variable",
                SetupVersion = 1,
                PolicyVersion = IssuancePolicy.Version
            };
            State.Seed(IssuanceLedger.Key("one"), ledger);
            State.Seed(IssuanceSlot.Key(0), new()
            {
                OperationId = "one",
                Generation = generation,
                RepositoryId = repositoryId,
                Purpose = "runtime",
                Epoch = Epoch,
                ReservedAt = created,
                PayloadPurgeAt = created.AddHours(24),
                GlobalSlot = 0,
                ApprovalKind = "environment-variable",
                SetupVersion = 1,
                PolicyVersion = IssuancePolicy.Version
            });
            State.Seed(EnrollmentLocator.Key(enrollmentId), new() { RepositoryId = repositoryId, EnrollmentId = enrollmentId, PendingId = repositoryId + "_" + Guid.NewGuid().ToString("N") });
            State.Seed(IssuanceAttempt.Key(repositoryId, "one"), new()
            {
                RepositoryId = repositoryId,
                EnrollmentId = enrollmentId,
                Epoch = Epoch,
                State = "outstanding",
                Purpose = "runtime",
                OperationId = "one",
                Generation = generation,
                ReservedAt = created,
                PayloadPurgeAt = created.AddHours(24),
                GlobalSlot = 0,
                ApprovalKind = "environment-variable",
                SetupVersion = 1,
                PolicyVersion = IssuancePolicy.Version
            });
        }
        State.Seed(RepositoryGate.Key(repositoryId), new()
        {
            RepositoryId = repositoryId,
            Epoch = Epoch,
            OwnerEnrollmentId = enrollmentId,
            State = "active",
            Sequence = 7,
            OutstandingTokenCount = outstanding
        });
        var challenge = new string('a', 43);
        State.Seed(ControllerEnrollment.Key(repositoryId, enrollmentId), new()
        {
            RepositoryId = repositoryId,
            EnrollmentId = enrollmentId,
            Epoch = Epoch,
            ControllerSecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
            ExpiresAt = created.AddHours(1),
            State = "active",
            OutstandingTokenCount = outstanding,
            Acknowledged = true,
            AppId = IssuancePolicy.AppId,
            AppSlug = IssuancePolicy.AppSlug,
            GitHubHost = "github.com",
            PolicyVersion = IssuancePolicy.Version,
            Repository = repository,
            InstallationId = 789,
            Profile = "polling",
            Environment = "approval/environment",
            EnvironmentId = 987,
            Variable = "APPROVAL",
            Challenge = challenge,
            Marker = ApprovalMarker.Create(ApprovalContract.Legacy, repositoryId, repository, 789, enrollmentId, challenge, "polling", Epoch,
                "APPROVAL", "approval/environment", 987)
        });
        return new(enrollmentId, secret);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record EnrolledDevice(string DeviceId, string Secret);

internal sealed class IssuanceClock : TimeProvider
{
    private DateTimeOffset current = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => current;
    public void Advance(TimeSpan amount) => current += amount;
}
