using System.Net;
using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

// Cleanup runs over retained allocations that real journeys left behind: what each run redacts, reclaims and retires,
// how a failed or competing run keeps its position, and the request accounting its summary reports.
public sealed partial class BrokerCleanupTests
{
    // One cleanup run, with the summary's request accounting checked against what the run did.
    private static async Task<CleanupRun> Run(InMemoryBroker f)
    {
        var lists = f.State.Lists;
        var run = await f.CleanupAsync();
        AssertAccounting(run, f.State.Lists - lists);
        return run;
    }

    // The summary reports the requests the run's budget counted, within the run's bounds, and the listing pages it
    // asked the state for (null when another run listed at the same time).
    private static void AssertAccounting(CleanupRun run, int? listed)
    {
        var summary = run.Summary;
        var dispatches = run.Int("sdkTableDispatches");
        var batches = run.Int("sdkBatchDispatches");
        Assert.Equal(run.Budget.Dispatches, dispatches);
        Assert.Equal(run.Budget.BatchDispatches, batches);
        Assert.InRange(dispatches, 0, 128);
        Assert.InRange(batches, 0, 32);
        Assert.InRange(batches, 0, dispatches);
        if (listed is not null) Assert.Equal(listed, run.Int("logicalQueryPages"));
        Assert.InRange(run.Int("logicalQueryPages"), 0, 16);
        foreach (var oldName in new[] { "tableRequests", "egts", "pages" }) Assert.False(summary.TryGetProperty(oldName, out _));
        var envelope = summary.GetProperty("reviewedHttp11Envelope");
        var sends = run.Budget.MaxSendsPerDispatch;
        Assert.Equal(run.Budget.EnvelopeScope, envelope.GetProperty("scope").GetString());
        Assert.Equal(sends, envelope.GetProperty("maxSendsPerSdkDispatch").GetInt32());
        Assert.Equal(sends * dispatches, envelope.GetProperty("tableRequestUpperBound").GetInt32());
        Assert.Equal(sends * batches, envelope.GetProperty("batchRequestUpperBound").GetInt32());
        Assert.InRange(envelope.GetProperty("tableRequestUpperBound").GetInt32(), 0, sends * 128);
        Assert.InRange(envelope.GetProperty("batchRequestUpperBound").GetInt32(), 0, sends * 32);
        Assert.InRange(summary.GetProperty("elapsedMilliseconds").GetInt64(), 0, 60_000);
    }

    // Whether the stored row holds the property; the row must exist.
    private static bool Stores(InMemoryBroker f, StateKey key, string property) =>
        (f.State.Row(key) ?? throw new InvalidOperationException("No such record: " + key)).ContainsKey(property);

    private static bool Incomplete(CleanupRun run) => run.Summary.GetProperty("incompleteCoverage").GetBoolean();

    [Theory]
    [InlineData("polling", false, false)]
    [InlineData("polling", true, false)]
    [InlineData("polling-clone", false, false)]
    [InlineData("polling-clone", true, false)]
    [InlineData("polling", true, true)]
    [InlineData("polling-clone", true, true)]
    public async Task Cleanup_preserves_binding_and_releases_only_settled_terminal_owners(string profile, bool acknowledge, bool revoke)
    {
        var f = new InMemoryBroker();
        var journey = acknowledge ? await Retained.ActiveAgent(f, profile) : await Retained.CompletedAgent(f, profile);
        var runtimeVersion = profile == "polling-clone" ? 2 : 1;
        if (revoke) Assert.Equal(HttpStatusCode.NoContent, (await f.Revoke(journey.Device, journey.Secret, runtimeVersion)).StatusCode);
        f.Clock.Advance(TimeSpan.FromDays(2));
        var pendingKey = PendingControllerEnrollment.Key(Retained.Repository, journey.Id);
        var enrollmentKey = ControllerEnrollment.Key(Retained.Repository, journey.Device);
        var gateKey = RepositoryGate.Key(Retained.Repository);
        var active = f.State.Get(enrollmentKey)!;
        Assert.Equal(!acknowledge, Stores(f, pendingKey, "ProtectedCompletion"));
        Assert.Equal(acknowledge, active.ExpiresAt > f.Clock.GetUtcNow());
        var ledgers = Retained.IssuanceLedgers(f).Select(ledger => ledger.Value).ToList();
        Assert.NotEmpty(ledgers);
        Assert.Contains(ledgers, ledger => Stores(f, Retained.Attempt(ledger), "ProtectedToken"));

        for (var run = 0; run < 8; run++)
        {
            var result = await Run(f);
            Assert.Equal(0, result.Exit);
            if (!Incomplete(result) && (acknowledge && !revoke || !Stores(f, gateKey, "OwnerDeviceId"))) break;
        }

        Assert.False(Stores(f, pendingKey, "ProtectedCompletion"));
        Assert.False(Stores(f, pendingKey, "RecoveryHash"));
        var preserved = f.State.Get(enrollmentKey)!;
        Assert.Equal(active.ExpiresAt, preserved.ExpiresAt);
        Assert.Equal(active.PolicyVersion, preserved.PolicyVersion);
        Assert.Equal(active.Profile, preserved.Profile);
        Assert.Equal(active.Acknowledged, preserved.Acknowledged);
        if (acknowledge && !revoke)
        {
            Assert.NotNull(preserved.ControllerSecretHash);
            Assert.Equal(active.ControllerSecretHash, preserved.ControllerSecretHash);
            Assert.Equal(active.LastActivityAt, preserved.LastActivityAt);
        }
        else
        {
            Assert.False(Stores(f, enrollmentKey, "SecretHash"));
            Assert.Equal("revoked", preserved.State);
            Assert.Equal("vacant", f.Gate.State);
            Assert.False(Stores(f, gateKey, "OwnerDeviceId"));
            Assert.Equal(journey.Device, f.State.Get(RevocationReceipt.Key(Retained.Repository, journey.Device))!.EnrollmentId);
        }
        foreach (var ledger in ledgers)
        {
            Assert.False(Stores(f, Retained.Attempt(ledger), "ProtectedToken"));
            var attempt = f.State.Get(Retained.Attempt(ledger))!;
            Assert.Equal(ledger.PolicyVersion, attempt.PolicyVersion);
            Assert.Equal(ledger.PayloadPurgeAt, attempt.PayloadPurgeAt);
        }
        if (acknowledge && !revoke)
        {
            // The current grant still issues under the current epoch, so the restored epoch's denial is the fence.
            Assert.Equal(HttpStatusCode.OK, (await f.Issue(journey.Device, journey.Secret, runtimeVersion)).StatusCode);
            var mints = f.GitHub.Mints;
            var signs = f.GitHub.Authorizations;
            f.DeploymentEpoch = "restored-epoch";
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Issue(journey.Device, journey.Secret, runtimeVersion)).StatusCode);
            Assert.Equal(mints, f.GitHub.Mints);
            Assert.Equal(signs, f.GitHub.Authorizations);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task New_epoch_cleanup_redacts_payload_without_settling_unknown_authority(bool missingDeadline, bool sending)
    {
        var f = new InMemoryBroker();
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        f.GitHub.BeforeRevoke = _ => throw new GitHubUnavailable("Unobserved revocation");
        if (sending) f.GitHub.BeforeMint = _ => throw new GitHubUnavailable("Unknown send outcome");
        // GitHub's answer carries the token, but neither a readable expiry nor any scope.
        else f.GitHub.MintAnswer = (_, _) => GitHubRead.Found(new MintedGitHubToken("unsettled-test-token", null, null, null, null));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(Retained.AgentRequest(Guid.NewGuid().ToString("N")), 2)).StatusCode);
        var ledger = Assert.Single(Retained.IssuanceLedgers(f)).Value;
        var attemptKey = Retained.Attempt(ledger);
        Assert.Equal(!sending, Stores(f, attemptKey, "ProtectedToken"));
        Assert.Equal(sending ? "sending" : "known", f.State.Get(attemptKey)!.State);
        if (missingDeadline) f.State.Change(attemptKey, row =>
        {
            row.Remove("ReservedAt");
            row.Remove("PayloadPurgeAt");
        });
        var attempt = f.State.Row(attemptKey)!;
        var quota = f.State.Get(IssuanceQuota.Key)!;
        f.Clock.Advance(TimeSpan.FromDays(2));
        f.DeploymentEpoch = "new-epoch";
        var unknownObserved = 0;
        var unsettledObserved = 0;
        for (var run = 0; run < 8; run++)
        {
            var result = await f.CleanupAsync();
            Assert.Equal(0, result.Exit);
            unknownObserved += result.Int("unknownDeadlineCount");
            unsettledObserved += result.Int("unsettledAllocationsObserved");
            if (!Incomplete(result)) break;
        }
        var after = f.State.Row(attemptKey)!;
        Assert.False(after.ContainsKey("ProtectedToken"));
        Assert.False(after.ContainsKey("SettledAt"));
        foreach (var (name, value) in attempt)
            if (name != "ProtectedToken") Assert.Equal(value, after.GetValueOrDefault(name));
        var charged = f.State.Get(IssuanceQuota.Key)!;
        Assert.Equal(quota.Slots, charged.Slots);
        Assert.Equal(quota.RetainedIssuanceCount, charged.RetainedIssuanceCount);
        Assert.True(unsettledObserved > 0);
        if (missingDeadline) Assert.True(unknownObserved > 0);
    }

    [Fact]
    public async Task Failed_stream_keeps_its_position_while_other_family_redacts_and_transport_counts_remain_honest()
    {
        var f = new InMemoryBroker();
        await Retained.StartAgent(f);
        var attemptLedger = Assert.Single(Retained.IssuanceLedgers(f)).Value;
        f.Clock.Advance(TimeSpan.FromDays(2));
        // Every listing of pending enrollment ledgers fails; issuance operations list in their own scope.
        f.State.BeforeList = scope => scope == IssuanceCapacity.EnrollmentScope
            ? throw new BrokerStateUnavailable("The enrollment listing does not answer") : Task.CompletedTask;
        var result = await Run(f);
        Assert.Equal(1, result.Exit);
        Assert.True(Incomplete(result));
        Assert.True(result.Int("failures") > 0);
        Assert.Equal("", f.State.Get(CleanupCursor.Payload)!.Enrollment.LastKey);
        Assert.False(Stores(f, Retained.Attempt(attemptLedger), "ProtectedToken"));
    }

    [Fact]
    public async Task Backlog_beyond_one_page_resumes_across_runs_and_includes_behind_cursor_insertions_next_cycle()
    {
        var f = new InMemoryBroker();
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        var begin = f.Clock.GetUtcNow();
        var keys = new List<StateKey<EnrollmentLedger>>();
        for (var i = 1; i <= 35; i++)
        {
            var id = i.ToString("x32");
            Assert.Equal(HttpStatusCode.OK, (await f.Start(Retained.AgentRequest(id), 2)).StatusCode);
            keys.Add(EnrollmentLedger.Key(id));
            // Real prior capacity release leaves retained audit and due payload behind.
            f.Clock.Advance(TimeSpan.FromHours(2));
            await Retained.ReclaimEnrollment(f, id);
            foreach (var issuance in Retained.IssuanceLedgers(f))
                if (issuance.Value.CapacityState != "released") await Retained.ReclaimIssuance(f, issuance.Value);
        }
        f.Clock.Advance(begin + TimeSpan.FromDays(6) - f.Clock.GetUtcNow());
        Assert.True(f.State.Rows(IssuanceCapacity.EnrollmentScope).Count(row => row.Name.StartsWith("pending:", StringComparison.Ordinal)) > 32,
            "The backlog must not fit one listing page.");
        string? firstLast = null;
        var budgetStops = 0;
        var behind = EnrollmentLedger.Key(new string('0', 32));
        for (var run = 0; run < 80; run++)
        {
            var result = await Run(f);
            Assert.Equal(0, result.Exit);
            foreach (var ages in result.Summary.GetProperty("cursorAndCycleAges").EnumerateArray())
                Assert.True(ages.GetProperty("cycleAgeSeconds").GetDouble() >= ages.GetProperty("cursorAgeSeconds").GetDouble());
            if (result.StopReason == "request-budget")
            {
                budgetStops++;
                Assert.True(Incomplete(result));
                // A whole 40-request turn cannot be admitted once actual use exceeds 88.
                Assert.InRange(result.Int("sdkTableDispatches"), 89, 128);
            }
            var cursor = f.State.Get(CleanupCursor.Payload)!;
            firstLast ??= cursor.Enrollment.LastKey;
            if (run == 0)
            {
                Assert.False(string.IsNullOrEmpty(firstLast));
                Assert.Equal(HttpStatusCode.OK, (await f.Start(Retained.AgentRequest(new string('0', 32)), 2)).StatusCode);
                // Its payload is due at once, but it sorts behind both lanes' positions.
                f.Clock.Advance(f.State.Get(behind)!.PayloadPurgeAt!.Value - f.Clock.GetUtcNow());
            }
            Assert.True(Stores(f, behind, "RecoveryHash"), "A behind-cursor insertion must wait for the next traversal cycle.");
            if (cursor.Enrollment.Cycle > 0) break;
            Assert.True(run < 79, "Bounded runs must eventually finish a finite enrollment cycle.");
        }
        Assert.True(budgetStops > 0);
        Assert.False(string.IsNullOrEmpty(firstLast));
        foreach (var key in keys) Assert.False(Stores(f, key, "RecoveryHash"));
        Assert.Equal(["cleanup:audit-v1", "cleanup:payload-v1"], f.State.Rows(IssuanceCapacity.EnrollmentScope)
            .Where(row => row.Name.StartsWith("cleanup:", StringComparison.Ordinal)).Select(row => row.Name));

        // The first cycle reset its key; the second must include the inserted smaller key.
        for (var run = 0; run < 80; run++)
        {
            Assert.Equal(0, (await Run(f)).Exit);
            if (!Stores(f, behind, "RecoveryHash")) break;
            Assert.True(run < 79);
        }
        Assert.False(Stores(f, behind, "RecoveryHash"));
    }

    [Fact]
    public async Task Lost_committed_cursor_response_is_reconciled_on_restart_without_duplicate_capacity_refund()
    {
        var f = new InMemoryBroker();
        var (id, _) = await Retained.StartAgent(f);
        f.Clock.Advance(TimeSpan.FromDays(2));
        var ledgerKey = EnrollmentLedger.Key(id);
        var dropped = false;
        f.State.AfterCommit = transaction =>
        {
            if (dropped || !transaction.Touches(CleanupCursor.Payload.Name) || !transaction.Writes("EnrollmentLastKey", ledgerKey.Name))
                return Task.CompletedTask;
            dropped = true;
            throw new BrokerStateUnavailable("Lost committed cursor checkpoint response");
        };
        var interrupted = await Run(f);
        Assert.True(dropped, "The committed cursor checkpoint's response must be lost.");
        Assert.Equal(1, interrupted.Exit);
        Assert.True(Incomplete(interrupted));
        Assert.False(Stores(f, ledgerKey, "RecoveryHash"));
        for (var i = 0; i < 8; i++) Assert.Equal(0, (await Run(f)).Exit);
        var quota = f.State.Get(EnrollmentQuota.Key)!;
        Assert.Equal(0L, quota.Count);
        Assert.Equal(1L, quota.RetainedEnrollmentCount);
        var issuance = f.State.Get(IssuanceQuota.Key)!;
        Assert.Equal(0L, issuance.Slots);
        Assert.Equal(1L, issuance.RetainedIssuanceCount);
    }

    [Fact]
    public async Task Competing_runs_use_versions_and_do_not_skip_due_payload()
    {
        var f = new InMemoryBroker();
        // Warm only the two fixed cursors; no allocation is present yet.
        Assert.Equal(0, (await Run(f)).Exit);
        var (id, _) = await Retained.StartAgent(f);
        f.Clock.Advance(TimeSpan.FromDays(2));
        // Each run's first payload cursor transaction is held until both runs read the same cursor version.
        static bool PayloadCursor(StateTransaction transaction) => transaction.Touches(CleanupCursor.Payload.Name);
        var first = new CommitPause(f.State, PayloadCursor);
        var second = new CommitPause(f.State, PayloadCursor);
        var lists = f.State.Lists;
        var a = f.CleanupAsync();
        await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var b = f.CleanupAsync();
        await second.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        first.Release.TrySetResult();
        second.Release.TrySetResult();
        var results = await Task.WhenAll(a, b);
        Assert.True(results.Sum(result => result.Int("conflicts")) >= 1);
        foreach (var result in results) AssertAccounting(result, null);
        Assert.Equal(f.State.Lists - lists, results.Sum(result => result.Int("logicalQueryPages")));
        for (var i = 0; i < 4; i++) Assert.Equal(0, (await Run(f)).Exit);
        Assert.False(Stores(f, EnrollmentLedger.Key(id), "RecoveryHash"));
        Assert.Equal(2, f.State.Rows(IssuanceCapacity.EnrollmentScope).Count(row => row.Name.StartsWith("cleanup:", StringComparison.Ordinal)));
        Assert.Equal(0L, f.State.Get(EnrollmentQuota.Key)!.Count);
    }

    [Fact]
    public async Task Permanent_audit_checkpoint_failure_does_not_starve_urgent_payload_in_either_family()
    {
        var f = new InMemoryBroker();
        var (id, _) = await Retained.StartAgent(f);
        var issuance = Assert.Single(Retained.IssuanceLedgers(f)).Value;
        f.Clock.Advance(TimeSpan.FromDays(2));
        // Every audit checkpoint (a Replace) fails; creating the cursor is an Add, so it may.
        f.State.BeforeCommit = transaction =>
            transaction.Writes.Any(write => write.Key.Name == CleanupCursor.Audit.Name && write.Kind == StateWriteKind.Replace)
                ? throw new BrokerStateUnavailable("The audit checkpoint is never confirmed") : Task.CompletedTask;
        var result = await Run(f);
        Assert.Equal(1, result.Exit);
        Assert.True(Incomplete(result));
        Assert.True(result.Int("payloadFieldsRemovedObserved") >= 3);
        Assert.True(result.Summary.GetProperty("oldestObservedOverduePayloadAgeSeconds").GetDouble() > 0);
        Assert.False(Stores(f, EnrollmentLedger.Key(id), "RecoveryHash"));
        Assert.False(Stores(f, Retained.Attempt(issuance), "ProtectedToken"));
        var audit = f.State.Get(CleanupCursor.Audit)!;
        Assert.Equal("", audit.Enrollment.LastKey);
        Assert.Equal("", audit.Issuance.LastKey);
    }

    [Fact]
    public async Task Slow_storage_requests_stop_at_the_cooperative_deadline_with_incomplete_coverage()
    {
        var f = new InMemoryBroker();
        var timers = new TimerClock(f.Clock);
        var started = f.Clock.GetUtcNow();
        var requests = 0;
        IReadOnlyList<TimeSpan>? due = null;
        // Every store request takes 2.5 seconds. Once 60 seconds have passed the run's timers fire, and the request in
        // flight fails with the cancellation, as a Table request does.
        Task Slow()
        {
            requests++;
            f.Clock.Advance(TimeSpan.FromMilliseconds(2500));
            if (f.Clock.GetUtcNow() - started < TimeSpan.FromSeconds(60)) return Task.CompletedTask;
            due ??= timers.Due;
            timers.Fire();
            throw new OperationCanceledException("The run's deadline cancelled the request in flight.");
        }
        f.State.BeforeRead = _ => Slow();
        f.State.BeforeList = _ => Slow();
        f.State.BeforeCommit = _ => Slow();
        var lists = f.State.Lists;
        var result = await f.CleanupAsync(new ElapsedClock(f.Clock, timers));
        Assert.NotNull(due);
        Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(due));
        AssertAccounting(result, f.State.Lists - lists);
        Assert.Equal(0, result.Exit);
        Assert.Equal("time-budget", result.StopReason);
        Assert.True(Incomplete(result));
        Assert.InRange(result.Summary.GetProperty("elapsedMilliseconds").GetInt64(), 55_000, 65_000);
        Assert.Equal(requests, result.Int("sdkTableDispatches"));
        Assert.InRange(requests, 1, 128);
        Assert.Equal(JsonValueKind.Null, result.Summary.GetProperty("oldestObservedOverduePayloadAgeSeconds").ValueKind);
    }

    [Fact]
    public async Task Eligible_resolved_audit_is_retired_by_cleanup_and_retained_capacity_is_refunded_once()
    {
        var f = new InMemoryBroker();
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        f.GitHub.MintAnswer = (_, token) => GitHubRead.Found(token with { Value = "invalid-scope-test-token", OnlyListedRepositories = false });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(Retained.AgentRequest(Guid.NewGuid().ToString("N")), 2)).StatusCode);
        var ledger = Assert.Single(Retained.IssuanceLedgers(f)).Value;
        var attemptKey = Retained.Attempt(ledger);
        Assert.Equal("settled", f.State.Get(attemptKey)!.State);
        await Retained.ReclaimIssuance(f, ledger);
        Assert.Equal(1L, f.State.Get(IssuanceQuota.Key)!.RetainedIssuanceCount);
        f.Clock.Advance(TimeSpan.FromDays(40));
        var deletionObserved = 0;
        for (var i = 0; i < 12; i++)
        {
            var result = await Run(f);
            Assert.Equal(0, result.Exit);
            deletionObserved += result.Int("deletedRowsObserved");
            if (f.State.Row(IssuanceLedger.Key(ledger.OperationId!)) is null) break;
            Assert.True(i < 11);
        }
        Assert.True(deletionObserved >= 2);
        Assert.Null(f.State.Row(attemptKey));
        Assert.Equal(0, (await Run(f)).Exit);
        var quota = f.State.Get(IssuanceQuota.Key)!;
        Assert.Equal(0L, quota.Slots);
        Assert.Equal(0L, quota.RetainedIssuanceCount);
    }
}
