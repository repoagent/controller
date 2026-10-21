using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

internal static class CleanupProcessFixture
{
    internal static async Task<(int Exit, JsonElement Summary)> RunAsync(RevocationFixture storage,
        string? endpoint = null, string epoch = RevocationFixture.Epoch)
    {
        var token = TestContext.Current.CancellationToken;
        var home = Directory.CreateTempSubdirectory("cleanup-conformance-");
        try
        {
            using var occupied = new TcpListener(IPAddress.Loopback, 0);
            occupied.Start();
            endpoint ??= storage.Table.Uri.AbsoluteUri[..^(storage.TableName.Length + 1)];
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = home.FullName, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false
            };
            foreach (var arg in new[] { typeof(Program).Assembly.Location, "cleanup", "--once" }) start.ArgumentList.Add(arg);
            start.Environment.Clear();
            start.Environment["HOME"] = home.FullName;
            start.Environment["DOTNET_ENVIRONMENT"] = "Development";
            start.Environment["ASPNETCORE_URLS"] = "http://" + occupied.LocalEndpoint;
            start.Environment["Broker__TableEndpoint"] = endpoint;
            start.Environment["Broker__TableName"] = storage.TableName;
            start.Environment["Broker__DeploymentEpoch"] = epoch;
            start.Environment["Broker__Cleanup__DevelopmentConnectionString"] =
                "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
                "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;TableEndpoint=" + endpoint;
            using var child = Process.Start(start)!;
            var stdout = child.StandardOutput.ReadToEndAsync(token);
            var stderr = child.StandardError.ReadToEndAsync(token);
            try { await child.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(75), token); }
            finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
            Assert.True((await stderr).Length == 0, "Child must emit no raw errors or host logs.");
            Assert.Empty(Directory.EnumerateFileSystemEntries(home.FullName, "*", SearchOption.AllDirectories));
            using var json = JsonDocument.Parse(await stdout);
            return (child.ExitCode, json.RootElement.Clone());
        }
        finally { home.Delete(recursive: true); }
    }
}

// This proxy forwards real SDK requests to real Azurite. It never emulates Table decisions.
internal sealed class CleanupTableProxy : IAsyncDisposable
{
    internal sealed record Exchange(string Method, string Path, string Query, string Body, string? ClientRequestId, string? UserAgent);
    private readonly HttpListener listener = new();
    private readonly HttpClient client = new(new HttpClientHandler { UseProxy = false });
    private readonly Uri destination;
    private readonly Task serving;
    private readonly List<Task> active = [];
    internal readonly List<Exchange> Requests = [];
    internal readonly List<int> PageSizes = [];
    internal Func<Exchange, Task<bool>>? Before;
    internal Func<Exchange, Task<bool>>? After { get; set; }
    internal string Endpoint { get; }

    internal CleanupTableProxy(RevocationFixture storage)
    {
        destination = new Uri(storage.Table.Uri.GetLeftPart(UriPartial.Authority));
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var number = ((IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        var origin = $"http://127.0.0.1:{number}/";
        Endpoint = origin + "devstoreaccount1";
        listener.Prefixes.Add(origin);
        listener.Start();
        serving = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (listener.IsListening)
            {
                var context = await listener.GetContextAsync();
                active.Add(ForwardSafelyAsync(context));
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { }
    }

    private async Task ForwardSafelyAsync(HttpListenerContext context)
    {
        try { await ForwardAsync(context); }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
            // Expected when a real child cancels or proxy disposal closes an active response.
        }
    }

    private async Task ForwardAsync(HttpListenerContext context)
    {
        var request = context.Request;
        using var data = new MemoryStream();
        await request.InputStream.CopyToAsync(data);
        var bytes = data.ToArray();
        var exchange = new Exchange(request.HttpMethod, request.Url!.AbsolutePath,
            request.Url.Query, System.Text.Encoding.UTF8.GetString(bytes), request.Headers["x-ms-client-request-id"], request.UserAgent);
        lock (Requests) Requests.Add(exchange);
        if (Before is not null && !await Before(exchange))
        {
            context.Response.StatusCode = 503;
            context.Response.Close();
            return;
        }
        using var forwarded = new HttpRequestMessage(new HttpMethod(request.HttpMethod),
            new Uri(destination, request.RawUrl));
        if (bytes.Length != 0) forwarded.Content = new ByteArrayContent(bytes);
        foreach (var name in request.Headers.AllKeys)
        {
            if (name is null || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
            if (!forwarded.Headers.TryAddWithoutValidation(name, request.Headers[name]))
                forwarded.Content?.Headers.TryAddWithoutValidation(name, request.Headers[name]);
        }
        using var response = await client.SendAsync(forwarded);
        if (After is not null && !await After(exchange))
        {
            // The real storage response was received, but the client never receives it.
            context.Response.Abort();
            return;
        }
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers.Concat(response.Content.Headers))
            if (!header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                context.Response.Headers[header.Key] = string.Join(",", header.Value);
        var body = await response.Content.ReadAsByteArrayAsync();
        if (response.IsSuccessStatusCode && exchange.Query.Contains("$filter", StringComparison.Ordinal))
        {
            using var page = JsonDocument.Parse(body);
            lock (PageSizes) PageSizes.Add(page.RootElement.GetProperty("value").GetArrayLength());
        }
        context.Response.ContentLength64 = body.Length;
        try
        {
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
            // A real client cancellation can disconnect while a delayed response is forwarded.
            context.Response.Abort();
        }
    }

    public async ValueTask DisposeAsync()
    {
        listener.Close();
        await serving;
        await Task.WhenAll(active);
        client.Dispose();
    }
}

// HttpListener.Abort does not produce the required EOF on this stack. This narrow
// shim forwards unchanged storage traffic through CleanupTableProxy, but sends ZERO
// response bytes for its first bodyless GET after consuming the genuine response.
internal sealed class CleanupGetEofShim : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly Uri upstream;
    private readonly List<Task> connections = [];
    private readonly Task serving;
    private int drop = 1;
    internal bool Dropped { get; private set; }
    internal string Endpoint { get; }

    internal CleanupGetEofShim(string upstreamEndpoint)
    {
        upstream = new Uri(upstreamEndpoint);
        listener.Start();
        Endpoint = "http://" + listener.LocalEndpoint + "/devstoreaccount1";
        serving = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (true) connections.Add(ForwardAsync(await listener.AcceptTcpClientAsync()));
        }
        catch (SocketException) { }
    }

    private async Task ForwardAsync(TcpClient incoming)
    {
        using (incoming)
        using (var backend = new TcpClient())
        {
            Task? uploading = null;
            try
            {
                var source = incoming.GetStream();
                using var headers = new MemoryStream();
                var one = new byte[1];
                while (headers.Length < 32768)
                {
                    if (await source.ReadAsync(one) == 0) return;
                    headers.WriteByte(one[0]);
                    if (headers.Length >= 4 && headers.GetBuffer().AsSpan((int)headers.Length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
                }
                if (headers.Length >= 32768) throw new InvalidDataException("EOF fixture header bound exceeded.");
                var lines = System.Text.Encoding.ASCII.GetString(headers.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                var firstGet = lines[0].StartsWith("GET ", StringComparison.Ordinal) && Interlocked.Exchange(ref drop, 0) == 1;
                if (firstGet && lines.Any(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                    && l["Content-Length:".Length..].Trim() != "0")) throw new InvalidDataException("Expected bodyless GET.");
                // One request per backend connection keeps this a bounded header shim;
                // body bytes and genuine responses are streamed without Table emulation.
                var forwarded = lines.Where(l => !l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)
                    && !l.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase));
                var bytes = System.Text.Encoding.ASCII.GetBytes(string.Join("\r\n", forwarded)
                    + "\r\nHost: " + upstream.Authority + "\r\nConnection: close\r\n\r\n");
                await backend.ConnectAsync(upstream.Host, upstream.Port);
                var target = backend.GetStream();
                await target.WriteAsync(bytes);
                if (firstGet)
                {
                    using var response = new MemoryStream();
                    await target.CopyToAsync(response);
                    if (response.Length == 0) throw new InvalidDataException("Real storage response was not received.");
                    Dropped = true;
                    incoming.Client.Shutdown(SocketShutdown.Send);
                    return;
                }
                uploading = source.CopyToAsync(target);
                await target.CopyToAsync(source);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
            finally
            {
                incoming.Close(); backend.Close();
                if (uploading is not null)
                    try { await uploading; }
                    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        listener.Stop();
        await serving;
        await Task.WhenAll(connections);
    }
}
