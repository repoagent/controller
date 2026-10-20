using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace DiscussionBridge;

/// <summary>Owns the App identity and a renewable, repository-scoped installation credential.</summary>
public sealed class GitHubAppCredentials : IDisposable
{
    readonly HttpClient http;
    readonly string appId;
    readonly RSA key;
    readonly string repository;
    readonly TimeProvider clock;
    readonly bool repositoryWebhooks;
    readonly SemaphoreSlim refreshLock = new(1);
    string? token;
    DateTimeOffset expiresAt;
    long installationId;
    string appSlug = "";

    public string AgentLogin { get; private set; } = "";
    public string TokenFile { get; }

    public GitHubAppCredentials(HttpClient http, string appId, string privateKeyPath, string repository,
        string credentialDirectory, TimeProvider? clock = null, bool repositoryWebhooks = false)
    {
        this.http = http;
        this.appId = appId;
        this.repository = repository;
        this.clock = clock ?? TimeProvider.System;
        this.repositoryWebhooks = repositoryWebhooks;
        SecureDirectory(credentialDirectory);
        TokenFile = Path.Combine(credentialDirectory, "installation-token.json");
        // Never trust a previous process's credential before authenticating this configuration.
        File.Delete(TokenFile);
        key = RSA.Create();
        try { key.ImportFromPem(File.ReadAllText(privateKeyPath)); }
        catch { key.Dispose(); throw; }
    }

    public async Task Initialize(long? expectedInstallation, CancellationToken ct)
    {
        var app = await Send(HttpMethod.Get, "app", CreateJwt(), null, ct);
        var slug = app.Text("slug");
        if (string.IsNullOrWhiteSpace(slug)) throw new InvalidOperationException("GitHub returned no App slug");
        appSlug = slug;
        AgentLogin = slug + "[bot]";
        var installation = await Send(HttpMethod.Get, $"repos/{repository}/installation", CreateJwt(), null, ct);
        installationId = installation["id"]?.GetValue<long>() ?? 0;
        if (installationId <= 0 || installation["app_id"]?.GetValue<long>() != app["id"]?.GetValue<long>())
            throw new InvalidOperationException("Repository installation does not belong to the configured GitHub App");
        if (expectedInstallation is not null && expectedInstallation != installationId)
            throw new InvalidOperationException("Configured installation ID does not match the repository's App installation");
        if (installation["permissions"]?.Text("discussions") != "write")
            throw new InvalidOperationException("Install the GitHub App on this repository with Discussions read/write permission");
        if (repositoryWebhooks && installation["permissions"]?.Text("repository_hooks") != "write")
            throw new InvalidOperationException("Webhook mode requires Webhooks: read and write; approve the updated App installation permissions");
        await GetToken(ct);
        Log.Write("app-installation", $"app={slug} installation={installationId} repository={repository} actor={AgentLogin}");
    }

    public bool IsAgentActor(JsonNode? actor)
    {
        if (appSlug.Length == 0 || actor is null) return false;
        var login = actor.Text("login");
        return string.Equals(login, AgentLogin, StringComparison.OrdinalIgnoreCase) ||
            (actor.Text("__typename") == "Bot" && string.Equals(login, appSlug, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string> GetToken(CancellationToken ct)
    {
        await refreshLock.WaitAsync(ct);
        try
        {
            if (installationId == 0) throw new InvalidOperationException("GitHub App authentication has not been initialized");
            if (token is not null && expiresAt > clock.GetUtcNow().AddMinutes(5)) return token;
            try
            {
                var permissions = Json.Object(("discussions", "write"));
                if (repositoryWebhooks) permissions["repository_hooks"] = "write";
                var result = await Send(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", CreateJwt(),
                    Json.Object(("repositories", new JsonArray(repository.Split('/')[1])),
                        ("permissions", permissions)), ct);
                var freshToken = result.Text("token");
                if (string.IsNullOrWhiteSpace(freshToken) ||
                    !DateTimeOffset.TryParse(result.Text("expires_at"), out var expiry) || expiry <= clock.GetUtcNow().AddMinutes(5))
                    throw new InvalidOperationException("GitHub returned an unusable installation token or expiry");
                Log.Protect(freshToken);
                var temporary = TokenFile + ".tmp";
                var options = new FileStreamOptions
                {
                    Mode = FileMode.Create, Access = FileAccess.Write
                };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var file = new FileStream(temporary, options))
                {
                    var cache = Json.Object(("token", freshToken), ("expiresAt", expiry.ToString("O")));
                    file.Write(Encoding.UTF8.GetBytes(cache.ToJsonString()));
                    file.Flush(true);
                }
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temporary, TokenFile, true);
                token = freshToken;
                expiresAt = expiry;
                Log.Write("app-token-refreshed", $"installation={installationId} expires={expiry:O}");
                return token;
            }
            catch
            {
                token = null;
                File.Delete(TokenFile);
                File.Delete(TokenFile + ".tmp");
                throw;
            }
        }
        finally { refreshLock.Release(); }
    }

    public async Task RefreshUntilCancelled(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), clock, ct);
                await GetToken(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception error) { Log.Write("app-refresh-error", error.Message); }
        }
    }

    async Task<JsonNode> Send(HttpMethod method, string path, string bearer, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri("https://api.github.com/"), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("repoagent-bridge/0.1");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub App authentication failed (HTTP {(int)response.StatusCode}) for {method} {path}; check App key, installation and permissions");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonNode.ParseAsync(stream, cancellationToken: ct) ?? throw new IOException("Empty GitHub App response");
    }

    string CreateJwt()
    {
        var now = clock.GetUtcNow();
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var payload = Encode(Encoding.UTF8.GetBytes(Json.Object(("iat", now.AddSeconds(-60).ToUnixTimeSeconds()),
            ("exp", now.AddMinutes(9).ToUnixTimeSeconds()), ("iss", appId)).ToJsonString()));
        var signed = header + "." + payload;
        var jwt = signed + "." + Encode(key.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Log.Protect(jwt);
        return jwt;
    }

    internal static void SecureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose() { key.Dispose(); refreshLock.Dispose(); }
}
