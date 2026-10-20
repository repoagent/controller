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
        // A child process isolates managed-identity environment selection from every other test.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var identity = new TcpListener(IPAddress.Loopback, 0);
        identity.Start();
        var identityEndpoint = (IPEndPoint)identity.LocalEndpoint;
        var identityRequests = RefuseIdentityAsync(identity, deadline.Token);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add("http://127.0.0.1:0");
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
        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync(deadline.Token);
        var output = new StringBuilder();
        try
        {
            string? address = null;
            while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } line)
            {
                output.AppendLine(line);
                const string listening = "Now listening on: ";
                var offset = line.IndexOf(listening, StringComparison.Ordinal);
                if (offset < 0) continue;
                address = line[(offset + listening.Length)..].Trim();
                break;
            }
            if (address is null)
                Assert.Fail($"Application did not start. {output} {await errors}");
            using var client = new HttpClient { BaseAddress = new Uri(address!) };
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", deadline.Token)).StatusCode);
            var ready = await client.GetAsync("/health/ready", deadline.Token);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }
        finally
        {
            testOutput.WriteLine(output.ToString());
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await deadline.CancelAsync();
            identity.Stop();
            try { await identityRequests; }
            catch (OperationCanceledException) { }
            catch (SocketException) when (deadline.IsCancellationRequested) { }
        }
    }

    private static async Task RefuseIdentityAsync(TcpListener listener, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            using var connection = await listener.AcceptTcpClientAsync(cancellation);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (await reader.ReadLineAsync(cancellation) is { Length: > 0 }) { }
            const string body = "{\"error\":\"invalid_request\",\"error_description\":\"Local identity boundary unavailable\"}";
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 400 Bad Request\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}");
            await stream.WriteAsync(response, cancellation);
        }
    }
}
