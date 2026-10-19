using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DiscussionBridge;

sealed class GitHub(string repository, GitHubAppCredentials credentials, string executable)
{
    public string Repository { get; } = repository;
    public string AgentLogin => credentials.AgentLogin;
    public MentionUser? MentionUser { get; private set; }
    public bool IsAgentActor(JsonNode? actor) => credentials.IsAgentActor(actor);
    const string MessageFields = "id url body bodyHTML createdAt updatedAt author { __typename login } reactionGroups { content viewerHasReacted }";
    const string PageInfo = "pageInfo { hasNextPage endCursor }";

    public async Task<JsonNode> Query(string query, JsonObject variables, CancellationToken ct)
    {
        await credentials.GetToken(ct);
        var start = AppGitHubCommand.Create(executable, credentials.TokenFile, ["api", "graphql", "--method", "POST", "--input", "-"]);
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new IOException("Could not start gh");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.StandardInput.WriteAsync(Json.Encode(Json.Object(("query", query), ("variables", variables))));
        process.StandardInput.Close();
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct); }
        catch { process.Kill(true); throw; }
        var text = await output;
        if (process.ExitCode != 0) throw new IOException($"gh failed: {await error}");
        var response = JsonNode.Parse(text) ?? throw new IOException("Empty GraphQL response");
        if (response["errors"] is { } errors) throw new IOException($"GraphQL errors: {errors}");
        return response["data"] ?? throw new IOException("No GraphQL data");
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
            var data = await Query("query($owner:String!,$name:String!,$cursor:String) { repository(owner:$owner,name:$name) { discussions(first:100,after:$cursor) { nodes { " + MessageFields + " number title closed locked answer { id } category { id name } } " + PageInfo + " } } }", Json.Object(("owner", parts[0]), ("name", parts[1]), ("cursor", cursor)), ct);
            var connection = data["repository"]?["discussions"] ?? throw new IOException("Repository not found or Discussions inaccessible");
            discussions.AddRange(connection["nodes"]!.AsArray().Select(n => n!.Copy()));
            cursor = Next(connection);
        } while (cursor is not null);
        return discussions;
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
