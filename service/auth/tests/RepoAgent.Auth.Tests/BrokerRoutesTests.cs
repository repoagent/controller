using System.Text;
using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

// What each public route decides around the operation it calls: the credential and body it accepts, in the order it
// checks them, and which answers carry a body. "No work" means no service is composed and nothing reaches the broker
// state or GitHub. Every flow test reaches the routes with valid requests through InMemoryBroker.
public sealed class BrokerRoutesTests
{
    private const string Id = "baa0968867944f7bb5390b9ba1f9d0f1";
    private static readonly string Secret = InMemoryBroker.SetupSecret;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public enum RouteName { Revoke, Issue, Start, Status, Complete, Acknowledge }

    public static TheoryData<RouteName> Routes => [.. Enum.GetValues<RouteName>()];

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Credentials_other_than_one_short_Bearer_value_answer_401_with_no_work(RouteName name)
    {
        PresentedAuthorization[][] rejected =
        [
            [],
            [Bearer(Secret), Bearer(Secret)],
            [new(136, "Bearer", Secret)],
            [new(50, null, null)],
            [new(50, "Basic", Secret)],
        ];
        foreach (var authorization in rejected)
        {
            var route = new Route(name);
            var answer = await route.Call(Request(authorization, route.Body));
            Assert.Equal(401, answer.Status);
            Assert.Null(answer.Body);
            route.AssertNoWork();
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task A_Bearer_value_of_135_characters_in_any_case_reaches_the_operation(RouteName name)
    {
        var route = new Route(name, composed: false);
        var answer = await route.Call(Request([new(135, "bEARER", Secret)], route.Body));
        if (name == RouteName.Revoke) Assert.Equal((401, 1), (answer.Status, route.Broker.State.Reads));
        else Assert.Equal((503, 1), (answer.Status, route.Composed));
    }

    // The device routes leave the controller secret's grammar to the operation; the enrollment routes require it.
    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("contains.a.dot.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Only_the_enrollment_routes_reject_a_malformed_secret_themselves(string? secret)
    {
        foreach (var name in Enum.GetValues<RouteName>().Except([RouteName.Revoke]))
        {
            var route = new Route(name, composed: false);
            var answer = await route.Call(Request([new(7 + (secret?.Length ?? 0), "Bearer", secret)], route.Body));
            Assert.Equal(name == RouteName.Issue ? (503, 1) : (401, 0), (answer.Status, route.Composed));
        }
    }

    [Theory]
    [InlineData(RouteName.Revoke, 400)]
    [InlineData(RouteName.Issue, 400)]
    [InlineData(RouteName.Status, 401)]
    [InlineData(RouteName.Complete, 401)]
    public async Task Bodiless_routes_refuse_a_body_in_their_own_order(RouteName name, int withoutCredential)
    {
        (PresentedAuthorization[] Authorization, int Expected)[] cases = [([], withoutCredential), ([Bearer(Secret)], 400)];
        foreach (var (authorization, expected) in cases)
        {
            var route = new Route(name);
            var answer = await route.Call(Request(authorization, "{}"u8.ToArray()));
            Assert.Equal(expected, answer.Status);
            route.AssertNoWork();
        }
    }

    [Theory]
    [InlineData(RouteName.Issue)]
    [InlineData(RouteName.Start)]
    [InlineData(RouteName.Status)]
    [InlineData(RouteName.Complete)]
    [InlineData(RouteName.Acknowledge)]
    public async Task A_route_without_its_service_answers_503_before_it_reads_the_body(RouteName name)
    {
        var route = new Route(name, composed: false);
        var answer = await route.Call(Request([Bearer(Secret)], name is RouteName.Start or RouteName.Acknowledge ? "{"u8.ToArray() : null));
        Assert.Equal((503, 1), (answer.Status, route.Composed));
        Assert.Equal(0, route.Broker.State.Reads + route.Broker.State.Commits);
    }

    // Each body answers 400 before the operation: nothing is read, written, minted or asked of GitHub.
    [Theory]
    [InlineData(1, "")]
    [InlineData(1, "{")]
    [InlineData(1, "null")]
    [InlineData(1, "[]")]
    [InlineData(1, "other-version")]
    [InlineData(2, "other-version")]
    [InlineData(1, "duplicate")]
    [InlineData(2, "duplicate")]
    [InlineData(1, "8193-bytes")]
    [InlineData(2, "8193-bytes")]
    public async Task Start_accepts_only_its_setup_versions_bounded_object_body(int setupVersion, string body)
    {
        var f = new InMemoryBroker();
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        var before = f.Rows();
        var answer = await BrokerRoutes.StartAsync(Request([Bearer(Secret)], StartBody(setupVersion, body)), f.Enrollment, setupVersion);
        Assert.Equal(400, answer.Status);
        Assert.Equal((0, 0, 0), (f.GitHub.Reads, f.GitHub.Mints, f.State.Reads + f.State.Commits));
        Assert.Equal(before.Select(row => (row.Key, row.Version)), f.Rows().Select(row => (row.Key, row.Version)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Start_reads_a_body_of_exactly_8_KiB(int setupVersion)
    {
        var f = new InMemoryBroker();
        if (setupVersion == 2) AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        var read = InMemoryBroker.Body(StartBody(setupVersion, "8192-bytes"));
        var requested = new List<int>();
        var answer = await BrokerRoutes.StartAsync(new([Bearer(Secret)], true, async count =>
        {
            var bytes = await read(count);
            requested.Add(count);
            return bytes;
        }, Token), f.Enrollment, setupVersion);
        Assert.Equal(200, answer.Status);
        Assert.NotNull(answer.Body);
        // One byte past the limit is enough to refuse a larger body, so the adapter never waits for a byte after that.
        Assert.Equal([8193], requested);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"completionId":1}""")]
    [InlineData("""{"completionId":null}""")]
    [InlineData("""{"CompletionId":"65c38567e3df4922b9620950b66dfdf3"}""")]
    [InlineData("""{"completionId":"65c38567e3df4922b9620950b66dfdf3","other":1}""")]
    [InlineData("""{"completionId":"65c38567e3df4922b9620950b66dfdf3","CompletionId":"65c38567e3df4922b9620950b66dfdf3"}""")]
    public async Task Acknowledgment_takes_exactly_one_completion_ID(string body)
    {
        var route = new Route(RouteName.Acknowledge);
        var answer = await route.Call(Request([Bearer(Secret)], Encoding.UTF8.GetBytes(body)));
        Assert.Equal(400, answer.Status);
        Assert.Equal(0, route.Broker.State.Reads + route.Broker.State.Commits);
    }

    // The issuer's own body passes only with its 200; with any other status the route drops whatever body the issuer gave.
    [Theory]
    [InlineData(200, true)]
    [InlineData(403, false)]
    [InlineData(503, false)]
    public async Task Installation_token_route_answers_the_issuers_body_only_with_200(int status, bool withBody)
    {
        var issued = new IssuanceResult(status, new { token = "ghs_private" });
        TokenIssuance issuer = (_, _, _, _) => Task.FromResult(issued);
        var answer = await BrokerRoutes.IssueAsync(Request([Bearer(Secret)], null), Id, () => issuer, 1);
        Assert.Equal(status, answer.Status);
        Assert.Same(withBody ? issued.Body : null, answer.Body);
    }

    // The request's cancellation is the operation's caller: a request cancelled before its operation runs reaches no
    // store call and isn't answered as served.
    [Theory]
    [MemberData(nameof(Routes))]
    public async Task A_cancelled_request_reaches_no_store_call(RouteName name)
    {
        var route = await Route.ForActiveEnrollment(name);
        var (reads, commits) = (route.Broker.State.Reads, route.Broker.State.Commits);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        try { Assert.Equal(503, (await route.Call(new([Bearer(route.Secret)], route.Body is not null, InMemoryBroker.Body(route.Body ?? []), cancellation.Token))).Status); }
        catch (OperationCanceledException) { }
        Assert.Equal((reads, commits), (route.Broker.State.Reads, route.Broker.State.Commits));
    }

    // A request the client aborts as its body ends is read in full, and its operation still runs on the request's
    // cancellation: it answers 503 without a store call.
    [Theory]
    [InlineData(RouteName.Start, 1)]
    [InlineData(RouteName.Start, 2)]
    [InlineData(RouteName.Acknowledge, 3)]
    public async Task A_request_cancelled_once_its_body_is_read_reaches_no_store_call(RouteName name, int setupVersion)
    {
        var route = name == RouteName.Acknowledge ? await Route.ForActiveEnrollment(name)
            : new Route(name) { Version = setupVersion, Body = StartBody(setupVersion, "valid") };
        if (name == RouteName.Start && setupVersion == 2) AgentVariableEnrollmentTests.ConfigureGitHub(route.Broker.GitHub);
        var (reads, commits) = (route.Broker.State.Reads, route.Broker.State.Commits);
        using var cancellation = new CancellationTokenSource();

        var answer = await route.Call(new([Bearer(route.Secret)], true, AbortedAtEnd(route.Body!, cancellation), cancellation.Token));
        Assert.Equal((503, reads, commits), (answer.Status, route.Broker.State.Reads, route.Broker.State.Commits));
    }

    // Revocation runs on the request's cancellation: cancelled after its first read, it commits nothing and the gate
    // stays active.
    [Fact]
    public async Task Revocation_stops_at_its_next_store_call_once_the_request_is_cancelled()
    {
        var f = new InMemoryBroker();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f.GitHub);
        var active = await ActionsVariableEnrollmentTests.Active(f, github);
        using var cancellation = new CancellationTokenSource();
        f.State.BeforeRead = _ => cancellation.CancelAsync();
        var commits = f.State.Commits;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BrokerRoutes.RevokeAsync(
            new([Bearer(active.Secret)], false, InMemoryBroker.Body([]), cancellation.Token), active.Device,
            new DeviceRevocation(f.State, f.DeploymentEpoch, f.Clock), 1));
        Assert.Equal(commits, f.State.Commits);
        Assert.Equal("active", f.Gate.State);
    }

    [Theory]
    [InlineData(null, "1")]
    [InlineData(0d, "1")]
    [InlineData(0.2, "1")]
    [InlineData(1d, "1")]
    [InlineData(1.2, "2")]
    [InlineData(59.5, "60")]
    public void Retry_after_is_the_limiters_delay_in_whole_seconds_and_at_least_one(double? seconds, string expected) =>
        Assert.Equal(expected, BrokerRoutes.RetryAfter(seconds is { } value ? TimeSpan.FromSeconds(value) : null));

    private static PresentedAuthorization Bearer(string secret) => new("Bearer ".Length + secret.Length, "Bearer", secret);

    private static BrokerRequest Request(PresentedAuthorization[] authorization, byte[]? body) =>
        new(authorization, body is not null, InMemoryBroker.Body(body ?? []), Token);

    // A setup v1 or v2 start body, or one of the malformed variants of it.
    private static byte[] StartBody(int setupVersion, string variant)
    {
        object request = setupVersion == 1
            ? InMemoryBroker.Request()
            : new RepositoryVariableEnrollmentRequest(Guid.NewGuid().ToString("N"), 123456, "owner/project", "APPROVAL", "polling");
        if (variant == "other-version")
            request = setupVersion == 1 ? new RepositoryVariableEnrollmentRequest(Guid.NewGuid().ToString("N"), 123456, "owner/project", "APPROVAL", "polling")
                : InMemoryBroker.Request();
        var json = JsonSerializer.Serialize(request, JsonSerializerOptions.Web);
        return Encoding.UTF8.GetBytes(variant switch
        {
            "duplicate" => json.Replace("\"repository\":", "\"Repository\":\"owner/other\",\"repository\":", StringComparison.Ordinal),
            "8192-bytes" => json.PadRight(8192),
            "8193-bytes" => json.PadRight(8193),
            "valid" or "other-version" => json,
            _ => variant,
        });
    }

    // A body the adapter reads in full just as the client aborts the request.
    private static Func<int, Task<byte[]>> AbortedAtEnd(byte[] body, CancellationTokenSource abort) => async count =>
    {
        var read = await InMemoryBroker.Body(body)(count);
        await abort.CancelAsync();
        return read;
    };

    // One route over a broker, as a request reaches it: the identity in its path, its version, the credential and the body
    // it normally takes. Composing its service counts as work; an uncomposed route has no service.
    private sealed class Route(RouteName name, InMemoryBroker? broker = null, bool composed = true)
    {
        public InMemoryBroker Broker { get; } = broker ?? new();
        public int Composed { get; private set; }
        public string Id { get; init; } = BrokerRoutesTests.Id;
        public int Version { get; init; } = 1;
        public string Secret { get; init; } = BrokerRoutesTests.Secret;
        public byte[]? Body { get; init; } = name switch
        {
            RouteName.Start => JsonSerializer.SerializeToUtf8Bytes(InMemoryBroker.Request(), JsonSerializerOptions.Web),
            RouteName.Acknowledge => JsonSerializer.SerializeToUtf8Bytes(new { completionId = BrokerRoutesTests.Id }),
            _ => null,
        };

        // The route over a broker with an active enrollment: the device routes name it with its controller secret, the
        // setup routes take setup v3 and name its pending enrollment, and acknowledgment takes its completion ID.
        public static async Task<Route> ForActiveEnrollment(RouteName name)
        {
            var f = new InMemoryBroker();
            var active = await ActionsVariableEnrollmentTests.Active(f, ActionsVariableEnrollmentTests.Repository.Configure(f.GitHub));
            var device = name is RouteName.Revoke or RouteName.Issue or RouteName.Acknowledge;
            return new(name, f)
            {
                Id = device ? active.Device : active.Pending,
                Version = name is RouteName.Revoke or RouteName.Issue ? 1 : 3,
                Secret = device ? active.Secret : BrokerRoutesTests.Secret,
                Body = name switch
                {
                    RouteName.Start => JsonSerializer.SerializeToUtf8Bytes(ActionsVariableEnrollmentTests.Request(), JsonSerializerOptions.Web),
                    RouteName.Acknowledge => JsonSerializer.SerializeToUtf8Bytes(new { completionId = active.Completion }),
                    _ => null,
                },
            };
        }

        public Task<IssuanceResult> Call(BrokerRequest request) => name switch
        {
            RouteName.Revoke => BrokerRoutes.RevokeAsync(request, Id, new DeviceRevocation(Broker.State, Broker.DeploymentEpoch, Broker.Clock), Version),
            RouteName.Issue => BrokerRoutes.IssueAsync(request, Id, () => Compose(Broker.Issuer) is { } issuer ? issuer.IssueAsync : null, Version),
            RouteName.Start => BrokerRoutes.StartAsync(request, () => Compose(Broker.Enrollment), Version),
            RouteName.Status => BrokerRoutes.StatusAsync(request, Id, () => Compose(Broker.Enrollment), Version),
            RouteName.Complete => BrokerRoutes.CompleteAsync(request, Id, () => Compose(Broker.Enrollment), Version),
            RouteName.Acknowledge => BrokerRoutes.AcknowledgeAsync(request, Id, () => Compose(Broker.Enrollment), Version),
            _ => throw new InvalidOperationException("Not a public route: " + name),
        };

        public void AssertNoWork()
        {
            Assert.Equal(0, Composed);
            Assert.Equal((0, 0, 0), (Broker.State.Reads, Broker.State.Lists, Broker.State.Commits));
            Assert.Equal((0, 0), (Broker.GitHub.Reads, Broker.GitHub.Mints));
        }

        private T? Compose<T>(Func<T> service) where T : class
        {
            Composed++;
            return composed ? service() : null;
        }
    }
}
