using System.Net;
using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class CloneEnrollmentContractTests
{
    private const string ClonePolicy = "repository-agent-clone-approval-v2";

    [Theory]
    [InlineData("polling", 1, "repository-approval-v1")]
    [InlineData("polling", 1, "repository-agent-approval-v2")]
    [InlineData("polling-clone", 2, ClonePolicy)]
    [InlineData("webhook-clone", 2, ClonePolicy)]
    public async Task Fresh_Agent_approval_and_private_ack_enable_only_the_selected_runtime_family(
        string profile, int runtimeVersion, string policy)
    {
        var f = new InMemoryBroker();
        var setupVersion = policy == "repository-approval-v1" ? 1 : 2;
        if (setupVersion == 2) ConfigureGitHub(f.GitHub, profile);
        var start = setupVersion == 1 ? await f.Start(InMemoryBroker.Request())
            : await f.Start(new RepositoryVariableEnrollmentRequest(Guid.NewGuid().ToString("N"), 123456, "owner/project", "APPROVAL", profile), 2);
        Assert.True(start.StatusCode == HttpStatusCode.OK,
            $"Setup v{setupVersion} start profile={profile}: expected HTTP 200, actual HTTP {(int)start.StatusCode}; response={start.Text}");
        var pending = start.Json;
        var pendingId = pending.GetProperty("pendingId").GetString()!;
        var marker = pending.GetProperty("marker").GetString()!;
        var approval = JsonSerializer.Deserialize<JsonElement>(marker);
        Assert.Equal(setupVersion, approval.GetProperty("version").GetInt32());
        if (setupVersion == 2) Assert.Equal("repository-agent-variable", approval.GetProperty("approvalKind").GetString());
        else Assert.False(approval.TryGetProperty("approvalKind", out _));
        Assert.Equal(policy, approval.GetProperty("policyVersion").GetString());
        Assert.Equal(profile, approval.GetProperty("profile").GetString());

        f.GitHub.Marker = marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var completion = await f.Complete(pendingId, setupVersion);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var receipt = completion.Json;
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
        var enrollmentId = enrollment.GetProperty("deviceId").GetString()!;
        var secret = enrollment.GetProperty("deviceSecret").GetString()!;
        var unacknowledged = await f.Issue(enrollmentId, secret, runtimeVersion);
        Assert.Equal(HttpStatusCode.Unauthorized, unacknowledged.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var ack = await f.Acknowledge(enrollmentId, receipt.GetProperty("completionId").GetString()!, secret, setupVersion);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        var beforeMismatch = DurableVersions(f);
        var mints = f.GitHub.Mints;
        var wrongFamily = await f.Issue(enrollmentId, secret, 3 - runtimeVersion);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongFamily.StatusCode);
        var wrongRevoke = await f.Revoke(enrollmentId, secret, 3 - runtimeVersion);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongRevoke.StatusCode);
        Assert.Equal(mints, f.GitHub.Mints);
        // No durable quota, attempt, slot or enrollment write may precede family rejection.
        Assert.Equal(beforeMismatch, DurableVersions(f));
        JsonElement? previousLease = null;
        for (var renewal = 0; renewal < 2; renewal++)
        {
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            var runtime = await f.Issue(enrollmentId, secret, runtimeVersion);
            Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
            var lease = runtime.Json;
            Assert.Equal(runtimeVersion, lease.GetProperty("protocolVersion").GetInt32());
            Assert.Equal(123456, lease.GetProperty("repositoryId").GetInt64());
            Assert.Equal("owner/project", lease.GetProperty("repository").GetString());
            AssertPermissions(profile, lease.GetProperty("permissions"));
            Assert.False(string.IsNullOrWhiteSpace(lease.GetProperty("token").GetString()));
            if (previousLease is { } previous)
            {
                Assert.Equal(previous.GetProperty("token").GetString(), lease.GetProperty("token").GetString());
                Assert.Equal(previous.GetProperty("expiresAt").GetString(), lease.GetProperty("expiresAt").GetString());
            }
            previousLease = lease.Clone();
            if (runtimeVersion == 2)
            {
                Assert.Equal(profile, lease.GetProperty("profile").GetString());
                Assert.Equal(policy, lease.GetProperty("policyVersion").GetString());
            }
        }
        Assert.Equal(mints + 1, f.GitHub.Mints);
        if (runtimeVersion == 2)
        {
            f.GitHub.Marker = "";
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            var removed = await f.Issue(enrollmentId, secret, runtimeVersion);
            Assert.Equal(HttpStatusCode.Unauthorized, removed.StatusCode);
            f.GitHub.Marker = marker;
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            var restored = await f.Issue(enrollmentId, secret, runtimeVersion);
            Assert.Equal(HttpStatusCode.Unauthorized, restored.StatusCode);
        }
    }

    [Theory]
    [InlineData("ApprovalKind", "")]
    [InlineData("SetupVersion", "")]
    [InlineData("PolicyVersion", "repository-agent-approval-v2")]
    [InlineData("Profile", "polling")]
    public async Task Clone_durable_family_corruption_denies_before_reservation_or_provider(string field, string value)
    {
        var f = new InMemoryBroker();
        ConfigureGitHub(f.GitHub, "polling-clone");
        var start = await f.Start(new RepositoryVariableEnrollmentRequest(Guid.NewGuid().ToString("N"), 123456, "owner/project", "APPROVAL", "polling-clone"), 2);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var pending = start.Json;
        f.GitHub.Marker = pending.GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var completion = await f.Complete(pending.GetProperty("pendingId").GetString()!, 2);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var receipt = completion.Json;
        var file = receipt.GetProperty("enrollment");
        var enrollmentId = file.GetProperty("deviceId").GetString()!;
        var secret = file.GetProperty("deviceSecret").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var ack = await f.Acknowledge(enrollmentId, receipt.GetProperty("completionId").GetString()!, secret, 2);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        f.State.Change(ControllerEnrollment.Key(InMemoryBroker.RepositoryId, enrollmentId), row =>
        {
            if (value.Length == 0) row.Remove(field);
            else row[field] = value;
        });
        var before = DurableVersions(f);
        var requests = f.GitHub.Requests.Count;
        var denied = await f.Issue(enrollmentId, secret, 2);
        Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
        Assert.Equal("", denied.Text);
        Assert.Equal(requests, f.GitHub.Requests.Count);
        Assert.Equal(before, DurableVersions(f));
    }

    private static string[] DurableVersions(InMemoryBroker f) => [.. f.Rows().Select(row => row.Key + ":" + row.Version.Token)];

    private static void AssertPermissions(string profile, JsonElement actual) =>
        AssertPermissions(profile, actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!));

    private static void AssertPermissions(string profile, IReadOnlyDictionary<string, string> actual)
    {
        var expected = new Dictionary<string, string> { ["discussions"] = "write" };
        if (profile.EndsWith("-clone", StringComparison.Ordinal)) expected["contents"] = "read";
        if (profile == "webhook-clone") expected["repository_hooks"] = "write";
        Assert.Equal(expected.OrderBy(p => p.Key), actual.OrderBy(p => p.Key));
    }

    // Runtime tokens of clone enrollments carry exactly the clone profile's authority.
    internal static void ConfigureGitHub(InMemoryGitHub github, string profile)
    {
        AgentVariableEnrollmentTests.ConfigureGitHub(github);
        var agent = github.BeforeMint!;
        github.BeforeMint = mint =>
        {
            if (mint.Authority.Permissions.ContainsKey("agent_variables")) return agent(mint);
            Assert.Equal(InMemoryGitHub.Project.Id, mint.Repository.Id);
            AssertPermissions(profile, mint.Authority.Permissions);
            return Task.CompletedTask;
        };
    }
}
