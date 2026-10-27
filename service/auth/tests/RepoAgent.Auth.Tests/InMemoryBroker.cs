using System.Net;
using System.Text.Json;

namespace RepoAgent.Auth.Tests;

// The broker's public routes as the HTTP adapter calls them, decided by the core over the in-memory broker state, a manual
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
            RetentionSchemaVersion = 1,
            RetainedIssuanceCount = 0,
            Window = 0,
            Admissions = 0,
            Reads = 0,
            Signs = 0,
            Mints = 0,
            Slots = 0
        });
    }

    public EnrollmentService Enrollment() => new(new EnrollmentStore(State, DeploymentEpoch, Retained, Secrets, Clock, Limits),
        GitHub, GitHub, GitHub, BrokerOrigin, Clock);
    public InstallationTokenIssuer Issuer() => new(new IssuanceStore(State, DeploymentEpoch, Retained.Issuance, Secrets, Clock, Limits),
        GitHub, GitHub, GitHub, Clock);

    // A setup v1 start body for owner/project and its approval environment.
    public static EnrollmentRequest Request() => new(Guid.NewGuid().ToString("N"), 123456, "owner/project", "approval/environment", "APPROVAL", "polling");

    // POST /v1/enrollments.
    public Task<Answer> Start(EnrollmentRequest request, string? secret = null) => StartWith(request, 1, secret);
    // POST /v{setupVersion}/enrollments for setup v2 and v3.
    public Task<Answer> Start(RepositoryVariableEnrollmentRequest request, int setupVersion, string? secret = null) =>
        StartWith(request, setupVersion, secret);
    // POST /v{setupVersion}/enrollments with any body.
    public async Task<Answer> StartWith(object body, int setupVersion, string? secret = null) =>
        Answer.From(await BrokerRoutes.StartAsync(Request(secret ?? SetupSecret, body), Enrollment, setupVersion));
    // GET /v{setupVersion}/enrollments/{id}.
    public async Task<Answer> Status(string id, int setupVersion = 1, string? secret = null) =>
        Answer.From(await BrokerRoutes.StatusAsync(Request(secret ?? SetupSecret), id, Enrollment, setupVersion));
    // POST /v{setupVersion}/enrollments/{id}/completion.
    public async Task<Answer> Complete(string id, int setupVersion = 1, string? secret = null) =>
        Answer.From(await BrokerRoutes.CompleteAsync(Request(secret ?? SetupSecret), id, Enrollment, setupVersion));
    // POST /v{setupVersion}/devices/{id}/acknowledgment with the completion ID.
    public async Task<Answer> Acknowledge(string enrollmentId, string completionId, string secret, int setupVersion = 1) =>
        Answer.From(await BrokerRoutes.AcknowledgeAsync(Request(secret, new { completionId }), enrollmentId, Enrollment, setupVersion));
    // POST /v{runtimeVersion}/devices/{id}/installation-token.
    public async Task<Answer> Issue(string enrollmentId, string secret, int runtimeVersion = 1) =>
        Answer.From(await BrokerRoutes.IssueAsync(Request(secret), enrollmentId, () => Issuer().IssueAsync, runtimeVersion));
    // DELETE /v{runtimeVersion}/devices/{id}.
    public async Task<Answer> Revoke(string enrollmentId, string secret, int runtimeVersion = 1) =>
        Answer.From(await BrokerRoutes.RevokeAsync(Request(secret), enrollmentId, new DeviceRevocation(State, DeploymentEpoch, Clock), runtimeVersion));

    // A request as the HTTP adapter hands it to a route: one Bearer credential and, when there is one, a web JSON body.
    public static BrokerRequest Request(string? secret, object? body = null) => new(
        secret is null ? [] : [new("Bearer ".Length + secret.Length, "Bearer", secret)], body is not null,
        Body(body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, JsonSerializerOptions.Web)), Token);

    // A request body as the adapter reads it: at most the count of bytes the route asks for.
    public static Func<int, Task<byte[]>> Body(byte[] body) => count => Task.FromResult(body.Length > count ? body[..count] : body);

    // The cleanup command's allocations, constructed with no provider, signing or key dependency.
    public CapacityReclamation Reclamation(IBrokerState? state = null) => new(state ?? State, DeploymentEpoch, Clock);
    public PayloadRedaction Redaction(IBrokerState? state = null) => new(state ?? State, Clock);

    // One `cleanup --once` run, as the command runs it, with the summary it prints.
    public async Task<CleanupRun> CleanupAsync(TimeProvider? clock = null, int dispatches = 128, int batches = 32)
    {
        var budget = new CleanupBudget(State, dispatches, batches);
        var cleanup = new BrokerCleanup(budget, DeploymentEpoch, budget, clock ?? Clock);
        var exit = await cleanup.RunAsync(Token);
        return new(exit, JsonSerializer.SerializeToElement(cleanup.Summary()), budget);
    }

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
