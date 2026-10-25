using System.Net;
using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class AgentVariableEnrollmentTests
{
    [Fact]
    public async Task Agent_approval_requires_private_ack_and_observed_removal_permanently_closes_runtime()
    {
        var f = new InMemoryBroker();
        ConfigureGitHub(f.GitHub);

        var request = new RepositoryVariableEnrollmentRequest(Guid.NewGuid().ToString("N"), 123456, "owner/project", "APPROVAL", "polling");
        var start = await f.Start(request, 2);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var pending = start.Json;
        var pendingId = pending.GetProperty("pendingId").GetString()!;
        var marker = pending.GetProperty("marker").GetString()!;
        var publicApproval = JsonSerializer.Deserialize<JsonElement>(marker);
        Assert.Equal(2, publicApproval.GetProperty("version").GetInt32());
        Assert.Equal("repository-agent-variable", publicApproval.GetProperty("approvalKind").GetString());
        Assert.Equal("repository-agent-approval-v2", publicApproval.GetProperty("policyVersion").GetString());
        Assert.False(publicApproval.TryGetProperty("environment", out _));
        Assert.False(publicApproval.TryGetProperty("environmentId", out _));
        var duplicate = await f.Start(request, 2);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Equal(start.Text, duplicate.Text);

        f.GitHub.Marker = marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var completion = await f.Complete(pendingId, 2);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var receipt = completion.Json;
        var device = receipt.GetProperty("enrollment").GetProperty("deviceId").GetString()!;
        var secret = receipt.GetProperty("enrollment").GetProperty("deviceSecret").GetString()!;
        Assert.DoesNotContain(secret, marker);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Issue(device, secret)).StatusCode);

        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await f.Complete(pendingId, 2);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(completion.Text, recovered.Text);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await f.Acknowledge(device, receipt.GetProperty("completionId").GetString()!, secret, 2)).StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(device, secret)).StatusCode);
        Assert.Equal(1, f.GitHub.VerifierMints);
        Assert.Contains("read variable agents owner/project APPROVAL", f.GitHub.Requests);

        f.GitHub.Marker = "";
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Issue(device, secret)).StatusCode);
        f.GitHub.Marker = marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Issue(device, secret)).StatusCode);
    }

    // Agent enrollments mint their verifier with exactly Agent variables read, and runtime tokens with Discussions
    // write plus, for webhook profiles, repository hooks write. The Agent variables hold github.Marker.
    internal static void ConfigureGitHub(InMemoryGitHub github) => github.BeforeMint = mint =>
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
