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

    // The legacy Agent location is read by name, like the Actions location.
    private const string Named = "/repos/owner/project/agents/variables/APPROVAL", Listed = "/repos/owner/project/agents/variables";

    [Theory]
    [InlineData("denied")]
    [InlineData("isolated-404")]
    [InlineData("404-without-variables-access")]
    [InlineData("malformed")]
    [InlineData("other-name")]
    [InlineData("page-contradicts-404")]
    [InlineData("malformed-page")]
    [InlineData("stale")]
    public async Task Unverifiable_named_read_denies_without_closing_and_a_fresh_positive_read_recovers(string observation)
    {
        await using var f = await AgentFixture();
        var pending = await Start(f);
        var receipt = await Complete(f, pending);
        var normal = f.GitHub.Before!;
        f.GitHub.Before = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path is not (Named or Listed)) return normal(request);
            // A 404 is absence only with a later access proof; a lost or narrowed verifier also gets 404.
            if (path == Named && observation is "isolated-404" or "404-without-variables-access" or "page-contradicts-404" or "malformed-page")
                return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.NotFound));
            if (observation == "stale") f.Clock.Advance(TimeSpan.FromSeconds(11));
            return (path, observation) switch
            {
                (Named, "denied") => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.Forbidden)),
                (Listed, "isolated-404") => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.NotFound)),
                (Listed, "404-without-variables-access") => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.Forbidden)),
                (Named, "malformed") => Reply(new { name = "APPROVAL", value = 42 }),
                (Named, "other-name") => Reply(Variable("UNRELATED", "private-provider-value")),
                (Listed, "page-contradicts-404") => Reply(Listing(1, [Variable("APPROVAL", f.GitHub.Marker)])),
                (Listed, "malformed-page") => Reply(Listing(2, Unrelated(2))),
                _ => normal(request)
            };
        };
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var denied = await f.Send($"/v2/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        Assert.DoesNotContain("deviceSecret", await denied.Content.ReadAsStringAsync(Token));
        Assert.DoesNotContain("private-provider-value", await denied.Content.ReadAsStringAsync(Token));

        f.GitHub.Before = normal;
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
            if (change == "legacy-marker" && path == Named)
                return Reply(Variable("APPROVAL", f.GitHub.Marker.Replace("\"version\":2", "\"version\":1", StringComparison.Ordinal)));
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
        var before = await Rows(f);
        using var legacyBody = await f.Send("/v2/enrollments", f.Request());
        using var agentBody = await f.Send("/v1/enrollments", Request());
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

    [Theory]
    [InlineData("changed")]
    [InlineData("absent")]
    public async Task Stale_negative_runtime_observation_denies_without_closure_and_fresh_approval_recovers(string observation)
    {
        await using var f = await AgentFixture();
        var receipt = await Complete(f, await Start(f));
        await Ack(f, receipt);
        var normal = f.GitHub.Before!;
        var variableReads = 0;
        f.GitHub.Before = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path is not (Named or Listed)) return normal(request);
            variableReads++;
            if (path == Named && observation == "absent") return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.NotFound));
            // The final response is well formed but its oldest contributing read
            // is now11s old: outside freshness10s, inside operation lifetime15s.
            f.Clock.Advance(TimeSpan.FromSeconds(11));
            return Reply(observation == "changed" ? Variable("APPROVAL", "changed-public-marker") : Listing(0, []));
        };
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var stale = await f.Send($"/v1/devices/{Device(receipt)}/installation-token", secret: Secret(receipt));
        Assert.NotEqual(HttpStatusCode.OK, stale.StatusCode);
        Assert.Equal(observation == "absent" ? 2 : 1, variableReads);
        Assert.DoesNotContain("token", await stale.Content.ReadAsStringAsync(Token));

        f.GitHub.Before = normal;
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var recovered = await f.Send($"/v1/devices/{Device(receipt)}/installation-token", secret: Secret(receipt));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(Device(receipt), (await Rows(f)).Single(r => r.RowKey == "gate").GetString("OwnerDeviceId"));
    }

    [Theory]
    [InlineData("installation", true)]
    [InlineData("repository-name", true)]
    [InlineData("repository-id", true)]
    [InlineData("installation-stale", false)]
    [InlineData("repository-name-stale", false)]
    [InlineData("repository-id-stale", false)]
    [InlineData("denied", false)]
    [InlineData("malformed", false)]
    public async Task Runtime_binding_change_is_terminal_only_when_well_formed_and_fresh(string observation, bool terminal)
    {
        await using var f = await AgentFixture();
        var receipt = await Complete(f, await Start(f));
        await Ack(f, receipt);
        var normal = f.GitHub.Before!;
        var observed = false;
        f.GitHub.Before = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (observation == "denied" && path.EndsWith("/installation", StringComparison.Ordinal))
            {
                observed = true;
                return Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.Forbidden));
            }
            if (observation.StartsWith("installation", StringComparison.Ordinal) && path.EndsWith("/installation", StringComparison.Ordinal))
            {
                observed = true;
                if (observation.EndsWith("-stale", StringComparison.Ordinal)) f.Clock.Advance(TimeSpan.FromSeconds(11));
                return Reply(new { id = 790, app_id = IssuancePolicy.AppId, app_slug = IssuancePolicy.AppSlug,
                    suspended_at = (string?)null, permissions = new { discussions = "write", agent_variables = "read" } });
            }
            if (path == "/installation/repositories")
            {
                observed = true;
                if (observation.EndsWith("-stale", StringComparison.Ordinal)) f.Clock.Advance(TimeSpan.FromSeconds(11));
                if (observation == "malformed") return Reply(new { total_count = 1, repositories = new[] { new { id = 123456 } } });
                return Reply(new { total_count = 1, repositories = new[] { new {
                    id = observation.StartsWith("repository-id", StringComparison.Ordinal) ? 999999 : 123456,
                    full_name = observation.StartsWith("repository-name", StringComparison.Ordinal) ? "other/project" : "owner/project" } } });
            }
            return normal(request);
        };
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var changed = await f.Send($"/v1/devices/{Device(receipt)}/installation-token", secret: Secret(receipt));
        Assert.True(observed);
        Assert.NotEqual(HttpStatusCode.OK, changed.StatusCode);
        Assert.DoesNotContain("token", await changed.Content.ReadAsStringAsync(Token));

        f.GitHub.Before = normal;
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var restored = await f.Send($"/v1/devices/{Device(receipt)}/installation-token", secret: Secret(receipt));
        Assert.Equal(terminal ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, restored.StatusCode);
        if (terminal) Assert.Equal("closed", (await Rows(f)).Single(r => r.RowKey == "gate").GetString("State"));
    }

    [Theory]
    [InlineData("empty", false, false)]
    [InlineData("empty", true, false)]
    [InlineData("nonpositive-id", false, false)]
    [InlineData("nonpositive-id", true, false)]
    [InlineData("null-name", false, false)]
    [InlineData("null-name", true, false)]
    [InlineData("installation-stale", false, false)]
    [InlineData("installation-stale", true, false)]
    [InlineData("repository-id-stale", false, false)]
    [InlineData("repository-id-stale", true, false)]
    [InlineData("repository-name-stale", false, false)]
    [InlineData("repository-name-stale", true, false)]
    [InlineData("installation-fresh", true, true)]
    [InlineData("repository-name-fresh", true, true)]
    public async Task Completion_and_ack_binding_uncertainty_is_recoverable_but_fresh_changes_are_terminal(string observation, bool acknowledge, bool terminal)
    {
        await using var f = await AgentFixture();
        var pending = await Start(f);
        var receipt = await Complete(f, pending);
        var normal = f.GitHub.Before!;
        var observed = false;
        f.GitHub.Before = request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (observation.StartsWith("installation-", StringComparison.Ordinal) && path.EndsWith("/installation", StringComparison.Ordinal))
            {
                observed = true;
                if (observation.EndsWith("-stale", StringComparison.Ordinal)) f.Clock.Advance(TimeSpan.FromSeconds(11));
                return Reply(new { id = 790, app_id = IssuancePolicy.AppId, app_slug = IssuancePolicy.AppSlug,
                    suspended_at = (string?)null, permissions = new { discussions = "write", agent_variables = "read" } });
            }
            if (!observation.StartsWith("installation-", StringComparison.Ordinal) && path == "/installation/repositories")
            {
                observed = true;
                if (observation.EndsWith("-stale", StringComparison.Ordinal)) f.Clock.Advance(TimeSpan.FromSeconds(11));
                object[] repositories = observation == "empty" ? [] : [new {
                    id = observation == "nonpositive-id" ? 0 : observation == "repository-id-stale" ? 999999 : 123456,
                    full_name = observation == "null-name" ? null : observation.StartsWith("repository-name-", StringComparison.Ordinal) ? "other/project" : "owner/project" }];
                return Reply(new { total_count = 1, repositories });
            }
            return normal(request);
        };
        var continuationPath = acknowledge ? $"/v2/devices/{Device(receipt)}/acknowledgment" : $"/v2/enrollments/{pending}/completion";
        object? body = acknowledge ? new { completionId = receipt.GetProperty("completionId").GetString() } : null;
        var credential = acknowledge ? Secret(receipt) : null;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var denied = await f.Send(continuationPath, body, credential);
        Assert.True(observed);
        Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
        Assert.DoesNotContain(Secret(receipt), await denied.Content.ReadAsStringAsync(Token));

        f.GitHub.Before = normal;
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var restored = await f.Send(continuationPath, body, credential);
        Assert.Equal(terminal ? HttpStatusCode.Gone : HttpStatusCode.OK, restored.StatusCode);
        if (terminal) return;
        if (!acknowledge)
        {
            Assert.Equal(receipt.ToString(), (await Json(restored)).ToString());
            await Ack(f, receipt);
        }
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var runtime = await f.Send($"/v1/devices/{Device(receipt)}/installation-token", secret: Secret(receipt));
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
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
