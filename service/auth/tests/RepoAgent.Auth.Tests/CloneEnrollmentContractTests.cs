using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class CloneEnrollmentContractTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string ClonePolicy = "repository-agent-clone-approval-v2";

    [Theory]
    [InlineData("polling", 1, "repository-agent-approval-v2")]
    [InlineData("polling-clone", 2, ClonePolicy)]
    [InlineData("webhook-clone", 2, ClonePolicy)]
    public async Task Fresh_Agent_approval_and_private_ack_enable_only_the_selected_runtime_family(
        string profile, int runtimeVersion, string policy)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        ConfigureGitHub(f, profile);
        using var start = await f.Send("/v2/enrollments", new
        {
            idempotencyId = Guid.NewGuid().ToString("N"), repositoryId = 123456,
            repository = "owner/project", variable = "APPROVAL", profile
        });
        Assert.True(start.StatusCode == HttpStatusCode.OK,
            $"POST /v2/enrollments profile={profile}: expected HTTP 200, actual HTTP {(int)start.StatusCode}; response={await start.Content.ReadAsStringAsync(Token)}");
        var pending = await start.Content.ReadFromJsonAsync<JsonElement>(Token);
        var pendingId = pending.GetProperty("pendingId").GetString()!;
        var marker = pending.GetProperty("marker").GetString()!;
        var approval = JsonSerializer.Deserialize<JsonElement>(marker);
        Assert.Equal(2, approval.GetProperty("version").GetInt32());
        Assert.Equal("repository-agent-variable", approval.GetProperty("approvalKind").GetString());
        Assert.Equal(policy, approval.GetProperty("policyVersion").GetString());
        Assert.Equal(profile, approval.GetProperty("profile").GetString());

        f.GitHub.Marker = marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var completion = await f.Send($"/v2/enrollments/{pendingId}/completion");
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var receipt = await completion.Content.ReadFromJsonAsync<JsonElement>(Token);
        var enrollment = receipt.GetProperty("enrollment");
        Assert.Equal(runtimeVersion, enrollment.GetProperty("protocolVersion").GetInt32());
        AssertPermissions(profile, enrollment.GetProperty("permissions"));
        if (runtimeVersion == 2)
        {
            Assert.Equal(profile, enrollment.GetProperty("profile").GetString());
            Assert.Equal(policy, enrollment.GetProperty("policyVersion").GetString());
        }
        else
        {
            Assert.False(enrollment.TryGetProperty("profile", out _));
            Assert.False(enrollment.TryGetProperty("policyVersion", out _));
        }
        var device = enrollment.GetProperty("deviceId").GetString()!;
        var secret = enrollment.GetProperty("deviceSecret").GetString()!;
        var route = $"/v{runtimeVersion}/devices/{device}/installation-token";
        using var unacknowledged = await f.Send(route, secret: secret);
        Assert.Equal(HttpStatusCode.Unauthorized, unacknowledged.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var ack = await f.Send($"/v2/devices/{device}/acknowledgment",
            new { completionId = receipt.GetProperty("completionId").GetString() }, secret);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        if (runtimeVersion == 2)
        {
            var mints = f.GitHub.Mints;
            using var wrongFamily = await f.Send($"/v1/devices/{device}/installation-token", secret: secret);
            Assert.Equal(HttpStatusCode.Unauthorized, wrongFamily.StatusCode);
            Assert.Equal(mints, f.GitHub.Mints);
        }
        for (var renewal = 0; renewal < 2; renewal++)
        {
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            using var runtime = await f.Send(route, secret: secret);
            Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
            var lease = await runtime.Content.ReadFromJsonAsync<JsonElement>(Token);
            Assert.Equal(runtimeVersion, lease.GetProperty("protocolVersion").GetInt32());
            Assert.Equal(123456, lease.GetProperty("repositoryId").GetInt64());
            Assert.Equal("owner/project", lease.GetProperty("repository").GetString());
            AssertPermissions(profile, lease.GetProperty("permissions"));
            Assert.False(string.IsNullOrWhiteSpace(lease.GetProperty("token").GetString()));
            if (runtimeVersion == 2)
            {
                Assert.Equal(profile, lease.GetProperty("profile").GetString());
                Assert.Equal(policy, lease.GetProperty("policyVersion").GetString());
            }
        }
    }

    private static void AssertPermissions(string profile, JsonElement actual)
    {
        var expected = new Dictionary<string, string> { ["discussions"] = "write" };
        if (profile.EndsWith("-clone", StringComparison.Ordinal)) expected["contents"] = "read";
        if (profile == "webhook-clone") expected["repository_hooks"] = "write";
        Assert.Equal(expected.OrderBy(p => p.Key), actual.EnumerateObject()
            .Select(p => new KeyValuePair<string, string>(p.Name, p.Value.GetString()!)).OrderBy(p => p.Key));
    }

    private static void ConfigureGitHub(EnrollmentFixture f, string profile)
    {
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        var agent = f.GitHub.Before!;
        f.GitHub.Before = async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(Token);
                var permissions = body.GetProperty("permissions");
                if (!permissions.TryGetProperty("agent_variables", out _))
                {
                    Assert.Equal(123456, Assert.Single(body.GetProperty("repository_ids").EnumerateArray()).GetInt64());
                    AssertPermissions(profile, permissions);
                    return GitHubTransport.Json(new
                    {
                        token = "runtime-token-" + f.GitHub.Mints, expires_at = f.Clock.GetUtcNow().AddHours(1),
                        repository_selection = "selected", permissions,
                        repositories = new[] { new { id = 123456, full_name = "owner/project" } }
                    }, HttpStatusCode.Created);
                }
            }
            if (path.EndsWith("/installation", StringComparison.Ordinal))
                return GitHubTransport.Json(new
                {
                    id = 789, app_id = IssuancePolicy.AppId, app_slug = IssuancePolicy.AppSlug,
                    suspended_at = (string?)null,
                    permissions = new { discussions = "write", contents = "read", repository_hooks = "write", agent_variables = "read" }
                });
            return await agent(request);
        };
    }
}
