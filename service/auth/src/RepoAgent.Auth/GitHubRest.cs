using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RepoAgent.Auth;

// The broker's client of api.github.com. Every request carries the same headers and a 3 s timeout, and every answer
// body is at most 64 KiB of JSON that names no member twice. The HttpClient must have redirects and retries disabled;
// only fixed api.github.com paths are used.
internal sealed class GitHubRest(HttpClient client)
{
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

    // At most 64 KiB; null when GitHub sends more.
    internal static async Task<byte[]?> BodyAsync(HttpResponseMessage response, CancellationToken ct)
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

    internal static HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, new Uri("https://api.github.com/" + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("repoagent-credential-broker/1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        return request;
    }

    internal static bool HasDuplicateMember(JsonElement element)
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
