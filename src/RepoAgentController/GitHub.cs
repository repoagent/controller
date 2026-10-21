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
            throw new IOException("Repository metadata does not match the approved checkout");
        return new(id, name, remote, branch);
    }

    async Task<string> Api(string method, string resource, JsonNode? input, CancellationToken ct)
    {
        await credentials.GetToken(ct);
        var arguments = new List<string> { "api", resource, "--hostname", "github.com", "--method", method,
            "--header", "X-GitHub-Api-Version: 2026-03-10" };
        if (input is not null) arguments.AddRange(["--input", "-"]);
        var start = AppGitHubCommand.Create(executable, credentials.TokenFile, arguments);
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
        if (process.ExitCode != 0) throw new IOException($"gh failed: {await error}");
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

    public async Task Receipt(JsonNode source, CancellationToken ct)
    {
        if (source["reactionGroups"]!.AsArray().Any(g => g!.Text("content") == "EYES" && g!["viewerHasReacted"]!.GetValue<bool>()))
        {
            Log.Write("receipt-reused", source.Text("url"));
            return;
        }
        var data = await Query("mutation($id:ID!) { addReaction(input:{subjectId:$id,content:EYES}) { reaction { id } } }", Json.Object(("id", source.Text("id"))), ct);
        if (data["addReaction"]?["reaction"]?["id"] is null) throw new IOException("Receipt mutation returned no reaction");
        Log.Write("receipt", source.Text("url"));
    }
}
