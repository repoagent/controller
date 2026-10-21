using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;

namespace RepoAgent.Auth.Tests;

// Remote identity/ingestion responses; retain the real SDK credential and final serialization.
internal sealed class TelemetryCapture : HttpMessageHandler
{
    internal readonly ConcurrentQueue<JsonElement> Envelopes = new();
    private readonly string? previousEndpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT");
    private readonly string? previousHeader = Environment.GetEnvironmentVariable("IDENTITY_HEADER");
    internal TelemetryCapture()
    {
        Environment.SetEnvironmentVariable("IDENTITY_ENDPOINT", "http://127.0.0.1/identity");
        Environment.SetEnvironmentVariable("IDENTITY_HEADER", "synthetic-capture-header");
    }
    protected override void Dispose(bool disposing)
    {
        Environment.SetEnvironmentVariable("IDENTITY_ENDPOINT", previousEndpoint);
        Environment.SetEnvironmentVariable("IDENTITY_HEADER", previousHeader);
        base.Dispose(disposing);
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.RequestUri!.Scheme == "http")
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                access_token = "synthetic-capture-token", expires_in = "3600", expires_on = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds().ToString(),
                resource = "https://monitor.azure.com", token_type = "Bearer", client_id = "11111111-1111-1111-1111-111111111111"
            }), Encoding.UTF8, "application/json") };
        foreach (var line in (await request.Content!.ReadAsStringAsync(token)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var item in json.RootElement.EnumerateArray()) Envelopes.Enqueue(item.Clone());
            else Envelopes.Enqueue(json.RootElement.Clone());
        }
        return new(HttpStatusCode.OK) { Content = new StringContent("{\"itemsReceived\":8,\"itemsAccepted\":8,\"errors\":[]}") };
    }
    internal const string ConnectionForCli = "InstrumentationKey=22222222-2222-2222-2222-222222222222;IngestionEndpoint=https://test.in.applicationinsights.azure.com/";
    internal readonly string Connection = $"InstrumentationKey={Guid.NewGuid()};IngestionEndpoint=https://local.in.applicationinsights.azure.com/";
    internal AzureMonitorExporterOptions Options(HttpClient client)
    {
        var transport = new HttpClientTransport(client);
        return new()
        {
            ConnectionString = Connection, Credential = Credential(transport), Transport = transport,
            DisableOfflineStorage = true, EnableLiveMetrics = false, EnableStandardMetrics = false, EnablePerformanceCounters = false
        };
    }
    internal static TokenCredential Credential(HttpClientTransport transport) => new ManagedIdentityCredential(
        new ManagedIdentityCredentialOptions(ManagedIdentityId.FromUserAssignedClientId("11111111-1111-1111-1111-111111111111"))
        { Transport = transport, Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(1) } });

}

// Finite native TCP/TLS endpoints. Trust is restricted to one fixture certificate.
internal sealed class TelemetryServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentBag<Task> connections = [];
    private readonly Task serving;
    private readonly X509Certificate2? certificate;
    private readonly string mode;
    private readonly bool identity;
    internal readonly ConcurrentQueue<string> Bodies = new();
    internal int Count;
    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    internal TelemetryServer(string mode, bool identity, X509Certificate2? certificate = null)
    {
        this.mode = mode; this.identity = identity; this.certificate = certificate;
        listener.Start(); serving = Serve();
    }
    internal static X509Certificate2 Certificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
    }
    private async Task Serve()
    {
        try { while (!stop.IsCancellationRequested) connections.Add(Handle(await listener.AcceptTcpClientAsync(stop.Token))); }
        catch (OperationCanceledException) { }
    }
    private async Task Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using Stream stream = certificate is null ? client.GetStream() : new SslStream(client.GetStream());
                if (stream is SslStream ssl) await ssl.AuthenticateAsServerAsync(certificate!);
                var header = new StringBuilder(); var one = new byte[1];
                while (await stream.ReadAsync(one, stop.Token) != 0)
                {
                    header.Append((char)one[0]);
                    if (header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) break;
                    if (header.Length > 32768) throw new IOException("Oversized header");
                }
                var length = header.ToString().Split("\r\n").SingleOrDefault(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                if (length is not null)
                {
                    var body = new byte[int.Parse(length.Split(':')[1])];
                    await stream.ReadExactlyAsync(body, stop.Token); Bodies.Enqueue(Encoding.UTF8.GetString(body));
                }
                var count = Interlocked.Increment(ref Count);
                Console.Error.WriteLine($"native identity={identity} request={count}");
                if (mode == "hang" || mode == "refresh" && count > 1) { await Task.Delay(Timeout.Infinite, stop.Token); return; }
                if (mode == "slow") await Task.Delay(700, stop.Token);
                // Remain valid during SDK parsing; the refresh case still requires a second identity request.
                var payload = identity ? JsonSerializer.Serialize(new
                {
                    access_token = "synthetic-native-token", expires_on = DateTimeOffset.UtcNow.AddSeconds(mode == "refresh" ? 60 : 3600).ToUnixTimeSeconds().ToString(),
                    resource = "https://monitor.azure.com", token_type = "Bearer", client_id = "11111111-1111-1111-1111-111111111111"
                }) : "{\"itemsReceived\":8,\"itemsAccepted\":8,\"errors\":[]}";
                var bytes = Encoding.UTF8.GetBytes(mode == "500" ? "{}" : payload);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(mode == "500" ? "500 Failure" : "200 OK")}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                await stream.WriteAsync(bytes, stop.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException) { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); await serving; listener.Stop(); await Task.WhenAll(connections); stop.Dispose();
    }
}
