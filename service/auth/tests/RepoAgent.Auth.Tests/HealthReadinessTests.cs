using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class HealthReadinessTests(ITestOutputHelper testOutput)
{
    [Fact]
    public async Task Fully_configured_application_is_ready_after_startup_with_unavailable_identity()
    {
        await CheckApplicationAsync(null, HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("missing-signing", 503)]
    [InlineData("missing-origin", 503)]
    [InlineData("revoke-only", 503)]
    [InlineData("bad-origin", 0)]
    [InlineData("bad-signing", 0)]
    [InlineData("bad-partial-wrapping", 0)]
    [InlineData("local-storage-production", 0)]
    [InlineData("local-vault-production", 0)]
    [InlineData("local-settings-production", 0)]
    [InlineData("remote-development-storage", 0)]
    public Task Incomplete_or_malformed_configuration_is_not_ready(string scenario, int status) =>
        CheckApplicationAsync(scenario, (HttpStatusCode)status);

    private async Task CheckApplicationAsync(string? scenario, HttpStatusCode expected)
    {
        // A child process isolates managed-identity environment selection from every other test.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var identity = new TcpListener(IPAddress.Loopback, 0);
        identity.Start();
        var identityEndpoint = (IPEndPoint)identity.LocalEndpoint;
        int outboundRequests = 0;
        var identityRequests = RefuseIdentityAsync(identity, () => Interlocked.Increment(ref outboundRequests), deadline.Token);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--urls");
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var address = "http://" + reservation.LocalEndpoint;
        reservation.Stop();
        start.ArgumentList.Add(address);
        foreach (var (name, value) in new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["Broker__TableEndpoint"] = "https://readiness.table.core.windows.net",
            ["Broker__TableName"] = "readiness",
            ["Broker__DeploymentEpoch"] = "readiness-test",
            ["Broker__BrokerOrigin"] = "https://broker.example.test",
            ["Auth__SigningKeyUri"] = "https://readiness.vault.azure.net/keys/signing/version",
            ["Auth__DataProtectionKeyUri"] = "https://readiness.vault.azure.net/keys/wrapping/version",
            ["Auth__DataProtectionBlobUri"] = "https://readiness.blob.core.windows.net/data-protection/keyring.xml",
            ["Auth__DataProtectionApplicationName"] = "readiness-test",
            ["IDENTITY_ENDPOINT"] = $"http://127.0.0.1:{identityEndpoint.Port}/identity",
            ["IDENTITY_HEADER"] = "local-readiness-test"
        }) start.Environment[name] = value;
        foreach (var name in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
            start.Environment[name] = $"http://127.0.0.1:{identityEndpoint.Port}";
        start.Environment["NO_PROXY"] = start.Environment["no_proxy"] = "127.0.0.1,localhost";
        if (scenario == "missing-signing") start.Environment.Remove("Auth__SigningKeyUri");
        if (scenario is "revoke-only" or "bad-partial-wrapping")
            foreach (var name in new[] { "SigningKeyUri", "DataProtectionKeyUri", "DataProtectionBlobUri", "DataProtectionApplicationName" })
                start.Environment.Remove("Auth__" + name);
        if (scenario is "missing-origin" or "revoke-only") start.Environment.Remove("Broker__BrokerOrigin");
        if (scenario == "bad-origin") start.Environment["Broker__BrokerOrigin"] = "https://user:secret@broker.example.test/path";
        if (scenario == "bad-signing") start.Environment["Auth__SigningKeyUri"] = "https://vault.example.test/keys/unversioned";
        if (scenario == "bad-partial-wrapping") start.Environment["Auth__DataProtectionKeyUri"] = "http://invalid.test";
        if (scenario == "local-storage-production") start.Environment["Broker__DevelopmentConnectionString"] = "UseDevelopmentStorage=true";
        if (scenario == "local-vault-production") start.Environment["Auth__DevelopmentVaultUri"] = "https://localhost:4997";
        if (scenario == "local-settings-production") start.Environment["Broker__DevelopmentSettingsFile"] = "does-not-exist.json";
        if (scenario == "remote-development-storage")
        {
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.Environment["Broker__DevelopmentConnectionString"] = "UseDevelopmentStorage=true";
        }
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync(deadline.Token);
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        try
        {
            using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(address) };
            if ((int)expected == 0)
            {
                await process.WaitForExitAsync(deadline.Token);
                Assert.NotEqual(0, process.ExitCode);
                await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/health/live", deadline.Token));
                return;
            }
            // Observe the real HTTP listener, independently of the configured log level.
            while (true)
            {
                if (process.HasExited) Assert.Fail($"Application exited before liveness. {await output} {await errors}");
                try
                {
                    using var live = await client.GetAsync("/health/live", deadline.Token);
                    Assert.Equal(HttpStatusCode.OK, live.StatusCode);
                    await AssertGenericResponseAsync(live);
                    break;
                }
                catch (HttpRequestException) { await Task.Delay(25, deadline.Token); }
            }
            // Normal hosted Data Protection preload has completed before Kestrel serves liveness.
            var startupRequests = Volatile.Read(ref outboundRequests);
            if (scenario is null or "missing-origin") Assert.True(startupRequests > 0);
            for (var repeat = 0; repeat < 3; repeat++) await ProbeAsync();
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => ProbeAsync()));
            Assert.Equal(startupRequests, Volatile.Read(ref outboundRequests));
            testOutput.WriteLine($"External HTTP boundary requests: startup={startupRequests}; probes=0 (19 sequential/concurrent requests).");

            async Task ProbeAsync()
            {
                using var ready = await client.GetAsync("/health/ready", deadline.Token);
                Assert.Equal(expected, ready.StatusCode);
                await AssertGenericResponseAsync(ready);
            }

            async Task AssertGenericResponseAsync(HttpResponseMessage response)
            {
                Assert.True(response.Headers.CacheControl?.NoStore);
                var body = await response.Content.ReadAsStringAsync(deadline.Token);
                Assert.True(body.Length <= 128);
                foreach (var detail in new[] { "readiness-test", "broker.example.test", "invalid_request",
                    "keyring.xml", "EnrollmentService", "InstallationTokenIssuer", "secret" })
                    Assert.DoesNotContain(detail, body, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (output.IsCompletedSuccessfully) testOutput.WriteLine(await output);
            await deadline.CancelAsync();
            identity.Stop();
            try { await identityRequests; }
            catch (OperationCanceledException) { }
            catch (SocketException) when (deadline.IsCancellationRequested) { }
        }
    }

    private static async Task RefuseIdentityAsync(TcpListener listener, Action observeRequest, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            using var connection = await listener.AcceptTcpClientAsync(cancellation);
            observeRequest();
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (await reader.ReadLineAsync(cancellation) is { Length: > 0 }) { }
            const string body = "{\"error\":\"invalid_request\",\"error_description\":\"Local identity boundary unavailable\"}";
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 400 Bad Request\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
            await stream.WriteAsync(response, cancellation);
        }
    }
}
