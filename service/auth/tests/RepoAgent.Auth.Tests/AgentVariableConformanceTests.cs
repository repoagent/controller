using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class AgentVariableConformanceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("pagination-shift")]
    [InlineData("partial")]
    [InlineData("oversize")]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    [InlineData("denied")]
    [InlineData("404")]
    [InlineData("stale")]
    public async Task Unverifiable_listing_denies_without_closing_and_page_two_positive_can_recover(string observation)
    {
        await using var f = await AgentFixture();
        var pending = await Start(f);
        var receipt = await Complete(f, pending);
        var normal = f.GitHub.Before!;
        var pages = new List<int>();
        f.GitHub.Before = request =>
        {
            if (request.RequestUri!.AbsolutePath != "/repos/owner/project/agents/variables") return normal(request);
            var page = request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal) ? 2 : 1;
            pages.Add(page);
            // Initially the unchanged approval is at position31. An unrelated early
            // deletion shifts it into the already-read first page; a new last item
            // preserves total31 and unique names across the two responses.
            if (observation is "pagination-shift" or "duplicate" or "stale")
            {
                if (page == 1) return Reply(Listing(31, Unrelated(30)));
                if (observation == "stale") f.Clock.Advance(TimeSpan.FromSeconds(11));
                return Reply(Listing(31, observation == "duplicate" ? [Variable("N00", "unrelated")]
                    : observation == "stale" ? [Variable("APPROVAL", f.GitHub.Marker)] : [Variable("ZZ_NEW", "unrelated")]));
            }
            return observation switch
            {
                "denied" => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.Forbidden)),
                "404" => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.NotFound)),
                "oversize" => Reply(Listing(61, Unrelated(30))),
                "malformed" => Reply(Listing(1, [new { name = "APPROVAL", value = 42 }])),
                _ => Reply(Listing(2, [Variable("UNRELATED", "private-provider-value")]))
            };
        };
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var denied = await f.Send($"/v2/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        Assert.DoesNotContain("deviceSecret", await denied.Content.ReadAsStringAsync(Token));
        Assert.DoesNotContain("private-provider-value", await denied.Content.ReadAsStringAsync(Token));
        if (observation is "pagination-shift" or "duplicate" or "stale") Assert.Equal([1, 2], pages);

        // A positive selected value on page2 is usable, unlike nonappearance there.
        f.GitHub.Before = request => request.RequestUri!.AbsolutePath == "/repos/owner/project/agents/variables"
            ? Reply(request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal)
                ? Listing(31, [Variable("APPROVAL", f.GitHub.Marker)]) : Listing(31, Unrelated(30)))
            : normal(request);
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var recovered = await f.Send($"/v2/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(receipt.ToString(), (await Json(recovered)).ToString());
        await Ack(f, receipt);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var runtime = await f.Send($"/v1/devices/{Device(receipt)}/installation-token", secret: Secret(receipt));
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        var result = await Json(runtime);
        Assert.Equal(1, result.GetProperty("protocolVersion").GetInt32());
        Assert.False(result.GetProperty("permissions").TryGetProperty("agent_variables", out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Versions_share_idempotency_but_cannot_replay_setup_completion_or_ack(int version)
    {
        await using var f = await AgentFixture();
        var agent = Request();
        var legacy = f.Request() with { IdempotencyId = agent.IdempotencyId };
        var agentHandler = f.GitHub.Before!;
        if (version == 1) f.GitHub.Before = null;
        using var start = await f.Send($"/v{version}/enrollments", version == 1 ? legacy : agent);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var body = await Json(start);
        var pending = body.GetProperty("pendingId").GetString()!;
        f.GitHub.Marker = body.GetProperty("marker").GetString()!;
        var mints = f.GitHub.Mints;
        using var crossed = await f.Send($"/v{3 - version}/enrollments", version == 1 ? agent : legacy);
        Assert.Equal(HttpStatusCode.Conflict, crossed.StatusCode);
        var reads = f.GitHub.Reads;
        using var status = await f.Send($"/v{3 - version}/enrollments/{pending}", method: HttpMethod.Get);
        using var completion = await f.Send($"/v{3 - version}/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.Conflict, status.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, completion.StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
        Assert.Equal(mints, f.GitHub.Mints);
        var rows = await Rows(f);
        Assert.Single(rows, r => r.PartitionKey == RevocationFixture.Partition && r.RowKey.StartsWith("pending:", StringComparison.Ordinal));
        if (version == 1)
            f.GitHub.Before = request => request.RequestUri!.AbsolutePath.EndsWith("/variables", StringComparison.Ordinal)
                ? Reply(Listing(1, [Variable("APPROVAL", f.GitHub.Marker)])) : Task.FromResult<HttpResponseMessage?>(null);
        else f.GitHub.Before = agentHandler;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var receipt = await Complete(f, pending, version);
        reads = f.GitHub.Reads;
        using var ack = await f.Send($"/v{3 - version}/devices/{Device(receipt)}/acknowledgment",
            new { completionId = receipt.GetProperty("completionId").GetString() }, Secret(receipt));
        Assert.Equal(HttpStatusCode.Conflict, ack.StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var recovered = await f.Send($"/v{version}/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(receipt.ToString(), (await Json(recovered)).ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_incumbent_or_unknown_authority_blocks_Agent_completion(bool unknown)
    {
        await using var f = await AgentFixture();
        var agentHandler = f.GitHub.Before!;
        f.GitHub.Before = request =>
        {
            if (unknown && request.Method == HttpMethod.Post) throw new HttpRequestException("Unknown legacy mint outcome");
            return request.RequestUri!.AbsolutePath.EndsWith("/variables", StringComparison.Ordinal)
                ? Reply(Listing(1, [Variable("APPROVAL", f.GitHub.Marker)])) : Task.FromResult<HttpResponseMessage?>(null);
        };
        using var legacy = await f.Send("/v1/enrollments", f.Request());
        Assert.Equal(unknown ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK, legacy.StatusCode);
        string? incumbent = null;
        if (!unknown)
        {
            var body = await Json(legacy);
            f.GitHub.Marker = body.GetProperty("marker").GetString()!;
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            incumbent = Device(await Complete(f, body.GetProperty("pendingId").GetString()!, 1));
        }
        var before = (await Rows(f)).Single(r => r.RowKey.StartsWith("attempt:", StringComparison.Ordinal));
        f.GitHub.Before = agentHandler;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var pending = await Start(f);
        var reads = f.GitHub.Reads;
        using var completion = await f.Send($"/v2/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.Conflict, completion.StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
        var rows = await Rows(f);
        Assert.Equal(before.ETag, rows.Single(r => r.RowKey == before.RowKey && r.PartitionKey == before.PartitionKey).ETag);
        if (unknown) Assert.DoesNotContain(rows, r => r.RowKey.StartsWith("device:", StringComparison.Ordinal));
        else Assert.Equal(incumbent, rows.Single(r => r.RowKey == "gate").GetString("OwnerDeviceId"));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("extra")]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("independent-name")]
    public async Task Invalid_Agent_verifier_binding_never_discloses_marker_and_known_tokens_stay_accounted(string fault)
    {
        await using var f = await AgentFixture();
        var normal = f.GitHub.Before!;
        var accountedBeforeRevoke = false;
        f.GitHub.Before = async request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                var known = Assert.Single(await Rows(f), r => r.RowKey.StartsWith("attempt:", StringComparison.Ordinal));
                accountedBeforeRevoke = known.GetString("State") == "known" && known.ContainsKey("ProtectedToken")
                    && known.GetBoolean("ScopeValid") == false;
            }
            if (fault == "independent-name" && request.RequestUri!.AbsolutePath == "/installation/repositories")
                return GitHubTransport.Json(new { total_count = 1, repositories = new[] { new { id = 123456, full_name = "other/project" } } });
            if (request.Method != HttpMethod.Post || fault == "independent-name") return await normal(request);
            var input = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(Token));
            Assert.Equal("read", input.GetProperty("permissions").GetProperty("agent_variables").GetString());
            object permissions = fault switch
            {
                "write" => new { agent_variables = "write" },
                "extra" => new { agent_variables = "read", actions = "read" },
                _ => new { agent_variables = "read" }
            };
            return GitHubTransport.Json(new
            {
                token = "invalid-binding-token", expires_at = f.Clock.GetUtcNow().AddHours(1),
                repository_selection = "selected", permissions,
                repositories = new[] { new { id = fault == "id" ? 999999 : 123456, full_name = fault == "name" ? "other/project" : "owner/project" } }
            }, HttpStatusCode.Created);
        };
        using var start = await f.Send("/v2/enrollments", Request());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, start.StatusCode);
        Assert.Equal("", await start.Content.ReadAsStringAsync(Token));
        var attempt = Assert.Single(await Rows(f), r => r.RowKey.StartsWith("attempt:", StringComparison.Ordinal));
        Assert.DoesNotContain(await Rows(f), r => r.RowKey.StartsWith("device:", StringComparison.Ordinal));
        if (fault != "independent-name")
        {
            Assert.True(accountedBeforeRevoke);
            Assert.Equal(1, f.GitHub.Revokes);
            Assert.Equal("settled", attempt.GetString("State"));
        }
        else Assert.True(attempt.ContainsKey("ProtectedToken"));
    }

    [Theory]
    [InlineData("missing-both")]
    [InlineData("missing-kind")]
    [InlineData("missing-version")]
    [InlineData("unknown-kind")]
    [InlineData("wrong-policy")]
    public async Task Incomplete_or_conflicting_durable_discriminators_cannot_select_Agent_runtime(string fault)
    {
        await using var f = await AgentFixture();
        var receipt = await Complete(f, await Start(f));
        await Ack(f, receipt);
        var row = (await f.Storage.Table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "device:" + Device(receipt), cancellationToken: Token)).Value;
        if (fault == "missing-both") { row.Remove("ApprovalKind"); row.Remove("SetupVersion"); }
        if (fault == "missing-kind") row.Remove("ApprovalKind");
        if (fault == "missing-version") row.Remove("SetupVersion");
        if (fault == "unknown-kind") row["ApprovalKind"] = "unknown";
        if (fault == "wrong-policy") row["PolicyVersion"] = IssuancePolicy.Version;
        await f.Storage.Table.UpdateEntityAsync(row, row.ETag, TableUpdateMode.Replace, Token);
        await f.RestartAsync();
        var reads = f.GitHub.Reads;
        using var denied = await f.Send($"/v1/devices/{Device(receipt)}/installation-token", secret: Secret(receipt));
        Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
        Assert.Equal("", await denied.Content.ReadAsStringAsync(Token));
        Assert.Equal(reads, f.GitHub.Reads);
    }

    [Theory]
    [InlineData("polling")]
    [InlineData("webhook")]
    public async Task V1_runtime_and_self_revoke_dispatch_from_authenticated_Agent_device(string profile)
    {
        await using var f = await AgentFixture();
        var receipt = await Complete(f, await Start(f, profile));
        await Ack(f, receipt);
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var issued = await f.Send($"/v1/devices/{Device(receipt)}/installation-token", secret: Secret(receipt));
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var permissions = (await Json(issued)).GetProperty("permissions");
        Assert.Equal("write", permissions.GetProperty("discussions").GetString());
        Assert.Equal(profile == "webhook", permissions.TryGetProperty("repository_hooks", out _));
        Assert.Equal(profile == "webhook" ? 2 : 1, permissions.EnumerateObject().Count());
        using var revoked = await f.Send($"/v1/devices/{Device(receipt)}", secret: Secret(receipt), method: HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var runtime = await f.Send($"/v1/devices/{Device(receipt)}/installation-token", secret: Secret(receipt));
        Assert.Equal(HttpStatusCode.Unauthorized, runtime.StatusCode);
        var rows = await Rows(f);
        Assert.Equal("closed", rows.Single(r => r.RowKey == "gate").GetString("State"));
        Assert.Equal("revoked", rows.Single(r => r.RowKey == "device:" + Device(receipt)).GetString("State"));
    }

    [Fact]
    public async Task Legacy_protected_completion_cannot_recover_Agent_receipt_even_with_same_keys_and_binding()
    {
        await using var f = await AgentFixture();
        var pending = await Start(f);
        var receipt = await Complete(f, pending);
        var rowKey = "pending:" + pending.Split('_')[1];
        var row = (await f.Storage.Table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, rowKey, cancellationToken: Token)).Value;
        var original = row.GetString("ProtectedCompletion")!;
        // Authentic legacy purpose with the same real key ring, repository, epoch,
        // pending ID and plaintext. Only the approval protocol domain differs.
        var legacy = f.Protection.CreateProtector("repoagent.enrollment.v1", RevocationFixture.RepositoryId,
            RevocationFixture.Epoch, pending);
        row["ProtectedCompletion"] = legacy.Protect(receipt.GetProperty("enrollment").GetRawText());
        await f.Storage.Table.UpdateEntityAsync(row, row.ETag, TableUpdateMode.Replace, Token);
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var denied = await f.Send($"/v2/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        Assert.DoesNotContain(Secret(receipt), await denied.Content.ReadAsStringAsync(Token));
        row = (await f.Storage.Table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, rowKey, cancellationToken: Token)).Value;
        row["ProtectedCompletion"] = original;
        await f.Storage.Table.UpdateEntityAsync(row, row.ETag, TableUpdateMode.Replace, Token);
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var recovered = await f.Send($"/v2/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(receipt.ToString(), (await Json(recovered)).ToString());
    }

    [Theory]
    [InlineData("installation")]
    [InlineData("repository-id")]
    [InlineData("repository-name")]
    [InlineData("legacy-marker")]
    public async Task Confirmed_changed_binding_or_copied_legacy_marker_closes_Agent_approval(string change)
    {
        await using var f = await AgentFixture();
        var pending = await Start(f);
        await Complete(f, pending);
        var normal = f.GitHub.Before!;
        f.GitHub.Before = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (change == "installation" && path.EndsWith("/installation", StringComparison.Ordinal))
                return Reply(new { id = 790, app_id = IssuancePolicy.AppId, app_slug = IssuancePolicy.AppSlug,
                    suspended_at = (string?)null, permissions = new { discussions = "write", agent_variables = "read" } });
            if (change.StartsWith("repository-", StringComparison.Ordinal) && path == "/installation/repositories")
                return Reply(new { total_count = 1, repositories = new[] { new {
                    id = change == "repository-id" ? 999999 : 123456,
                    full_name = change == "repository-name" ? "other/project" : "owner/project" } } });
            if (change == "legacy-marker" && path == "/repos/owner/project/agents/variables")
                return Reply(Listing(1, [Variable("APPROVAL", f.GitHub.Marker.Replace("\"version\":2", "\"version\":1", StringComparison.Ordinal))]));
            return normal(request);
        };
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var changed = await f.Send($"/v2/enrollments/{pending}/completion");
        Assert.DoesNotContain("deviceSecret", await changed.Content.ReadAsStringAsync(Token));
        f.GitHub.Before = normal;
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var restored = await f.Send($"/v2/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.Gone, restored.StatusCode);
    }

    [Fact]
    public async Task Setup_version_requires_its_own_body_without_provider_work()
    {
        await using var f = await AgentFixture();
        using var legacyBody = await f.Send("/v2/enrollments", f.Request());
        using var agentBody = await f.Send("/v1/enrollments", Request());
        Assert.Equal(HttpStatusCode.BadRequest, legacyBody.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, agentBody.StatusCode);
        Assert.Equal(0, f.GitHub.Reads);
        Assert.Equal(0, f.GitHub.Mints);
        Assert.Empty(await Rows(f));
    }

    private static async Task<EnrollmentFixture> AgentFixture()
    {
        var f = await EnrollmentFixture.CreateAsync();
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        return f;
    }
    private static AgentEnrollmentRequest Request(string profile = "polling") => new(Guid.NewGuid().ToString("N"), 123456, "owner/project", "APPROVAL", profile);
    private static object Variable(string name, string value) => new { name, value };
    private static object[] Unrelated(int count) => Enumerable.Range(0, count).Select(i => Variable($"N{i:00}", "unrelated")).ToArray();
    private static object Listing(int total, object[] variables) => new { total_count = total, variables };
    private static Task<HttpResponseMessage?> Reply(object body) => Task.FromResult<HttpResponseMessage?>(GitHubTransport.Json(body));
    private static string Device(JsonElement receipt) => receipt.GetProperty("enrollment").GetProperty("deviceId").GetString()!;
    private static string Secret(JsonElement receipt) => receipt.GetProperty("enrollment").GetProperty("deviceSecret").GetString()!;
    private static Task<JsonElement> Json(HttpResponseMessage response) => response.Content.ReadFromJsonAsync<JsonElement>(Token);
    private static async Task<string> Start(EnrollmentFixture f, string profile = "polling")
    {
        using var start = await f.Send("/v2/enrollments", Request(profile));
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var body = await Json(start);
        f.GitHub.Marker = body.GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        return body.GetProperty("pendingId").GetString()!;
    }
    private static async Task<JsonElement> Complete(EnrollmentFixture f, string pending, int version = 2)
    {
        using var response = await f.Send($"/v{version}/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await Json(response);
        Assert.Equal(1, receipt.GetProperty("enrollment").GetProperty("protocolVersion").GetInt32());
        return receipt;
    }
    private static async Task Ack(EnrollmentFixture f, JsonElement receipt)
    {
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var response = await f.Send($"/v2/devices/{Device(receipt)}/acknowledgment",
            new { completionId = receipt.GetProperty("completionId").GetString() }, Secret(receipt));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
    private static async Task<List<TableEntity>> Rows(EnrollmentFixture f)
    {
        var rows = new List<TableEntity>();
        await foreach (var row in f.Storage.Table.QueryAsync<TableEntity>(cancellationToken: Token)) rows.Add(row);
        return rows;
    }
}
