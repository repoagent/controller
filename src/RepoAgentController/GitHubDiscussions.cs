using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>The <see cref="IDiscussions"/> adapter: the App's EYES receipts and owned dashboard, through gh's GraphQL API with the
/// installation token. Failures are classified as <see cref="GitHubRequestException"/>.</summary>
sealed class GitHubDiscussions(GitHub github) : IDiscussions
{
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
        return node is null ? throw new GitHubRequestException(GitHubFailure.Permanent, "receipt subject no longer exists") : GitHub.HasViewerReceipt(node);
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
            if (connection["nodes"]!.AsArray().FirstOrDefault(node => github.IsDashboard(node!)) is { } owned) return new(owned.Text("id"), owned.Text("url"));
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
