using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class AgentVariableConformanceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Which answers prove a withdrawal is the core's rule (ApprovalObserverTests); this is the completion route's outcome.
    [Fact]
    public async Task Unverifiable_approval_read_denies_completion_without_closing_and_a_fresh_positive_read_recovers()
    {
        await using var f = await AgentFixture();
        var pending = await Start(f);
        var receipt = await Complete(f, pending);
        // GitHub answers the named read with another variable, whose value the response must not disclose.
        f.GitHub.VariableAnswer = _ => GitHubRead.Found(new GitHubVariable("UNRELATED", InMemoryGitHub.PrivateValue));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var denied = await f.Send($"/v2/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        var body = await denied.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("deviceSecret", body);
        Assert.DoesNotContain(InMemoryGitHub.PrivateValue, body);

        f.GitHub.VariableAnswer = null;
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
        // Setup v1 mints its own verifier; the Agent mint handler would refuse its permissions.
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
        f.GitHub.Before = request => unknown && request.Method == HttpMethod.Post
            ? throw new HttpRequestException("Unknown legacy mint outcome") : Task.FromResult<HttpResponseMessage?>(null);
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
        if (fault == "independent-name")
            f.GitHub.Repositories = GitHubRead.Found(new InstallationRepositories(1, [new RepositoryIdentity(123456, "other/project")]));
        f.GitHub.Before = async request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                var known = Assert.Single(await Rows(f), r => r.RowKey.StartsWith("attempt:", StringComparison.Ordinal));
                accountedBeforeRevoke = known.GetString("State") == "known" && known.ContainsKey("ProtectedToken")
                    && known.GetBoolean("ScopeValid") == false;
            }
            if (request.Method != HttpMethod.Post || fault == "independent-name") return await normal(request);
            var input = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(Token));
            Assert.Equal("read", input.GetProperty("permissions").GetProperty("agent_variables").GetString());
            object permissions = fault switch
            {
                "write" => new { agent_variables = "write" },
                "extra" => new { agent_variables = "read", actions = "read" },
                _ => new { agent_variables = "read" }
            };
            return InMemoryGitHub.Json(new
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
        // Authentic legacy purpose with the same secret protection, repository, epoch,
        // pending ID and plaintext. Only the approval protocol domain differs.
        var legacy = ApprovalContract.Legacy.CompletionPurpose(RevocationFixture.RepositoryId, RevocationFixture.Epoch, pending);
        row["ProtectedCompletion"] = f.Secrets.Protect(legacy, receipt.GetProperty("enrollment").GetRawText());
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
    [InlineData("legacy-marker")]
    public async Task Confirmed_changed_binding_or_copied_legacy_marker_closes_Agent_approval(string change)
    {
        await using var f = await AgentFixture();
        var pending = await Start(f);
        await Complete(f, pending);
        var marker = f.GitHub.Marker;
        if (change == "installation") f.GitHub.Installation = Moved;
        else
        {
            f.GitHub.Agent["APPROVAL"] = marker.Replace("\"version\":2", "\"version\":1", StringComparison.Ordinal);
            Assert.NotEqual(marker, f.GitHub.Agent["APPROVAL"]);
        }
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var changed = await f.Send($"/v2/enrollments/{pending}/completion");
        Assert.DoesNotContain("deviceSecret", await changed.Content.ReadAsStringAsync(Token));
        f.GitHub.Installation = GitHubRead.Found(InMemoryGitHub.Installed);
        f.GitHub.Marker = marker;
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

    [Fact]
    public async Task Stale_changed_runtime_observation_denies_without_closure_and_fresh_approval_recovers()
    {
        await using var f = await AgentFixture();
        var receipt = await Complete(f, await Start(f));
        await Ack(f, receipt);
        var marker = f.GitHub.Marker;
        f.GitHub.Agent["APPROVAL"] = InMemoryGitHub.PrivateValue;
        var variableReads = 0;
        f.GitHub.BeforeRead = request =>
        {
            if (!request.StartsWith("variable ", StringComparison.Ordinal)) return Task.CompletedTask;
            variableReads++;
            // The changed answer is well formed but its oldest contributing read
            // is now 11 s old: outside freshness (10 s), inside the operation lifetime (15 s).
            f.Clock.Advance(TimeSpan.FromSeconds(11));
            return Task.CompletedTask;
        };
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var stale = await f.Send($"/v1/devices/{Device(receipt)}/installation-token", secret: Secret(receipt));
        Assert.NotEqual(HttpStatusCode.OK, stale.StatusCode);
        Assert.Equal(1, variableReads);
        var body = await stale.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("token", body);
        Assert.DoesNotContain(InMemoryGitHub.PrivateValue, body);

        f.GitHub.BeforeRead = null;
        f.GitHub.Marker = marker;
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var recovered = await f.Send($"/v1/devices/{Device(receipt)}/installation-token", secret: Secret(receipt));
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(Device(receipt), (await Rows(f)).Single(r => r.RowKey == "gate").GetString("OwnerDeviceId"));
    }

    // Which installation and repository answers prove a binding change is the core's rule (ApprovalObserverTests);
    // these are the runtime and continuation routes' outcomes.
    [Theory]
    [InlineData("installation", true)]
    [InlineData("installation-stale", false)]
    [InlineData("denied", false)]
    public async Task Runtime_binding_change_is_terminal_only_when_well_formed_and_fresh(string observation, bool terminal)
    {
        await using var f = await AgentFixture();
        var receipt = await Complete(f, await Start(f));
        await Ack(f, receipt);
        var observed = ObserveInstallationReads(f, stale: observation.EndsWith("-stale", StringComparison.Ordinal));
        f.GitHub.Installation = observation == "denied" ? new GitHubRead<AppInstallation>.Refused() : Moved;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var changed = await f.Send($"/v1/devices/{Device(receipt)}/installation-token", secret: Secret(receipt));
        Assert.True(observed());
        Assert.NotEqual(HttpStatusCode.OK, changed.StatusCode);
        Assert.DoesNotContain("token", await changed.Content.ReadAsStringAsync(Token));

        f.GitHub.BeforeRead = null;
        f.GitHub.Installation = GitHubRead.Found(InMemoryGitHub.Installed);
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var restored = await f.Send($"/v1/devices/{Device(receipt)}/installation-token", secret: Secret(receipt));
        Assert.Equal(terminal ? HttpStatusCode.Unauthorized : HttpStatusCode.OK, restored.StatusCode);
        if (terminal) Assert.Equal("closed", (await Rows(f)).Single(r => r.RowKey == "gate").GetString("State"));
    }

    [Theory]
    [InlineData("empty", false, false)]
    [InlineData("installation-stale", true, false)]
    [InlineData("installation-fresh", true, true)]
    [InlineData("repository-name-fresh", true, true)]
    public async Task Completion_and_ack_binding_uncertainty_is_recoverable_but_fresh_changes_are_terminal(string observation, bool acknowledge, bool terminal)
    {
        await using var f = await AgentFixture();
        var pending = await Start(f);
        var receipt = await Complete(f, pending);
        var installation = observation.StartsWith("installation-", StringComparison.Ordinal);
        var observed = installation ? ObserveInstallationReads(f, stale: observation.EndsWith("-stale", StringComparison.Ordinal))
            : ObserveRepositoryReads(f);
        if (installation) f.GitHub.Installation = Moved;
        else f.GitHub.Repositories = GitHubRead.Found(new InstallationRepositories(1,
            observation == "empty" ? [] : [new RepositoryIdentity(123456, "other/project")]));
        var continuationPath = acknowledge ? $"/v2/devices/{Device(receipt)}/acknowledgment" : $"/v2/enrollments/{pending}/completion";
        object? body = acknowledge ? new { completionId = receipt.GetProperty("completionId").GetString() } : null;
        var credential = acknowledge ? Secret(receipt) : null;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var denied = await f.Send(continuationPath, body, credential);
        Assert.True(observed());
        Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
        Assert.DoesNotContain(Secret(receipt), await denied.Content.ReadAsStringAsync(Token));

        f.GitHub.BeforeRead = null;
        f.GitHub.Installation = GitHubRead.Found(InMemoryGitHub.Installed);
        f.GitHub.Repositories = GitHubRead.Found(new InstallationRepositories(1, [InMemoryGitHub.Project]));
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

    // The same App now installed under another installation id.
    private static GitHubRead<AppInstallation> Moved => GitHubRead.Found(InMemoryGitHub.Installed with { Id = 790 });

    // Records whether the installation was read; a stale read lands 11 s late, outside freshness (10 s).
    private static Func<bool> ObserveInstallationReads(EnrollmentFixture f, bool stale)
    {
        var observed = false;
        f.GitHub.BeforeRead = request =>
        {
            if (!request.StartsWith("installation ", StringComparison.Ordinal)) return Task.CompletedTask;
            observed = true;
            if (stale) f.Clock.Advance(TimeSpan.FromSeconds(11));
            return Task.CompletedTask;
        };
        return () => observed;
    }
    private static Func<bool> ObserveRepositoryReads(EnrollmentFixture f)
    {
        var observed = false;
        f.GitHub.BeforeRead = request =>
        {
            observed |= request == "repositories";
            return Task.CompletedTask;
        };
        return () => observed;
    }

    private static async Task<EnrollmentFixture> AgentFixture()
    {
        var f = await EnrollmentFixture.CreateAsync();
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        return f;
    }
    private static RepositoryVariableEnrollmentRequest Request(string profile = "polling") => new(Guid.NewGuid().ToString("N"), 123456, "owner/project", "APPROVAL", profile);
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
