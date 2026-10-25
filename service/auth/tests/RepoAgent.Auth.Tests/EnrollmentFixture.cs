using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RepoAgent.Auth.Tests;

// The broker host over Azurite with the in-memory GitHub, clock and secret protection: the cleanup tests' enrollment
// setup, and the route tests.
internal sealed class EnrollmentFixture : IAsyncDisposable
{
    public RevocationFixture Storage { get; } = new();
    public IssuanceClock Clock { get; } = new();
    public InMemoryGitHub GitHub { get; }
    public InMemorySecretProtection Secrets { get; } = new();
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host = null!;
    private HttpClient client = null!;
    private readonly Dictionary<string, string?> settings = new() { ["Broker:BrokerOrigin"] = "https://broker.example" };
    private IssuanceLimits limits = new();
    public EnrollmentFixture() => GitHub = new(Clock);
    public static async Task<EnrollmentFixture> CreateAsync(HttpPipelineTransport? transport = null, Dictionary<string, string?>? settings = null, IssuanceLimits? limits = null)
    {
        var f = new EnrollmentFixture();
        if (limits is not null) f.limits = limits;
        await f.Storage.InitializeAsync();
        await f.RestartAsync(transport, settings: settings);
        return f;
    }
    public async Task RestartAsync(HttpPipelineTransport? transport = null, string? epoch = null, Dictionary<string, string?>? settings = null)
    {
        client?.Dispose();
        if (host is not null) await host.DisposeAsync();
        if (settings is not null)
            foreach (var setting in settings) this.settings[setting.Key] = setting.Value;
        host = Storage.CreateHost(transport, settings: this.settings).WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
        {
            if (epoch is not null) configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Broker:DeploymentEpoch"] = epoch });
        }).ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<ISecretProtection>(Secrets);
            services.AddSingleton(limits);
            services.AddSingleton<IGitHubTokens>(GitHub);
            services.AddSingleton<IAppInstallations>(GitHub);
            services.AddSingleton<IControllerApprovals>(GitHub);
            services.AddSingleton(IssuanceServices.CreateStore); services.AddSingleton<InstallationTokenIssuer>();
            services.AddSingleton(IssuanceServices.CreateEnrollmentService);
        }));
        client = host.CreateClient();
    }
    public EnrollmentRequest Request() => new(Guid.NewGuid().ToString("N"), 123456, "owner/project", "approval/environment", "APPROVAL", "polling");
    public async Task<HttpResponseMessage> Send(string path, object? body = null, string? secret = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, path);
        request.Headers.Authorization = new("Bearer", secret ?? new string('a', 43));
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }
    // An acknowledged setup v2 (Agent variable) or v3 (Actions variable) enrollment through the broker routes.
    public async Task<ActionsVariableEnrollmentTests.ActiveGrant> ActiveAsync(ActionsVariableEnrollmentTests.Repository github, int version = 3, string profile = "polling")
    {
        var request = ActionsVariableEnrollmentTests.Request(profile) with { Variable = version == 3 ? "repoagent_approval" : "APPROVAL" };
        using var start = await Send($"/v{version}/enrollments", request);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var body = await start.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var (pending, marker) = (body.GetProperty("pendingId").GetString()!, body.GetProperty("marker").GetString()!);
        (version == 3 ? github.Actions : github.Agent)[request.Variable.ToUpperInvariant()] = marker;
        Clock.Advance(TimeSpan.FromMinutes(1));
        using var completion = await Send($"/v{version}/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var receipt = await completion.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        var enrollment = receipt.GetProperty("enrollment");
        var grant = new ActionsVariableEnrollmentTests.ActiveGrant(request, pending, marker, enrollment.GetProperty("deviceId").GetString()!,
            enrollment.GetProperty("deviceSecret").GetString()!, receipt.GetProperty("completionId").GetString()!, enrollment);
        Clock.Advance(TimeSpan.FromMinutes(1));
        using var ack = await Send($"/v{version}/devices/{grant.Device}/acknowledgment", new { completionId = grant.Completion }, grant.Secret);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        Clock.Advance(TimeSpan.FromMinutes(1));
        return grant;
    }
    public async Task StopHttpAsync()
    {
        client?.Dispose();
        if (host is not null) await host.DisposeAsync();
        host = null!;
    }
    public async ValueTask DisposeAsync()
    {
        await StopHttpAsync();
        await Storage.DisposeAsync();
    }
}
