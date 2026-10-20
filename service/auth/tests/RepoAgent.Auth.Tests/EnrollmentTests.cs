using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core.Pipeline;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class EnrollmentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lost_start_and_completion_responses_recover_one_device_and_only_ack_enables_runtime()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var request = f.Request();
        var start = await f.Send("/v1/enrollments", request);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var pending = await start.Content.ReadFromJsonAsync<JsonElement>(Token);
        var id = pending.GetProperty("pendingId").GetString()!;
        var marker = pending.GetProperty("marker").GetString()!;
        using var duplicate = await f.Send("/v1/enrollments", request);
        Assert.Equal(pending.ToString(), (await duplicate.Content.ReadFromJsonAsync<JsonElement>(Token)).ToString());
        Assert.Equal(1, f.GitHub.VerifierMints);
        f.GitHub.Marker = marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var completion = await f.Send($"/v1/enrollments/{id}/completion");
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var delivered = await completion.Content.ReadFromJsonAsync<JsonElement>(Token);
        var envelope = delivered.GetProperty("enrollment");
        var device = envelope.GetProperty("deviceId").GetString()!;
        var secret = envelope.GetProperty("deviceSecret").GetString()!;
        using var denied = await f.Send($"/v1/devices/{device}/installation-token", secret: secret);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var recovered = await f.Send($"/v1/enrollments/{id}/completion");
        Assert.Equal(delivered.ToString(), (await recovered.Content.ReadFromJsonAsync<JsonElement>(Token)).ToString());
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var ackBody = new { completionId = delivered.GetProperty("completionId").GetString() };
        using var ack = await f.Send($"/v1/devices/{device}/acknowledgment", ackBody, secret);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        using var ackRetry = await f.Send($"/v1/devices/{device}/acknowledgment", ackBody, secret);
        Assert.Equal(HttpStatusCode.OK, ackRetry.StatusCode);
        using var retired = await f.Send($"/v1/enrollments/{id}/completion");
        Assert.Equal(HttpStatusCode.Unauthorized, retired.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var minted = await f.Send($"/v1/devices/{device}/installation-token", secret: secret);
        Assert.Equal(HttpStatusCode.OK, minted.StatusCode);
        Assert.Equal(1, f.GitHub.VerifierMints);
    }

    [Fact]
    public async Task Pending_capacity_and_wrong_recovery_possession_never_allocate_more_provider_work()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var request = f.Request();
        using var first = await f.Send("/v1/enrollments", request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var reads = f.GitHub.Reads;
        using var wrong = await f.Send("/v1/enrollments", request, new string('b', 43));
        Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
        for (var i = 0; i < 3; i++)
        {
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            using var admitted = await f.Send("/v1/enrollments", f.Request());
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        }
        using var full = await f.Send("/v1/enrollments", f.Request());
        Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);
        Assert.Equal(4, f.GitHub.VerifierMints);
    }

    [Fact]
    public async Task Forged_repository_name_and_ID_combination_never_returns_approval_material()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        using var response = await f.Send("/v1/enrollments", f.Request() with { Repository = "other/project" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, f.GitHub.Revokes);
        Assert.Equal("", await response.Content.ReadAsStringAsync(Token));
    }
}

internal sealed class EnrollmentFixture : IAsyncDisposable
{
    public RevocationFixture Storage { get; } = new();
    public IssuanceClock Clock { get; } = new();
    public GitHubTransport GitHub { get; } = new();
    private readonly DirectoryInfo keys = new(Path.Combine(Path.GetTempPath(), "enrollment-keys-" + Guid.NewGuid().ToString("N")));
    public IDataProtectionProvider Protection => DataProtectionProvider.Create(keys);
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host = null!;
    private HttpClient client = null!;
    private readonly Dictionary<string, string?> settings = new() { ["Broker:BrokerOrigin"] = "https://broker.example" };
    public static async Task<EnrollmentFixture> CreateAsync(HttpPipelineTransport? transport = null, Dictionary<string, string?>? settings = null)
    {
        var f = new EnrollmentFixture();
        await f.Storage.InitializeAsync();
        f.GitHub.Before = request => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.EndsWith("/variables", StringComparison.Ordinal)
            ? GitHubTransport.Json(new { total_count = f.GitHub.Marker.Length == 0 ? 0 : 1, variables = f.GitHub.Marker.Length == 0 ? Array.Empty<object>() : [new { name = "APPROVAL", value = f.GitHub.Marker }] }) : null);
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
            services.AddSingleton(Protection);
            services.AddSingleton(new IssuanceLimits());
            services.AddSingleton(new GitHubIssuance(new HttpClient(GitHub), Clock));
            services.AddSingleton(new AppJwtSigner(new SigningTransport(), Clock));
            services.AddSingleton<IssuanceStore>(); services.AddSingleton<InstallationTokenIssuer>();
            services.AddSingleton<EnrollmentStore>(); services.AddSingleton<EnrollmentService>();
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
    public async ValueTask DisposeAsync()
    {
        client?.Dispose();
        if (host is not null) await host.DisposeAsync();
        await Storage.DisposeAsync();
        if (keys.Exists) keys.Delete(true);
    }
}
