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
        Assert.Contains("read variable agents owner/project APPROVAL", f.GitHub.Requests);

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

    // Agent enrollments mint their verifier with exactly Agent variables read, and runtime tokens with Discussions
    // write plus, for webhook profiles, repository hooks write. The Agent variables hold f.GitHub.Marker.
    internal static void ConfigureGitHub(EnrollmentFixture f) => f.GitHub.BeforeMint = mint =>
    {
        Assert.Equal(InMemoryGitHub.Project.Id, mint.Repository.Id);
        var permissions = mint.Authority.Permissions;
        if (permissions.ContainsKey("agent_variables"))
            Assert.Equal(new Dictionary<string, string> { ["agent_variables"] = "read" }, permissions);
        else
        {
            Assert.DoesNotContain(permissions.Keys, name => name is "actions" or "environments");
            Assert.Equal("write", permissions["discussions"]);
            if (permissions.TryGetValue("repository_hooks", out var hooks))
            {
                Assert.Equal("write", hooks);
                Assert.Equal(2, permissions.Count);
            }
            else Assert.Single(permissions);
        }
        return Task.CompletedTask;
    };

}
