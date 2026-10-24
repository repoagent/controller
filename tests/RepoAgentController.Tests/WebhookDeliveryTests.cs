using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using static RepoAgentController.Tests.ControllerHarness;

namespace RepoAgentController.Tests;

// The repository webhook receiver's contract with GitHub: its route, signature check and status codes, on the real
// controller in webhook mode. The secret is the one the controller registered with GitHub.
[Collection("Managed checkout environment")]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")] // The App gh wrapper and native relay use Unix tools.
public sealed class WebhookDeliveryTests
{
    [Fact]
    public async Task A_signed_Discussion_delivery_is_accepted_and_starts_a_turn_for_its_Discussion()
    {
        await using var controller = new Controller(discussions: 0);
        await controller.StartWebhooks();
        controller.Open(1);

        Assert.Equal(HttpStatusCode.Accepted, await Send(controller, new(Event(1))));

        await controller.Logged(" webhook-observed discussion=1 success=True");
        Assert.Equal(1, (await controller.Turn(1)).Discussion);
    }

    [Fact]
    public async Task Unsigned_and_wrongly_signed_deliveries_are_rejected_without_observing_their_Discussion()
    {
        await using var controller = new Controller(discussions: 0);
        await controller.StartWebhooks();
        controller.Open(1);
        var body = Event(1);
        var other = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(new string('0', 64)), Encoding.UTF8.GetBytes(body)));

        string?[] signatures = [null, "sha256=" + other, "sha256=not-hex", "sha1=" + Signature(controller, body)[7..], ""];
        foreach (var signature in signatures)
            Assert.Equal(HttpStatusCode.Unauthorized, await Send(controller, new(body) { Signature = signature ?? Unsigned }));

        Assert.Equal(signatures.Length, controller.Log.Count(line => line.Contains(" webhook-rejected reason=signature", StringComparison.Ordinal)));
        Assert.DoesNotContain(controller.Log, line => line.Contains(" webhook-delivery ", StringComparison.Ordinal));
        Assert.Equal(0, controller.Native.TurnCount);
    }

    [Fact]
    public async Task Malformed_and_unrelated_signed_deliveries_get_their_status_codes_and_schedule_nothing()
    {
        await using var controller = new Controller(discussions: 0);
        await controller.StartWebhooks();
        controller.Open(1);
        (string Case, Delivery Delivery, HttpStatusCode Expected)[] cases =
        [
            ("not JSON", new("{not json"), HttpStatusCode.BadRequest),
            ("not an object", new("[]"), HttpStatusCode.BadRequest),
            ("another repository", new(Event(1, repository: "other/repository")), HttpStatusCode.Forbidden),
            ("no delivery ID", new(Event(1)) { Id = null }, HttpStatusCode.BadRequest),
            ("malformed delivery ID", new(Event(1)) { Id = "delivery-1" }, HttpStatusCode.BadRequest),
            ("ping", new(Event(1)) { Event = "ping" }, HttpStatusCode.NoContent),
            ("unsubscribed event", new(Event(1)) { Event = "issues" }, HttpStatusCode.NoContent),
            ("push without a managed clone", new(Event(1)) { Event = "push" }, HttpStatusCode.NoContent),
            ("malformed action", new(Event(1, action: "Created!")), HttpStatusCode.BadRequest),
            ("no Discussion number", new(Event(null)), HttpStatusCode.BadRequest),
            ("non-positive Discussion number", new(Event(0)), HttpStatusCode.BadRequest),
            ("the App's own comment", new(Event(1, sender: ("test-app[bot]", "Bot"))) { Event = "discussion_comment" }, HttpStatusCode.NoContent),
            ("GET", new(Event(1)) { Method = "GET" }, HttpStatusCode.MethodNotAllowed),
            ("another path", new(Event(1)) { Path = "/github/other" }, HttpStatusCode.NotFound),
        ];
        foreach (var (name, delivery, expected) in cases)
            Assert.True(expected == await Send(controller, delivery), name);
        Assert.Equal(413, await Announce(controller, 2 * 1024 * 1024 + 1)); // Refused unread.
        Assert.Equal(408, await Announce(controller, 100)); // The body never arrives within five seconds.

        Assert.Contains(controller.Log, line => line.Contains(" webhook-rejected reason=repository", StringComparison.Ordinal));
        Assert.Contains(controller.Log, line => line.Contains(" webhook-ping delivery=", StringComparison.Ordinal));
        Assert.Contains(controller.Log, line => line.Contains(" webhook-ignored delivery=", StringComparison.Ordinal) && line.EndsWith(" discussion=1 reason=own-comment"));
        Assert.DoesNotContain(controller.Log, line => line.Contains(" webhook-delivery ", StringComparison.Ordinal));
        Assert.Equal(0, controller.Native.TurnCount);
    }

    [Fact]
    public async Task The_webhook_is_registered_at_startup_and_deactivated_on_shutdown()
    {
        await using var controller = new Controller(discussions: 0);
        await controller.StartWebhooks();
        var registration = Assert.Single(controller.Hooks);
        Assert.Equal(("POST", "repos/owner/repository/hooks"), (registration.Text("method"), registration.Text("resource")));
        Assert.Equal("https://fixture.invalid/github/webhook", registration["input"]!["config"]!.Text("url"));

        Assert.Equal(0, await controller.Stop());

        var deactivation = controller.Hooks[^1];
        Assert.Equal(2, controller.Hooks.Length);
        Assert.Equal(("PATCH", "repos/owner/repository/hooks/7"), (deactivation.Text("method"), deactivation.Text("resource")));
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["active"] = false }, deactivation["input"]));
        Assert.Contains(controller.Log, line => line.EndsWith(" webhook-disabled hook=7"));
    }

    const string Unsigned = "\0";
    static readonly HttpClient Client = new();

    /// <summary>A delivery as GitHub sends it; unless a signature is given, it is signed with the registered secret.</summary>
    sealed record Delivery(string Body)
    {
        public string? Event { get; init; } = "discussion";
        public string? Id { get; init; } = Guid.NewGuid().ToString();
        public string? Signature { get; init; }
        public string Method { get; init; } = "POST";
        public string Path { get; init; } = "/github/webhook";
    }

    static async Task<HttpStatusCode> Send(Controller controller, Delivery delivery)
    {
        using var request = new HttpRequestMessage(new HttpMethod(delivery.Method), $"http://127.0.0.1:{controller.WebhookPort}{delivery.Path}")
        {
            Content = new StringContent(delivery.Body, Encoding.UTF8, "application/json")
        };
        if (delivery.Event is not null) request.Headers.Add("X-GitHub-Event", delivery.Event);
        if (delivery.Id is not null) request.Headers.Add("X-GitHub-Delivery", delivery.Id);
        var signature = delivery.Signature ?? Signature(controller, delivery.Body);
        if (signature != Unsigned) request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signature);
        using var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    /// <summary>
    /// Announces a delivery body of <paramref name="length"/> bytes, sends none of it and reads the answer. A receiver that
    /// refuses a body unread closes the connection, so a client still sending it could not read the status.
    /// </summary>
    static async Task<int> Announce(Controller controller, int length)
    {
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, controller.WebhookPort, TestContext.Current.CancellationToken);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /github/webhook HTTP/1.1\r\nHost: 127.0.0.1\r\nX-GitHub-Event: discussion\r\n" +
            $"X-GitHub-Delivery: {Guid.NewGuid()}\r\nContent-Type: application/json\r\nContent-Length: {length}\r\n\r\n"),
            TestContext.Current.CancellationToken);
        var status = await new StreamReader(stream, Encoding.ASCII).ReadLineAsync(TestContext.Current.CancellationToken);
        return int.Parse(status!.Split(' ')[1]);
    }

    static string Signature(Controller controller, string body)
    {
        var secret = controller.Hooks[0]["input"]!["config"]!.Text("secret");
        return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));
    }

    static string Event(int? number, string action = "created", string repository = "owner/repository",
        (string Login, string Type)? sender = null) => new JsonObject
    {
        ["action"] = action,
        ["repository"] = new JsonObject { ["full_name"] = repository, ["id"] = 1 },
        ["discussion"] = number is null ? new JsonObject() : new JsonObject { ["number"] = number, ["user"] = new JsonObject { ["login"] = "participant", ["type"] = "User" }, ["body"] = "Hello" },
        ["sender"] = new JsonObject { ["login"] = sender?.Login ?? "participant", ["type"] = sender?.Type ?? "User" }
    }.ToJsonString();
}
