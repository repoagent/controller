using System.Net;
using System.Text.Json;

namespace RepoAgent.Auth.Tests;

// Each call constructs a new service, so recovery depends only on durable current state.
internal sealed class InMemoryBroker
{
    public const string Epoch = "test-epoch";
    public static readonly string SetupSecret = new('a', 43);
    public InMemoryBrokerState State { get; } = new();
    public IssuanceClock Clock { get; } = new();
    public InMemoryGitHub GitHub { get; }
    public InMemorySecretProtection Secrets { get; } = new();
    public IssuanceLimits Limits { get; set; } = new();
    public string DeploymentEpoch { get; set; } = Epoch;
    public InMemoryBroker()
    {
        GitHub = new(Clock);
        State.Seed(CurrentBrokerQuota.Key, CurrentBrokerQuota.Empty());
    }
    public CurrentRepositoryStore Store() => new(State, DeploymentEpoch, Clock, Limits);
    public EnrollmentService Enrollment() => new(Store(), GitHub, GitHub, GitHub, Secrets, new Uri("https://broker.example"), Clock);
    public GitHubTokenIssuer Issuer() => new(Store(), GitHub, GitHub, GitHub, Clock);
    public CurrentRepository Row => State.Get(CurrentRepository.Key(123456))!;
    public static BrokerRequest Request(string? secret, object? body = null) => new(
        secret is null ? [] : [new("Bearer ".Length + secret.Length, "Bearer", secret)], body is not null,
        Body(body is null ? [] : JsonSerializer.SerializeToUtf8Bytes(body, JsonSerializerOptions.Web)), Xunit.TestContext.Current.CancellationToken);
    public static Func<int, Task<byte[]>> Body(byte[] body) => count => Task.FromResult(body.Length > count ? body[..count] : body);
}
internal sealed record Answer(HttpStatusCode StatusCode, string Text)
{
    public JsonElement Json => JsonSerializer.Deserialize<JsonElement>(Text);
    public static Answer From(IssuanceResult result) => new((HttpStatusCode)result.Status,
        result.Body is null ? "" : JsonSerializer.Serialize(result.Body, JsonSerializerOptions.Web));
}
