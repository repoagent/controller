using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>GitHub repository files, committed without a checkout or a native attachment endpoint.</summary>
internal sealed class GitHubDiscussionAssets(HttpClient http, string repository,
    Func<CancellationToken, Task<GitHubToolToken>> credential, Func<string, CancellationToken, Task> reject) : IDiscussionAssetRepository
{
    const string MarkerPath = ".repoagent-assets";
    byte[]? marker;
    public string Repository => repository;

    public async Task<bool> HasDiscussion(int number, CancellationToken ct)
    {
        var parts = repository.Split('/');
        var result = await Send("POST", "graphql", Json.Object(
            ("query", "query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){databaseId defaultBranchRef{name} discussion(number:$number){number}}}"),
            ("variables", Json.Object(("owner", parts[0]), ("name", parts[1]), ("number", number)))), ct);
        if (result?["errors"] is not null) throw new IOException("Could not verify the Discussion in the approved repository");
        var found = result?["data"]?["repository"];
        if (found?["databaseId"]?.GetValue<long>() is not > 0 || found["discussion"]?["number"]?.GetValue<int>() != number) return false;
        if (found["defaultBranchRef"]?.Text("name") == DiscussionAssets.Branch)
            throw new InvalidOperationException("The asset branch cannot be the repository default branch");
        marker = Encoding.UTF8.GetBytes($"repoagent-discussion-assets-v1\nrepository={found["databaseId"]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture)}\n");
        return true;
    }

    public async Task<AssetBranch?> Read(string path, CancellationToken ct)
    {
        var commit = await Head(ct);
        if (commit is null) return null;
        var owner = await Content(MarkerPath, commit, ct);
        var owned = marker is not null && owner is not null && marker.AsSpan().SequenceEqual(owner);
        return new(commit, owned, owned ? await Content(path, commit, ct) : null);
    }

    async Task<string?> Head(CancellationToken ct)
    {
        var head = await Send("GET", Repo("git/ref/heads/" + DiscussionAssets.Branch), null, ct, missing: true);
        if (head is null) return null;
        var commit = head["object"]?.Text("sha") ?? "";
        if (head.Text("ref") != "refs/heads/" + DiscussionAssets.Branch || head["object"]?.Text("type") != "commit" ||
            commit.Length != 40 || !commit.All(char.IsAsciiHexDigitLower)) throw new IOException("Invalid asset branch reference");
        return commit;
    }

    public async Task<bool> Append(AssetBranch? expected, string path, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        if (marker is null || expected is { Owned: false }) throw new InvalidOperationException("Unverified asset repository");
        var blob = await Blob(content, ct);
        var entries = new JsonArray(Entry(path, blob));
        var tree = Json.Object(("tree", entries));
        if (expected is null) entries.Add((JsonNode)Entry(MarkerPath, await Blob(marker, ct)));
        else
        {
            var parent = await Send("GET", Repo("git/commits/" + expected.Commit), null, ct);
            tree["base_tree"] = parent!["tree"]!.Text("sha");
        }
        var createdTree = await Send("POST", Repo("git/trees"), tree, ct);
        var commit = await Send("POST", Repo("git/commits"), Json.Object(
            ("message", "Publish Discussion asset"), ("tree", createdTree!.Text("sha")),
            ("parents", expected is null ? new JsonArray() : new JsonArray(expected.Commit))), ct);
        var update = expected is null
            ? Json.Object(("ref", "refs/heads/" + DiscussionAssets.Branch), ("sha", commit!.Text("sha")))
            : Json.Object(("sha", commit!.Text("sha")), ("force", false));
        try
        {
            return await Send(expected is null ? "POST" : "PATCH", Repo(expected is null ? "git/refs" : "git/refs/heads/" + DiscussionAssets.Branch),
                update, ct, referenceUpdate: true) is not null;
        }
        catch (ReferenceUpdateException)
        {
            // Status alone cannot distinguish a competing writer from validation or branch-rule rejection.
            if (await Head(ct) != expected?.Commit) return false;
            throw;
        }
    }

    static JsonObject Entry(string path, string sha) => Json.Object(("path", path), ("mode", "100644"), ("type", "blob"), ("sha", sha));
    async Task<string> Blob(ReadOnlyMemory<byte> bytes, CancellationToken ct) =>
        (await Send("POST", Repo("git/blobs"), Json.Object(("content", Convert.ToBase64String(bytes.Span)), ("encoding", "base64")), ct))!.Text("sha");

    async Task<byte[]?> Content(string path, string commit, CancellationToken ct)
    {
        var file = await Send("GET", Repo("contents/" + path + "?ref=" + commit), null, ct, missing: true, mediaType: "application/vnd.github.object+json");
        if (file is null) return null;
        if (file.Text("type") != "file" || file["size"]?.GetValue<long>() is not (>= 0 and <= DiscussionAsset.MaximumBytes))
            throw new IOException("Invalid or oversized stored asset");
        if (file.Text("encoding") != "base64")
            file = await Send("GET", Repo("git/blobs/" + file.Text("sha")), null, ct);
        if (file!.Text("encoding") != "base64") throw new IOException("Invalid stored asset encoding");
        var bytes = Convert.FromBase64String(file!.Text("content"));
        if (bytes.Length > DiscussionAsset.MaximumBytes) throw new IOException("Oversized stored asset");
        return bytes;
    }

    string Repo(string path) => "repos/" + repository + "/" + path;

    sealed class ReferenceUpdateException(int status) : IOException($"Asset publication failed (HTTP {status}) updating the branch reference; check the App's repository permission and branch rules");

    async Task<JsonNode?> Send(string method, string path, JsonNode? body, CancellationToken ct, bool missing = false, bool referenceUpdate = false, string mediaType = "application/vnd.github+json")
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(http.Timeout == Timeout.InfiniteTimeSpan ? TimeSpan.FromSeconds(60) : http.Timeout);
        var work = deadline.Token;
        try
        {
            var token = await credential(work);
            if (!string.Equals(token.Repository, repository, StringComparison.OrdinalIgnoreCase) || token.Profile?.EndsWith("-images", StringComparison.Ordinal) != true)
                throw new InvalidOperationException("Image publication requires the approved repository's image-publishing profile");
            using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(new Uri("https://api.github.com/"), path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            request.Headers.UserAgent.ParseAdd("repoagent-assets/1");
            request.Headers.Accept.ParseAdd(mediaType);
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, work);
            if (response.StatusCode == HttpStatusCode.Unauthorized) await reject(token.Token, work);
            if (missing && response.StatusCode == HttpStatusCode.NotFound) return null;
            if (referenceUpdate && response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
                throw new ReferenceUpdateException((int)response.StatusCode);
            if (!response.IsSuccessStatusCode) throw new IOException($"Asset publication failed (HTTP {(int)response.StatusCode}) during {method}; check the App's repository permission and branch rules");
            if (response.Content.Headers.ContentLength > 16 * 1024 * 1024) throw new IOException("Oversized GitHub asset response");
            await response.Content.LoadIntoBufferAsync(16 * 1024 * 1024, work);
            await using var stream = await response.Content.ReadAsStreamAsync(work);
            return await JsonNode.ParseAsync(stream, cancellationToken: work) ?? throw new IOException("Empty GitHub asset response");
        }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        { throw new IOException("Asset publication request timed out", error); }
    }
}
