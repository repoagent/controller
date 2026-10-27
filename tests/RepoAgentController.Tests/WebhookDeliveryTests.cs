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
        // The body never arrives: the receiver answers once five seconds of controlled time have passed.
        var deadline = controller.Armed(TimeSpan.FromSeconds(5));
        Assert.Equal(408, await Announce(controller, 100, async () =>
        {
            await deadline;
            controller.Elapse(TimeSpan.FromSeconds(5));
        }));

        Assert.Contains(controller.Log, line => line.Contains(" webhook-rejected reason=repository", StringComparison.Ordinal));
        Assert.Contains(controller.Log, line => line.Contains(" webhook-ping delivery=", StringComparison.Ordinal));
        Assert.Contains(controller.Log, line => line.Contains(" webhook-ignored delivery=", StringComparison.Ordinal) && line.EndsWith(" discussion=1 reason=own-comment", StringComparison.Ordinal));
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
        Assert.Contains(controller.Log, line => line.EndsWith(" webhook-disabled hook=7", StringComparison.Ordinal));
    }

    /// <summary>
    /// Announces a delivery body of <paramref name="length"/> bytes, sends none of it, runs <paramref name="meanwhile"/> and
    /// reads the answer. A receiver that refuses a body unread closes the connection, so a client still sending it could not
    /// read the status.
    /// </summary>
    static async Task<int> Announce(Controller controller, int length, Func<Task>? meanwhile = null)
    {
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, controller.WebhookPort, TestContext.Current.CancellationToken);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /github/webhook HTTP/1.1\r\nHost: 127.0.0.1\r\nX-GitHub-Event: discussion\r\n" +
            $"X-GitHub-Delivery: {Guid.NewGuid()}\r\nContent-Type: application/json\r\nContent-Length: {length}\r\n\r\n"),
            TestContext.Current.CancellationToken);
        if (meanwhile is not null) await meanwhile();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        var status = await reader.ReadLineAsync(TestContext.Current.CancellationToken)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        return int.Parse(status!.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
    }
}
