using System.Text.Json.Nodes;
using Xunit;
using static RepoAgentController.Tests.ControllerHarness;

namespace RepoAgentController.Tests;

// GET /status on the real controller: busy or idle with the operator status's work counts, and nothing else (ADR 0009).
[Collection("Managed checkout environment")]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")] // The App gh wrapper and native relay use Unix tools.
public sealed class StatusEndpointTests
{
    [Fact]
    public async Task A_polling_controller_is_busy_while_an_agent_turn_runs_and_idle_after_it()
    {
        await using var controller = new Controller(discussions: 1);
        await controller.Start();

        var turn = await controller.Turn(1);
        AssertStatus("""{"state":"busy","work":{"sessions":1,"pending":0,"retrying":0,"waiting":0,"running":1,"limit":4,"admissionBlocked":null}}""",
            await controller.Status());

        await controller.Finish(turn);
        await controller.Logged($" turn-ended {Url(1)} ");
        AssertStatus("""{"state":"idle","work":{"sessions":1,"pending":0,"retrying":0,"waiting":0,"running":0,"limit":4,"admissionBlocked":null}}""",
            await controller.Status());
    }

    [Fact]
    public async Task A_webhook_controller_is_busy_while_an_agent_turn_runs_and_idle_after_it()
    {
        await using var controller = new Controller(discussions: 1);
        await controller.StartWebhooks();

        var turn = await controller.Turn(1);
        Assert.Equal("busy", (await controller.Status())["state"]!.GetValue<string>());

        await controller.Finish(turn);
        await controller.Logged($" turn-ended {Url(1)} ");
        Assert.Equal("idle", (await controller.Status())["state"]!.GetValue<string>());

        // Only the local status port serves status; the webhook port serves only the webhook receiver.
        using var client = new HttpClient();
        using var response = await client.GetAsync($"http://127.0.0.1:{controller.WebhookPort}/status", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_turn_whose_Discussion_was_deleted_keeps_the_controller_busy_until_the_agent_ends_it()
    {
        await using var controller = new Controller(discussions: 1);
        await controller.StartWebhooks();
        var turn = await controller.Turn(1);

        // The controller forgets the deleted Discussion's record, but the agent still runs its turn.
        controller.Delete(1);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, await Send(controller, new(Event(1, action: "deleted"))));
        await controller.Logged($" discussion-forgotten {Url(1)} ");
        var status = await controller.Status();
        AssertStatus("""{"state":"busy","work":{"sessions":0,"pending":0,"retrying":0,"waiting":0,"running":1,"limit":4,"admissionBlocked":null}}""",
            status);
        AssertStatus(status["work"]!.ToJsonString(), (await controller.OperatorStatus())["work"]!);

        await controller.Finish(turn);
        status = await controller.Status();
        AssertStatus("""{"state":"idle","work":{"sessions":0,"pending":0,"retrying":0,"waiting":0,"running":0,"limit":4,"admissionBlocked":null}}""",
            status);
        AssertStatus(status["work"]!.ToJsonString(), (await controller.OperatorStatus())["work"]!);
    }

    [Fact]
    public async Task Input_waiting_to_retry_a_delivery_keeps_the_controller_busy_without_a_running_turn()
    {
        await using var controller = new Controller(discussions: 1);
        controller.Native.Reject(1);
        await controller.Start();
        await controller.Logged($" delivery-retry {Url(1)} ");

        AssertStatus("""{"state":"busy","work":{"sessions":1,"pending":0,"retrying":1,"waiting":1,"running":0,"limit":4,"admissionBlocked":null}}""",
            await controller.Status());
    }

    [Theory]
    [InlineData(new[] { "--status-port", "0" }, "--status-port must be between 1 and 65535")]
    [InlineData(new[] { "--status-port", "65536" }, "--status-port must be between 1 and 65535")]
    [InlineData(new[] { "--status-port", "9000", "--webhook-url", "https://fixture.invalid", "--listen-port", "9000" }, "--status-port must differ from --listen-port")]
    public async Task An_unusable_status_port_stops_the_controller_before_it_starts(string[] options, string error)
    {
        using var log = new LogCapture();
        Assert.Equal(1, await BridgeCommand.Run(["--repo", "owner/repository", "--app-id", "123", .. options],
            shutdownToken: TestContext.Current.CancellationToken));
        Assert.Contains(log.Lines, line => line.EndsWith(" fatal " + error, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ASPNETCORE_ENVIRONMENT")]
    [InlineData("DOTNET_ENVIRONMENT")]
    public async Task A_failing_request_gets_an_empty_500_even_when_the_environment_names_Development(string variable)
    {
        // The controller behaves the same in every environment (ADR 0009): no developer exception page.
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "Development");
        try
        {
            var port = FreePort();
            await using var server = await ControllerHttpServer.Start(port, () => throw new InvalidOperationException("status detail"), null,
                TestContext.Current.CancellationToken);
            using var client = new HttpClient();
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/status", TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    static void AssertStatus(string expected, JsonNode actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), actual), actual.ToJsonString());
}
