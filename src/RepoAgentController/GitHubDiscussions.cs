using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>The <see cref="IDiscussions"/> adapter: the repository's Discussions, the App's EYES receipts and its owned dashboard,
/// through gh's GraphQL API with the installation token. <see cref="DiscussionJson"/> maps the nodes to records.</summary>
sealed class GitHubDiscussions(GitHub github) : IDiscussions
{
    const string MessageFields = "id url body bodyHTML createdAt updatedAt author { __typename login } reactionGroups { content viewerHasReacted }";
    const string DiscussionFields = MessageFields + " number title closed locked answer { id } category { id name }";

    public async Task<IReadOnlyList<Discussion>> List(CancellationToken ct)
    {
        var parts = github.Repository.Split('/');
        var discussions = new List<Discussion>();
        string? cursor = null;
        do
        {
            var data = await github.Query("query($owner:String!,$name:String!,$cursor:String) { repository(owner:$owner,name:$name) { discussions(first:100,after:$cursor) { nodes { " + DiscussionFields + " } " + GitHub.PageInfo + " } } }", Json.Object(("owner", parts[0]), ("name", parts[1]), ("cursor", cursor)), ct);
            var connection = data["repository"]?["discussions"] ?? throw new IOException("Repository not found or Discussions inaccessible");
            discussions.AddRange(connection["nodes"]!.AsArray().Select(n => DiscussionJson.ReadDiscussion(n!, github.MentionUser)));
            cursor = GitHub.Next(connection);
        } while (cursor is not null);
        return discussions;
    }

    /// <summary>Returns null only when GitHub confirms that the configured repository has no Discussion with this number.</summary>
    public async Task<Discussion?> Find(int number, CancellationToken ct)
    {
        var parts = github.Repository.Split('/');
        var response = await github.Post("query($owner:String!,$name:String!,$number:Int!) { repository(owner:$owner,name:$name) { discussion(number:$number) { " + DiscussionFields + " } } }",
            Json.Object(("owner", parts[0]), ("name", parts[1]), ("number", number)), ct);
        // Only the typed NOT_FOUND for this field inside the resolved repository confirms deletion. Message text,
        // repository access loss, other error types and incomplete data stay classified failures.
        if (GitHub.ConfirmsAbsent(response, "repository", "discussion")) return null;
        return DiscussionJson.ReadDiscussion(GitHub.Classify(response)["repository"]?["discussion"]
            ?? throw new GitHubRequestException(GitHubFailure.Transient, "incomplete Discussion response"), github.MentionUser);
    }

    public async Task<DiscussionConversation> Read(Discussion discussion, CancellationToken ct)
    {
        var comments = await Connection(discussion.Id, "Discussion", "comments", MessageFields + " replyTo { id }", ct);
        var sources = new List<JsonNode>(comments);
        foreach (var root in comments)
            sources.AddRange(await Connection(root.Text("id"), "DiscussionComment", "replies", MessageFields + " replyTo { id }", ct));
        var reactions = await Reactions(discussion.Id, "Discussion", ct);
        var read = new List<DiscussionComment>();
        foreach (var source in sources)
            read.Add(DiscussionJson.ReadComment(source, await Reactions(source.Text("id"), "DiscussionComment", ct), github.MentionUser));
        return new(discussion, reactions, read);
    }

    async Task<IReadOnlyList<Reaction>> Reactions(string id, string type, CancellationToken ct) =>
        [.. (await Connection(id, type, "reactions", "id content user { __typename login } createdAt", ct)).Select(DiscussionJson.ReadReaction)];

    public async Task<IReadOnlyList<string>> Labels(string discussionId, CancellationToken ct) =>
        (await Connection(discussionId, "Discussion", "labels", "name", ct)).Select(label => label.Text("name")).ToList();

    async Task<List<JsonNode>> Connection(string id, string type, string field, string fields, CancellationToken ct)
    {
        var nodes = new List<JsonNode>();
        string? cursor = null;
        do
        {
            var query = "query($id:ID!,$cursor:String) { node(id:$id) { ... on " + type + " { " + field + "(first:100,after:$cursor) { nodes { " + fields + " } " + GitHub.PageInfo + " } } } }";
            var connection = (await github.Query(query, Json.Object(("id", id), ("cursor", cursor)), ct))["node"]?[field] ?? throw new IOException($"Missing {type} {id}");
            nodes.AddRange(connection["nodes"]!.AsArray().Select(n => n!.Copy()));
            cursor = GitHub.Next(connection);
        } while (cursor is not null);
        return nodes;
    }

    public async Task AddEyesReceipt(string subjectId, CancellationToken ct)
    {
        var data = await github.Send("mutation($id:ID!) { addReaction(input:{subjectId:$id,content:EYES}) { reaction { id } } }", Json.Object(("id", subjectId)), ct);
        if (data["addReaction"]?["reaction"]?["id"] is null)
            throw new GitHubRequestException(GitHubFailure.Transient, "receipt mutation returned no reaction", uncertain: true);
    }

    /// <summary>Reads whether the App's EYES receipt exists, to reconcile an uncertain earlier write.</summary>
    public async Task<bool> HasEyesReceipt(string subjectId, CancellationToken ct)
    {
        var node = (await github.Send("query($id:ID!) { node(id:$id) { ... on Reactable { reactionGroups { content viewerHasReacted } } } }", Json.Object(("id", subjectId)), ct))["node"];
        return node is null ? throw new GitHubRequestException(GitHubFailure.Permanent, "receipt subject no longer exists")
            : DiscussionMessage.HasEyes(DiscussionJson.ReadReactionGroups(node["reactionGroups"]));
    }

    /// <summary>Resolves the repository and named Discussion category that hold the dashboard.</summary>
    public async Task<DashboardLocation> DashboardCategory(string name, CancellationToken ct)
    {
        var parts = github.Repository.Split('/');
        var repository = (await github.Send("query($owner:String!,$name:String!) { repository(owner:$owner,name:$name) { id discussionCategories(first:100) { nodes { id name } } } }",
            Json.Object(("owner", parts[0]), ("name", parts[1])), ct))["repository"] ?? throw new GitHubRequestException(GitHubFailure.Permanent, "repository not found");
        var category = repository["discussionCategories"]?["nodes"]?.AsArray().FirstOrDefault(node => string.Equals(node!.Text("name"), name, StringComparison.OrdinalIgnoreCase))
            ?? throw new GitHubRequestException(GitHubFailure.Permanent, $"dashboard Discussion category '{name}' does not exist");
        return new(repository.Text("id"), category.Text("id"));
    }

    /// <summary>Finds the oldest owned dashboard in the category, which also settles an uncertain earlier creation.</summary>
    public async Task<DashboardDiscussion?> FindDashboard(DashboardLocation at, CancellationToken ct)
    {
        var parts = github.Repository.Split('/');
        string? cursor = null;
        do
        {
            var connection = (await github.Send("query($owner:String!,$name:String!,$category:ID!,$cursor:String) { repository(owner:$owner,name:$name) { discussions(first:50,after:$cursor,categoryId:$category,orderBy:{field:CREATED_AT,direction:ASC}) { nodes { id url body author { __typename login } } " + GitHub.PageInfo + " } } }",
                Json.Object(("owner", parts[0]), ("name", parts[1]), ("category", at.CategoryId), ("cursor", cursor)), ct))["repository"]?["discussions"]
                ?? throw new GitHubRequestException(GitHubFailure.Permanent, "repository Discussions are not readable");
            if (connection["nodes"]!.AsArray().FirstOrDefault(node => github.Identity.IsDashboard(DiscussionJson.ReadActor(node!["author"]), node.Text("body"))) is { } owned)
                return new(owned.Text("id"), owned.Text("url"));
            cursor = GitHub.Next(connection);
        } while (cursor is not null);
        return null;
    }

    public async Task<DashboardDiscussion> CreateDashboard(DashboardLocation at, string title, string body, CancellationToken ct)
    {
        var created = (await github.Send("mutation($repository:ID!,$category:ID!,$title:String!,$body:String!) { createDiscussion(input:{repositoryId:$repository,categoryId:$category,title:$title,body:$body}) { discussion { id url } } }",
            Json.Object(("repository", at.RepositoryId), ("category", at.CategoryId), ("title", title), ("body", body)), ct))["createDiscussion"]?["discussion"]
            ?? throw new GitHubRequestException(GitHubFailure.Transient, "dashboard creation returned no Discussion", uncertain: true);
        return new(created.Text("id"), created.Text("url"));
    }

    public async Task UpdateDashboard(string id, string title, string body, CancellationToken ct)
    {
        // Replacing the whole title and body is idempotent, so an uncertain earlier update needs no reconciliation.
        if ((await github.Send("mutation($id:ID!,$title:String!,$body:String!) { updateDiscussion(input:{discussionId:$id,title:$title,body:$body}) { discussion { id } } }",
            Json.Object(("id", id), ("title", title), ("body", body)), ct))["updateDiscussion"]?["discussion"] is null)
            throw new GitHubRequestException(GitHubFailure.Transient, "dashboard update returned no Discussion", uncertain: true);
    }
}
