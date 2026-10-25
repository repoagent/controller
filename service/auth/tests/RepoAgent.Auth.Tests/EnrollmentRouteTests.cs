using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

// What the broker routes add to the core's results over HTTP: the body grammar each setup version accepts, and the
// uncached web JSON the installation-token route answers.
public sealed class EnrollmentRouteTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Setup_version_requires_its_own_body_without_provider_work()
    {
        var f = new InMemoryBroker();
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        var before = f.Rows();
        await using var host = InMemoryHost.Create(f);
        using var client = host.CreateClient();
        using var legacyBody = await Send(client, "/v2/enrollments", InMemoryBroker.Request());
        using var agentBody = await Send(client, "/v1/enrollments",
            new RepositoryVariableEnrollmentRequest(Guid.NewGuid().ToString("N"), 123456, "owner/project", "APPROVAL", "polling"));
        Assert.Equal(HttpStatusCode.BadRequest, legacyBody.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, agentBody.StatusCode);
        Assert.Equal(0, f.GitHub.Reads);
        Assert.Equal(0, f.GitHub.Mints);
        Assert.Equal(0, f.State.Commits);
        // Only the offline schema quotas, unchanged.
        Assert.Equal(before.Select(row => (row.Key, row.Version)), f.Rows().Select(row => (row.Key, row.Version)));
        Assert.Equal(2, before.Count);
    }

    [Fact]
    public async Task Installation_token_route_answers_the_issuers_v1_body_and_never_lets_a_GitHub_token_be_cached()
    {
        var f = new InMemoryBroker();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f.GitHub);
        var active = await ActionsVariableEnrollmentTests.Active(f, github);
        await using var host = InMemoryHost.Create(f);
        using var client = host.CreateClient();
        using var runtime = await Send(client, $"/v1/devices/{active.Device}/installation-token", secret: active.Secret);
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        Assert.True(runtime.Headers.CacheControl!.NoStore);
        var body = await runtime.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(9, body.EnumerateObject().Count());
        Assert.Equal(123456, body.GetProperty("repositoryId").GetInt64());
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, object? body = null, string? secret = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret ?? InMemoryBroker.SetupSecret);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request, Token);
    }
}
