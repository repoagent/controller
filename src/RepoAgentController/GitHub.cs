using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RepoAgentController;

sealed class GitHub(string repository, GitHubAppCredentials credentials, string executable)
{
    public string Repository { get; } = repository;
    public string AgentLogin => credentials.AgentLogin;
    public MentionUser? MentionUser { get; private set; }
    public bool IsAgentActor(JsonNode? actor) => credentials.IsAgentActor(actor);
    const string MessageFields = "id url body bodyHTML createdAt updatedAt author { __typename login } reactionGroups { content viewerHasReacted }";
    const string DiscussionFields = MessageFields + " number title closed locked answer { id } category { id name }";
    const string PageInfo = "pageInfo { hasNextPage endCursor }";

    public async Task<JsonNode> Query(string query, JsonObject variables, CancellationToken ct)
    {
        var response = JsonNode.Parse(await Api("POST", "graphql", Json.Object(("query", query), ("variables", variables)), ct)) ?? throw new IOException("Empty GraphQL response");
        if (response["errors"] is { } errors) throw new IOException($"GraphQL errors: {errors}");
        return response["data"] ?? throw new IOException("No GraphQL data");
    }

    public async Task<JsonNode?> Rest(string method, string resource, JsonNode? input, CancellationToken ct)
    {
        var text = await Api(method, $"repos/{Repository}/{resource}", input, ct);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    internal sealed class CheckoutIdentityException(string message) : IOException(message);

    public async Task<RepositoryCheckout> CheckoutRepository(CancellationToken ct)
    {
        var value = JsonNode.Parse(await Api("GET", $"repos/{Repository}", null, ct)) ?? throw new IOException("Missing repository metadata");
        var id = value["id"]?.GetValue<long>() ?? 0;
        var name = value.Text("full_name");
        var remote = value.Text("clone_url");
        var branch = value.Text("default_branch");
        if (id <= 0 || !Regex.IsMatch(name, @"\A[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}\z") ||
            name.Split('/').Any(part => part is "." or "..") || !string.Equals(name, Repository, StringComparison.OrdinalIgnoreCase) ||
            remote != $"https://github.com/{name}.git" || string.IsNullOrWhiteSpace(branch) || branch.StartsWith('-') ||
            branch.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || branch.Contains("..", StringComparison.Ordinal) ||
            branch.IndexOfAny(['~', '^', ':', '?', '*', '[', '\\']) >= 0 || branch.Contains("@{", StringComparison.Ordinal) ||
            branch.Split('/').Any(part => part.Length == 0 || part.StartsWith('.') || part.EndsWith('.') || part.EndsWith(".lock", StringComparison.Ordinal)))
            throw new CheckoutIdentityException("Repository metadata does not match the approved checkout");
        return new(id, name, remote, branch);
    }

    public async Task<string> CheckoutCommit(string branch, CancellationToken ct)
    {
        var reference = await Rest("GET", "git/ref/heads/" + Uri.EscapeDataString(branch), null, ct);
        var sha = reference?["object"]?.Text("sha") ?? "";
        if (reference?.Text("ref") != "refs/heads/" + branch || reference?["object"]?.Text("type") != "commit" ||
            !Regex.IsMatch(sha, "\\A[0-9a-f]{40}\\z")) throw new IOException("Invalid authoritative default-branch commit");
        return sha;
    }

    async Task<string> Api(string method, string resource, JsonNode? input, CancellationToken ct, bool include = false)
    {
        var credential = await credentials.GetCredential(ct);
        var arguments = new List<string> { "api", resource, "--hostname", "github.com", "--method", method,
            "--header", "X-GitHub-Api-Version: 2026-03-10" };
        if (include) arguments.Add("--include");
        if (input is not null) arguments.AddRange(["--input", "-"]);
        var start = AppGitHubCommand.Create(executable, credential, credentials.CredentialDirectory, arguments);
        start.RedirectStandardInput = input is not null;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new IOException("Could not start gh");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(Json.Encode(input));
            process.StandardInput.Close();
        }
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var text = await output;
        // With --include, gh prints any HTTP response it received, including rejections, for classification.
        if (process.ExitCode != 0 && !(include && text.StartsWith("HTTP/", StringComparison.Ordinal))) throw new IOException($"gh failed: {await error}");
        return text;
    }

    public async Task VerifyIdentity(CancellationToken ct)
    {
        var viewer = (await Query("query { viewer { login } }", new JsonObject(), ct))["viewer"]!.Text("login");
        if (!string.Equals(viewer, AgentLogin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Authenticated GitHub viewer {viewer} differs from the configured App identity {AgentLogin}");
        Log.Write("identity", viewer);
    }

    public async Task ConfigureMentionUser(string? login, CancellationToken ct)
    {
        if (login is null) return;
        if (!Regex.IsMatch(login, @"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?\z"))
            throw new ArgumentException("--mention-user must be a GitHub username without @");
        var user = (await Query("query($login:String!) { user(login:$login) { login } }", Json.Object(("login", login)), ct))["user"];
        if (user is null) throw new ArgumentException("--mention-user must identify an existing GitHub user, not an organization");
        MentionUser = new MentionUser(user.Text("login"));
        Log.Write("mention-user", $"@{MentionUser.Login} publishes-as={AgentLogin}");
    }

    public bool IsEnrollmentMention(JsonNode source) => !IsAgentActor(source["author"]) &&
        ((MentionUser?.IsMentioned(source.Text("bodyHTML")) ?? false) ||
         Regex.IsMatch(source.Text("body"), @"(?<![\w-])@" + Regex.Escape(AgentLogin) + @"(?![\w-])", RegexOptions.IgnoreCase));

    public async Task<List<JsonNode>> Discussions(CancellationToken ct)
    {
        var parts = Repository.Split('/');
        var discussions = new List<JsonNode>();
        string? cursor = null;
        do
        {
            var data = await Query("query($owner:String!,$name:String!,$cursor:String) { repository(owner:$owner,name:$name) { discussions(first:100,after:$cursor) { nodes { " + DiscussionFields + " } " + PageInfo + " } } }", Json.Object(("owner", parts[0]), ("name", parts[1]), ("cursor", cursor)), ct);
            var connection = data["repository"]?["discussions"] ?? throw new IOException("Repository not found or Discussions inaccessible");
            discussions.AddRange(connection["nodes"]!.AsArray().Select(n => n!.Copy()));
            cursor = Next(connection);
        } while (cursor is not null);
        return discussions;
    }

    public async Task<JsonNode?> Discussion(int number, CancellationToken ct)
    {
        var parts = Repository.Split('/');
        var data = await Query("query($owner:String!,$name:String!,$number:Int!) { repository(owner:$owner,name:$name) { discussion(number:$number) { " + DiscussionFields + " } } }",
            Json.Object(("owner", parts[0]), ("name", parts[1]), ("number", number)), ct);
        return data["repository"]?["discussion"]?.Copy();
    }

    public async Task<List<JsonNode>> Sources(JsonNode discussion, CancellationToken ct)
    {
        var sources = new List<JsonNode> { discussion.Copy() };
        sources.AddRange(await Connection(discussion.Text("id"), "Discussion", "comments", MessageFields + " replyTo { id }", ct));
        foreach (var root in sources.Skip(1).ToArray())
            sources.AddRange(await Connection(root.Text("id"), "DiscussionComment", "replies", MessageFields + " replyTo { id }", ct));
        foreach (var source in sources)
        {
            var type = source.Text("id") == discussion.Text("id") ? "Discussion" : "DiscussionComment";
            source["reactions"] = Json.Array(await Connection(source.Text("id"), type, "reactions", "id content user { __typename login } createdAt", ct));
        }
        return sources;
    }

    async Task<List<JsonNode>> Connection(string id, string type, string field, string fields, CancellationToken ct)
    {
        var nodes = new List<JsonNode>();
        string? cursor = null;
        do
        {
            var query = "query($id:ID!,$cursor:String) { node(id:$id) { ... on " + type + " { " + field + "(first:100,after:$cursor) { nodes { " + fields + " } " + PageInfo + " } } } }";
            var connection = (await Query(query, Json.Object(("id", id), ("cursor", cursor)), ct))["node"]?[field] ?? throw new IOException($"Missing {type} {id}");
            nodes.AddRange(connection["nodes"]!.AsArray().Select(n => n!.Copy()));
            cursor = Next(connection);
        } while (cursor is not null);
        return nodes;
    }

    static string? Next(JsonNode connection) => connection["pageInfo"]!["hasNextPage"]!.GetValue<bool>() ? connection["pageInfo"]!.Text("endCursor") : null;

    public static bool HasViewerReceipt(JsonNode source) =>
        source["reactionGroups"]?.AsArray().Any(g => g!.Text("content") == "EYES" && g!["viewerHasReacted"]!.GetValue<bool>()) ?? false;

    public async Task AddReceipt(string subjectId, CancellationToken ct)
    {
        var data = await Send("mutation($id:ID!) { addReaction(input:{subjectId:$id,content:EYES}) { reaction { id } } }", Json.Object(("id", subjectId)), ct);
        if (data["addReaction"]?["reaction"]?["id"] is null)
            throw new GitHubWriteException(GitHubFailure.Transient, "receipt mutation returned no reaction", uncertain: true);
    }

    /// <summary>Reads whether the App's EYES receipt exists, to reconcile an uncertain earlier write.</summary>
    public async Task<bool> HasReceipt(string subjectId, CancellationToken ct)
    {
        var node = (await Send("query($id:ID!) { node(id:$id) { ... on Reactable { reactionGroups { content viewerHasReacted } } } }", Json.Object(("id", subjectId)), ct))["node"];
        return node is null ? throw new GitHubWriteException(GitHubFailure.Permanent, "receipt subject no longer exists") : HasViewerReceipt(node);
    }

    /// <summary>Hidden in the rendered dashboard body; with App authorship it identifies the controller-owned dashboard.</summary>
    public const string DashboardMarker = "<!-- repoagent:controller-dashboard -->";

    /// <summary>The controller's own status Discussion: authored by this App and marked. Participants cannot author as the App.</summary>
    public bool IsDashboard(JsonNode discussion) =>
        IsAgentActor(discussion["author"]) && discussion.Text("body").Contains(DashboardMarker, StringComparison.Ordinal);

    /// <summary>Resolves the repository and named Discussion category that hold the dashboard.</summary>
    public async Task<(string Repository, string Category)> DashboardCategory(string name, CancellationToken ct)
    {
        var parts = Repository.Split('/');
        var repository = (await Send("query($owner:String!,$name:String!) { repository(owner:$owner,name:$name) { id discussionCategories(first:100) { nodes { id name } } } }",
            Json.Object(("owner", parts[0]), ("name", parts[1])), ct))["repository"] ?? throw new GitHubWriteException(GitHubFailure.Permanent, "repository not found");
        var category = repository["discussionCategories"]?["nodes"]?.AsArray().FirstOrDefault(node => string.Equals(node!.Text("name"), name, StringComparison.OrdinalIgnoreCase))
            ?? throw new GitHubWriteException(GitHubFailure.Permanent, $"dashboard Discussion category '{name}' does not exist");
        return (repository.Text("id"), category.Text("id"));
    }

    /// <summary>Finds the oldest owned dashboard in the category, which also settles an uncertain earlier creation.</summary>
    public async Task<(string Id, string Url)?> FindDashboard(string category, CancellationToken ct)
    {
        var parts = Repository.Split('/');
        string? cursor = null;
        do
        {
            var connection = (await Send("query($owner:String!,$name:String!,$category:ID!,$cursor:String) { repository(owner:$owner,name:$name) { discussions(first:50,after:$cursor,categoryId:$category,orderBy:{field:CREATED_AT,direction:ASC}) { nodes { id url body author { __typename login } } " + PageInfo + " } } }",
                Json.Object(("owner", parts[0]), ("name", parts[1]), ("category", category), ("cursor", cursor)), ct))["repository"]?["discussions"]
                ?? throw new GitHubWriteException(GitHubFailure.Permanent, "repository Discussions are not readable");
            if (connection["nodes"]!.AsArray().FirstOrDefault(node => IsDashboard(node!)) is { } owned) return (owned.Text("id"), owned.Text("url"));
            cursor = Next(connection);
        } while (cursor is not null);
        return null;
    }

    public async Task<(string Id, string Url)> CreateDashboard((string Repository, string Category) target, string title, string body, CancellationToken ct)
    {
        var created = (await Send("mutation($repository:ID!,$category:ID!,$title:String!,$body:String!) { createDiscussion(input:{repositoryId:$repository,categoryId:$category,title:$title,body:$body}) { discussion { id url } } }",
            Json.Object(("repository", target.Repository), ("category", target.Category), ("title", title), ("body", body)), ct))["createDiscussion"]?["discussion"]
            ?? throw new GitHubWriteException(GitHubFailure.Transient, "dashboard creation returned no Discussion", uncertain: true);
        return (created.Text("id"), created.Text("url"));
    }

    public async Task UpdateDashboard(string id, string title, string body, CancellationToken ct)
    {
        // Replacing the whole title and body is idempotent, so an uncertain earlier update needs no reconciliation.
        if ((await Send("mutation($id:ID!,$title:String!,$body:String!) { updateDiscussion(input:{discussionId:$id,title:$title,body:$body}) { discussion { id } } }",
            Json.Object(("id", id), ("title", title), ("body", body)), ct))["updateDiscussion"]?["discussion"] is null)
            throw new GitHubWriteException(GitHubFailure.Transient, "dashboard update returned no Discussion", uncertain: true);
    }

    // Controller-owned requests: classify by HTTP status, rate-limit headers and GraphQL error type.
    async Task<JsonNode> Send(string query, JsonObject variables, CancellationToken ct)
    {
        string response;
        try { response = await Api("POST", "graphql", Json.Object(("query", query), ("variables", variables)), ct, include: true); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // No HTTP response was observed, so the request may still have been applied.
            throw new GitHubWriteException(GitHubFailure.Transient, error.Message, uncertain: true);
        }
        return GitHubWriteException.Classify(response);
    }
}

enum GitHubFailure { Transient, RateLimited, Permanent }

/// <summary>A classified controller-owned GitHub request failure. Uncertain means GitHub may have applied it.</summary>
sealed class GitHubWriteException(GitHubFailure kind, string message, bool uncertain = false, TimeSpan? retryAfter = null,
    DateTimeOffset? resetAt = null) : IOException(message)
{
    static readonly string[] Rejections = ["NOT_FOUND", "FORBIDDEN", "UNPROCESSABLE"];
    public GitHubFailure Kind { get; } = kind;
    public bool Uncertain { get; } = uncertain;

    /// <summary>The earliest retry GitHub signalled, if any.</summary>
    public DateTimeOffset? NotBefore(DateTimeOffset now)
    {
        var after = retryAfter is { } delay ? now + delay : (DateTimeOffset?)null;
        return after is null ? resetAt : resetAt is null || after > resetAt ? after : resetAt;
    }

    /// <summary>Returns GraphQL data from gh --include output, or throws the classified failure.</summary>
    public static JsonNode Classify(string response)
    {
        var lines = response.Split('\n');
        if (!int.TryParse(lines[0].Split(' ', 3).ElementAtOrDefault(1), out var status))
            throw new GitHubWriteException(GitHubFailure.Transient, "unrecognized gh response", uncertain: true);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 1;
        for (; index < lines.Length && lines[index].TrimEnd('\r').Length != 0; index++)
            if (lines[index].Split(':', 2) is [var name, var field]) headers[name.Trim()] = field.Trim();
        JsonNode? body = null;
        try { body = JsonNode.Parse(string.Join('\n', lines.Skip(index + 1))); } catch (System.Text.Json.JsonException) { }
        var errors = body?["errors"] as JsonArray ?? [];
        var types = errors.Select(error => error?["type"] is JsonValue type && type.TryGetValue<string>(out var text) ? text : "").ToArray();
        var detail = (errors.FirstOrDefault()?["message"] ?? body?["message"]) is JsonValue value && value.TryGetValue<string>(out var message) ? message : "";
        var description = $"HTTP {status}{(types.FirstOrDefault(t => t.Length != 0) is { } first ? " " + first : "")}{(detail.Length != 0 ? ": " + detail : "")}";
        var exhausted = headers.GetValueOrDefault("X-RateLimit-Remaining") == "0";
        TimeSpan? retryAfter = int.TryParse(headers.GetValueOrDefault("Retry-After"), out var seconds) && seconds >= 0 ? TimeSpan.FromSeconds(seconds) : null;
        DateTimeOffset? reset = exhausted && long.TryParse(headers.GetValueOrDefault("X-RateLimit-Reset"), out var epoch) ? DateTimeOffset.FromUnixTimeSeconds(epoch) : null;
        if (status == 429 || types.Contains("RATE_LIMITED") || (status == 403 && (retryAfter is not null || exhausted ||
            detail.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || detail.Contains("abuse", StringComparison.OrdinalIgnoreCase))))
            throw new GitHubWriteException(GitHubFailure.RateLimited, description, retryAfter: retryAfter, resetAt: reset);
        if (status is >= 200 and < 300)
        {
            if (errors.Count == 0) return body?["data"] ?? throw new GitHubWriteException(GitHubFailure.Transient, description + ": no data", uncertain: true);
            if (types.All(Rejections.Contains)) throw new GitHubWriteException(GitHubFailure.Permanent, description);
            throw new GitHubWriteException(GitHubFailure.Transient, description, uncertain: true);
        }
        // 401: renewal may recover the credential. 408, 409 and 5xx may have been applied before failing.
        if (status == 401) throw new GitHubWriteException(GitHubFailure.Transient, description);
        if (status is 400 or 403 or 404 or 410 or 422) throw new GitHubWriteException(GitHubFailure.Permanent, description);
        throw new GitHubWriteException(GitHubFailure.Transient, description, uncertain: true);
    }
}
