using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RepoAgentController;

sealed record ActivityEndpoint(string State, string Repository, string Lifetime, string Socket);
sealed record ActivityRequest(string State, string Repository, string Lifetime, string? Discussion, int Limit, string? Cursor, bool Follow);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ActivityEndpoint))]
[JsonSerializable(typeof(ActivityRequest))]
[JsonSerializable(typeof(ActivityFrame))]
sealed partial class ActivityJson : JsonSerializerContext;

/// <summary>Lifetime-bound private operator IPC. Independent from HTTP and credential handoff; no runtime creation authority.</summary>
sealed class ActivitySocket : IAsyncDisposable
{
    const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    const UnixFileMode PrivateDirectory = PrivateFile | UnixFileMode.UserExecute;
    readonly Socket listener;
    readonly ActivityEndpoint endpoint;
    readonly string pointer, directory;
    readonly SessionActivityInspector inspector;
    readonly TimeProvider clock;
    readonly CancellationTokenSource stop;
    readonly ConcurrentDictionary<int, Task> clients = new();
    readonly Task accepting;
    int nextClient;

    ActivitySocket(Socket listener, ActivityEndpoint endpoint, string pointer, string directory,
        SessionActivityInspector inspector, TimeProvider clock, CancellationToken ct)
    {
        this.listener = listener; this.endpoint = endpoint; this.pointer = pointer; this.directory = directory;
        this.inspector = inspector; this.clock = clock;
        stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        accepting = Accept();
    }

    public static string Pointer(string state) => state + ".activity/endpoint.json";

    public static async Task<ActivitySocket?> TryStart(string state, string repository, SessionActivityInspector inspector,
        string lifetime, TimeProvider clock, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) return null;
        try { return await Start(state, repository, lifetime, inspector, clock, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { Log.Write("activity-unavailable", "Private local activity inspection did not become available within its startup deadline."); return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SocketException or PlatformNotSupportedException or JsonException or ArgumentException)
        { Log.Write("activity-unavailable", "Private local activity inspection is unavailable on this filesystem."); return null; }
    }

    public static async Task<ActivitySocket> Start(string state, string repository, string lifetime, SessionActivityInspector inspector,
        TimeProvider clock, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Activity inspection requires Unix sockets and private Unix file permissions.");
        state = Path.GetFullPath(state);
        ManagedCheckout.CheckPath(state);
        var pointer = Pointer(state);
        var pointerDirectory = Path.GetDirectoryName(pointer)!;
        ManagedCheckout.CheckPath(pointerDirectory);
        Directory.CreateDirectory(pointerDirectory, PrivateDirectory);
        CheckDirectory(pointerDirectory);
        if (File.Exists(pointer))
        {
            var previous = JsonSerializer.Deserialize(ManagedCheckout.ReadPrivate(pointer), ActivityJson.Default.ActivityEndpoint)
                ?? throw new IOException("Invalid private activity endpoint.");
            if (previous.State != state || previous.Repository != repository) throw new IOException("Activity endpoint identity changed.");
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1), clock);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            var active = false;
            try { await probe.ConnectAsync(new UnixDomainSocketEndPoint(previous.Socket), bounded.Token); active = true; }
            catch (SocketException) { }
            if (active) throw new IOException("A private activity endpoint is already running.");
            File.Delete(pointer); // Stale descriptor only. Never unlink an endpoint named by a previous lifetime.
        }
        var directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : "/tmp", "repoagent-activity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, PrivateDirectory);
        CheckDirectory(directory);
        var path = Path.Combine(directory, "operator.sock");
        Socket? listener = null;
        var temporary = pointer + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var createdTemporary = false;
        var transferred = false;
        try
        {
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            File.SetUnixFileMode(path, PrivateFile);
            if (File.GetUnixFileMode(path) != PrivateFile) throw new IOException("Activity socket is not private.");
            listener.Listen(8);
            var endpoint = new ActivityEndpoint(state, repository, lifetime, path);
            await using (var output = new FileStream(temporary, new FileStreamOptions
            { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read, UnixCreateMode = PrivateFile }))
            {
                createdTemporary = true;
                await JsonSerializer.SerializeAsync(output, endpoint, ActivityJson.Default.ActivityEndpoint, ct);
            }
            _ = ManagedCheckout.ReadPrivate(temporary);
            File.Move(temporary, pointer);
            var server = new ActivitySocket(listener, endpoint, pointer, directory, inspector, clock, ct);
            listener = null;
            transferred = true;
            return server;
        }
        finally
        {
            listener?.Dispose();
            if (!transferred && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            if (createdTemporary && File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static void CheckDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ManagedCheckout.CheckPath(path);
        if (!Directory.Exists(path) || File.GetUnixFileMode(path) != PrivateDirectory)
            throw new IOException("Activity endpoint requires a private 0700 directory.");
    }

    async Task Accept()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptAsync(stop.Token);
                if (clients.Count >= 8) { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref nextClient);
                var serving = Serve(client);
                clients[id] = serving;
                _ = serving.ContinueWith(_ => clients.TryRemove(id, out var ignored), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    async Task Serve(Socket client)
    {
        using (client)
        await using (var stream = new NetworkStream(client, ownsSocket: false))
        {
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(connection.Token, deadline.Token);
                var text = await new ActivityLines(stream).Read(16384, bounded.Token);
                var request = text is null ? null : JsonSerializer.Deserialize(text, ActivityJson.Default.ActivityRequest);
                if (request is null || request.State != endpoint.State || request.Repository != endpoint.Repository || request.Lifetime != endpoint.Lifetime ||
                    request.Limit is < 1 or > 100 || request.Cursor?.Length > 8192 || request.Discussion?.Length > 512 ||
                    (request.Discussion is null && (request.Cursor is not null || request.Follow)))
                    throw new IOException("Activity request identity or selection is invalid.");
                if (request.Discussion is null)
                    await Write(stream, new("sessions", endpoint.Lifetime, Inventory: await inspector.List(bounded.Token)), connection.Token);
                else if (!request.Follow)
                    await Write(stream, new("snapshot", endpoint.Lifetime, Snapshot: await inspector.Read(request.Discussion,
                        request.Limit, request.Cursor, bounded.Token)), connection.Token);
                else
                {
                    // A viewer disappearing cancels inspection immediately, without sending an agent lifecycle command.
                    var disconnected = PeerClosed(stream, connection);
                    try
                    {
                        await foreach (var frame in inspector.Follow(request.Discussion, request.Limit, request.Cursor, connection.Token))
                            await Write(stream, frame, connection.Token);
                    }
                    finally { await connection.CancelAsync(); await disconnected; }
                }
            }
            catch (Exception)
            {
                if (!connection.IsCancellationRequested)
                {
                    try { await Write(stream, new("error", endpoint.Lifetime, Notice: "Activity is unavailable or the selection is invalid; read a new snapshot."), stop.Token); }
                    catch (Exception failure) when (failure is IOException or SocketException or OperationCanceledException) { }
                }
            }
        }
    }

    static async Task PeerClosed(NetworkStream stream, CancellationTokenSource connection)
    {
        try { _ = await stream.ReadAsync(new byte[1], connection.Token); }
        catch (Exception error) when (error is IOException or OperationCanceledException or SocketException) { }
        finally { await connection.CancelAsync(); }
    }

    async Task Write(NetworkStream stream, ActivityFrame frame, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, ActivityJson.Default.ActivityFrame);
        if (bytes.Length >= 1024 * 1024) throw new IOException("Activity response exceeds its limit.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        await stream.WriteAsync(bytes, bounded.Token);
        await stream.WriteAsync("\n"u8.ToArray(), bounded.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        listener.Dispose(); // .NET removes the pathname it bound, inside this lifetime's private random directory.
        await accepting;
        await Task.WhenAll(clients.Values);
        if (File.Exists(pointer))
        {
            try
            {
                var current = JsonSerializer.Deserialize(ManagedCheckout.ReadPrivate(pointer), ActivityJson.Default.ActivityEndpoint);
                if (current == endpoint) File.Delete(pointer);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        }
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        stop.Dispose();
    }
}

static class ActivityClient
{
    public static async IAsyncEnumerable<ActivityFrame> Read(string state, string? discussion, int limit, string? cursor, bool follow,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) throw new IOException("Activity inspection requires a running Linux or macOS controller.");
        state = Path.GetFullPath(state);
        var pointer = ActivitySocket.Pointer(state);
        ActivitySocket.CheckDirectory(Path.GetDirectoryName(pointer)!);
        var endpoint = JsonSerializer.Deserialize(ManagedCheckout.ReadPrivate(pointer), ActivityJson.Default.ActivityEndpoint)
            ?? throw new IOException("No running controller activity endpoint.");
        if (endpoint.State != state || endpoint.Lifetime.Length is 0 or > 128 || endpoint.Repository.Length is 0 or > 256)
            throw new IOException("Controller activity endpoint identity is invalid.");
        ActivitySocket.CheckDirectory(Path.GetDirectoryName(endpoint.Socket)!);
        ManagedCheckout.CheckPath(endpoint.Socket);
        if (File.GetUnixFileMode(endpoint.Socket) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
            throw new IOException("Controller activity socket is not private.");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint.Socket), connecting.Token); }
        catch (SocketException) { throw new IOException("No running controller activity endpoint."); }
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var request = new ActivityRequest(state, endpoint.Repository, endpoint.Lifetime, discussion, limit, cursor, follow);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, ActivityJson.Default.ActivityRequest);
        if (bytes.Length > 16384) throw new IOException("Activity selection exceeds its limit.");
        await stream.WriteAsync(bytes, connecting.Token);
        await stream.WriteAsync("\n"u8.ToArray(), connecting.Token);
        var reader = new ActivityLines(stream);
        while (true)
        {
            using var receive = new CancellationTokenSource(TimeSpan.FromSeconds(follow ? 30 : 10));
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, receive.Token);
            var line = await reader.Read(1024 * 1024, bounded.Token);
            if (line is null) throw new IOException("Controller activity disconnected; read a new snapshot.");
            var frame = JsonSerializer.Deserialize(line, ActivityJson.Default.ActivityFrame) ?? throw new IOException("Invalid activity response.");
            if (frame.Lifetime != endpoint.Lifetime || frame.Kind == "error") throw new IOException("Activity is unavailable or the selection is invalid.");
            yield return frame;
            if (!follow || frame.Kind == "gap") yield break;
        }
    }
}

/// <summary>Bounded UTF-8 lines with preserved buffered bytes for subsequent live frames.</summary>
sealed class ActivityLines(Stream stream)
{
    readonly byte[] buffer = new byte[8192];
    int offset, length;
    public async Task<string?> Read(int maximumBytes, CancellationToken ct)
    {
        using var content = new MemoryStream();
        while (true)
        {
            if (offset == length)
            {
                length = await stream.ReadAsync(buffer, ct); offset = 0;
                if (length == 0)
                {
                    if (content.Length != 0) throw new IOException("Incomplete activity frame.");
                    return null;
                }
            }
            var newline = buffer.AsSpan(offset, length - offset).IndexOf((byte)'\n');
            var count = newline < 0 ? length - offset : newline;
            if (content.Length + count > maximumBytes) throw new IOException("Activity frame exceeds its limit.");
            content.Write(buffer, offset, count); offset += count;
            if (newline >= 0)
            {
                offset++;
                return new UTF8Encoding(false, true).GetString(content.GetBuffer(), 0, checked((int)content.Length));
            }
        }
    }
}
