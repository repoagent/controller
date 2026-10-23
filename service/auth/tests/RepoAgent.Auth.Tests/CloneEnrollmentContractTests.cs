using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class CloneEnrollmentContractTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string ClonePolicy = "repository-agent-clone-approval-v2";

    [Theory]
    [InlineData("polling", 1, "repository-approval-v1")]
    [InlineData("polling", 1, "repository-agent-approval-v2")]
    [InlineData("polling-clone", 2, ClonePolicy)]
    [InlineData("webhook-clone", 2, ClonePolicy)]
    public async Task Fresh_Agent_approval_and_private_ack_enable_only_the_selected_runtime_family(
        string profile, int runtimeVersion, string policy)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var setupVersion = policy == "repository-approval-v1" ? 1 : 2;
        if (setupVersion == 2) ConfigureGitHub(f, profile);
        object request = setupVersion == 1 ? f.Request() : new
        {
            idempotencyId = Guid.NewGuid().ToString("N"), repositoryId = 123456,
            repository = "owner/project", variable = "APPROVAL", profile
        };
        using var start = await f.Send($"/v{setupVersion}/enrollments", request);
        Assert.True(start.StatusCode == HttpStatusCode.OK,
            $"POST /v{setupVersion}/enrollments profile={profile}: expected HTTP 200, actual HTTP {(int)start.StatusCode}; response={await start.Content.ReadAsStringAsync(Token)}");
        var pending = await start.Content.ReadFromJsonAsync<JsonElement>(Token);
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
        using var completion = await f.Send($"/v{setupVersion}/enrollments/{pendingId}/completion");
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
        using var ack = await f.Send($"/v{setupVersion}/devices/{device}/acknowledgment",
            new { completionId = receipt.GetProperty("completionId").GetString() }, secret);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        var beforeMismatch = await DurableVersions(f);
        var mints = f.GitHub.Mints;
        using var wrongFamily = await f.Send($"/v{3 - runtimeVersion}/devices/{device}/installation-token", secret: secret);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongFamily.StatusCode);
        using var wrongRevoke = await f.Send($"/v{3 - runtimeVersion}/devices/{device}", secret: secret, method: HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongRevoke.StatusCode);
        Assert.Equal(mints, f.GitHub.Mints);
        // No durable quota, attempt, slot or enrollment write may precede family rejection.
        Assert.Equal(beforeMismatch, await DurableVersions(f));
        JsonElement? previousLease = null;
        for (var renewal = 0; renewal < 2; renewal++)
        {
            if (renewal > 0) await f.RestartAsync();
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            using var runtime = await f.Send(route, secret: secret);
            Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
            var lease = await runtime.Content.ReadFromJsonAsync<JsonElement>(Token);
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
            using var removed = await f.Send(route, secret: secret);
            Assert.Equal(HttpStatusCode.Unauthorized, removed.StatusCode);
            f.GitHub.Marker = marker;
            await f.RestartAsync();
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            using var restored = await f.Send(route, secret: secret);
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
        await using var f = await EnrollmentFixture.CreateAsync();
        ConfigureGitHub(f, "polling-clone");
        using var start = await f.Send("/v2/enrollments", new
        {
            idempotencyId = Guid.NewGuid().ToString("N"), repositoryId = 123456,
            repository = "owner/project", variable = "APPROVAL", profile = "polling-clone"
        });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var pending = await start.Content.ReadFromJsonAsync<JsonElement>(Token);
        f.GitHub.Marker = pending.GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var completion = await f.Send($"/v2/enrollments/{pending.GetProperty("pendingId").GetString()}/completion");
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var receipt = await completion.Content.ReadFromJsonAsync<JsonElement>(Token);
        var file = receipt.GetProperty("enrollment");
        var device = file.GetProperty("deviceId").GetString()!;
        var secret = file.GetProperty("deviceSecret").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var ack = await f.Send($"/v2/devices/{device}/acknowledgment",
            new { completionId = receipt.GetProperty("completionId").GetString() }, secret);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        var row = (await f.Storage.Table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "device:" + device,
            cancellationToken: Token)).Value;
        if (value.Length == 0) row.Remove(field);
        else row[field] = value;
        await f.Storage.Table.UpdateEntityAsync(row, row.ETag, TableUpdateMode.Replace, Token);
        await f.RestartAsync();
        var before = await DurableVersions(f);
        var requests = f.GitHub.Requests.Count;
        using var denied = await f.Send($"/v2/devices/{device}/installation-token", secret: secret);
        Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
        Assert.Equal("", await denied.Content.ReadAsStringAsync(Token));
        Assert.Equal(requests, f.GitHub.Requests.Count);
        Assert.Equal(before, await DurableVersions(f));
    }

    private static async Task<string[]> DurableVersions(EnrollmentFixture f)
    {
        var versions = new List<string>();
        await foreach (var row in f.Storage.Table.QueryAsync<TableEntity>(cancellationToken: Token))
            versions.Add(row.PartitionKey + "/" + row.RowKey + ":" + row.ETag);
        return versions.Order().ToArray();
    }

    private static void AssertPermissions(string profile, JsonElement actual)
    {
        var expected = new Dictionary<string, string> { ["discussions"] = "write" };
        if (profile.EndsWith("-clone", StringComparison.Ordinal)) expected["contents"] = "read";
        if (profile == "webhook-clone") expected["repository_hooks"] = "write";
        Assert.Equal(expected.OrderBy(p => p.Key), actual.EnumerateObject()
            .Select(p => new KeyValuePair<string, string>(p.Name, p.Value.GetString()!)).OrderBy(p => p.Key));
    }

    internal static void ConfigureGitHub(EnrollmentFixture f, string profile)
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
                        token = "runtime-token-" + f.GitHub.Mints, expires_at = f.Clock.GetUtcNow().Add(f.GitHub.RuntimeLifetime),
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
