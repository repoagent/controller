using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>A validated repository-bound App installation credential, held only in controller memory.</summary>
internal sealed record InstallationCredential(string Token, DateTimeOffset ExpiresAt, string? Repository = null, string? Profile = null);

/// <summary>
/// Hands the running controller's current in-memory App credential to its own gh and Git child commands.
/// The credential directory holds only a nonsecret pointer to an owner-only Unix socket in a private
/// temporary directory. The socket lives exactly as long as the controller lifetime; no token is written to disk.
/// </summary>
internal sealed class CredentialHandoff : IDisposable
{
    const string PointerName = "credential-handoff";
    const int MaximumBytes = 64 * 1024;
    const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    readonly string pointer, directory, endpoint;
    readonly Socket listener;
    readonly Func<InstallationCredential?> current;
    readonly Action<string> reject;
    readonly CancellationTokenSource stopping = new();

    public CredentialHandoff(string credentialDirectory, Func<InstallationCredential?> current, Action<string> reject)
    {
        this.current = current;
        this.reject = reject;
        pointer = Path.Combine(credentialDirectory, PointerName);
        // Local, owner-only (0700) and outside persistent state: SMB-backed state cannot host sockets.
        directory = Directory.CreateTempSubdirectory("repoagent-credential-").FullName;
        endpoint = Path.Combine(directory, "credential.sock");
        listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(endpoint));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(endpoint, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(16);
            var temporary = pointer + ".tmp";
            File.Delete(temporary);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options)) file.Write(Encoding.UTF8.GetBytes(endpoint));
            File.Move(temporary, pointer, true);
        }
        catch
        {
            listener.Dispose();
            Directory.Delete(directory, true);
            throw;
        }
        _ = Accept();
    }

    async Task Accept()
    {
        while (!stopping.IsCancellationRequested)
        {
            Socket connection;
            try { connection = await listener.AcceptAsync(stopping.Token); }
            catch (Exception) when (stopping.IsCancellationRequested) { return; }
            catch (SocketException) { await Task.Delay(TimeSpan.FromMilliseconds(100)); continue; }
            _ = Serve(connection);
        }
    }

    async Task Serve(Socket connection)
    {
        using (connection)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await using var stream = new NetworkStream(connection);
                var request = await ReadLine(stream, deadline.Token);
                var response = new JsonObject();
                switch (request.Text("operation"))
                {
                    case "get" when current() is { } credential:
                        response["token"] = credential.Token;
                        response["expiresAt"] = credential.ExpiresAt.ToString("O", CultureInfo.InvariantCulture);
                        if (credential.Repository is not null) response["repository"] = credential.Repository;
                        if (credential.Profile is not null) response["profile"] = credential.Profile;
                        break;
                    case "reject":
                        reject(request.Text("token"));
                        break;
                }
                await stream.WriteAsync(Encoding.UTF8.GetBytes(response.ToJsonString() + "\n"), deadline.Token);
            }
            catch (Exception) { } // Requests and responses can carry bearer tokens: never diagnose their content.
        }
    }

    /// <summary>The current credential of the running controller that owns <paramref name="credentialDirectory"/>.</summary>
    public static async Task<InstallationCredential> Request(string credentialDirectory, CancellationToken ct)
    {
        var response = await Exchange(credentialDirectory, Json.Object(("operation", "get")), ct);
        var token = response?["token"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
        if (token.Length == 0 || token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            !DateTimeOffset.TryParse(response!.Text("expiresAt"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry) ||
            expiry <= DateTimeOffset.UtcNow.AddSeconds(30))
            throw Unavailable();
        Log.Protect(token);
        return new(token, expiry, response["repository"]?.GetValue<string>(), response["profile"]?.GetValue<string>());
    }

    /// <summary>Asks the running controller to drop <paramref name="token"/> if it is still its current credential.</summary>
    public static async Task Reject(string credentialDirectory, string token, CancellationToken ct) =>
        await Exchange(credentialDirectory, Json.Object(("operation", "reject"), ("token", token)), ct);

    static async Task<JsonNode?> Exchange(string credentialDirectory, JsonNode request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var path = Path.Combine(credentialDirectory, PointerName);
            if (new FileInfo(path).Length is var length && length > 4096) throw Unavailable();
            var endpoint = await File.ReadAllTextAsync(path, deadline.Token);
            // Only a private directory the caller can traverse: 0700 and reachable means owned by this user.
            if (!Path.IsPathFullyQualified(endpoint) || endpoint.Any(char.IsControl) ||
                (!OperatingSystem.IsWindows() && File.GetUnixFileMode(Path.GetDirectoryName(endpoint)!) != PrivateDirectory))
                throw Unavailable();
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), deadline.Token);
            await using var stream = new NetworkStream(socket);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request.ToJsonString() + "\n"), deadline.Token);
            return await ReadLine(stream, deadline.Token);
        }
        catch (Exception error) when (error is IOException or SocketException or UnauthorizedAccessException or OperationCanceledException or
            ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            throw Unavailable();
        }
    }

    static async Task<JsonNode> ReadLine(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaximumBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0) break;
            length += read;
            if (Array.IndexOf(buffer, (byte)'\n', 0, length) is var end and >= 0)
                return JsonNode.Parse(buffer.AsSpan(0, end)) ?? throw new IOException("Empty credential message");
        }
        throw new IOException("Incomplete or oversized credential message");
    }

    static InvalidOperationException Unavailable() =>
        new("GitHub App credential is unavailable; only a running controller with a current credential can authorize gh or Git");

    public void Dispose()
    {
        stopping.Cancel();
        listener.Dispose();
        try { if (File.ReadAllText(pointer) == endpoint) File.Delete(pointer); }
        catch (IOException) { }
        try { Directory.Delete(directory, true); }
        catch (IOException) { }
    }
}
