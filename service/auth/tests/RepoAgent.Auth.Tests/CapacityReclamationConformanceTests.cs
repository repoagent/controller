using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed partial class CapacityReclamationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("pending")]
    [InlineData("enrollment-mint")]
    [InlineData("runtime-mint")]
    [InlineData("runtime-verifier")]
    public async Task Reclaimed_global_only_admission_cannot_publish_after_its_repository_fence(string stage)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var active = stage.StartsWith("runtime", StringComparison.Ordinal) ? await Active(f) : null;
        if (stage == "runtime-verifier") f.Clock.Advance(TimeSpan.FromMinutes(56));
        if (active is null) AgentVariableEnrollmentTests.ConfigureGitHub(f);
        var old = await IssuanceLedgers(f);
        var pause = new CapacityPause(body => body.Contains(stage == "pending" ? "\"State\":\"discovering\"" : "\"State\":\"reserved\"", StringComparison.Ordinal));
        await f.RestartAsync(pause.Transport);
        var id = Guid.NewGuid().ToString("N");
        var writer = active is null ? f.Send("/v2/enrollments", Request(id)) : f.Send($"/v1/devices/{active.Device}/installation-token", secret: active.Secret);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            var ledger = stage == "pending" ? await Read(f, "global:enrollment-v1", "pending:" + id)
                : Assert.Single(await IssuanceLedgers(f), x => old.All(b => b.RowKey != x.RowKey));
            var target = stage == "pending" ? ledger.RowKey : ledger.GetString("AttemptRowKey")!;
            Assert.False((await f.Storage.Table.GetEntityIfExistsAsync<TableEntity>(RevocationFixture.Partition, target, cancellationToken: Token)).HasValue);
            if (stage == "pending")
            {
                f.Clock.Advance(ledger.GetDateTimeOffset("Deadline")!.Value - f.Clock.GetUtcNow());
                await Cleanup(f).ReclaimEnrollmentAsync(id, ledger.GetString("Generation")!, Token);
            }
            else await Reclaim(f, ledger);
            var fenced = await Read(f, RevocationFixture.Partition, target);
            var gate = await Read(f, RevocationFixture.Partition, "gate");
            pause.Release.TrySetResult();
            using var response = await writer;
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(fenced.ETag, (await Read(f, fenced.PartitionKey, fenced.RowKey)).ETag);
            Assert.Equal(gate.GetInt64("OutstandingTokenCount"), (await Read(f, gate.PartitionKey, gate.RowKey)).GetInt64("OutstandingTokenCount"));
            Assert.Equal(active is null ? 0 : 1, f.GitHub.Mints);
            Assert.Equal(old.Count, (await Rows(f)).Count(x => x.PartitionKey == "global:issuance-v1" && x.RowKey.StartsWith("slot:", StringComparison.Ordinal)));
        }
        finally { pause.Release.TrySetResult(); using var response = await writer; }
    }

    [Fact]
    public async Task Stale_global_cleanup_commit_loses_after_another_worker_releases_and_reuses_its_slot()
    {
        await using var f = await EnrollmentFixture.CreateAsync(limits: new(GlobalAttempts: 1));
        _ = await Start(f);
        var old = Assert.Single(await IssuanceLedgers(f));
        var attempt = await Read(f, RevocationFixture.Partition, old.GetString("AttemptRowKey")!);
        f.Clock.Advance(attempt.GetDateTimeOffset("ExpiresAt")!.Value.AddMinutes(5) - f.Clock.GetUtcNow());
        var pause = new CapacityPause(body => body.Contains("\"CapacityState\":\"released\"", StringComparison.Ordinal));
        var stale = Reclaim(f, old, f.Storage.CreateClient(pause.Transport));
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            var obligation = await Read(f, RevocationFixture.Partition, "release:" + old.GetString("OperationId"));
            await Reclaim(f, old);
            _ = await Start(f);
            var replacement = await Read(f, "global:issuance-v1", "slot:000");
            Assert.NotEqual(old.GetString("Generation"), replacement.GetString("Generation"));
            pause.Release.TrySetResult();
            await Assert.ThrowsAsync<TableTransactionFailedException>(() => stale);
            Assert.Equal(replacement.ETag, (await Read(f, replacement.PartitionKey, replacement.RowKey)).ETag);
            Assert.Equal(1L, (await Read(f, "global:issuance-v1", "quota")).GetInt64("Slots"));
            Assert.Equal(2L, (await Read(f, "global:issuance-v1", "quota")).GetInt64("RetainedIssuanceCount"));
            Assert.Equal(obligation.ETag, (await Read(f, obligation.PartitionKey, obligation.RowKey)).ETag);
        }
        finally { pause.Release.TrySetResult(); try { await stale; } catch (TableTransactionFailedException) { } }
    }

    [Fact]
    public async Task Positive_provider_revocation_releases_invalid_scope_token_without_waiting_for_expiry()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        var previous = f.GitHub.Before;
        f.GitHub.Before = request => request.Method == HttpMethod.Post
            ? Task.FromResult<HttpResponseMessage?>(GitHubTransport.Json(new
            {
                token = "invalid-scope-token", expires_at = f.Clock.GetUtcNow().AddHours(1), repository_selection = "all",
                permissions = new { agent_variables = "read" },
                repositories = new[] { new { id = 123456, full_name = "owner/project" } }
            }, HttpStatusCode.Created)) : previous!(request);
        using var response = await f.Send("/v2/enrollments", Request(Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, f.GitHub.Revokes);
        var ledger = Assert.Single(await IssuanceLedgers(f));
        var settled = await Read(f, RevocationFixture.Partition, ledger.GetString("AttemptRowKey")!);
        Assert.Equal("settled", settled.GetString("State"));
        Assert.True(settled.ContainsKey("SettledAt"));
        await Reclaim(f, ledger);
        await Reclaim(f, ledger);
        Assert.Equal(0L, (await Read(f, "global:issuance-v1", "quota")).GetInt64("Slots"));
        Assert.Equal(0L, (await Read(f, RevocationFixture.Partition, "gate")).GetInt64("OutstandingTokenCount"));
        Assert.Equal(1L, (await Read(f, "global:issuance-v1", "quota")).GetInt64("RetainedIssuanceCount"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Send_claim_or_unsent_reclamation_winner_controls_provider_post_and_exact_charge(bool runtime, bool sendWins)
    {
        await using var f = await EnrollmentFixture.CreateAsync(limits: new(GlobalAttempts: 2));
        var active = runtime ? await Active(f) : null;
        if (!runtime) AgentVariableEnrollmentTests.ConfigureGitHub(f);
        var before = await IssuanceLedgers(f);
        var mints = f.GitHub.Mints;
        var send = new CapacityPause(body => body.Contains("\"State\":\"sending\"", StringComparison.Ordinal));
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishPost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var previous = f.GitHub.Before;
        f.GitHub.Before = async request =>
        {
            if (sendWins && request.Method == HttpMethod.Post)
            {
                posted.TrySetResult();
                await finishPost.Task.WaitAsync(TimeSpan.FromSeconds(12), Token);
            }
            return previous is null ? null : await previous(request);
        };
        await f.RestartAsync(send.Transport);
        var writer = runtime ? f.Send($"/v1/devices/{active!.Device}/installation-token", secret: active.Secret)
            : f.Send("/v2/enrollments", Request(Guid.NewGuid().ToString("N")));
        try
        {
            await send.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            var ledger = Assert.Single(await IssuanceLedgers(f), x => before.All(b => b.RowKey != x.RowKey));
            if (sendWins)
            {
                send.Release.TrySetResult();
                await posted.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            }
            await Reclaim(f, ledger);
            var attempt = await Read(f, RevocationFixture.Partition, ledger.GetString("AttemptRowKey")!);
            Assert.Equal(sendWins ? "sending" : "settled", attempt.GetString("State"));
            Assert.Equal(sendWins ? before.Count + 1L : before.Count,
                (await Read(f, "global:issuance-v1", "quota")).GetInt64("Slots"));
            send.Release.TrySetResult();
            finishPost.TrySetResult();
            using var response = await writer;
            Assert.Equal(sendWins ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(mints + (sendWins ? 1 : 0), f.GitHub.Mints);
            if (runtime && !sendWins)
            {
                var oldGeneration = ledger.GetString("Generation");
                f.Clock.Advance(TimeSpan.FromMinutes(1));
                await f.RestartAsync();
                using var next = await f.Send($"/v1/devices/{active!.Device}/installation-token", secret: active.Secret);
                Assert.Equal(HttpStatusCode.OK, next.StatusCode);
                var replacement = await Read(f, "global:issuance-v1", "slot:001");
                Assert.NotEqual(oldGeneration, replacement.GetString("Generation"));
                await Reclaim(f, ledger);
                Assert.Equal(replacement.ETag, (await Read(f, replacement.PartitionKey, replacement.RowKey)).ETag);
                Assert.Equal(2L, (await Read(f, "global:issuance-v1", "quota")).GetInt64("Slots"));
            }
        }
        finally { send.Release.TrySetResult(); finishPost.TrySetResult(); using var response = await writer; }
    }

    [Theory]
    [InlineData("before-repository")]
    [InlineData("after-repository")]
    [InlineData("after-global")]
    public async Task Lost_cleanup_response_converges_once_and_preserves_active_ack_identity(string boundary)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var active = await Active(f);
        var ledger = await Read(f, "global:enrollment-v1", "pending:" + active.Id);
        var device = await Read(f, RevocationFixture.Partition, "device:" + active.Device);
        var gate = await Read(f, RevocationFixture.Partition, "gate");
        var batches = 0;
        var transport = new ObservedTransport
        {
            Before = message =>
            {
                if (boundary == "before-repository" && ObservedTransport.IsBatch(message)) throw new HttpRequestException("Before repository cleanup commit");
                return Task.CompletedTask;
            },
            After = message =>
            {
                if (ObservedTransport.IsBatch(message) && ++batches == (boundary == "after-global" ? 2 : 1))
                    throw new HttpRequestException("Lost committed cleanup response");
                return Task.CompletedTask;
            }
        };
        await Assert.ThrowsAsync<HttpRequestException>(() => Cleanup(f, f.Storage.CreateClient(transport))
            .ReclaimEnrollmentAsync(active.Id, ledger.GetString("Generation")!, Token));
        await Cleanup(f).ReclaimEnrollmentAsync(active.Id, ledger.GetString("Generation")!, Token);
        var released = await Read(f, RevocationFixture.Partition, ledger.RowKey);
        var release = await Read(f, RevocationFixture.Partition, "release:" + ledger.RowKey);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        await Cleanup(f).ReclaimEnrollmentAsync(active.Id, ledger.GetString("Generation")!, Token);
        using var ack = await f.Send($"/v2/devices/{active.Device}/acknowledgment", new { completionId = active.Completion }, active.Secret);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        Assert.Equal(released.ETag, (await Read(f, released.PartitionKey, released.RowKey)).ETag);
        Assert.Equal(release.ETag, (await Read(f, release.PartitionKey, release.RowKey)).ETag);
        var currentDevice = await Read(f, device.PartitionKey, device.RowKey);
        foreach (var field in new[] { "LastActivityAt", "ExpiresAt", "CompletionId", "Acknowledged" }) Assert.Equal(device[field], currentDevice[field]);
        var currentGate = await Read(f, gate.PartitionKey, gate.RowKey);
        foreach (var field in new[] { "OwnerDeviceId", "Epoch", "State", "OutstandingTokenCount" }) Assert.Equal(gate[field], currentGate[field]);
        Assert.Equal(0L, currentGate.GetInt64("PendingCount"));
        Assert.Equal(0L, (await Read(f, "global:enrollment-v1", "quota")).GetInt64("Count"));
        Assert.Equal(0L, (await Read(f, "global:enrollment-v1", "repository:123456")).GetInt64("PendingCount"));
        Assert.Equal(1L, (await Read(f, "global:enrollment-v1", "quota")).GetInt64("RetainedEnrollmentCount"));
    }

    [Fact]
    public async Task Enrollment_reuses_lowest_hole_at_original_expiry_plus_five_minutes_but_retained_backlog_still_denies()
    {
        await using var f = await EnrollmentFixture.CreateAsync(settings: new() { ["Broker:RetainedIssuanceLimit"] = "3" },
            limits: new(GlobalAttempts: 2));
        var first = await Start(f);
        var a = Assert.Single(await IssuanceLedgers(f));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        _ = await Start(f);
        var second = (await IssuanceLedgers(f)).Single(x => x.RowKey != a.RowKey);
        Assert.Equal(0L, a.GetInt64("GlobalSlot"));
        Assert.Equal(1L, second.GetInt64("GlobalSlot"));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var full = await f.Send("/v2/enrollments", Request(Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);
        var attempt = await Read(f, RevocationFixture.Partition, a.GetString("AttemptRowKey")!);
        var due = attempt.GetDateTimeOffset("ExpiresAt")!.Value.AddMinutes(5);
        f.Clock.Advance(due.AddTicks(-1) - f.Clock.GetUtcNow());
        await Reclaim(f, a);
        Assert.Equal(2L, (await Read(f, "global:issuance-v1", "quota")).GetInt64("Slots"));
        f.Clock.Advance(TimeSpan.FromTicks(1));
        var before = await Read(f, "global:issuance-v1", "quota");
        await Reclaim(f, a);
        await Reclaim(f, a);
        var freed = await Read(f, "global:issuance-v1", "quota");
        Assert.Equal(1L, freed.GetInt64("Slots"));
        foreach (var field in new[] { "RetainedIssuanceCount", "Admissions", "Reads", "Signs", "Mints" })
            Assert.Equal(before[field], freed[field]);
        await Cleanup(f).ReclaimEnrollmentAsync(first.Id, (await Read(f, "global:enrollment-v1", "pending:" + first.Id)).GetString("Generation")!, Token);
        _ = await Start(f);
        var replacement = Assert.Single(await IssuanceLedgers(f), x => x.RowKey != a.RowKey && x.RowKey != second.RowKey);
        Assert.Equal(0L, replacement.GetInt64("GlobalSlot"));
        Assert.NotEqual(a.GetString("Generation"), replacement.GetString("Generation"));
        await Reclaim(f, a); // Old identity must never touch the new occupant of slot zero.
        var slot = await Read(f, "global:issuance-v1", "slot:000");
        Assert.Equal(replacement.GetString("Generation"), slot.GetString("Generation"));
        var rows = await Rows(f);
        Assert.Equal(new[] { "slot:000", "slot:001" }, rows.Where(x => x.PartitionKey == "global:issuance-v1" && x.RowKey.StartsWith("slot:", StringComparison.Ordinal)).Select(x => x.RowKey).Order());
        Assert.Equal(3L, (await Read(f, "global:issuance-v1", "quota")).GetInt64("RetainedIssuanceCount"));
        var latest = await Read(f, RevocationFixture.Partition, replacement.GetString("AttemptRowKey")!);
        f.Clock.Advance(latest.GetDateTimeOffset("ExpiresAt")!.Value.AddMinutes(5) - f.Clock.GetUtcNow());
        await Reclaim(f, replacement);
        await Cleanup(f).ReclaimEnrollmentAsync(replacement.GetString("PendingId")!.Split('_')[1],
            (await Read(f, "global:enrollment-v1", "pending:" + replacement.GetString("PendingId")!.Split('_')[1])).GetString("Generation")!, Token);
        using var retainedFull = await f.Send("/v2/enrollments", Request(Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.TooManyRequests, retainedFull.StatusCode);
        Assert.Equal(3, f.GitHub.VerifierMints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_absent_issuance_redaction_tombstone_releases_but_contrary_charge_evidence_quarantines(bool ambiguous)
    {
        var pause = new CapacityPause(body => body.Contains("\"State\":\"reserved\"", StringComparison.Ordinal));
        await using var f = await EnrollmentFixture.CreateAsync(pause.Transport);
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        var writer = f.Send("/v2/enrollments", Request(Guid.NewGuid().ToString("N")));
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            var a = Assert.Single(await IssuanceLedgers(f));
            Assert.False((await f.Storage.Table.GetEntityIfExistsAsync<TableEntity>(RevocationFixture.Partition, a.GetString("AttemptRowKey")!, cancellationToken: Token)).HasValue);
            f.Clock.Advance(a.GetDateTimeOffset("PayloadPurgeAt")!.Value - f.Clock.GetUtcNow());
            await new BrokerPayloadRedaction(f.Storage.Table, Options(f), f.Clock)
                .RedactIssuanceAsync(a.GetString("OperationId")!, a.GetString("Generation")!, Token);
            var tombstone = await Read(f, RevocationFixture.Partition, a.GetString("AttemptRowKey")!);
            Assert.Equal("redacted", tombstone.GetString("State"));
            if (ambiguous)
            {
                tombstone["RepositoryCharged"] = false;
                await f.Storage.Table.UpdateEntityAsync(tombstone, tombstone.ETag, TableUpdateMode.Replace, Token);
            }
            await Reclaim(f, a);
            Assert.Equal(ambiguous ? 1L : 0L, (await Read(f, "global:issuance-v1", "quota")).GetInt64("Slots"));
            Assert.Equal(0L, (await Read(f, RevocationFixture.Partition, "gate")).GetInt64("OutstandingTokenCount"));
            Assert.Equal(1L, (await Read(f, "global:issuance-v1", "quota")).GetInt64("RetainedIssuanceCount"));
        }
        finally { pause.Release.TrySetResult(); using var response = await writer; }
        Assert.Equal(0, f.GitHub.Mints);
    }

    [Theory]
    [InlineData("sending")]
    [InlineData("scope")]
    [InlineData("missing-expiry")]
    [InlineData("sentinel-expiry")]
    [InlineData("overflow-expiry")]
    [InlineData("before-reservation-expiry")]
    public async Task Unresolved_or_untrusted_actual_attempt_retains_occupancy(string invalid)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        _ = await Start(f);
        var a = Assert.Single(await IssuanceLedgers(f));
        var attempt = await Read(f, RevocationFixture.Partition, a.GetString("AttemptRowKey")!);
        if (invalid == "sending") attempt["State"] = "sending";
        if (invalid == "scope") attempt["ScopeValid"] = false;
        if (invalid == "missing-expiry") attempt.Remove("ExpiresAt");
        if (invalid == "sentinel-expiry") attempt["ExpiresAt"] = DateTimeOffset.MaxValue;
        if (invalid == "overflow-expiry") attempt["ExpiresAt"] = DateTimeOffset.MaxValue.AddMinutes(-1);
        if (invalid == "before-reservation-expiry") attempt["ExpiresAt"] = attempt.GetDateTimeOffset("ReservedAt")!.Value.AddMinutes(-1);
        await f.Storage.Table.UpdateEntityAsync(attempt, attempt.ETag, TableUpdateMode.Replace, Token);
        f.Clock.Advance(TimeSpan.FromDays(2));
        var before = await Read(f, RevocationFixture.Partition, "gate");
        await Reclaim(f, a);
        Assert.Equal(1L, (await Read(f, "global:issuance-v1", "quota")).GetInt64("Slots"));
        var after = await Read(f, RevocationFixture.Partition, "gate");
        Assert.Equal(before.ETag, after.ETag);
        Assert.Equal(1, f.GitHub.Mints);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Released_runtime_cleanup_recovers_each_lost_commit_without_refunding_device_twice(int lostBatch)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var active = await Active(f);
        using var response = await f.Send($"/v1/devices/{active.Device}/installation-token", secret: active.Secret);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var runtime = Assert.Single(await IssuanceLedgers(f), x => x.GetString("Purpose") == "runtime");
        var attempt = await Read(f, RevocationFixture.Partition, runtime.GetString("AttemptRowKey")!);
        Assert.Equal("released", attempt.GetString("State"));
        var device = await Read(f, RevocationFixture.Partition, "device:" + active.Device);
        Assert.Equal(1L, device.GetInt64("OutstandingTokenCount"));
        f.Clock.Advance(attempt.GetDateTimeOffset("ExpiresAt")!.Value.AddMinutes(5).AddTicks(-1) - f.Clock.GetUtcNow());
        await Reclaim(f, runtime);
        Assert.Equal(2L, (await Read(f, "global:issuance-v1", "quota")).GetInt64("Slots"));
        f.Clock.Advance(TimeSpan.FromTicks(1));
        var batches = 0;
        var fault = new ObservedTransport { After = message =>
        {
            if (ObservedTransport.IsBatch(message) && ++batches == lostBatch) throw new HttpRequestException("Lost runtime settlement response");
            return Task.CompletedTask;
        }};
        await Assert.ThrowsAsync<HttpRequestException>(() => Reclaim(f, runtime, f.Storage.CreateClient(fault)));
        await Reclaim(f, runtime);
        var obligation = await Read(f, RevocationFixture.Partition, "release:" + runtime.GetString("OperationId"));
        await Reclaim(f, runtime);
        Assert.Equal(obligation.ETag, (await Read(f, obligation.PartitionKey, obligation.RowKey)).ETag);
        // #71's duplicate settlement remains inert. After #81's audit horizon,
        // the resolved runtime allocation retires independently of its enrollment.
        f.Clock.Advance(TimeSpan.FromDays(31));
        await new BrokerPayloadRedaction(f.Storage.Table, Options(f), f.Clock)
            .RedactIssuanceAsync(runtime.GetString("OperationId")!, runtime.GetString("Generation")!, Token);
        await Reclaim(f, runtime);
        await AssertAbsent(f, obligation.PartitionKey, obligation.RowKey);
        Assert.Equal(1L, (await Read(f, "global:issuance-v1", "quota")).GetInt64("Slots"));
        Assert.Equal(1L, (await Read(f, "global:issuance-v1", "quota")).GetInt64("RetainedIssuanceCount"));
        Assert.Equal(1L, (await Read(f, RevocationFixture.Partition, "gate")).GetInt64("OutstandingTokenCount"));
        var current = await Read(f, device.PartitionKey, device.RowKey);
        Assert.Equal(0L, current.GetInt64("OutstandingTokenCount"));
        foreach (var field in new[] { "LastActivityAt", "ExpiresAt", "State" }) Assert.Equal(device[field], current[field]);
    }

    [Fact]
    public async Task Provider_unknown_send_stays_charged_after_deadline_and_device_receipt()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var active = await Active(f);
        var previous = f.GitHub.Before;
        f.GitHub.Before = request => request.Method == HttpMethod.Post
            ? throw new HttpRequestException("Provider send outcome unknown") : previous!(request);
        using var response = await f.Send($"/v1/devices/{active.Device}/installation-token", secret: active.Secret);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var runtime = Assert.Single(await IssuanceLedgers(f), x => x.GetString("Purpose") == "runtime");
        var attempt = await Read(f, RevocationFixture.Partition, runtime.GetString("AttemptRowKey")!);
        Assert.Equal("sending", attempt.GetString("State"));
        Assert.False(attempt.ContainsKey("ExpiresAt"));
        using var revoked = await f.Send($"/v1/devices/{active.Device}", secret: active.Secret, method: HttpMethod.Delete);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(active.Device, (await Read(f, RevocationFixture.Partition, "revocation:" + active.Device)).GetString("DeviceId"));
        f.Clock.Advance(TimeSpan.FromDays(2));
        await Reclaim(f, runtime);
        Assert.Equal(attempt.ETag, (await Read(f, attempt.PartitionKey, attempt.RowKey)).ETag);
        Assert.Equal(2L, (await Read(f, "global:issuance-v1", "quota")).GetInt64("Slots"));
        Assert.Equal(1L, (await Read(f, RevocationFixture.Partition, "device:" + active.Device)).GetInt64("OutstandingTokenCount"));
    }

    [Fact]
    public async Task Wrong_generation_and_underflow_cannot_refund_a_real_charged_attempt()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        _ = await Start(f);
        var ledger = Assert.Single(await IssuanceLedgers(f));
        var attempt = await Read(f, RevocationFixture.Partition, ledger.GetString("AttemptRowKey")!);
        f.Clock.Advance(attempt.GetDateTimeOffset("ExpiresAt")!.Value.AddMinutes(5) - f.Clock.GetUtcNow());
        await Assert.ThrowsAsync<IssuanceDenied>(() => Cleanup(f).ReclaimIssuanceAsync(ledger.GetString("OperationId")!, Guid.NewGuid().ToString("N"), Token));
        var gate = await Read(f, RevocationFixture.Partition, "gate");
        gate["OutstandingTokenCount"] = 0L;
        await f.Storage.Table.UpdateEntityAsync(gate, gate.ETag, TableUpdateMode.Replace, Token);
        await Assert.ThrowsAsync<IssuanceDenied>(() => Reclaim(f, ledger));
        Assert.Equal(attempt.ETag, (await Read(f, attempt.PartitionKey, attempt.RowKey)).ETag);
        Assert.Equal(1L, (await Read(f, "global:issuance-v1", "quota")).GetInt64("Slots"));
        Assert.False((await f.Storage.Table.GetEntityIfExistsAsync<TableEntity>(RevocationFixture.Partition, "release:" + ledger.GetString("OperationId"), cancellationToken: Token)).HasValue);
    }

    private static object Request(string id) => new { idempotencyId = id, repositoryId = 123456, repository = "owner/project", variable = "APPROVAL", profile = "polling" };
    private sealed record ActiveJourney(string Id, string Device, string Secret, string Completion);
    private static async Task<ActiveJourney> Active(EnrollmentFixture f)
    {
        var start = await Start(f);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var completion = await f.Send($"/v2/enrollments/{start.Pending}/completion");
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var receipt = await completion.Content.ReadFromJsonAsync<JsonElement>(Token);
        var envelope = receipt.GetProperty("enrollment");
        var j = new ActiveJourney(start.Id, envelope.GetProperty("deviceId").GetString()!, envelope.GetProperty("deviceSecret").GetString()!, receipt.GetProperty("completionId").GetString()!);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var ack = await f.Send($"/v2/devices/{j.Device}/acknowledgment", new { completionId = j.Completion }, j.Secret);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        return j;
    }
    private static async Task<(string Id, string Pending)> Start(EnrollmentFixture f)
    {
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        var id = Guid.NewGuid().ToString("N");
        using var response = await f.Send("/v2/enrollments", Request(id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        f.GitHub.Marker = json.GetProperty("marker").GetString()!;
        return (id, json.GetProperty("pendingId").GetString()!);
    }
    private static BrokerOptions Options(EnrollmentFixture f) => new(new Uri("https://example.table.core.windows.net"), f.Storage.TableName, RevocationFixture.Epoch, null, 120, 8, 15);
    private static BrokerCapacityReclamation Cleanup(EnrollmentFixture f, TableClient? table = null) => new(table ?? f.Storage.CreateClient(), Options(f), f.Clock);
    private static Task Reclaim(EnrollmentFixture f, TableEntity ledger, TableClient? table = null) => Cleanup(f, table).ReclaimIssuanceAsync(ledger.GetString("OperationId")!, ledger.GetString("Generation")!, Token);
    private static async Task<TableEntity> Read(EnrollmentFixture f, string partition, string key) => (await f.Storage.CreateClient().GetEntityAsync<TableEntity>(partition, key, cancellationToken: Token)).Value;
    private static async Task<List<TableEntity>> Rows(EnrollmentFixture f)
    {
        var rows = new List<TableEntity>();
        await foreach (var row in f.Storage.CreateClient().QueryAsync<TableEntity>(cancellationToken: Token)) rows.Add(row);
        return rows;
    }
    private static async Task<List<TableEntity>> IssuanceLedgers(EnrollmentFixture f) => (await Rows(f)).Where(x => x.PartitionKey == "global:issuance-v1" && x.RowKey.StartsWith("operation:", StringComparison.Ordinal)).ToList();
    private sealed class CapacityPause
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ObservedTransport Transport { get; }
        public CapacityPause(Func<string, bool> matches)
        {
            var paused = 0;
            Transport = new() { Before = async message =>
            {
                if (!ObservedTransport.IsBatch(message) || message.Request.Content is null) return;
                using var stream = new MemoryStream();
                await message.Request.Content.WriteToAsync(stream, Token);
                if (!matches(Encoding.UTF8.GetString(stream.ToArray())) || Interlocked.CompareExchange(ref paused, 1, 0) != 0) return;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(12), Token);
            }};
        }
    }
}
