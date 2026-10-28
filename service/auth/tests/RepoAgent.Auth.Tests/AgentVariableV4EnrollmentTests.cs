using System.Net;
using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

// Setup v4 over the in-memory broker state: approval is the REPOAGENT_APPROVAL repository Agent variable, beside the
// repository settings (ADR 0012). GitHub is the in-memory fake, whose Actions and Agent variables are separate stores.
public sealed class AgentVariableV4EnrollmentTests
{
    [Fact]
    public async Task Fresh_v4_Agent_variable_approval_completes_private_enrollment_and_observed_removal_closes_runtime()
    {
        var f = new InMemoryBroker();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f.GitHub);
        var (pending, marker) = await ActionsVariableEnrollmentTests.Start(f, 4, ActionsVariableEnrollmentTests.Request());
        var approval = JsonSerializer.Deserialize<JsonElement>(marker);
        // The v2 marker's property order, with version 4 and the v4 policy.
        Assert.Equal(["version", "approvalKind", "githubHost", "appId", "appSlug", "repositoryId", "repository", "installationId",
            "deviceId", "challenge", "profile", "policyVersion", "epoch", "variable"], approval.EnumerateObject().Select(p => p.Name));
        Assert.Equal((4, "repository-agent-variable", "repository-agent-approval-v4", "REPOAGENT_APPROVAL"),
            (approval.GetProperty("version").GetInt32(), approval.GetProperty("approvalKind").GetString(),
             approval.GetProperty("policyVersion").GetString(), approval.GetProperty("variable").GetString()));

        // The marker in Actions variables is the wrong location, and a copied v2 rendering of it is not this approval.
        var v2Era = marker.Replace("\"version\":4", "\"version\":2", StringComparison.Ordinal)
            .Replace("repository-agent-approval-v4", "repository-agent-approval-v2", StringComparison.Ordinal);
        foreach (var (store, value) in new[] { (github.Actions, marker), (github.Agent, v2Era) })
        {
            github.Actions.Clear();
            github.Agent.Clear();
            store["REPOAGENT_APPROVAL"] = value;
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            var early = await f.Complete(pending, 4);
            Assert.Equal(HttpStatusCode.OK, early.StatusCode);
            Assert.Equal("pending", early.Json.GetProperty("state").GetString());
        }

        github.Actions.Clear();
        github.Agent["REPOAGENT_APPROVAL"] = marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var completion = await f.Complete(pending, 4);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var receipt = completion.Json;
        var enrollment = receipt.GetProperty("enrollment");
        var enrollmentId = enrollment.GetProperty("deviceId").GetString()!;
        var secret = enrollment.GetProperty("deviceSecret").GetString()!;
        // A frozen polling enrollment keeps the policy-free runtime v1 file.
        Assert.Equal(1, enrollment.GetProperty("protocolVersion").GetInt32());
        Assert.False(enrollment.TryGetProperty("policyVersion", out _));
        Assert.DoesNotContain(secret, marker);

        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await f.Acknowledge(enrollmentId, receipt.GetProperty("completionId").GetString()!, secret, 4)).StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var runtime = await f.Issue(enrollmentId, secret);
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        Assert.Equal(new Dictionary<string, string> { ["discussions"] = "write" }, github.RuntimePermissions[^1]);
        Assert.Contains("read variable agents owner/project REPOAGENT_APPROVAL", f.GitHub.Requests);
        Assert.DoesNotContain(f.GitHub.Requests, r => r.Contains(" actions ", StringComparison.Ordinal));
        Assert.Equal((1, "agent_variables"), (github.VerifierMints, github.VerifierPermission));

        github.Agent.Remove("REPOAGENT_APPROVAL");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Issue(enrollmentId, secret)).StatusCode);
        github.Agent["REPOAGENT_APPROVAL"] = marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Issue(enrollmentId, secret)).StatusCode);
    }

    // Setup v2 predates the settings catalogue's reserved prefix. Its accepted variable names and idempotent retries
    // remain unchanged; only fresh v4 approval shares the settings store under the reserved-name rule.
    [Fact]
    public async Task Legacy_v2_approval_keeps_its_variable_name_and_retry_contract()
    {
        var f = new InMemoryBroker();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f.GitHub);
        var request = ActionsVariableEnrollmentTests.Request() with { Variable = "REPOAGENT_MODEL" };
        var start = await f.Start(request, 2);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal(start.Text, (await f.Start(request, 2)).Text);
        github.Agent[request.Variable] = start.Json.GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var completion = await f.Complete(start.Json.GetProperty("pendingId").GetString()!, 2);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);

        var fresh = new InMemoryBroker();
        ActionsVariableEnrollmentTests.Repository.Configure(fresh.GitHub);
        Assert.Equal(HttpStatusCode.BadRequest, (await fresh.Start(request, 4)).StatusCode);
        Assert.Empty(fresh.GitHub.Requests);
    }

    // The setup version is part of the durable contract: a pending setup is only ever resumed through its own routes,
    // and one idempotency ID never replays a request across setup versions.
    [Theory]
    [InlineData(4, 2)]
    [InlineData(4, 3)]
    [InlineData(2, 4)]
    [InlineData(3, 4)]
    public async Task A_v4_setup_and_another_setup_version_never_cross(int setupVersion, int route)
    {
        var f = new InMemoryBroker();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f.GitHub);
        var request = ActionsVariableEnrollmentTests.Request();
        var (pending, marker) = await ActionsVariableEnrollmentTests.Start(f, setupVersion, request);
        (setupVersion == 3 ? github.Actions : github.Agent)["REPOAGENT_APPROVAL"] = marker;

        var (reads, mints) = (f.GitHub.Reads, f.GitHub.Mints);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Start(request, route)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Status(pending, route)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Complete(pending, route)).StatusCode);
        Assert.Equal((reads, mints), (f.GitHub.Reads, f.GitHub.Mints));

        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var completion = await f.Complete(pending, setupVersion);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var enrollment = completion.Json.GetProperty("enrollment");
        var (enrollmentId, secret) = (enrollment.GetProperty("deviceId").GetString()!, enrollment.GetProperty("deviceSecret").GetString()!);
        var completionId = completion.Json.GetProperty("completionId").GetString()!;
        reads = f.GitHub.Reads;
        Assert.Equal(HttpStatusCode.Conflict, (await f.Acknowledge(enrollmentId, completionId, secret, route)).StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await f.Acknowledge(enrollmentId, completionId, secret, setupVersion)).StatusCode);
    }
}
