using System.Diagnostics;
using System.Text.Json;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Xunit;

namespace RepoAgent.Auth.Tests;

[CollectionDefinition("Telemetry", DisableParallelization = true)]
public sealed class TelemetryCollection;

[Collection("Telemetry")]
public sealed class TelemetryTests(ITestOutputHelper output)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] Suffixes = ["completed", "failures", "unknown_deadlines", "cursor_age_max_seconds", "cycle_age_max_seconds", "cycle_age_unknown", "overdue_payload_age_max_seconds", "stale_cursor_with_overdue_observation"];
    internal static JsonElement Summary(double? cursor = 950, double? cycle = 22000, double? overdue = 900) => JsonSerializer.SerializeToElement(new
    {
        failures = 2, unknownDeadlineCount = 3, stopReason = "request-budget",
        cursorAndCycleAges = new[] { new { cursorAgeSeconds = cursor, cycleAgeSeconds = cycle }, new { cursorAgeSeconds = cursor is null ? null : (double?)1, cycleAgeSeconds = (double?)null } },
        oldestObservedOverduePayloadAgeSeconds = overdue
    });
    [Theory]
    [InlineData(950d, 22000d, 900d, 1d)]
    [InlineData(899d, 1d, 900d, 0d)]
    [InlineData(900d, 1d, 899d, 0d)]
    [InlineData(900d, 1d, 900d, 1d)]
    [InlineData(null, null, null, 0d)]
    public void Cleanup_exports_observations_without_dimensions(double? cursor, double? cycle, double? overdue, double stale)
    {
        using var capture = new TelemetryCapture(); using var client = new HttpClient(capture);
        BrokerTelemetry.ExportCleanupOnce(Summary(cursor, cycle, overdue), capture.Options(client));
        var metrics = capture.Envelopes.SelectMany(e => e.GetProperty("data").GetProperty("baseData").GetProperty("metrics").EnumerateArray()).Where(m => m.GetProperty("name").GetString() != "_OTELRESOURCE_").ToArray();
        Assert.NotEmpty(metrics);
        var values = metrics.ToDictionary(m => m.GetProperty("name").GetString()!.Replace("repoagent.auth.cleanup.", "", StringComparison.Ordinal), m => m.GetProperty("value").GetDouble());
        Assert.All(values.Keys, name => Assert.Contains(name, Suffixes));
        Assert.Equal(cycle is null ? 5 : 8, values.Count);
        Assert.Equal(1, values["completed"]); Assert.Equal(2, values["failures"]); Assert.Equal(3, values["unknown_deadlines"]);
        Assert.Equal(cycle is null ? 2 : 1, values["cycle_age_unknown"]); Assert.Equal(stale, values["stale_cursor_with_overdue_observation"]);
        if (cursor is not null) Assert.Equal(cursor.Value, values["cursor_age_max_seconds"]); else Assert.DoesNotContain("cursor_age_max_seconds", values.Keys);
        if (cycle is not null) Assert.Equal(cycle.Value, values["cycle_age_max_seconds"]); else Assert.DoesNotContain("cycle_age_max_seconds", values.Keys);
        if (overdue is not null) Assert.Equal(overdue.Value, values["overdue_payload_age_max_seconds"]); else Assert.DoesNotContain("overdue_payload_age_max_seconds", values.Keys);
        foreach (var e in capture.Envelopes.Where(e => e.GetProperty("data").GetProperty("baseData").GetProperty("metrics")[0].GetProperty("name").GetString() == "_OTELRESOURCE_"))
            Assert.Equal(new[] { "service.instance.id", "service.name" }, e.GetProperty("data").GetProperty("baseData").GetProperty("properties").EnumerateObject().Select(p => p.Name).Order());
        foreach (var e in capture.Envelopes.Where(e => e.GetProperty("data").GetProperty("baseData").GetProperty("metrics")[0].GetProperty("name").GetString() != "_OTELRESOURCE_"))
        {
            var data = e.GetProperty("data").GetProperty("baseData");
            Assert.True(!data.TryGetProperty("properties", out var properties) || !properties.EnumerateObject().Any());
            Assert.Equal("repoagent-auth-cleanup", e.GetProperty("tags").GetProperty("ai.cloud.role").GetString());
            Assert.Equal("repoagent-auth-cleanup", e.GetProperty("tags").GetProperty("ai.cloud.roleInstance").GetString());
        }
        output.WriteLine(JsonSerializer.Serialize(values));
    }
    [Fact]
    public async Task Public_requests_export_only_safe_independent_observations()
    {
        var scenario = Environment.GetEnvironmentVariable("AUTH_TEST_PRIVACY_CASE");
        if (scenario is null)
        {
            foreach (var value in new[] { "matched", "unmatched", "unsampled", "disabled", "malformed", "unexpected-event" })
                await RunChild("Public_requests_export_only_safe_independent_observations", "AUTH_TEST_PRIVACY_CASE", value, 20);
            return;
        }
        var ratio = scenario == "unsampled" ? 0f : 1f;
        var path = scenario == "unmatched" ? "/private-unmatched?secret=private-query" : "/v2/devices/private-path/installation-token?secret=private-query";
        var requestName = scenario == "unmatched" ? "GET unmatched" : "POST /v2/devices/{deviceId}/installation-token";
        using var capture = new TelemetryCapture(); using var exportClient = new HttpClient(capture);
        Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING", scenario == "disabled" ? null : scenario == "malformed" ? "private-malformed" : capture.Connection);
        Environment.SetEnvironmentVariable("Broker__ManagedIdentityClientId", "11111111-1111-1111-1111-111111111111");
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");
        await using var factory = new RevocationFixture().CreateHost(settings: new Dictionary<string, string?>
        {
            ["Broker:ManagedIdentityClientId"] = "11111111-1111-1111-1111-111111111111"
        }).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.PostConfigure<AzureMonitorExporterOptions>(o =>
            {
                if (scenario == "matched")
                {
                    Assert.Equal(0.01f, o.SamplingRatio); Assert.Null(o.TracesPerSecond);
                    Assert.False(o.EnableTraceBasedLogsSampler); Assert.True(o.DisableOfflineStorage);
                    Assert.False(o.EnableLiveMetrics); Assert.False(o.EnableStandardMetrics); Assert.False(o.EnablePerformanceCounters);
                    Assert.Equal(0, o.Retry.MaxRetries); Assert.Equal(TimeSpan.FromSeconds(1), o.Retry.NetworkTimeout);
                }
                var transport = new HttpClientTransport(exportClient);
                o.Credential = TelemetryCapture.Credential(transport); o.Transport = transport; o.SamplingRatio = ratio;
            });
            services.AddSingleton<IStartupFilter, HostileLogging>();
        }));
        factory.UseKestrel(0);
        using var startClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()), Timeout = TimeSpan.FromSeconds(5) };
        output.WriteLine("Kestrel=" + client.BaseAddress);
        foreach (var sampled in new[] { "01", "00", "legacy" })
        {
            using var request = new HttpRequestMessage(scenario == "unmatched" ? HttpMethod.Get : HttpMethod.Post, path);
            if (sampled != "legacy") request.Headers.TryAddWithoutValidation("traceparent", "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-" + sampled);
            request.Headers.TryAddWithoutValidation("tracestate", "private=private-tracestate"); request.Headers.TryAddWithoutValidation("baggage", "private=private-baggage");
            request.Headers.TryAddWithoutValidation("Request-Id", "|private-legacy.parent."); request.Headers.TryAddWithoutValidation("Correlation-Context", "private=private-context");
            foreach (var name in new[] { "Authorization", "Cookie", "User-Agent", "Host", "X-Private" }) request.Headers.TryAddWithoutValidation(name, "private-header");
            using var response = await client.SendAsync(request, Token);
            Assert.Equal(scenario == "unmatched" ? 404 : 401, (int)response.StatusCode);
        }
        foreach (var health in new[] { "/health/live", "/health/ready" }) { using var response = await client.GetAsync(health, Token); }
        if (scenario is "disabled" or "malformed")
        {
            Assert.Null(factory.Services.GetService<TracerProvider>()); Assert.Empty(capture.Envelopes); return;
        }
        factory.Services.GetRequiredService<TracerProvider>().ForceFlush();
        factory.Services.GetRequiredService<LoggerProvider>().ForceFlush();
        Assert.Equal("true", Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED"));
        var envelopes = capture.Envelopes.ToArray(); Assert.NotEmpty(envelopes); output.WriteLine(JsonSerializer.Serialize(envelopes));
        foreach (var envelope in envelopes)
        {
            foreach (var secret in new[] { "private-", "PrivateStackSentinel", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb", "127.0.0.1", "localhost" }) Assert.All(Strings(envelope), value => Assert.DoesNotContain(secret, value));
            var tags = envelope.GetProperty("tags");
            Assert.Equal("repoagent-auth", tags.GetProperty("ai.cloud.role").GetString()); Assert.Equal("repoagent-auth", tags.GetProperty("ai.cloud.roleInstance").GetString());
            Assert.DoesNotContain(tags.EnumerateObject(), p => p.Name.StartsWith("ai.user", StringComparison.Ordinal) || p.Name.StartsWith("ai.session", StringComparison.Ordinal) || p.Name == "ai.location.ip");
        }
        var requests = envelopes.Where(e => e.GetProperty("data").GetProperty("baseType").GetString() == "RequestData").ToArray();
        var logs = envelopes.Where(e => e.GetProperty("data").GetProperty("baseType").GetString() == "MessageData").ToArray();
        Assert.True(logs.Length >= 5);
        Assert.True(logs.Count(e => e.GetProperty("data").GetProperty("baseData").GetProperty("message").GetString() == "Broker warning") >= 5);
        Assert.All(logs, e => Assert.Contains(e.GetProperty("data").GetProperty("baseData").GetProperty("message").GetString(), new[] { "Broker warning", "Broker error" }));
        foreach (var log in logs.Select(e => e.GetProperty("data").GetProperty("baseData")))
        {
            Assert.Contains(log.GetProperty("severityLevel").GetString(), new[] { "Warning", "Error", "Critical" });
            Assert.All(log.GetProperty("properties").EnumerateObject(), p => Assert.Contains(p.Name, new[] { "event.id", "origin", "CategoryName", "EventId" }));
        }
        var warnings = logs.Where(e => e.GetProperty("data").GetProperty("baseData").GetProperty("properties").GetProperty("origin").GetString() == "broker").ToArray();
        Assert.Equal(5, warnings.Length);
        foreach (var warning in warnings)
        {
            var tags = warning.GetProperty("tags");
            Assert.True(tags.TryGetProperty("ai.operation.id", out var trace), "Warning must retain broker-owned correlation.");
            Assert.True(tags.TryGetProperty("ai.operation.parentId", out var parent), "Warning must retain its broker-owned server span.");
            Assert.Equal(32, trace.GetString()!.Length); Assert.Equal(16, parent.GetString()!.Length);
        }
        foreach (var request in requests)
            Assert.Contains(warnings, warning => warning.GetProperty("tags").GetProperty("ai.operation.id").GetString() == request.GetProperty("tags").GetProperty("ai.operation.id").GetString()
                && warning.GetProperty("tags").GetProperty("ai.operation.parentId").GetString() == request.GetProperty("data").GetProperty("baseData").GetProperty("id").GetString());
        Assert.DoesNotContain(envelopes, e => e.GetProperty("data").GetProperty("baseType").GetString() is "RemoteDependencyData" or "ExceptionData");
        if (ratio == 0 || scenario == "unexpected-event") Assert.Empty(requests);
        else
        {
            Assert.Equal(3, requests.Length);
            foreach (var e in requests)
            {
                var data = e.GetProperty("data").GetProperty("baseData");
                Assert.Equal(requestName, data.GetProperty("name").GetString());
                Assert.Equal(scenario == "unmatched" ? "404" : "401", data.GetProperty("responseCode").GetString());
                Assert.True(TimeSpan.Parse(data.GetProperty("duration").GetString()!) >= TimeSpan.Zero);
                Assert.True(e.GetProperty("time").GetDateTimeOffset() > DateTimeOffset.UnixEpoch);
            }
        }

    }
    [Fact]
    public async Task Native_cleanup_lifetime()
    {
        var mode = Environment.GetEnvironmentVariable("AUTH_TEST_TELEMETRY_CASE");
        if (mode is null)
        {
            foreach (var scenario in new[] { "success", "500", "hang", "refresh", "slow" })
                await RunChild("Native_cleanup_lifetime", "AUTH_TEST_TELEMETRY_CASE", scenario, 8);
            return;
        }
        using var certificate = TelemetryServer.Certificate();
        await using var identity = new TelemetryServer(mode, true);
        await using var ingestion = new TelemetryServer(mode == "slow" ? "hang" : "success", false, certificate);
        Environment.SetEnvironmentVariable("IDENTITY_ENDPOINT", $"http://127.0.0.1:{identity.Port}/token"); Environment.SetEnvironmentVariable("IDENTITY_HEADER", "synthetic-fixture-header");
        Environment.SetEnvironmentVariable("MSI_ENDPOINT", null); Environment.SetEnvironmentVariable("MSI_SECRET", null);
        var directory = Directory.CreateTempSubdirectory("telemetry-native-"); var watch = Stopwatch.StartNew();
        using (var handler = new HttpClientHandler { UseProxy = false, ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.Thumbprint == certificate.Thumbprint })
        using (var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
        using (var transport = new HttpClientTransport(client))
        {
            var credential = new ManagedIdentityCredential(new ManagedIdentityCredentialOptions(ManagedIdentityId.FromUserAssignedClientId("11111111-1111-1111-1111-111111111111"))
            { Transport = transport, Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(1) } });
            if (mode == "refresh")
                await credential.GetTokenAsync(new Azure.Core.TokenRequestContext(["https://monitor.azure.com//.default"]), Token);
            var options = new AzureMonitorExporterOptions
            {
                ConnectionString = $"InstrumentationKey=22222222-2222-2222-2222-222222222222;IngestionEndpoint=https://127.0.0.1:{ingestion.Port}/",
                Credential = credential, Transport = transport, StorageDirectory = directory.FullName
            };
            BrokerTelemetry.ExportCleanupOnce(Summary(), options);
        }
        watch.Stop(); output.WriteLine($"{mode}: full lifetime={watch.ElapsedMilliseconds}ms identity={identity.Count} ingestion={ingestion.Count}");
        Assert.InRange(watch.ElapsedMilliseconds, 0, 5000); Assert.True(identity.Count > 0);
        if (mode == "refresh") Assert.True(identity.Count >= 2, "The near-expiry token must reach refresh.");
        Assert.Equal(mode is "success" or "slow" ? 1 : 0, ingestion.Count);
        if (mode is "success" or "slow")
        {
            var body = Assert.Single(ingestion.Bodies);
            Assert.Contains("repoagent.auth.cleanup.completed", body);
            Assert.DoesNotContain("synthetic-native-token", body);
        }
        var counts = (identity.Count, ingestion.Count); await Task.Delay(250, Token); Assert.Equal(counts, (identity.Count, ingestion.Count));
        Assert.Empty(directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories)); directory.Delete(true);
    }
    private static IEnumerable<string> Strings(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) yield return value.GetString()!;
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject()) foreach (var text in Strings(property.Value)) yield return text;
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) foreach (var text in Strings(item)) yield return text;
    }
    private async Task RunChild(string method, string key, string value, int seconds)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { typeof(TelemetryTests).Assembly.Location, "-method", "*" + method, "-showLiveOutput" }) start.ArgumentList.Add(arg);
        start.Environment[key] = value;
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync(Token); var stderr = child.StandardError.ReadToEndAsync(Token);
        try { await child.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(seconds), Token); }
        finally { if (!child.HasExited) child.Kill(true); }
        var result = await stdout + await stderr;
        output.WriteLine(value + ": " + result);
        if (key == "AUTH_TEST_PRIVACY_CASE") { Assert.Contains("private-body private-state", result); Assert.DoesNotContain("private-info", result); Assert.DoesNotContain("private-debug", result); }
        Assert.Contains("Total: 1, Errors: 0", result); Assert.Equal(0, child.ExitCode);
    }
    private sealed class HostileLogging : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, run) =>
            {
                var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RepoAgent.Auth.private-category");
                Activity.Current?.SetTag("private-attribute", "private-attribute-value");
                Activity.Current?.SetBaggage("private-added-baggage", "private-added-value");
                Activity.Current?.SetStatus(ActivityStatusCode.Error, "private-status-description");
                if (Environment.GetEnvironmentVariable("AUTH_TEST_PRIVACY_CASE") == "unexpected-event")
                {
                    Activity.Current?.AddEvent(new ActivityEvent("private-event"));
                    Activity.Current?.AddLink(new ActivityLink(new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded), new ActivityTagsCollection { { "private-link", "private-value" } }));
                }
                logger.LogInformation("private-info"); logger.LogDebug("private-debug");
                try { PrivateStackSentinel(); }
                catch (InvalidOperationException ex) { logger.LogWarning(new EventId(7, "private-event-name"), ex, "private-body {Secret}", "private-state"); }
                using var server = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); server.Start();
                using var dependency = new HttpClient(new HttpClientHandler { UseProxy = false });
                var send = dependency.GetAsync("http://" + server.LocalEndpoint + "/private-dependency", context.RequestAborted);
                using var connection = await server.AcceptTcpClientAsync(context.RequestAborted);
                await connection.GetStream().WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), context.RequestAborted);
                using var response = await send;
                Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
                await run(context);
            });
            next(app);
        };
        private static void PrivateStackSentinel() => throw new InvalidOperationException("private-exception");
    }

}
