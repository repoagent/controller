using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RepoAgentController;

sealed record ActivityEndpoint(string State, string Repository, string Lifetime, string Socket, int Protocol = 0);
sealed record ActivityRequest(string State, string Repository, string Lifetime, string? Discussion, int Limit, string? Cursor, bool Follow);
sealed record RemoteRequest(string State, string Repository, string Lifetime, string? Client = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ActivityEndpoint))]
[JsonSerializable(typeof(ActivityRequest))]
[JsonSerializable(typeof(ActivityFrame))]
[JsonSerializable(typeof(RemoteRequest))]
[JsonSerializable(typeof(RemoteAccessResult))]
sealed partial class ActivityJson : JsonSerializerContext;

/// <summary>One lifetime-bound private Unix HTTP listener for inspection and guarded administration.</summary>
sealed class ActivitySocket : IAsyncDisposable
{
    internal const int Protocol = 1;
    internal const string ActivityPath = "/v1/activity";
    const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    const UnixFileMode PrivateDirectory = PrivateFile | UnixFileMode.UserExecute;
    readonly WebApplication app;
    readonly ActivityEndpoint endpoint;
    readonly string pointer, directory;
    readonly SessionActivityInspector inspector;
    readonly TimeProvider clock;
    readonly CancellationTokenSource stop;

    ActivitySocket(WebApplication app, ActivityEndpoint endpoint, string pointer, string directory,
        SessionActivityInspector inspector, TimeProvider clock, CancellationToken ct)
    {
        this.app = app; this.endpoint = endpoint; this.pointer = pointer; this.directory = directory;
        this.inspector = inspector; this.clock = clock;
        stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        app.MapPost(ActivityPath, Activity);
        foreach (var operation in new[] { RemoteOperation.Enable, RemoteOperation.Pair, RemoteOperation.Status,
            RemoteOperation.Clients, RemoteOperation.Revoke, RemoteOperation.Disable })
            app.MapPost("/v1/remote/" + operation.ToString().ToLowerInvariant(), context => Remote(context, operation));
        // This host has no TCP listener. Unrecognized routes grant no authority.
        app.MapFallback(context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; });
    }

    public static string Pointer(string state) => state + ".activity/endpoint.json";

    public static async Task<ActivitySocket?> TryStart(string state, string repository, SessionActivityInspector inspector,
        string lifetime, TimeProvider clock, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) return null;
        try { return await Start(state, repository, lifetime, inspector, clock, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { Log.Write("activity-unavailable", "Private local commands did not become available within their startup deadline."); return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SocketException or PlatformNotSupportedException or JsonException or ArgumentException)
        { Log.Write("activity-unavailable", "Private local commands are unavailable; use matching clients and restart the selected controller on a private Unix filesystem."); return null; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The startup catch awaits server.DisposeAsync on failure; success transfers the server to the caller. CA2000 does not recognize this async cleanup.")]
    public static async Task<ActivitySocket> Start(string state, string repository, string lifetime, SessionActivityInspector inspector,
        TimeProvider clock, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Local commands require Unix sockets.");
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Local commands require Linux or macOS Unix peer credentials.");
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
                ?? throw new IOException("Invalid private controller endpoint.");
            if (previous.State != state || previous.Repository != repository) throw new IOException("Controller endpoint identity changed.");
            // A known legacy descriptor can be retired after its socket is dead, never while its controller is running.
            // Clients reject it outright; there is no legacy protocol fallback.
            if (previous.Protocol is not (0 or Protocol)) throw new LocalProtocolException();
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1), clock);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            var active = false;
            try { await probe.ConnectAsync(new UnixDomainSocketEndPoint(previous.Socket), bounded.Token); active = true; }
            catch (SocketException) { }
            if (active) throw new IOException("A private controller endpoint is already running.");
            File.Delete(pointer); // Stale descriptor only. Never unlink a previous lifetime's socket.
        }
        var directory = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : "/tmp", "repoagent-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, PrivateDirectory);
        CheckDirectory(directory);
        var path = Path.Combine(directory, "operator.sock");
        var temporary = pointer + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var createdTemporary = false;
        try
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            { Args = [], ContentRootPath = AppContext.BaseDirectory, EnvironmentName = Environments.Production });
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IHostLifetime, ControlledLifetime>();
            builder.Services.Configure<SocketTransportOptions>(options => options.Backlog = 8);
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.AddServerHeader = false;
                kestrel.Limits.MaxConcurrentConnections = 8;
                kestrel.Limits.MaxRequestBodySize = 16384;
                kestrel.Limits.MaxRequestHeadersTotalSize = 8192;
                kestrel.Limits.MaxRequestHeaderCount = 16;
                kestrel.Limits.MaxRequestLineSize = 4096;
                kestrel.Limits.MaxRequestBufferSize = 16384;
                kestrel.Limits.MaxResponseBufferSize = 65536;
                kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
                kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
                kestrel.Limits.MinRequestBodyDataRate = null; // Our body deadline owns this limit.
                kestrel.ListenUnixSocket(path, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1;
                    listen.Use((connection, next) =>
                    {
                        var socket = connection.Features.Get<IConnectionSocketFeature>()?.Socket;
                        connection.Features.Set(new UnixCaller(UnixPeer.Permission(socket)));
                        return next(connection);
                    });
                });
            });
            var app = builder.Build();
            var endpoint = new ActivityEndpoint(state, repository, lifetime, path, Protocol);
            var server = new ActivitySocket(app, endpoint, pointer, directory, inspector, clock, ct);
            try
            {
                using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
                using var starting = CancellationTokenSource.CreateLinkedTokenSource(ct, startup.Token);
                await app.StartAsync(starting.Token);
                File.SetUnixFileMode(path, PrivateFile);
                if (File.GetUnixFileMode(path) != PrivateFile) throw new IOException("Controller socket is not private.");
                await using (var output = new FileStream(temporary, new FileStreamOptions
                { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read, UnixCreateMode = PrivateFile }))
                {
                    createdTemporary = true;
                    await JsonSerializer.SerializeAsync(output, endpoint, ActivityJson.Default.ActivityEndpoint, starting.Token);
                }
                _ = ManagedCheckout.ReadPrivate(temporary);
                File.Move(temporary, pointer);
                return server;
            }
            catch { await server.DisposeAsync(); throw; }
        }
        catch
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            if (createdTemporary && File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    internal static void CheckDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ManagedCheckout.CheckPath(path);
        if (!Directory.Exists(path) || File.GetUnixFileMode(path) != PrivateDirectory)
            throw new IOException("Controller endpoint requires a private 0700 directory.");
    }

    bool Bound(string state, string repository, string lifetime) =>
        state == endpoint.State && repository == endpoint.Repository && lifetime == endpoint.Lifetime;

    async Task Activity(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        // A feature is installed only by the Unix connection middleware, never from headers or request claims.
        if (context.Features.Get<UnixCaller>()?.Permission is not (LocalCallerPermission.Inspection or LocalCallerPermission.Administration))
        { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stop.Token);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(connection.Token, deadline.Token);
            var request = await JsonSerializer.DeserializeAsync(context.Request.Body, ActivityJson.Default.ActivityRequest, bounded.Token);
            if (request is null || !Bound(request.State, request.Repository, request.Lifetime) || request.Limit is < 1 or > 100 ||
                request.Cursor?.Length > 8192 || request.Discussion?.Length > 512 ||
                (request.Discussion is null && (request.Cursor is not null || request.Follow)))
                throw new IOException("Invalid activity selection or controller lifetime.");
            context.Response.ContentType = "application/x-ndjson";
            if (request.Discussion is null)
                await Write(context, new("sessions", endpoint.Lifetime, Inventory: await inspector.List(bounded.Token)), connection.Token);
            else if (!request.Follow)
                await Write(context, new("snapshot", endpoint.Lifetime, Snapshot: await inspector.Read(request.Discussion,
                    request.Limit, request.Cursor, bounded.Token)), connection.Token);
            else
            {
                // The request body deadline ends at setup. A live follower has only per-write deadlines and disconnect cancellation.
                await foreach (var frame in inspector.Follow(request.Discussion, request.Limit, request.Cursor, connection.Token))
                    await Write(context, frame, connection.Token);
            }
        }
        catch (Exception)
        {
            if (connection.IsCancellationRequested) return;
            try { await Write(context, new("error", endpoint.Lifetime, Notice: "Activity is unavailable or the selection is invalid; read a new snapshot."), connection.Token); }
            catch (Exception failure) when (failure is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            { context.Abort(); }
        }
    }

    async Task Remote(HttpContext context, RemoteOperation operation)
    {
        context.Response.Headers.CacheControl = "no-store";
        var result = RemoteAccess.Execute(operation, context.Features.Get<UnixCaller>()?.Permission ?? LocalCallerPermission.Unknown);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stop.Token, deadline.Token);
        // Check authority before reading a selection or any protected application data, including status and clients.
        if (result.Outcome != RemoteAccessOutcome.Forbidden)
        {
            try
            {
                var request = await JsonSerializer.DeserializeAsync(context.Request.Body, ActivityJson.Default.RemoteRequest, bounded.Token);
                if (request is null || !Bound(request.State, request.Repository, request.Lifetime) || request.Client?.Length > 512 ||
                    (operation == RemoteOperation.Revoke && string.IsNullOrWhiteSpace(request.Client)))
                { context.Response.StatusCode = StatusCodes.Status409Conflict; return; }
            }
            catch (Exception error) when (error is JsonException or IOException or OperationCanceledException or Microsoft.AspNetCore.Http.BadHttpRequestException)
            { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
        }
        context.Response.StatusCode = result.Outcome == RemoteAccessOutcome.Forbidden ? StatusCodes.Status403Forbidden : StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(result, ActivityJson.Default.RemoteAccessResult, cancellationToken: bounded.Token);
    }

    async Task Write(HttpContext context, ActivityFrame frame, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, ActivityJson.Default.ActivityFrame);
        if (bytes.Length >= 1024 * 1024) throw new IOException("Activity response exceeds its limit.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        await context.Response.Body.WriteAsync(bytes, bounded.Token);
        await context.Response.Body.WriteAsync("\n"u8.ToArray(), bounded.Token);
        await context.Response.Body.FlushAsync(bounded.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await app.StopAsync(drain.Token); }
        finally { await app.DisposeAsync(); }
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

    sealed class ControlledLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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
