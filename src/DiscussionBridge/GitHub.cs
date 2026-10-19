using System.Diagnostics;
using System.Text.Json.Nodes;

namespace DiscussionBridge;

sealed class GitHub(string repository, GitHubAppCredentials credentials, string executable)
{
    public string Repository { get; } = repository;
    public string AgentLogin => credentials.AgentLogin;
    const string MessageFields = "id url body createdAt updatedAt author { login } reactionGroups { content viewerHasReacted }";
    const string PageInfo = "pageInfo { hasNextPage endCursor }";

    public async Task<JsonNode> Query(string query, object variables, CancellationToken ct)
    {
        await credentials.GetToken(ct);
        var start = AppGitHubCommand.Create(executable, credentials.TokenFile, ["api", "graphql", "--method", "POST", "--input", "-"]);
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new IOException("Could not start gh");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.StandardInput.WriteAsync(Json.Encode(new { query, variables }));
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
        var viewer = (await Query("query { viewer { login } }", new { }, ct))["viewer"]!.Text("login");
        if (!string.Equals(viewer, AgentLogin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Authenticated GitHub viewer {viewer} differs from --agent-login {AgentLogin}");
        Log.Write("identity", viewer);
    }

    public async Task<List<JsonNode>> Discussions(CancellationToken ct)
    {
        var parts = Repository.Split('/');
        var discussions = new List<JsonNode>();
        string? cursor = null;
        do
        {
            var data = await Query("query($owner:String!,$name:String!,$cursor:String) { repository(owner:$owner,name:$name) { discussions(first:100,after:$cursor) { nodes { " + MessageFields + " number title closed locked answer { id } category { id name } } " + PageInfo + " } } }", new { owner = parts[0], name = parts[1], cursor }, ct);
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
            source["reactions"] = Json.Array(await Connection(source.Text("id"), type, "reactions", "id content user { login } createdAt", ct));
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
            var connection = (await Query(query, new { id, cursor }, ct))["node"]?[field] ?? throw new IOException($"Missing {type} {id}");
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
        var data = await Query("mutation($id:ID!) { addReaction(input:{subjectId:$id,content:EYES}) { reaction { id } } }", new { id = source.Text("id") }, ct);
        if (data["addReaction"]?["reaction"]?["id"] is null) throw new IOException("Receipt mutation returned no reaction");
        Log.Write("receipt", source.Text("url"));
    }
}
