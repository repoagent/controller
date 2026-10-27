using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace RepoAgentController;

internal sealed class DirectAppTokenSource : IGitHubTokenSource
{
    public bool RequiresNewLifetimeOnFailure => false;

    readonly HttpClient http;
    readonly string appId;
    readonly RSA key;
    readonly string repository;
    readonly TimeProvider clock;
    RuntimeProfile profile;
    bool clone => profile.ManagedClone;
    public string? SettingsUnreadable { get; private set; }
    long repositoryId;
    string canonicalRepository = "";
    public long? RepositoryId => clone ? repositoryId : null;
    public string? GitRepository => clone ? canonicalRepository : null;
    // Direct App credentials name the clone profile Git credential helpers accept; repository settings do not change it.
    public string? GitProfile => clone ? (profile with { Settings = false }).Name : null;
    long installationId;
    string appSlug = "";
    public string AppSlug => appSlug;

    public DirectAppTokenSource(HttpClient http, string appId, string privateKeyPath, string repository, TimeProvider clock, RuntimeProfile profile)
    {
        this.profile = profile;
        this.http = http;
        this.appId = appId;
        this.repository = repository;
        this.clock = clock;
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

        var installation = await Send(HttpMethod.Get, $"repos/{repository}/installation", CreateJwt(), null, ct);
        installationId = installation["id"]?.GetValue<long>() ?? 0;
        if (installationId <= 0 || installation["app_id"]?.GetValue<long>() != app["id"]?.GetValue<long>())
            throw new InvalidOperationException("Repository installation does not belong to the configured GitHub App");
        if (expectedInstallation is not null && expectedInstallation != installationId)
            throw new InvalidOperationException("Configured installation ID does not match the repository's App installation");
        if (installation["permissions"]?.Text("discussions") != "write")
            throw new InvalidOperationException("Install the GitHub App on this repository with Discussions read/write permission");
        if (clone && installation["permissions"]?.Text("contents") is not ("read" or "write"))
            throw new InvalidOperationException("Clone mode requires Contents read permission");
        if (profile.Webhooks && installation["permissions"]?.Text("repository_hooks") != "write")
            throw new InvalidOperationException("Webhook mode requires Webhooks: read and write; approve the updated App installation permissions");
        // Settings need Agent variables: read. Without it the controller still starts: the token requests no more than the
        // installation grants, and the settings keep their last values. The grant is checked once, so accepting it takes effect at the next start.
        if (profile.Settings && installation["permissions"]?.Text("agent_variables") is not ("read" or "write"))
        {
            profile = profile with { Settings = false };
            SettingsUnreadable = "the App installation does not grant Agent variables: read";
            Log.Write("repository-settings", $"state=unreadable reason=\"{SettingsUnreadable}; approve the updated App installation permissions, then restart the controller\"");
        }

        Log.Write("app-installation", $"app={slug} installation={installationId} repository={repository} actor={appSlug}[bot]");
    }

    public async Task<GitHubTokenLease> Acquire(CancellationToken ct)
    {
        var permissions = profile.Permissions();
        var request = Json.Object(("permissions", permissions));
        if (clone && repositoryId > 0) request["repository_ids"] = new JsonArray(repositoryId);
        else request["repositories"] = new JsonArray(repository.Split('/')[1]);
        var result = await Send(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", CreateJwt(),
            request, ct);
        if (clone)
        {
            var granted = result["permissions"] as JsonObject;
            if (result.Text("repository_selection") != "selected" || granted is null || granted.Count < permissions.Count || granted.Any(p =>
                p.Key == "metadata" ? p.Value?.GetValue<string>() != "read" :
                !permissions.ContainsKey(p.Key) || p.Value?.GetValue<string>() != permissions[p.Key]?.GetValue<string>()) ||
                permissions.Any(p => granted[p.Key]?.GetValue<string>() != p.Value?.GetValue<string>()) ||
                result["repositories"] is not JsonArray repos || repos.Count != 1)
                throw new InvalidOperationException("GitHub returned a different clone scope");
            var bound = repos[0]!;
            var id = bound["id"]?.GetValue<long>() ?? 0;
            var canonical = bound.Text("full_name");
            if (id <= 0 || (repositoryId > 0 && id != repositoryId) || !string.Equals(canonical, repository, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("GitHub returned a different repository");
            repositoryId = id;
            canonicalRepository = canonical;
        }
        var freshToken = result.Text("token");
        if (string.IsNullOrWhiteSpace(freshToken) ||
            !DateTimeOffset.TryParse(result.Text("expires_at"), out var expiry) || expiry <= clock.GetUtcNow().AddMinutes(5) ||
            (clone && (expiry > clock.GetUtcNow().AddMinutes(65) || freshToken.Any(char.IsWhiteSpace) || freshToken.Any(char.IsControl))))
            throw new InvalidOperationException("GitHub returned an unusable installation token or expiry");
        Log.Protect(freshToken);
        return new(freshToken, expiry);
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

    public void Dispose() => key.Dispose();
}
