using System.Net;
using System.Text.Json;

namespace RepoAgent.Auth.Tests;

// The broker's public operations as its routes call them, decided by the core over the in-memory broker state, a manual
// clock, the in-memory GitHub and secret protection. Every call builds its services afresh, as a restarted process would,
// so an operation knows only what the state holds. Both global quotas exist as offline activation leaves them.
internal sealed class InMemoryBroker
{
    public const string Epoch = "test-epoch", RepositoryId = "123456", Partition = "repository:" + RepositoryId;
    // A setup secret of the frozen grammar; the tests' default.
    public static readonly string SetupSecret = new('a', 43);
    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    public InMemoryBrokerState State { get; } = new();
    public IssuanceClock Clock { get; } = new();
    public InMemoryGitHub GitHub { get; }
    public InMemorySecretProtection Secrets { get; } = new();
    // The deployment epoch the next operations run under.
    public string DeploymentEpoch { get; set; } = Epoch;
    public RetainedLimits Retained { get; set; } = new(128, 256, 4096);
    public IssuanceLimits Limits { get; set; } = new();
    public Uri? BrokerOrigin { get; set; } = new("https://broker.example");

    public InMemoryBroker()
    {
        GitHub = new(Clock);
        State.Seed(EnrollmentQuota.Key, new() { RetentionSchemaVersion = 1, CandidateCount = 0, RetainedEnrollmentCount = 0, Count = 0 });
        State.Seed(IssuanceQuota.Key, new()
        {
            RetentionSchemaVersion = 1, RetainedIssuanceCount = 0, Window = 0, Admissions = 0, Reads = 0, Signs = 0, Mints = 0, Slots = 0
        });
    }

    public EnrollmentService Enrollment() => new(new EnrollmentStore(State, DeploymentEpoch, Retained, Secrets, Clock, Limits),
        GitHub, GitHub, GitHub, BrokerOrigin, Clock);
    public InstallationTokenIssuer Issuer() => new(new IssuanceStore(State, DeploymentEpoch, Retained.Issuance, Secrets, Clock, Limits),
        GitHub, GitHub, GitHub, Clock);

    // A setup v1 start body for owner/project and its approval environment.
    public static EnrollmentRequest Request() => new(Guid.NewGuid().ToString("N"), 123456, "owner/project", "approval/environment", "APPROVAL", "polling");

    // POST /v1/enrollments.
    public async Task<Answer> Start(EnrollmentRequest request, string? secret = null) =>
        Answer.From(await Enrollment().StartAsync(request, secret ?? SetupSecret, Token));
    // POST /v{setupVersion}/enrollments for setup v2 and v3.
    public async Task<Answer> Start(RepositoryVariableEnrollmentRequest request, int setupVersion, string? secret = null) =>
        Answer.From(await Enrollment().StartRepositoryVariableAsync(request, secret ?? SetupSecret, Token, setupVersion));
    // GET /v{setupVersion}/enrollments/{id}.
    public async Task<Answer> Status(string id, int setupVersion = 1, string? secret = null) =>
        Answer.From(await Enrollment().StatusAsync(id, secret ?? SetupSecret, Token, setupVersion));
    // POST /v{setupVersion}/enrollments/{id}/completion.
    public async Task<Answer> Complete(string id, int setupVersion = 1, string? secret = null) =>
        Answer.From(await Enrollment().CompleteAsync(id, secret ?? SetupSecret, Token, setupVersion));
    // POST /v{setupVersion}/devices/{id}/acknowledgment with the completion ID.
    public async Task<Answer> Acknowledge(string enrollmentId, string completionId, string secret, int setupVersion = 1) =>
        Answer.From(await Enrollment().AcknowledgeAsync(enrollmentId, completionId, secret, Token, setupVersion));
    // POST /v{runtimeVersion}/devices/{id}/installation-token; the route writes a body only with 200.
    public async Task<Answer> Issue(string enrollmentId, string secret, int runtimeVersion = 1)
    {
        var result = await Issuer().IssueAsync(enrollmentId, secret, Token, runtimeVersion);
        return Answer.From(result.Status == 200 ? result : new(result.Status));
    }
    // DELETE /v{runtimeVersion}/devices/{id}.
    public async Task<Answer> Revoke(string enrollmentId, string secret, int runtimeVersion = 1) =>
        new((HttpStatusCode)await new DeviceRevocation(State, DeploymentEpoch, Clock).RevokeAsync(enrollmentId, secret, Token, runtimeVersion), "");

    public RepositoryGate Gate => State.Get(RepositoryGate.Key(RepositoryId))!;
    // The pending controller enrollment a pending ID ("{repository ID}_{idempotency ID}") names.
    public PendingControllerEnrollment Pending(string pendingId) => State.Get(PendingKey(pendingId))!;
    public static StateKey<PendingControllerEnrollment> PendingKey(string pendingId) =>
        PendingControllerEnrollment.Key(pendingId.Split('_')[0], pendingId.Split('_')[1]);
    public ControllerEnrollment Enrollment(string enrollmentId) => State.Get(ControllerEnrollment.Key(RepositoryId, enrollmentId))!;

    // Every stored row of every scope, with its version, ordered by scope and name.
    public IReadOnlyList<StoredRow> Rows() => [.. State.All().Select(row => new StoredRow(row.Scope, row.Name, row.Row, row.Version))];
}

// A row as the broker state holds it.
internal sealed record StoredRow(string Scope, string Name, Dictionary<string, object> Row, StateVersion Version)
{
    public T Read<T>() where T : BrokerRecord, new() => BrokerRecord.Read<T>(Row);
    public string Key => Scope + "/" + Name;
}

// What a route answers: the status and the JSON body, empty when there is none.
internal sealed record Answer(HttpStatusCode StatusCode, string Text)
{
    public JsonElement Json => JsonSerializer.Deserialize<JsonElement>(Text);

    // The routes write a result's body as web JSON (Results.Json).
    public static Answer From(IssuanceResult result) =>
        new((HttpStatusCode)result.Status, result.Body is null ? "" : JsonSerializer.Serialize(result.Body, JsonSerializerOptions.Web));
}
