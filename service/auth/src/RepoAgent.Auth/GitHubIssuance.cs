using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using static RepoAgent.Auth.IssuancePolicy;

namespace RepoAgent.Auth;

// HttpClient must have redirects and retries disabled. Only fixed api.github.com paths are used.
public sealed class GitHubIssuance(HttpClient client, TimeProvider clock)
{
    public Task<IssuedToken> MintAsync(GrantSnapshot grant, string profile, AppAuthorization app, CancellationToken ct) =>
        MintAsync(grant.RepositoryId, grant.Repository, grant.InstallationId, profile, app, ct);

    internal async Task<IssuedToken> MintAsync(string repositoryId, string repository, long installationId, string profile, AppAuthorization app, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", GitHubAppAuthorization.Jwt(app));
        request.Content = JsonContent.Create(new { repository_ids = new[] { Id(repositoryId) }, permissions = Permissions(profile) });
        using var response = await SendAsync(request, HttpStatusCode.Created, ct);
        var root = response.RootElement;
        var token = root.GetProperty("token").GetString();
        if (token is null || !IsBearer(token)) throw new IssuanceDenied();
        // Once token material is known, return it even if scope/expiry fails: the caller durably
        // protects and attempts to revoke it, rather than losing a known excessive capability.
        var valid = false;
        var expiry = DateTimeOffset.MaxValue;
        try
        {
            expiry = root.GetProperty("expires_at").GetDateTimeOffset();
            ValidateRepository(root.GetProperty("repositories"), repositoryId, repository);
            var expected = Permissions(profile);
            var actual = root.GetProperty("permissions");
            if (actual.ValueKind != JsonValueKind.Object) throw new IssuanceDenied();
            var actualMap = actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
            valid = root.GetProperty("repository_selection").GetString() == "selected"
                && expected.All(p => actualMap.TryGetValue(p.Key, out var value) && value == p.Value)
                && actualMap.All(p => expected.TryGetValue(p.Key, out var value) && value == p.Value || p.Key == "metadata" && p.Value == "read")
                && expiry > clock.GetUtcNow().AddMinutes(5) && expiry <= clock.GetUtcNow().AddMinutes(65);
        }
        catch (Exception ex) when (ex is IssuanceDenied or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException) { }
        return new(token, expiry, valid);
    }

    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Delete, "installation/token", token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != HttpStatusCode.NoContent) throw new IssuanceDenied();
    }

    // Reads one GitHub resource and reports what GitHub answered. The fact reads JSON only and decides
    // nothing: a missing member or another kind than GitHub documents makes the answer malformed.
    internal async Task<GitHubRead<T>> ReadAsync<T>(string path, string bearer, Func<JsonElement, T> fact, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, path, bearer);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) return new GitHubRead<T>.NotFound();
            if (response.StatusCode != HttpStatusCode.OK) return new GitHubRead<T>.Refused();
            if (await BodyAsync(response, timeout.Token) is not { } body) return new GitHubRead<T>.Malformed();
            JsonDocument document;
            try { document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 }); }
            catch (JsonException) { return new GitHubRead<T>.Malformed(); }
            using (document)
            {
                if (HasDuplicateMember(document.RootElement)) return new GitHubRead<T>.Malformed();
                try { return GitHubRead.Found(fact(document.RootElement)); }
                catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
                {
                    return new GitHubRead<T>.Malformed();
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new GitHubUnavailable("GitHub did not answer the read.", ex);
        }
    }

    // A JSON string GitHub documents as never null.
    internal static string JsonText(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString()! : throw new InvalidOperationException("Expected a JSON string.");

    internal static string RepositoryPath(string repository) => string.Join('/', repository.Split('/').Select(Uri.EscapeDataString));

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, HttpStatusCode expected, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != expected || await BodyAsync(response, timeout.Token) is not { } body) throw new IssuanceDenied();
        var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 32 });
        if (!HasDuplicateMember(document.RootElement)) return document;
        document.Dispose();
        throw new IssuanceDenied();
    }
    // At most 64 KiB; null when GitHub sends more.
    private static async Task<byte[]?> BodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is > 65536) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (bytes.Length + count > 65536) return null;
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }
    private static HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, new Uri("https://api.github.com/" + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("repoagent-credential-broker/1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        return request;
    }
    private static bool IsRepositoryName(string? repository) => repository is not null
        && repository.Split('/') is [var owner, var name] && Segment(owner) && Segment(name);
    private static bool Segment(string value) => value.Length is > 0 and <= 100 && value is not ("." or "..")
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    // Shape validation is separate from comparison: malformed evidence never proves withdrawal.
    private static void ValidateRepository(JsonElement repositories, string repositoryId, string repository)
    {
        if (repositories.ValueKind != JsonValueKind.Array || repositories.GetArrayLength() != 1) throw new IssuanceDenied();
        var id = repositories[0].GetProperty("id").GetInt64();
        var name = repositories[0].GetProperty("full_name").GetString();
        if (id <= 0 || !IsRepositoryName(name)) throw new IssuanceDenied();
        if (id != Id(repositoryId) || !string.Equals(name, repository, StringComparison.OrdinalIgnoreCase)) throw new IssuanceDenied();
    }

    private static bool HasDuplicateMember(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateMember(property.Value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray())
                if (HasDuplicateMember(child)) return true;
        return false;
    }
}
