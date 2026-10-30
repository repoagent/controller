using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace RepoAgentController;

sealed class LocalProtocolException() : IOException("Private controller protocol is incompatible. Install matching repoagent clients/images and restart the selected controller; no legacy fallback is supported.");

/// <summary>Common protected discovery, lifetime binding and Unix-only HTTP connection for local commands.</summary>
sealed class LocalControllerClient : IDisposable
{
    public ActivityEndpoint Endpoint { get; }
    readonly HttpClient client;

    public LocalControllerClient(string state)
    {
        if (OperatingSystem.IsWindows()) throw new IOException("Local commands require a running Linux or macOS controller.");
        state = Path.GetFullPath(state);
        var pointer = ActivitySocket.Pointer(state);
        ActivitySocket.CheckDirectory(Path.GetDirectoryName(pointer)!);
        Endpoint = JsonSerializer.Deserialize(ManagedCheckout.ReadPrivate(pointer), ActivityJson.Default.ActivityEndpoint)
            ?? throw new IOException("No running controller endpoint.");
        if (Endpoint.Protocol != ActivitySocket.Protocol) throw new LocalProtocolException();
        if (Endpoint.State != state || Endpoint.Lifetime is not { Length: > 0 and <= 128 } ||
            Endpoint.Repository is not { Length: > 0 and <= 256 } || !Path.IsPathFullyQualified(Endpoint.Socket))
            throw new IOException("Controller endpoint identity is invalid.");
        ActivitySocket.CheckDirectory(Path.GetDirectoryName(Endpoint.Socket)!);
        ManagedCheckout.CheckPath(Endpoint.Socket);
        if (File.GetUnixFileMode(Endpoint.Socket) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
            throw new IOException("Controller socket is not private.");
        SocketsHttpHandler? handler = new();
        try
        {
            handler.UseProxy = false;
            handler.AllowAutoRedirect = false;
            handler.ConnectTimeout = TimeSpan.FromSeconds(5);
            handler.MaxConnectionsPerServer = 1;
            handler.ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(Endpoint.Socket), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            };
            client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            handler = null;
        }
        finally { handler?.Dispose(); }
    }

    public async Task<HttpResponseMessage> Send(string path, byte[] body, TimeSpan headersDeadline, CancellationToken ct)
    {
        if (body.Length > 16384) throw new IOException("Local command selection exceeds its limit.");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("http://localhost" + path));
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        request.Headers.ConnectionClose = true;
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType = new("application/json");
        using var deadline = new CancellationTokenSource(headersDeadline);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded.Token); }
        catch (HttpRequestException) { throw new IOException("Private controller HTTP endpoint is unavailable. Install matching repoagent clients/images and restart the selected controller."); }
    }

    public async Task<RemoteAccessResult> Remote(RemoteOperation operation, string? selectedClient, CancellationToken ct)
    {
        var request = new RemoteRequest(Endpoint.State, Endpoint.Repository, Endpoint.Lifetime, selectedClient);
        using var response = await Send("/v1/remote/" + operation.ToString().ToLowerInvariant(),
            JsonSerializer.SerializeToUtf8Bytes(request, ActivityJson.Default.RemoteRequest), TimeSpan.FromSeconds(5), ct);
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable))
            throw new IOException("Remote selection or controller lifetime is invalid.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(bounded.Token);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, bounded.Token);
            if (count == 0) break;
            if (bytes.Length + count > 16384) throw new IOException("Remote response exceeds its limit.");
            bytes.Write(buffer, 0, count);
        }
        var result = JsonSerializer.Deserialize(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)), ActivityJson.Default.RemoteAccessResult)
            ?? throw new IOException("Invalid remote response.");
        if ((response.StatusCode == HttpStatusCode.Forbidden && result.Outcome != RemoteAccessOutcome.Forbidden) ||
            (response.StatusCode == HttpStatusCode.ServiceUnavailable && result.Outcome != RemoteAccessOutcome.Unavailable))
            throw new IOException("Invalid remote outcome.");
        return result;
    }

    public void Dispose() => client.Dispose();
}

static class ActivityClient
{
    public static async IAsyncEnumerable<ActivityFrame> Read(string state, string? discussion, int limit, string? cursor, bool follow,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var client = new LocalControllerClient(state);
        var endpoint = client.Endpoint;
        var request = new ActivityRequest(endpoint.State, endpoint.Repository, endpoint.Lifetime, discussion, limit, cursor, follow);
        using var response = await client.Send(ActivitySocket.ActivityPath,
            JsonSerializer.SerializeToUtf8Bytes(request, ActivityJson.Default.ActivityRequest), TimeSpan.FromSeconds(follow ? 30 : 10), ct);
        if (!response.IsSuccessStatusCode) throw new IOException("Activity permission or selection was refused.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var reader = new ActivityLines(stream);
        var receivedSnapshot = false;
        long sequence = 0;
        while (true)
        {
            using var receive = new CancellationTokenSource(TimeSpan.FromSeconds(follow ? 30 : 10));
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, receive.Token);
            string? line;
            try { line = await reader.Read(1024 * 1024, bounded.Token); }
            catch (IOException) when (follow && receivedSnapshot) { line = null; }
            if (line is null)
            {
                if (follow && receivedSnapshot)
                {
                    yield return new("gap", endpoint.Lifetime, Notice: "Controller disconnected; read a new snapshot.", Sequence: sequence);
                    yield break;
                }
                throw new IOException("Controller activity disconnected; read a new snapshot.");
            }
            var frame = JsonSerializer.Deserialize(line, ActivityJson.Default.ActivityFrame) ?? throw new IOException("Invalid activity response.");
            if (frame.Lifetime != endpoint.Lifetime || frame.Kind == "error") throw new IOException("Activity is unavailable or the selection is invalid.");
            receivedSnapshot |= frame.Snapshot is not null;
            sequence = Math.Max(sequence, Math.Max(frame.Sequence, frame.Snapshot?.Sequence ?? 0));
            yield return frame;
            if (!follow || frame.Kind == "gap") yield break;
        }
    }
}
