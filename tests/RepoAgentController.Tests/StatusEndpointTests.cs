using System.Text.Json.Nodes;
using Xunit;
using static RepoAgentController.Tests.ControllerHarness;

namespace RepoAgentController.Tests;

// Real HTTP serialization/routing and CLI validation. Core busy/idle decisions belong to ControllerStatusProjectionTests.
[Collection("Managed checkout environment")]
public sealed class StatusEndpointTests
{
    [Fact]
    public async Task Status_serialization_and_route_isolation_need_no_agent_or_GitHub_process()
    {
        var statusPort = FreePort();
        var webhookPort = FreePort(statusPort);
        var snapshot = new StatusSnapshot("idle", new WorkCounts(5, 0, 0, 0, 0, 4, null));
        var deliveries = 0;
        await using var server = await ControllerHttpServer.Start(statusPort, () => snapshot, (webhookPort, context =>
        {
            Interlocked.Increment(ref deliveries);
            context.Response.StatusCode = 204;
            return Task.CompletedTask;
        }
        ), TestContext.Current.CancellationToken);
        using var client = new HttpClient();
        using var status = await client.GetAsync($"http://127.0.0.1:{statusPort}/status", TestContext.Current.CancellationToken);
        Assert.True(status.Headers.CacheControl?.NoStore);
        AssertStatus("""{"state":"idle","work":{"sessions":5,"pending":0,"retrying":0,"waiting":0,"running":0,"limit":4,"admissionBlocked":null}}""",
            JsonNode.Parse(await status.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!);
        using var wrongStatusPort = await client.GetAsync($"http://127.0.0.1:{webhookPort}/status", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, wrongStatusPort.StatusCode);
        using var wrongWebhookPort = await client.PostAsync($"http://127.0.0.1:{statusPort}/github/webhook", null, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, wrongWebhookPort.StatusCode);
        using var delivered = await client.PostAsync($"http://127.0.0.1:{webhookPort}/github/webhook", null, TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, delivered.StatusCode);
        Assert.Equal(1, deliveries);
        foreach (var port in new[] { statusPort, webhookPort })
        foreach (var path in new[] { "/v1/activity", "/v1/remote/enable", "/v1/remote/pair", "/v1/remote/status",
            "/v1/remote/clients", "/v1/remote/revoke", "/v1/remote/disable" })
        {
            using var privateRoute = await client.PostAsync($"http://127.0.0.1:{port}{path}", null, TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, privateRoute.StatusCode);
        }
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
