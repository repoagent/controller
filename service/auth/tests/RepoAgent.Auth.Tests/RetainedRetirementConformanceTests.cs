using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed partial class CapacityReclamationTests
{
    [Theory]
    [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)] [InlineData(4, false)] [InlineData(5, false)]
    [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, true)] [InlineData(4, true)] [InlineData(5, true)]
    public async Task Retirement_lost_committed_phase_converges_with_one_refund(int boundary, bool enrollment)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var ledger = await RetirementEligible(f, enrollment);
        var count = 0;
        var fault = new ObservedTransport { After = message =>
        {
            if (ObservedTransport.IsBatch(message) && ++count == boundary) throw new HttpRequestException("Lost committed retirement phase " + boundary);
            return Task.CompletedTask;
        }};
        await Assert.ThrowsAsync<RequestFailedException>(() => Retirement(f, ledger, f.Storage.CreateClient(fault)));
        Assert.Equal(boundary < 5 ? 1L : 0L, (await Read(f, ledger.PartitionKey, "quota")).GetInt64(RetainedCounter(enrollment)));
        if (boundary < 2) _ = await Read(f, RevocationFixture.Partition, Target(ledger, enrollment));
        else await AssertAbsent(f, RevocationFixture.Partition, Target(ledger, enrollment));
        var resumedReads = 0;
        var resumedActions = new List<int>();
        var measured = new ObservedTransport { Before = async message =>
        {
            if (message.Request.Method == RequestMethod.Get) resumedReads++;
            if (ObservedTransport.IsBatch(message)) resumedActions.Add(Regex.Matches(await Body(message), "Content-Type: application/http", RegexOptions.IgnoreCase).Count);
        }};
        await Retirement(f, ledger, f.Storage.CreateClient(measured));
        Assert.InRange(resumedReads, 1, 20);
        Assert.InRange(resumedActions.Count, 0, 5);
        Assert.All(resumedActions, actions => Assert.InRange(actions, 1, 4));
        await Retirement(f, ledger);
        await RetirementGone(f, ledger, enrollment);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public async Task Retirement_uncommitted_phase_failure_keeps_credit_until_recovery(int boundary)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var ledger = await RetirementEligible(f, false);
        var count = 0;
        var fault = new ObservedTransport { Before = message =>
        {
            if (ObservedTransport.IsBatch(message) && ++count == boundary) throw new HttpRequestException("Before retirement commit " + boundary);
            return Task.CompletedTask;
        }};
        await Assert.ThrowsAsync<RequestFailedException>(() => Retirement(f, ledger, f.Storage.CreateClient(fault)));
        Assert.Equal(1L, (await Read(f, ledger.PartitionKey, "quota")).GetInt64("RetainedIssuanceCount"));
        await Retirement(f, ledger);
        await RetirementGone(f, ledger, false);
    }

    [Theory]
    [InlineData("target")] [InlineData("release")] [InlineData("gate")] [InlineData("reference")]
    [InlineData("quota")] [InlineData("ledger")]
    public async Task Retirement_real_ETag_conflict_rolls_back_delete_or_refund(string competing)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var ledger = await RetirementEligible(f, false);
        var count = 0;
        var fault = new ObservedTransport { Before = async message =>
        {
            if (!ObservedTransport.IsBatch(message) || ++count != (competing is "quota" or "ledger" ? 5 : 2)) return;
            var partition = competing is "quota" or "ledger" ? ledger.PartitionKey : RevocationFixture.Partition;
            var key = competing switch { "target" => Target(ledger, false), "release" => Release(ledger, false), "gate" => "gate", "reference" => "pending:" + ledger.GetString("PendingId")!.Split('_')[1], "quota" => "quota", _ => ledger.RowKey };
            var current = await Read(f, partition, key);
            current["ConcurrentProbe"] = "real committed competing writer";
            await f.Storage.Table.UpdateEntityAsync(current, current.ETag, TableUpdateMode.Replace, Token);
        }};
        var conflict = await Assert.ThrowsAsync<TableTransactionFailedException>(() => Retirement(f, ledger, f.Storage.CreateClient(fault)));
        Assert.Equal(412, conflict.Status);
        Assert.Equal(1L, (await Read(f, ledger.PartitionKey, "quota")).GetInt64("RetainedIssuanceCount"));
        if (competing is not ("quota" or "ledger"))
        {
            _ = await Read(f, RevocationFixture.Partition, Target(ledger, false));
            Assert.False((await Read(f, RevocationFixture.Partition, Release(ledger, false))).GetBoolean("RetirementTargetDeleted") == true);
        }
        await Retirement(f, ledger);
        await RetirementGone(f, ledger, false);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Retirement_unexplained_missing_target_or_obligation_never_refunds(bool missingRelease)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var ledger = await RetirementEligible(f, false);
        var row = await Read(f, RevocationFixture.Partition, missingRelease ? Release(ledger, false) : Target(ledger, false));
        await f.Storage.Table.DeleteEntityAsync(row.PartitionKey, row.RowKey, row.ETag, Token);
        await Assert.ThrowsAsync<IssuanceDenied>(() => Retirement(f, ledger));
        Assert.Equal(1L, (await Read(f, ledger.PartitionKey, "quota")).GetInt64("RetainedIssuanceCount"));
        Assert.False((await Read(f, ledger.PartitionKey, ledger.RowKey)).ContainsKey("RetirementState"));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Retirement_redactor_cannot_recreate_rows_with_fresh_or_stale_gate(bool enrollment, bool afterDecision)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var ledger = await RetirementEligible(f, enrollment);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hits = 0;
        var transport = new ObservedTransport
        {
            Before = async message =>
            {
                if (afterDecision && ObservedTransport.IsBatch(message) && Interlocked.Increment(ref hits) == 1)
                { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(12), Token); }
            },
            After = async message =>
            {
                if (!afterDecision && message.Request.Method == RequestMethod.Get && Interlocked.Increment(ref hits) == 1)
                { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(12), Token); }
            }
        };
        var redactor = new BrokerPayloadRedaction(f.Storage.CreateClient(transport), Options(f), f.Clock);
        var stale = enrollment ? redactor.RedactEnrollmentAsync(ledger.RowKey[8..], ledger.GetString("Generation")!, Token)
            : redactor.RedactIssuanceAsync(ledger.GetString("OperationId")!, ledger.GetString("Generation")!, Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            await Retirement(f, ledger);
            release.TrySetResult();
            if (afterDecision) await Assert.ThrowsAsync<TableTransactionFailedException>(() => stale);
            else await stale;
            await RetirementGone(f, ledger, enrollment);
        }
        finally { release.TrySetResult(); try { await stale; } catch (TableTransactionFailedException) { } }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Retirement_late_first_settlement_uses_original_audit_clock(bool enrollment)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var ledger = await RetirementEligible(f, enrollment, lateSettlement: true);
        await Retirement(f, ledger);
        await RetirementGone(f, ledger, enrollment);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Retirement_transport_budget_and_metadata_survive_exact_audit_boundary(bool enrollment)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var ledger = await RetirementEligible(f, enrollment);
        f.Clock.Advance(TimeSpan.FromTicks(-1));
        var gate = await Read(f, RevocationFixture.Partition, "gate");
        gate["OwnerDeviceId"] = "unrelated-owner"; gate["VerifierAttempt"] = "unrelated-verifier"; gate["BarrierProbe"] = "preserved";
        await f.Storage.Table.UpdateEntityAsync(gate, gate.ETag, TableUpdateMode.Replace, Token);
        gate = await Read(f, RevocationFixture.Partition, "gate");
        var quota = await Read(f, "global:issuance-v1", "quota");
        await Retirement(f, ledger);
        Assert.Equal(gate.ETag, (await Read(f, gate.PartitionKey, gate.RowKey)).ETag);
        Assert.Equal(1L, (await Read(f, ledger.PartitionKey, "quota")).GetInt64(RetainedCounter(enrollment)));
        f.Clock.Advance(TimeSpan.FromTicks(1));
        var reads = 0;
        var transactions = new List<int>();
        var transport = new ObservedTransport { Before = async message =>
        {
            if (message.Request.Method == RequestMethod.Get) reads++;
            if (ObservedTransport.IsBatch(message)) transactions.Add(Regex.Matches(await Body(message), "Content-Type: application/http", RegexOptions.IgnoreCase).Count);
        }};
        await Retirement(f, ledger, f.Storage.CreateClient(transport));
        Assert.InRange(reads, 1, 20);
        Assert.Equal(5, transactions.Count);
        Assert.All(transactions, actions => Assert.InRange(actions, 1, 4));
        await RetirementGone(f, ledger, enrollment);
        var after = await Read(f, gate.PartitionKey, gate.RowKey);
        foreach (var field in gate.Keys.Where(k => k is not ("Sequence" or "Timestamp" or "odata.etag"))) Assert.Equal(gate[field], after[field]);
        var currentQuota = await Read(f, quota.PartitionKey, quota.RowKey);
        foreach (var field in new[] { "Window", "Admissions", "Reads", "Signs", "Mints" }) Assert.Equal(quota[field], currentQuota[field]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Retirement_mint_work_claim_and_cleanup_have_one_real_gate_winner(bool mintWins)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        var id = Guid.NewGuid().ToString("N");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = 0;
        var transport = new ObservedTransport { Before = async message =>
        {
            if (!ObservedTransport.IsBatch(message)) return;
            var body = await Body(message);
            var matches = mintWins ? body.Contains("\"PartitionKey\":\"global:issuance-v1\"", StringComparison.Ordinal)
                : body.Contains("\"MintAdmissionStarted\":true", StringComparison.Ordinal);
            if (!matches || Interlocked.CompareExchange(ref paused, 1, 0) != 0) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(12), Token);
            if (mintWins) throw new HttpRequestException("Stop before global mint charge after durable claim");
        }};
        await f.RestartAsync(transport);
        var writer = f.Send("/v2/enrollments", Request(id));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            var ledger = await Read(f, "global:enrollment-v1", "pending:" + id);
            var pending = await Read(f, RevocationFixture.Partition, ledger.RowKey);
            Assert.Equal(mintWins, pending.GetBoolean("MintAdmissionStarted") == true);
            Assert.False(pending.ContainsKey("Attempt"));
            Assert.Empty(await IssuanceLedgers(f));
            f.Clock.Advance(ledger.GetDateTimeOffset("Deadline")!.Value - f.Clock.GetUtcNow());
            await Retirement(f, ledger);
            f.Clock.Advance(TimeSpan.FromDays(30));
            await new BrokerPayloadRedaction(f.Storage.Table, Options(f), f.Clock).RedactEnrollmentAsync(id, ledger.GetString("Generation")!, Token);
            await Retirement(f, ledger);
            if (mintWins)
            {
                Assert.True((await Read(f, RevocationFixture.Partition, ledger.RowKey)).GetBoolean("MintAdmissionStarted"));
                Assert.Equal(1L, (await Read(f, ledger.PartitionKey, "quota")).GetInt64("RetainedEnrollmentCount"));
                await Retirement(f, ledger);
            }
            else await RetirementGone(f, ledger, true);
            release.TrySetResult();
            using var response = await writer;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Empty(await IssuanceLedgers(f));
            Assert.Equal(0, f.GitHub.Mints);
        }
        finally { release.TrySetResult(); using var response = await writer; }
    }

    [Fact]
    public async Task Retirement_new_writer_resuming_historical_global_allocation_never_adds_version()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var id = Guid.NewGuid().ToString("N");
        var ledger = await UnpublishedEnrollment(f, id);
        ledger.Remove("ReclamationVersion");
        await f.Storage.Table.UpdateEntityAsync(ledger, ledger.ETag, TableUpdateMode.Replace, Token);
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        using var resumed = await f.Send("/v2/enrollments", Request(id));
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.False((await Read(f, ledger.PartitionKey, ledger.RowKey)).ContainsKey("ReclamationVersion"));
        var pending = await Read(f, RevocationFixture.Partition, ledger.RowKey);
        Assert.False(pending.ContainsKey("ReclamationVersion"));
        Assert.True(pending.GetBoolean("MintAdmissionStarted"));
        f.Clock.Advance(TimeSpan.FromDays(32));
        await Retirement(f, ledger);
        await Retirement(f, ledger);
        Assert.Equal(1L, (await Read(f, ledger.PartitionKey, "quota")).GetInt64("RetainedEnrollmentCount"));
    }

    [Theory]
    [InlineData("unversioned")] [InlineData("mint-started")] [InlineData("completed")]
    public async Task Retirement_manual_enrollment_proofs_remain_charged(string reason)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var ledger = await RetirementEligible(f, true);
        var pending = await Read(f, RevocationFixture.Partition, ledger.RowKey);
        if (reason == "unversioned")
        {
            ledger = await Read(f, ledger.PartitionKey, ledger.RowKey);
            ledger.Remove("ReclamationVersion"); pending.Remove("ReclamationVersion");
            await f.Storage.Table.UpdateEntityAsync(ledger, ledger.ETag, TableUpdateMode.Replace, Token);
        }
        else if (reason == "mint-started") pending["MintAdmissionStarted"] = true;
        else pending["CompletionId"] = Guid.NewGuid().ToString("N");
        await f.Storage.Table.UpdateEntityAsync(pending, pending.ETag, TableUpdateMode.Replace, Token);
        await Retirement(f, ledger);
        await Retirement(f, ledger);
        Assert.Equal(1L, (await Read(f, ledger.PartitionKey, "quota")).GetInt64("RetainedEnrollmentCount"));
        _ = await Read(f, RevocationFixture.Partition, ledger.RowKey);
    }

    [Fact]
    public async Task Retirement_runtime_is_independent_of_completed_enrollment_publication_charge()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var active = await Active(f);
        using var minted = await f.Send($"/v1/devices/{active.Device}/installation-token", secret: active.Secret);
        Assert.Equal(HttpStatusCode.OK, minted.StatusCode);
        var runtime = Assert.Single(await IssuanceLedgers(f), x => x.GetString("Purpose") == "runtime");
        var attempt = await Read(f, RevocationFixture.Partition, Target(runtime, false));
        var enrollment = await Read(f, "global:enrollment-v1", "pending:" + active.Id);
        f.Clock.Advance(attempt.GetDateTimeOffset("ExpiresAt")!.Value.AddMinutes(5) - f.Clock.GetUtcNow());
        await Retirement(f, runtime);
        await Retirement(f, enrollment);
        var device = await Read(f, RevocationFixture.Partition, "device:" + active.Device);
        Assert.Equal(0L, device.GetInt64("OutstandingTokenCount"));
        f.Clock.Advance(TimeSpan.FromDays(30));
        await Retirement(f, enrollment);
        await Retirement(f, runtime);
        await AssertAbsent(f, runtime.PartitionKey, runtime.RowKey);
        await AssertAbsent(f, RevocationFixture.Partition, Target(runtime, false));
        await AssertAbsent(f, RevocationFixture.Partition, Release(runtime, false));
        Assert.Equal(1L, (await Read(f, runtime.PartitionKey, "quota")).GetInt64("RetainedIssuanceCount"));
        Assert.Equal(1L, (await Read(f, enrollment.PartitionKey, "quota")).GetInt64("RetainedEnrollmentCount"));
        Assert.Equal(device.ETag, (await Read(f, device.PartitionKey, device.RowKey)).ETag);
        _ = await Read(f, "locator:" + active.Device, "locator");
        _ = await Read(f, RevocationFixture.Partition, enrollment.RowKey);
    }

    [Fact]
    public async Task Retirement_delayed_unconditional_locator_publisher_keeps_enrollment_credit()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var start = await Start(f);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await f.RestartAsync(new ObservedTransport { Before = async message =>
        {
            if (ObservedTransport.IsBatch(message) || !((await Body(message)).Contains("\"PartitionKey\":\"locator:", StringComparison.Ordinal))) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(12), Token);
        }});
        var publisher = f.Send($"/v2/enrollments/{start.Pending}/completion");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            var ledger = await Read(f, "global:enrollment-v1", "pending:" + start.Id);
            await AssertAbsent(f, "locator:" + ledger.GetString("DeviceId"), "locator");
            var pending = await Read(f, RevocationFixture.Partition, ledger.RowKey);
            Assert.True(pending.ContainsKey("CompletionId"));
            _ = await Read(f, RevocationFixture.Partition, "device:" + ledger.GetString("DeviceId"));
            f.Clock.Advance(ledger.GetDateTimeOffset("Deadline")!.Value.AddDays(31) - f.Clock.GetUtcNow());
            await Retirement(f, ledger);
            await Retirement(f, ledger);
            Assert.Equal(1L, (await Read(f, ledger.PartitionKey, "quota")).GetInt64("RetainedEnrollmentCount"));
            release.TrySetResult();
            using var response = await publisher;
            Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
            var locator = await Read(f, "locator:" + ledger.GetString("DeviceId"), "locator");
            Assert.Equal(ledger.GetString("PendingId"), locator.GetString("PendingId"));
            Assert.Equal(ledger.GetString("DeviceId"), locator.GetString("DeviceId"));
            Assert.Equal(1L, (await Read(f, ledger.PartitionKey, "quota")).GetInt64("RetainedEnrollmentCount"));
        }
        finally { release.TrySetResult(); using var response = await publisher; }
    }

    [Theory]
    [InlineData("sending")] [InlineData("unknown")] [InlineData("invalid-scope")]
    public async Task Retirement_unresolved_authority_remains_charged_after_payload_and_audit_ages(string state)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        _ = await Start(f);
        var ledger = Assert.Single(await IssuanceLedgers(f));
        var attempt = await Read(f, RevocationFixture.Partition, Target(ledger, false));
        if (state == "invalid-scope") attempt["ScopeValid"] = false;
        else { attempt["State"] = state; attempt.Remove("ExpiresAt"); }
        await f.Storage.Table.UpdateEntityAsync(attempt, attempt.ETag, TableUpdateMode.Replace, Token);
        f.Clock.Advance(TimeSpan.FromDays(90));
        await new BrokerPayloadRedaction(f.Storage.Table, Options(f), f.Clock).RedactIssuanceAsync(ledger.GetString("OperationId")!, ledger.GetString("Generation")!, Token);
        await Retirement(f, ledger);
        await Retirement(f, ledger);
        Assert.Equal(1L, (await Read(f, ledger.PartitionKey, "quota")).GetInt64("RetainedIssuanceCount"));
        Assert.Equal(1L, (await Read(f, ledger.PartitionKey, "quota")).GetInt64("Slots"));
        Assert.Equal(1L, (await Read(f, RevocationFixture.Partition, "gate")).GetInt64("OutstandingTokenCount"));
        Assert.False((await Read(f, ledger.PartitionKey, ledger.RowKey)).ContainsKey("RetirementState"));
        await AssertAbsent(f, RevocationFixture.Partition, Release(ledger, false));
    }

    [Fact]
    public async Task Retirement_live_pending_reference_blocks_then_terminal_reference_and_exact_verifier_clear_allow_deletion()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var ledger = await RetirementEligible(f, false);
        var pending = await Read(f, RevocationFixture.Partition, "pending:" + ledger.GetString("PendingId")!.Split('_')[1]);
        // A live reference is independently authoritative even if its date contradicts the older allocation.
        pending["Deadline"] = f.Clock.GetUtcNow().AddMinutes(15);
        await f.Storage.Table.UpdateEntityAsync(pending, pending.ETag, TableUpdateMode.Replace, Token);
        await Retirement(f, ledger);
        Assert.False((await Read(f, ledger.PartitionKey, ledger.RowKey)).ContainsKey("RetirementState"));
        Assert.Equal(1L, (await Read(f, ledger.PartitionKey, "quota")).GetInt64("RetainedIssuanceCount"));
        pending = await Read(f, pending.PartitionKey, pending.RowKey);
        pending["State"] = "closed";
        await f.Storage.Table.UpdateEntityAsync(pending, pending.ETag, TableUpdateMode.Replace, Token);
        var gate = await Read(f, RevocationFixture.Partition, "gate");
        gate["VerifierAttempt"] = ledger.GetString("OperationId")!;
        await f.Storage.Table.UpdateEntityAsync(gate, gate.ETag, TableUpdateMode.Replace, Token);
        await Retirement(f, ledger);
        await RetirementGone(f, ledger, false);
        Assert.False((await Read(f, gate.PartitionKey, gate.RowKey)).ContainsKey("VerifierAttempt"));
        _ = await Read(f, pending.PartitionKey, pending.RowKey);
    }

    [Fact]
    public async Task Retirement_repeated_real_enrollment_cycles_leave_only_fixed_rows()
    {
        await using var f = await EnrollmentFixture.CreateAsync(settings: new() { ["Broker:RetainedEnrollmentLimit"] = "1" });
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var ledger = await RetirementEligible(f, true);
            await Retirement(f, ledger);
            await RetirementGone(f, ledger, true);
            var rows = await Rows(f);
            Assert.Equal(4, rows.Count);
            Assert.All(rows, row => Assert.True(row.RowKey is "quota" or "gate" or "repository:123456"));
            Assert.Equal(1L, (await Read(f, ledger.PartitionKey, "quota")).GetInt64("CandidateCount"));
        }
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        using var next = await f.Send("/v2/enrollments", Request(Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task Retirement_two_workers_race_real_commits_and_refund_once(int pausedBatch)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var ledger = await RetirementEligible(f, false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = 0;
        var stale = Retirement(f, ledger, f.Storage.CreateClient(new ObservedTransport { Before = async message =>
        {
            if (!ObservedTransport.IsBatch(message) || ++batches != pausedBatch) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(12), Token);
        }}));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            await Retirement(f, ledger);
            release.TrySetResult();
            var conflict = await Assert.ThrowsAsync<TableTransactionFailedException>(() => stale);
            Assert.Contains(conflict.Status, new[] { 404, 412 });
            await RetirementGone(f, ledger, false);
            await Retirement(f, ledger);
            await RetirementGone(f, ledger, false);
        }
        finally { release.TrySetResult(); try { await stale; } catch (TableTransactionFailedException) { } }
    }

    [Fact]
    public async Task Retirement_old_generation_cannot_touch_reallocated_identical_enrollment_identity()
    {
        await using var f = await EnrollmentFixture.CreateAsync(settings: new() { ["Broker:RetainedEnrollmentLimit"] = "1" });
        var old = await RetirementEligible(f, true);
        await Retirement(f, old);
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        using var replacement = await f.Send("/v2/enrollments", Request(old.RowKey[8..]));
        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
        var current = await Read(f, old.PartitionKey, old.RowKey);
        var pending = await Read(f, RevocationFixture.Partition, old.RowKey);
        Assert.NotEqual(old.GetString("Generation"), current.GetString("Generation"));
        await Assert.ThrowsAsync<IssuanceDenied>(() => Retirement(f, old));
        await Assert.ThrowsAsync<IssuanceDenied>(() => new BrokerPayloadRedaction(f.Storage.Table, Options(f), f.Clock)
            .RedactEnrollmentAsync(old.RowKey[8..], old.GetString("Generation")!, Token));
        Assert.Equal(current.ETag, (await Read(f, current.PartitionKey, current.RowKey)).ETag);
        Assert.Equal(pending.ETag, (await Read(f, pending.PartitionKey, pending.RowKey)).ETag);
        Assert.Equal(1L, (await Read(f, current.PartitionKey, "quota")).GetInt64("RetainedEnrollmentCount"));
    }

    private static string RetainedCounter(bool enrollment) => enrollment ? "RetainedEnrollmentCount" : "RetainedIssuanceCount";
    private static string Target(TableEntity ledger, bool enrollment) => enrollment ? ledger.RowKey : ledger.GetString("AttemptRowKey")!;
    private static string Release(TableEntity ledger, bool enrollment) => "release:" + (enrollment ? ledger.RowKey : ledger.GetString("OperationId"));
    private static Task Retirement(EnrollmentFixture f, TableEntity ledger, TableClient? client = null) => ledger.PartitionKey == "global:enrollment-v1"
        ? Cleanup(f, client).ReclaimEnrollmentAsync(ledger.RowKey[8..], ledger.GetString("Generation")!, Token) : Reclaim(f, ledger, client);
    private static async Task RetirementGone(EnrollmentFixture f, TableEntity ledger, bool enrollment)
    {
        await AssertAbsent(f, ledger.PartitionKey, ledger.RowKey);
        await AssertAbsent(f, RevocationFixture.Partition, Target(ledger, enrollment));
        await AssertAbsent(f, RevocationFixture.Partition, Release(ledger, enrollment));
        Assert.Equal(0L, (await Read(f, ledger.PartitionKey, "quota")).GetInt64(RetainedCounter(enrollment)));
    }
    private static async Task<string> Body(HttpMessage message)
    {
        using var stream = new MemoryStream();
        if (message.Request.Content is not null) await message.Request.Content.WriteToAsync(stream, Token);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    private static async Task<TableEntity> UnpublishedEnrollment(EnrollmentFixture f, string? id = null)
    {
        var interrupted = false;
        await f.RestartAsync(new ObservedTransport { After = message =>
        {
            if (ObservedTransport.IsBatch(message) && !interrupted) { interrupted = true; throw new HttpRequestException("Lost new allocation response"); }
            return Task.CompletedTask;
        }});
        id ??= Guid.NewGuid().ToString("N");
        using var response = await f.Send("/v2/enrollments", Request(id));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(interrupted);
        await f.RestartAsync();
        return await Read(f, "global:enrollment-v1", "pending:" + id);
    }
    private static async Task<TableEntity> RetirementEligible(EnrollmentFixture f, bool enrollment, bool lateSettlement = false)
    {
        TableEntity ledger;
        DateTimeOffset resolved;
        if (enrollment)
        {
            ledger = await UnpublishedEnrollment(f);
            Assert.Equal(1L, ledger.GetInt64("ReclamationVersion"));
            resolved = ledger.GetDateTimeOffset("Deadline")!.Value;
        }
        else
        {
            _ = await Start(f);
            ledger = Assert.Single(await IssuanceLedgers(f));
            resolved = (await Read(f, RevocationFixture.Partition, Target(ledger, false))).GetDateTimeOffset("ExpiresAt")!.Value.AddMinutes(5);
        }
        f.Clock.Advance(resolved.AddDays(lateSettlement ? 31 : 0) - f.Clock.GetUtcNow());
        await Retirement(f, ledger);
        if (!lateSettlement) f.Clock.Advance(TimeSpan.FromDays(30));
        return ledger;
    }
}
