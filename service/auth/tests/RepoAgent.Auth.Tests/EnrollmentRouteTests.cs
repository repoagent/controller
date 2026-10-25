using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

// What the broker routes add to the core's results over HTTP and the Table adapter: the body grammar each setup
// version accepts, and the uncached web JSON the installation-token route answers.
public sealed class EnrollmentRouteTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Setup_version_requires_its_own_body_without_provider_work()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        var before = await Rows(f);
        using var legacyBody = await f.Send("/v2/enrollments", f.Request());
        using var agentBody = await f.Send("/v1/enrollments",
            new RepositoryVariableEnrollmentRequest(Guid.NewGuid().ToString("N"), 123456, "owner/project", "APPROVAL", "polling"));
        Assert.Equal(HttpStatusCode.BadRequest, legacyBody.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, agentBody.StatusCode);
        Assert.Equal(0, f.GitHub.Reads);
        Assert.Equal(0, f.GitHub.Mints);
        var after = await Rows(f);
        Assert.Equal(2, after.Count); // Only the offline schema quotas, with no request writes.
        foreach (var row in before)
            Assert.Equal(row.ETag, Assert.Single(after, current => current.PartitionKey == row.PartitionKey
                && current.RowKey == row.RowKey).ETag);
    }

    [Fact]
    public async Task Installation_token_route_answers_the_issuers_v1_body_and_never_lets_a_GitHub_token_be_cached()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f);
        var active = await f.ActiveAsync(github);
        using var runtime = await f.Send($"/v1/devices/{active.Device}/installation-token", secret: active.Secret);
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        Assert.True(runtime.Headers.CacheControl!.NoStore);
        var body = await runtime.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(9, body.EnumerateObject().Count());
        Assert.Equal(123456, body.GetProperty("repositoryId").GetInt64());
    }

    private static async Task<List<TableEntity>> Rows(EnrollmentFixture f)
    {
        var rows = new List<TableEntity>();
        await foreach (var row in f.Storage.Table.QueryAsync<TableEntity>(cancellationToken: Token)) rows.Add(row);
        return rows;
    }
}
