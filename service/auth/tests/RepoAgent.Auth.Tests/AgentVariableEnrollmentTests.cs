using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class AgentVariableEnrollmentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Agent_approval_requires_private_ack_and_observed_removal_permanently_closes_runtime()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        ConfigureGitHub(f);

        var request = new
        {
            idempotencyId = Guid.NewGuid().ToString("N"), repositoryId = 123456,
            repository = "owner/project", variable = "APPROVAL", profile = "polling"
        };
        using var start = await f.Send("/v2/enrollments", request);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var pending = await start.Content.ReadFromJsonAsync<JsonElement>(Token);
        var pendingId = pending.GetProperty("pendingId").GetString()!;
        var marker = pending.GetProperty("marker").GetString()!;
        var publicApproval = JsonSerializer.Deserialize<JsonElement>(marker);
        Assert.Equal(2, publicApproval.GetProperty("version").GetInt32());
        Assert.Equal("repository-agent-variable", publicApproval.GetProperty("approvalKind").GetString());
        Assert.Equal("repository-agent-approval-v2", publicApproval.GetProperty("policyVersion").GetString());
        Assert.False(publicApproval.TryGetProperty("environment", out _));
        Assert.False(publicApproval.TryGetProperty("environmentId", out _));
        using var duplicate = await f.Send("/v2/enrollments", request);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(pending.ToString(), (await duplicate.Content.ReadFromJsonAsync<JsonElement>(Token)).ToString());

        f.GitHub.Marker = marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var completion = await f.Send($"/v2/enrollments/{pendingId}/completion");
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var receipt = await completion.Content.ReadFromJsonAsync<JsonElement>(Token);
        var device = receipt.GetProperty("enrollment").GetProperty("deviceId").GetString()!;
        var secret = receipt.GetProperty("enrollment").GetProperty("deviceSecret").GetString()!;
        Assert.DoesNotContain(secret, marker);
        using var unacknowledged = await f.Send($"/v1/devices/{device}/installation-token", secret: secret);
        Assert.Equal(HttpStatusCode.Unauthorized, unacknowledged.StatusCode);

        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var recovered = await f.Send($"/v2/enrollments/{pendingId}/completion");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(receipt.ToString(), (await recovered.Content.ReadFromJsonAsync<JsonElement>(Token)).ToString());
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var ack = await f.Send($"/v2/devices/{device}/acknowledgment",
            new { completionId = receipt.GetProperty("completionId").GetString() }, secret);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var runtime = await f.Send($"/v1/devices/{device}/installation-token", secret: secret);
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        Assert.Equal(1, f.GitHub.VerifierMints);
        Assert.Contains(f.GitHub.Requests, r => r.StartsWith("GET /repos/owner/project/agents/variables?", StringComparison.Ordinal));

        f.GitHub.Marker = "";
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var removed = await f.Send($"/v1/devices/{device}/installation-token", secret: secret);
        Assert.Equal(HttpStatusCode.Unauthorized, removed.StatusCode);
        f.GitHub.Marker = marker;
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var restored = await f.Send($"/v1/devices/{device}/installation-token", secret: secret);
        Assert.Equal(HttpStatusCode.Unauthorized, restored.StatusCode);
    }

    internal static void ConfigureGitHub(EnrollmentFixture f)
    {
        f.GitHub.Before = async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                var input = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(Token));
                var permissions = input.GetProperty("permissions");
                if (permissions.TryGetProperty("agent_variables", out var access))
                {
                    Assert.Equal("read", access.GetString());
                    Assert.Single(permissions.EnumerateObject());
                    var ids = input.GetProperty("repository_ids");
                    Assert.Equal(123456, Assert.Single(ids.EnumerateArray()).GetInt64());
                    f.GitHub.VerifierMints++;
                    return GitHubTransport.Json(new
                    {
                        token = "agent-verifier-token", expires_at = f.Clock.GetUtcNow().AddHours(1),
                        repository_selection = "selected", permissions,
                        repositories = new[] { new { id = 123456, full_name = "owner/project" } }
                    }, HttpStatusCode.Created);
                }
                Assert.DoesNotContain(permissions.EnumerateObject(), p => p.Name is "actions" or "environments");
                Assert.Equal("write", permissions.GetProperty("discussions").GetString());
                if (permissions.TryGetProperty("repository_hooks", out var hooks))
                {
                    Assert.Equal("write", hooks.GetString());
                    Assert.Equal(2, permissions.EnumerateObject().Count());
                }
                else Assert.Single(permissions.EnumerateObject());
                return GitHubTransport.Json(new
                {
                    token = "runtime-token-" + f.GitHub.Mints, expires_at = f.Clock.GetUtcNow().AddHours(1),
                    repository_selection = "selected", permissions,
                    repositories = new[] { new { id = 123456, full_name = "owner/project" } }
                }, HttpStatusCode.Created);
            }
            if (path.EndsWith("/installation", StringComparison.Ordinal))
                return GitHubTransport.Json(new
                {
                    id = 789, app_id = IssuancePolicy.AppId, app_slug = IssuancePolicy.AppSlug,
                    suspended_at = (string?)null,
                    permissions = new { discussions = "write", repository_hooks = "write", agent_variables = "read" }
                });
            if (path == "/repos/owner/project/agents/variables")
            {
                Assert.Contains("per_page=30", request.RequestUri.Query);
                return GitHubTransport.Json(new
                {
                    total_count = f.GitHub.Marker.Length == 0 ? 0 : 1,
                    variables = f.GitHub.Marker.Length == 0 ? Array.Empty<object>()
                        : [new { name = "APPROVAL", value = f.GitHub.Marker }]
                });
            }
            Assert.DoesNotContain("/environments", path);
            Assert.DoesNotContain("/orgs/", path);
            return null;
        };
    }

}
