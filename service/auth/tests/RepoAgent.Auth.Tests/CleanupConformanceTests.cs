using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed partial class CleanupProcessTests
{
    private static async Task<TableEntity> Read(RevocationFixture f, string partition, string key) =>
        (await f.CreateClient().GetEntityAsync<TableEntity>(partition, key, cancellationToken: Token)).Value;
    private static async Task<List<TableEntity>> Rows(RevocationFixture f, string partition, string prefix)
    {
        var rows = new List<TableEntity>();
        await foreach (var row in f.Table.QueryAsync<TableEntity>(
            TableClient.CreateQueryFilter($"PartitionKey eq {partition} and RowKey ge {prefix} and RowKey lt {prefix + "~"}"), cancellationToken: Token)) rows.Add(row);
        return rows;
    }
    private static Task<HttpResponseMessage> Start(EnrollmentFixture f, string id, string profile = "polling") =>
        f.Send("/v2/enrollments", new { idempotencyId = id, repositoryId = 123456, repository = "owner/project", variable = "APPROVAL", profile });
    private static BrokerCapacityReclamation Reclaim(EnrollmentFixture f) => new(f.Storage.Table,
        new(new Uri("https://example.table.core.windows.net"), f.Storage.TableName, RevocationFixture.Epoch, null, 120, 8, 15), f.Clock);
    private static void AssertSdkAccounting(JsonElement summary)
    {
        var dispatches = summary.GetProperty("sdkTableDispatches").GetInt32();
        var batches = summary.GetProperty("sdkBatchDispatches").GetInt32();
        Assert.InRange(dispatches, 0, 128);
        Assert.InRange(batches, 0, 32);
        Assert.InRange(batches, 0, dispatches);
        Assert.InRange(summary.GetProperty("logicalQueryPages").GetInt32(), 0, 16);
        foreach (var oldName in new[] { "tableRequests", "egts", "pages" }) Assert.False(summary.TryGetProperty(oldName, out _));
        var envelope = summary.GetProperty("reviewedHttp11Envelope");
        Assert.Equal("Azure.Core1.62.0/.NET10.0.13/direct HTTP1.x/no handler authentication", envelope.GetProperty("scope").GetString());
        Assert.Equal(4, envelope.GetProperty("maxSendsPerSdkDispatch").GetInt32());
        Assert.Equal(4 * dispatches, envelope.GetProperty("tableRequestUpperBound").GetInt32());
        Assert.Equal(4 * batches, envelope.GetProperty("batchRequestUpperBound").GetInt32());
        Assert.InRange(envelope.GetProperty("tableRequestUpperBound").GetInt32(), 0, 512);
        Assert.InRange(envelope.GetProperty("batchRequestUpperBound").GetInt32(), 0, 128);
    }

    private static void AssertBudget(JsonElement summary, CleanupTableProxy proxy)
    {
        AssertSdkAccounting(summary);
        // These existing cases do not inject bodyless-GET EOF replay. Equality here
        // is a scoped observation; the separate replay case deliberately has extra sends.
        Assert.Equal(proxy.Requests.Count, summary.GetProperty("sdkTableDispatches").GetInt32());
        Assert.InRange(proxy.Requests.Count, 1, 128);
        Assert.Equal(proxy.Requests.Count(r => r.Path.EndsWith("/$batch", StringComparison.Ordinal)), summary.GetProperty("sdkBatchDispatches").GetInt32());
        Assert.InRange(summary.GetProperty("sdkBatchDispatches").GetInt32(), 0, 32);
        var queries = proxy.Requests.Where(r => r.Method == "GET" && r.Query.Contains("$filter", StringComparison.Ordinal)).ToList();
        Assert.Equal(queries.Count, summary.GetProperty("logicalQueryPages").GetInt32());
        Assert.InRange(queries.Count, 0, 16);
        Assert.All(proxy.PageSizes, size => Assert.InRange(size, 0, 32));
        foreach (var request in queries)
        {
            var query = Uri.UnescapeDataString(request.Query.Replace("+", " "));
            Assert.Contains("$top=32", query);
            Assert.Contains("PartitionKey eq 'global:", query);
            Assert.Contains("RowKey ge '", query);
            Assert.Contains("RowKey lt '", query);
            Assert.Contains("RowKey gt '", query);
        }
        Assert.InRange(summary.GetProperty("elapsedMilliseconds").GetInt64(), 0, 60_000);
    }

    [Theory]
    [InlineData("polling", false)]
    [InlineData("polling", true)]
    [InlineData("polling-clone", false)]
    [InlineData("polling-clone", true)]
    public async Task Completion_and_token_are_removed_but_active_acknowledged_device_and_policy_survive(string profile, bool acknowledge)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        CloneEnrollmentContractTests.ConfigureGitHub(f, profile);
        f.Clock.Advance(TimeSpan.FromDays(-2));
        var id = Guid.NewGuid().ToString("N");
        using var start = await Start(f, id, profile);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var started = await start.Content.ReadFromJsonAsync<JsonElement>(Token);
        f.GitHub.Marker = started.GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var completion = await f.Send($"/v2/enrollments/{started.GetProperty("pendingId").GetString()}/completion");
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var receipt = await completion.Content.ReadFromJsonAsync<JsonElement>(Token);
        var device = receipt.GetProperty("enrollment");
        var deviceId = device.GetProperty("deviceId").GetString()!;
        var secret = device.GetProperty("deviceSecret").GetString()!;
        if (acknowledge)
        {
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            using var ack = await f.Send($"/v2/devices/{deviceId}/acknowledgment", new { completionId = receipt.GetProperty("completionId").GetString() }, secret);
            Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        }
        var key = "pending:" + id;
        var pending = await Read(f.Storage, RevocationFixture.Partition, key);
        var active = await Read(f.Storage, RevocationFixture.Partition, "device:" + deviceId);
        Assert.Equal(!acknowledge, pending.ContainsKey("ProtectedCompletion"));
        Assert.Equal(acknowledge, active.GetDateTimeOffset("ExpiresAt") > DateTimeOffset.UtcNow);
        var ledgers = await Rows(f.Storage, "global:issuance-v1", "operation:");
        Assert.NotEmpty(ledgers);
        Assert.Contains(ledgers, l => f.Storage.Table.GetEntity<TableEntity>(RevocationFixture.Partition, l.GetString("AttemptRowKey")!, cancellationToken: Token).Value.ContainsKey("ProtectedToken"));
        await f.StopHttpAsync();
        for (var run = 0; run < 8; run++)
        {
            var result = await CleanupProcessFixture.RunAsync(f.Storage);
            Assert.Equal(0, result.Exit);
            if (!result.Summary.GetProperty("incompleteCoverage").GetBoolean()) break;
        }
        var after = await Read(f.Storage, RevocationFixture.Partition, key);
        Assert.False(after.ContainsKey("ProtectedCompletion"));
        Assert.False(after.ContainsKey("RecoveryHash"));
        var preserved = await Read(f.Storage, active.PartitionKey, active.RowKey);
        foreach (var field in new[] { "ExpiresAt", "PolicyVersion", "Profile", "Acknowledged" })
            Assert.Equal(active[field], preserved[field]);
        if (acknowledge)
        {
            Assert.Equal(active.GetString("SecretHash"), preserved.GetString("SecretHash"));
            Assert.Equal(active.GetDateTimeOffset("LastActivityAt"), preserved.GetDateTimeOffset("LastActivityAt"));
        }
        else Assert.False(preserved.ContainsKey("SecretHash"));
        foreach (var ledger in ledgers)
        {
            var attempt = await Read(f.Storage, RevocationFixture.Partition, ledger.GetString("AttemptRowKey")!);
            Assert.False(attempt.ContainsKey("ProtectedToken"));
            Assert.Equal(ledger.GetString("PolicyVersion"), attempt.GetString("PolicyVersion"));
            Assert.Equal(ledger.GetDateTimeOffset("PayloadPurgeAt"), attempt.GetDateTimeOffset("PayloadPurgeAt"));
        }
        if (acknowledge)
        {
            // Prove provider/signing setup and the current grant work before testing the epoch fence.
            f.Clock.Advance(DateTimeOffset.UtcNow - f.Clock.GetUtcNow());
            await f.RestartAsync(epoch: RevocationFixture.Epoch);
            var route = $"/v{(profile == "polling-clone" ? 2 : 1)}/devices/{deviceId}/installation-token";
            using var control = await f.Send(route, secret: secret);
            Assert.Equal(HttpStatusCode.OK, control.StatusCode);
            var mintsBeforeRestore = f.GitHub.Mints;
            var signsBeforeRestore = f.Signing.Calls;
            await f.RestartAsync(epoch: "restored-epoch");
            using var denied = await f.Send(route, secret: secret);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
            Assert.Equal(mintsBeforeRestore, f.GitHub.Mints);
            Assert.Equal(signsBeforeRestore, f.Signing.Calls);
            output.WriteLine("Matching current epoch issued HTTP200; restored epoch denied HTTP503 with zero additional signing/mint calls.");
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task New_epoch_cleanup_redacts_payload_without_settling_unknown_authority(bool missingDeadline, bool sending)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        var normal = f.GitHub.Before!;
        f.GitHub.Before = request => request.Method == HttpMethod.Delete
            ? throw new HttpRequestException("Unobserved revocation")
            : request.Method == HttpMethod.Post
                ? sending ? throw new HttpRequestException("Unknown send outcome")
                    : Task.FromResult<HttpResponseMessage?>(GitHubTransport.Json(new { token = "unsettled-test-token", expires_at = "invalid" }, HttpStatusCode.Created))
                : normal(request);
        f.Clock.Advance(TimeSpan.FromDays(-2));
        using var start = await Start(f, Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, start.StatusCode);
        var ledger = Assert.Single(await Rows(f.Storage, "global:issuance-v1", "operation:"));
        var attempt = await Read(f.Storage, RevocationFixture.Partition, ledger.GetString("AttemptRowKey")!);
        Assert.Equal(!sending, attempt.ContainsKey("ProtectedToken"));
        Assert.Equal(sending ? "sending" : "known", attempt.GetString("State"));
        if (missingDeadline)
        {
            attempt.Remove("ReservedAt"); attempt.Remove("PayloadPurgeAt");
            await f.Storage.Table.UpdateEntityAsync(attempt, attempt.ETag, TableUpdateMode.Replace, Token);
        }
        var quota = await Read(f.Storage, "global:issuance-v1", "quota");
        await f.StopHttpAsync();
        var unknownObserved = 0;
        var unsettledObserved = 0;
        for (var run = 0; run < 8; run++)
        {
            var result = await CleanupProcessFixture.RunAsync(f.Storage, epoch: "new-epoch");
            Assert.Equal(0, result.Exit);
            unknownObserved += result.Summary.GetProperty("unknownDeadlineCount").GetInt32();
            unsettledObserved += result.Summary.GetProperty("unsettledAllocationsObserved").GetInt32();
            if (!result.Summary.GetProperty("incompleteCoverage").GetBoolean()) break;
        }
        var after = await Read(f.Storage, attempt.PartitionKey, attempt.RowKey);
        Assert.False(after.ContainsKey("ProtectedToken"));
        Assert.False(after.ContainsKey("SettledAt"));
        foreach (var field in attempt.Keys.Except(new[] { "ProtectedToken", "Timestamp", "odata.etag" })) Assert.Equal(attempt[field], after[field]);
        var charged = await Read(f.Storage, quota.PartitionKey, quota.RowKey);
        Assert.Equal(quota.GetInt64("Slots"), charged.GetInt64("Slots"));
        Assert.Equal(quota.GetInt64("RetainedIssuanceCount"), charged.GetInt64("RetainedIssuanceCount"));
        Assert.True(unsettledObserved > 0);
        if (missingDeadline) Assert.True(unknownObserved > 0);
    }

    [Fact]
    public async Task Failed_stream_keeps_its_position_while_other_family_redacts_and_transport_counts_remain_honest()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        f.Clock.Advance(TimeSpan.FromDays(-2));
        using var start = await Start(f, Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var attemptLedger = Assert.Single(await Rows(f.Storage, "global:issuance-v1", "operation:"));
        await f.StopHttpAsync();
        await using var proxy = new CleanupTableProxy(f.Storage);
        proxy.Before = r => Task.FromResult(!Uri.UnescapeDataString(r.Query).Contains("pending:", StringComparison.Ordinal));
        var result = await CleanupProcessFixture.RunAsync(f.Storage, proxy.Endpoint);
        Assert.Equal(1, result.Exit);
        Assert.True(result.Summary.GetProperty("incompleteCoverage").GetBoolean());
        Assert.True(result.Summary.GetProperty("failures").GetInt32() > 0);
        AssertBudget(result.Summary, proxy);
        var cursor = await Read(f.Storage, "global:enrollment-v1", "cleanup:payload-v1");
        Assert.Equal("", cursor.GetString("EnrollmentLastKey"));
        var issuance = await Read(f.Storage, RevocationFixture.Partition, attemptLedger.GetString("AttemptRowKey")!);
        Assert.False(issuance.ContainsKey("ProtectedToken"));
        output.WriteLine("Failed enrollment queries did not advance that stream; issuance payload progressed. Actual requests={0}, batches={1}.", proxy.Requests.Count, result.Summary.GetProperty("sdkBatchDispatches"));
    }
    [Fact]
    public async Task Real_backlog_beyond_one_page_resumes_across_processes_and_includes_behind_cursor_insertions_next_cycle()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        f.Clock.Advance(TimeSpan.FromDays(-6));
        var keys = new List<string>();
        for (var i = 1; i <= 35; i++)
        {
            var id = i.ToString("x32");
            using var start = await Start(f, id);
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
            var ledger = await Read(f.Storage, "global:enrollment-v1", "pending:" + id);
            keys.Add(ledger.RowKey);
            // Authentic prior capacity release leaves retained audit and due payload behind.
            f.Clock.Advance(TimeSpan.FromHours(2));
            await Reclaim(f).ReclaimEnrollmentAsync(id, ledger.GetString("Generation")!, Token);
            foreach (var issuance in await Rows(f.Storage, "global:issuance-v1", "operation:"))
                if (issuance.GetString("CapacityState") != "released")
                    await Reclaim(f).ReclaimIssuanceAsync(issuance.GetString("OperationId")!, issuance.GetString("Generation")!, Token);
        }
        await f.StopHttpAsync();
        string? firstLast = null;
        var budgetStops = 0;
        var sawFullPage = false;
        var behind = "pending:" + new string('0', 32);
        for (var run = 0; run < 80; run++)
        {
            await using var proxy = new CleanupTableProxy(f.Storage);
            var result = await CleanupProcessFixture.RunAsync(f.Storage, proxy.Endpoint);
            Assert.Equal(0, result.Exit);
            AssertBudget(result.Summary, proxy);
            sawFullPage |= proxy.PageSizes.Contains(32);
            foreach (var ages in result.Summary.GetProperty("cursorAndCycleAges").EnumerateArray())
                Assert.True(ages.GetProperty("cycleAgeSeconds").GetDouble() >= ages.GetProperty("cursorAgeSeconds").GetDouble());
            if (result.Summary.GetProperty("stopReason").GetString() == "request-budget")
            {
                budgetStops++;
                Assert.True(result.Summary.GetProperty("incompleteCoverage").GetBoolean());
                // A whole 40-request turn cannot be admitted once actual use exceeds 88.
                Assert.InRange(result.Summary.GetProperty("sdkTableDispatches").GetInt32(), 89, 128);
            }
            var cursor = await Read(f.Storage, "global:enrollment-v1", "cleanup:payload-v1");
            firstLast ??= cursor.GetString("EnrollmentLastKey");
            if (run == 0)
            {
                Assert.False(string.IsNullOrEmpty(firstLast));
                await f.RestartAsync();
                using var inserted = await Start(f, new string('0', 32));
                Assert.Equal(HttpStatusCode.OK, inserted.StatusCode);
                await f.StopHttpAsync();
            }
            Assert.True((await Read(f.Storage, "global:enrollment-v1", behind)).ContainsKey("RecoveryHash"),
                "A behind-cursor insertion must wait for the next traversal cycle.");
            if (cursor.GetInt64("EnrollmentCycle") > 0) break;
            Assert.True(run < 79, "Bounded process restarts must eventually finish a finite enrollment cycle.");
        }
        Assert.True(budgetStops > 0);
        Assert.True(sawFullPage, "At least one real query must return the full 32-entity page.");
        Assert.False(string.IsNullOrEmpty(firstLast));
        foreach (var key in keys)
            Assert.False((await Read(f.Storage, "global:enrollment-v1", key)).ContainsKey("RecoveryHash"));
        var fixedRows = await Rows(f.Storage, "global:enrollment-v1", "cleanup:");
        Assert.Equal(2, fixedRows.Count);
        Assert.All(fixedRows, row => Assert.Contains(row.RowKey, new[] { "cleanup:payload-v1", "cleanup:audit-v1" }));

        // The first cycle reset its key; the second must include the inserted smaller key.
        for (var run = 0; run < 80; run++)
        {
            var result = await CleanupProcessFixture.RunAsync(f.Storage);
            Assert.Equal(0, result.Exit);
            if (!(await Read(f.Storage, "global:enrollment-v1", behind)).ContainsKey("RecoveryHash")) break;
            Assert.True(run < 79);
        }
        Assert.False((await Read(f.Storage, "global:enrollment-v1", behind)).ContainsKey("RecoveryHash"));
        output.WriteLine("35 authentic retained enrollments plus issuance audit crossed run budgets; {0} budget stops, two fixed cursor rows.", budgetStops);
    }

    [Fact]
    public async Task Lost_committed_cursor_response_is_reconciled_on_restart_without_duplicate_capacity_refund()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        f.Clock.Advance(TimeSpan.FromDays(-2));
        var id = Guid.NewGuid().ToString("N");
        using var start = await Start(f, id);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        await f.StopHttpAsync();
        await using var proxy = new CleanupTableProxy(f.Storage);
        var dropped = false;
        proxy.After = r =>
        {
            if (!dropped && r.Path.EndsWith("/$batch", StringComparison.Ordinal)
                && r.Body.Contains("cleanup:payload-v1", StringComparison.Ordinal) && r.Body.Contains("pending:" + id, StringComparison.Ordinal))
            {
                dropped = true;
                return Task.FromResult(false);
            }
            return Task.FromResult(true);
        };
        var interrupted = await CleanupProcessFixture.RunAsync(f.Storage, proxy.Endpoint);
        Assert.True(dropped, "Proxy must drop an actual committed cursor-checkpoint response.");
        Assert.Equal(1, interrupted.Exit);
        AssertBudget(interrupted.Summary, proxy);
        Assert.True(interrupted.Summary.GetProperty("incompleteCoverage").GetBoolean());
        Assert.False((await Read(f.Storage, "global:enrollment-v1", "pending:" + id)).ContainsKey("RecoveryHash"));
        for (var i = 0; i < 8; i++) Assert.Equal(0, (await CleanupProcessFixture.RunAsync(f.Storage)).Exit);
        var quota = await Read(f.Storage, "global:enrollment-v1", "quota");
        Assert.Equal(0L, quota.GetInt64("Count"));
        Assert.Equal(1L, quota.GetInt64("RetainedEnrollmentCount"));
        var issuance = await Read(f.Storage, "global:issuance-v1", "quota");
        Assert.Equal(0L, issuance.GetInt64("Slots"));
        Assert.Equal(1L, issuance.GetInt64("RetainedIssuanceCount"));
    }

    [Fact]
    public async Task Competing_processes_use_real_ETags_and_do_not_skip_due_payload()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        // Warm only the two fixed cursors; no allocation is present yet.
        await f.StopHttpAsync();
        Assert.Equal(0, (await CleanupProcessFixture.RunAsync(f.Storage)).Exit);
        await f.RestartAsync();
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        f.Clock.Advance(TimeSpan.FromDays(-2));
        var id = Guid.NewGuid().ToString("N");
        using var start = await Start(f, id);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        await f.StopHttpAsync();
        await using var a = new CleanupTableProxy(f.Storage);
        await using var b = new CleanupTableProxy(f.Storage);
        var arrived = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Gate(CleanupTableProxy.Exchange r)
        {
            if (r.Path.EndsWith("/$batch", StringComparison.Ordinal) && r.Body.Contains("cleanup:payload-v1", StringComparison.Ordinal)
                && Interlocked.Increment(ref arrived) <= 2)
            {
                if (Volatile.Read(ref arrived) == 2) both.TrySetResult();
                await both.Task.WaitAsync(TimeSpan.FromSeconds(2), Token);
            }
            return true;
        }
        a.Before = Gate; b.Before = Gate;
        var results = await Task.WhenAll(CleanupProcessFixture.RunAsync(f.Storage, a.Endpoint), CleanupProcessFixture.RunAsync(f.Storage, b.Endpoint));
        Assert.True(arrived >= 2);
        Assert.True(results.Sum(r => r.Summary.GetProperty("conflicts").GetInt32()) >= 1);
        AssertBudget(results[0].Summary, a); AssertBudget(results[1].Summary, b);
        for (var i = 0; i < 4; i++) Assert.Equal(0, (await CleanupProcessFixture.RunAsync(f.Storage)).Exit);
        Assert.False((await Read(f.Storage, "global:enrollment-v1", "pending:" + id)).ContainsKey("RecoveryHash"));
        Assert.Equal(2, (await Rows(f.Storage, "global:enrollment-v1", "cleanup:")).Count);
        Assert.Equal(0L, (await Read(f.Storage, "global:enrollment-v1", "quota")).GetInt64("Count"));
    }

    [Fact]
    public async Task Permanent_audit_checkpoint_failure_does_not_starve_urgent_payload_in_either_family()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        f.Clock.Advance(TimeSpan.FromDays(-2));
        var id = Guid.NewGuid().ToString("N");
        using var start = await Start(f, id);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var issuance = Assert.Single(await Rows(f.Storage, "global:issuance-v1", "operation:"));
        await f.StopHttpAsync();
        await using var proxy = new CleanupTableProxy(f.Storage);
        proxy.Before = r => Task.FromResult(!r.Path.EndsWith("/$batch", StringComparison.Ordinal)
            || !r.Body.Contains("cleanup:audit-v1", StringComparison.Ordinal));
        var result = await CleanupProcessFixture.RunAsync(f.Storage, proxy.Endpoint);
        Assert.Equal(1, result.Exit);
        AssertBudget(result.Summary, proxy);
        Assert.True(result.Summary.GetProperty("incompleteCoverage").GetBoolean());
        Assert.True(result.Summary.GetProperty("payloadFieldsRemovedObserved").GetInt32() >= 3);
        Assert.True(result.Summary.GetProperty("oldestObservedOverduePayloadAgeSeconds").GetDouble() > 0);
        Assert.False((await Read(f.Storage, "global:enrollment-v1", "pending:" + id)).ContainsKey("RecoveryHash"));
        Assert.False((await Read(f.Storage, RevocationFixture.Partition, issuance.GetString("AttemptRowKey")!)).ContainsKey("ProtectedToken"));
        var audit = await Read(f.Storage, "global:enrollment-v1", "cleanup:audit-v1");
        Assert.Equal("", audit.GetString("EnrollmentLastKey"));
        Assert.Equal("", audit.GetString("IssuanceLastKey"));
    }

    [Fact]
    public async Task Slow_real_storage_requests_stop_at_the_cooperative_deadline_with_incomplete_coverage()
    {
        await using var storage = new RevocationFixture();
        await storage.InitializeAsync();
        await using var proxy = new CleanupTableProxy(storage);
        proxy.Before = async _ => { await Task.Delay(TimeSpan.FromMilliseconds(2500), Token); return true; };
        var result = await CleanupProcessFixture.RunAsync(storage, proxy.Endpoint);
        Assert.Equal(0, result.Exit);
        Assert.Equal("time-budget", result.Summary.GetProperty("stopReason").GetString());
        Assert.True(result.Summary.GetProperty("incompleteCoverage").GetBoolean());
        Assert.InRange(result.Summary.GetProperty("elapsedMilliseconds").GetInt64(), 55_000, 65_000);
        Assert.Equal(proxy.Requests.Count, result.Summary.GetProperty("sdkTableDispatches").GetInt32());
        Assert.InRange(proxy.Requests.Count, 1, 128);
        Assert.True(result.Summary.GetProperty("oldestObservedOverduePayloadAgeSeconds").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Eligible_resolved_audit_is_retired_by_the_real_process_and_retained_capacity_is_refunded_once()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        var normal = f.GitHub.Before!;
        f.GitHub.Before = request => request.Method == HttpMethod.Post
            ? Task.FromResult<HttpResponseMessage?>(GitHubTransport.Json(new
            {
                token = "invalid-scope-test-token", expires_at = f.Clock.GetUtcNow().AddHours(1), repository_selection = "all",
                permissions = new { agent_variables = "read" },
                repositories = new[] { new { id = 123456, full_name = "owner/project" } }
            }, HttpStatusCode.Created)) : normal(request);
        f.Clock.Advance(TimeSpan.FromDays(-40));
        using var start = await Start(f, Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, start.StatusCode);
        var ledger = Assert.Single(await Rows(f.Storage, "global:issuance-v1", "operation:"));
        var attempt = await Read(f.Storage, RevocationFixture.Partition, ledger.GetString("AttemptRowKey")!);
        Assert.Equal("settled", attempt.GetString("State"));
        await Reclaim(f).ReclaimIssuanceAsync(ledger.GetString("OperationId")!, ledger.GetString("Generation")!, Token);
        Assert.Equal(1L, (await Read(f.Storage, "global:issuance-v1", "quota")).GetInt64("RetainedIssuanceCount"));
        await f.StopHttpAsync();
        var deletionObserved = 0;
        for (var i = 0; i < 12; i++)
        {
            await using var proxy = new CleanupTableProxy(f.Storage);
            var result = await CleanupProcessFixture.RunAsync(f.Storage, proxy.Endpoint);
            Assert.Equal(0, result.Exit);
            AssertBudget(result.Summary, proxy);
            deletionObserved += result.Summary.GetProperty("deletedRowsObserved").GetInt32();
            if (!(await f.Storage.Table.GetEntityIfExistsAsync<TableEntity>(ledger.PartitionKey, ledger.RowKey, cancellationToken: Token)).HasValue) break;
            Assert.True(i < 11);
        }
        Assert.True(deletionObserved >= 2);
        Assert.False((await f.Storage.Table.GetEntityIfExistsAsync<TableEntity>(attempt.PartitionKey, attempt.RowKey, cancellationToken: Token)).HasValue);
        Assert.Equal(0, (await CleanupProcessFixture.RunAsync(f.Storage)).Exit);
        var quota = await Read(f.Storage, "global:issuance-v1", "quota");
        Assert.Equal(0L, quota.GetInt64("Slots"));
        Assert.Equal(0L, quota.GetInt64("RetainedIssuanceCount"));
    }

}
