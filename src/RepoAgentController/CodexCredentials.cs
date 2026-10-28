using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>
/// Operator-supplied Codex account credentials: a Codex auth.json-format file, separate from GitHub App credentials.
/// Supplied content is installed into the private agent home only while no owned app-server runs. The pinned
/// agent neither watches nor reloads a replaced auth.json, so a replacement restarts the owned app-server at idle.
/// </summary>
sealed class CodexCredentials
{
    const int MaximumBytes = 64 * 1024;
    static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);
    // Fingerprint of the supplied content last installed. Agent refresh rewrites auth.json, so an unchanged
    // supplied file must not overwrite the agent's current (possibly rotated) credentials on restart.
    const string Marker = "repoagent-supplied-auth.sha256";
    readonly string source, home;
    byte[] content;

    CodexCredentials(string source, string home, byte[] content) { this.source = source; this.home = home; this.content = content; }

    /// <summary>Validates the supplied file and the private agent home before any effects.</summary>
    public static CodexCredentials Open(string source) => Open(source, Codex.PrivateHome("--codex-auth"));

    /// <summary>Validates the supplied file for the already validated private agent home <paramref name="home"/>.</summary>
    internal static CodexCredentials Open(string source, string home) => new(Path.GetFullPath(source), home, Read(source));

    /// <summary>Installs the supplied credentials unless this exact supply is already installed. Call while no agent runs.</summary>
    public void Install()
    {
        var installed = File.Exists(Path.Combine(home, "auth.json")) && File.Exists(Path.Combine(home, Marker)) &&
            ManagedCheckout.ReadPrivate(Path.Combine(home, Marker)) == Fingerprint(content);
        if (!installed) Store();
        Log.Write("codex-auth-supplied", $"source={source} installed={(installed ? "retained" : "new")}");
    }

    void Store()
    {
        ManagedCheckout.SavePrivate(Path.Combine(home, "auth.json"), System.Text.Encoding.UTF8.GetString(content));
        ManagedCheckout.SavePrivate(Path.Combine(home, Marker), Fingerprint(content));
    }

    /// <summary>
    /// Until disposed, applies each valid replacement of the supplied file through <paramref name="applyAtIdle"/>, which
    /// returns false while agent work still runs and otherwise calls its argument with no agent process running.
    /// A failed application calls <paramref name="fail"/>: the owned agent state is then unknown.
    /// </summary>
    public IAsyncDisposable Watch(Func<Action, CancellationToken, Task<bool>> applyAtIdle, TimeProvider clock, Action fail, CancellationToken ct)
    {
        var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        return new Watcher(stop, Run(applyAtIdle, clock, fail, stop.Token));
    }

    async Task Run(Func<Action, CancellationToken, Task<bool>> applyAtIdle, TimeProvider clock, Action fail, CancellationToken ct)
    {
        var applied = Fingerprint(content);
        string? invalid = null, waiting = null;
        try
        {
            while (true)
            {
                await Task.Delay(CheckInterval, clock, ct);
                byte[] next;
                try { next = Read(source); }
                catch (ArgumentException error)
                {
                    if (invalid != error.Message) Log.Write("codex-auth-invalid", error.Message + "; the current credentials remain in use");
                    invalid = error.Message;
                    continue;
                }
                invalid = null;
                var fingerprint = Fingerprint(next);
                if (fingerprint == applied) continue;
                if (waiting != fingerprint) Log.Write("codex-auth-replaced", $"source={source}: restarting the owned Codex app-server when no turn is running");
                waiting = fingerprint;
                if (!await applyAtIdle(() => { content = next; Store(); }, ct)) continue;
                applied = fingerprint;
                waiting = null;
                Log.Write("codex-auth-applied", $"source={source}: the Codex app-server restarted with the replaced credentials; sessions resume on their next input");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            Log.Write("codex-auth-error", $"Applying replaced Codex credentials failed: {error.Message}; stopping the controller");
            fail();
        }
    }

    sealed class Watcher(CancellationTokenSource stop, Task running) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            await running;
            stop.Dispose();
        }
    }

    static string Fingerprint(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));

    // Content never appears in errors: the file is a bearer credential.
    static byte[] Read(string path)
    {
        byte[] value;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new ArgumentException($"--codex-auth file {path} does not exist");
            if (info.Length is 0 or > MaximumBytes) throw new ArgumentException($"--codex-auth file {path} must contain 1..{MaximumBytes} bytes");
            value = File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new ArgumentException($"--codex-auth file {path} is unreadable: {error.GetType().Name}"); }
        JsonObject? document;
        try { document = JsonNode.Parse(value) as JsonObject; }
        catch (JsonException) { document = null; }
        static bool Present(JsonNode? node) => node.OptionalText() is { Length: > 0 };
        if (document is null || !(Present(document["OPENAI_API_KEY"]) ||
            document["tokens"] is JsonObject tokens && (Present(tokens["refresh_token"]) || Present(tokens["access_token"]))))
            throw new ArgumentException($"--codex-auth file {path} must be a Codex auth.json holding an API key or ChatGPT sign-in tokens");
        var signIn = document["tokens"] as JsonObject;
        foreach (var secret in new[] { document["OPENAI_API_KEY"], signIn?["access_token"], signIn?["refresh_token"], signIn?["id_token"] })
            if (Present(secret)) Log.Protect(secret!.GetValue<string>());
        return value;
    }
}
