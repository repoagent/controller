using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class IssuanceFaultTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    // Six-minute runtime leases enter the five-minute renewal margin at the next test minute.
    // These cases exercise new mints; published-lease reuse is covered separately.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Competing_global_reservation_retries_before_observation_without_duplicate_charges(bool existingQuota)
    {
        await using var f = await IssuanceFixture.CreateAsync(runtimeLifetime: TimeSpan.FromMinutes(6));
        // Keep the two-second contention delay inside one fixed UTC quota window.
        var midMinute = DateTimeOffset.FromUnixTimeSeconds(f.Clock.GetUtcNow().ToUnixTimeSeconds() / 60 * 60 + 90);
        f.Clock.Advance(midMinute - f.Clock.GetUtcNow());
        var competingGrant = await SeedCompetingRepository(f);
        if (existingQuota)
        {
            Assert.Equal(200, (await f.IssueAsync(Token)).Status);
            f.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        var reservedAt = f.Clock.GetUtcNow();
        var priorAttempts = (await f.AttemptsAsync()).Select(a => a.RowKey).ToHashSet();
        TableEntity? competingAttempt = null;
        var injected = false;
        var conflictStatus = 0;
        var firstBatch = false;
        var transport = new ObservedTransport
        {
            Before = async message =>
            {
                if (!ObservedTransport.IsBatch(message) || injected) return;
                injected = firstBatch = true;
                // A real admission in another repository wins the shared global quota/slot.
                using var work = new IssuanceWork(f.Clock);
                (_, competingAttempt) = await f.Store().ReserveAsync(competingGrant, "verification", work);
                f.Clock.Advance(TimeSpan.FromSeconds(2));
            },
            After = async message =>
            {
                if (!ObservedTransport.IsBatch(message) || !firstBatch) return;
                firstBatch = false;
                // Table batches return202 with the definite transaction failure inside the body.
                var buffered = new MemoryStream();
                await message.Response.ContentStream!.CopyToAsync(buffered, Token);
                var body = System.Text.Encoding.UTF8.GetString(buffered.ToArray());
                buffered.Position = 0;
                message.Response.ContentStream = buffered;
                conflictStatus = body.Contains("HTTP/1.1 409", StringComparison.Ordinal) ? 409
                    : body.Contains("HTTP/1.1 412", StringComparison.Ordinal) ? 412 : 0;
            }
        };
        var result = await f.IssueAsync(Token, transport);
        Assert.True(injected);
        Assert.Equal(412, conflictStatus); // Offline schema already created the global quota.
        Assert.Equal("reserved", competingAttempt!.GetString("State"));
        Assert.Equal(200, result.Status);
        var slots = existingQuota ? 5 : 4;
        var global = await GlobalQuota(f);
        Assert.Equal(slots, global.GetInt64("Slots"));
        Assert.Equal(existingQuota ? 2 : 3, global.GetInt64("Admissions"));
        var rows = await GlobalRows(f);
        Assert.Equal(slots, rows.Count(e => e.RowKey.StartsWith("slot:", StringComparison.Ordinal)));
        Assert.Single(rows, e => e.RowKey.StartsWith("slot:", StringComparison.Ordinal) && e.TryGetValue("OperationId", out var id) && (string)id == competingAttempt.GetString("OperationId"));
        var attempts = await f.AttemptsAsync();
        var admitted = attempts.Where(a => !priorAttempts.Contains(a.RowKey)).ToList();
        Assert.Equal(existingQuota ? 1 : 2, admitted.Count);
        foreach (var attempt in admitted)
            Assert.Single(rows, e => e.RowKey.StartsWith("slot:", StringComparison.Ordinal) && e.TryGetValue("OperationId", out var id) && (string)id == attempt.GetString("OperationId"));
        var retried = Assert.Single(admitted, a => a.GetString("Purpose") == (existingQuota ? "runtime" : "verification"));
        Assert.Equal(reservedAt, retried.GetDateTimeOffset("ReservedAt"));
        Assert.Equal(reservedAt.AddSeconds(-60), retried.GetDateTimeOffset("JwtIssuedAt"));
        Assert.Equal(reservedAt.AddSeconds(120), retried.GetDateTimeOffset("JwtExpiresAt"));
        Assert.Equal(existingQuota ? 3 : 2, f.Signing.Calls);
        Assert.Equal(existingQuota ? 3 : 2, f.GitHub.Mints);
        Assert.Equal(existingQuota ? 16 : 8, f.GitHub.Reads);
        Assert.Equal(1, f.GitHub.VerifierMints);
    }

    [Theory]
    [InlineData("gate")]
    [InlineData("device")]
    public async Task Transient_admission_conflict_rereads_authority_before_first_approval_observation(string changed)
    {
        await using var f = await IssuanceFixture.CreateAsync(runtimeLifetime: TimeSpan.FromMinutes(6));
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var reservedAt = f.Clock.GetUtcNow();
        var batches = 0;
        var transport = new ObservedTransport { Before = async message =>
        {
            // Conflict the repository admission before any approval read or provider mint.
            if (!ObservedTransport.IsBatch(message) || ++batches != 2) return;
            var row = changed == "gate" ? await f.GateAsync() : await f.DeviceAsync();
            row["ConcurrentObservation"] = "one harmless competing write";
            await f.Storage.Table.UpdateEntityAsync(row, row.ETag, TableUpdateMode.Replace, Token);
            f.Clock.Advance(TimeSpan.FromSeconds(2));
        }};
        Assert.Equal(200, (await f.IssueAsync(Token, transport)).Status);
        Assert.Equal(16, f.GitHub.Reads);
        Assert.Equal(3, f.GitHub.Mints);
        Assert.Equal(2, (await f.AttemptsAsync()).Count(a => a.GetString("State") == "released"));
        Assert.Equal(4, (await GlobalQuota(f)).GetInt64("Slots"));
        var slot = Assert.Single(await GlobalRows(f), e => e.RowKey == "slot:003");
        var attempt = Assert.Single(await f.AttemptsAsync(), a => a.GetString("OperationId") == slot.GetString("OperationId"));
        Assert.Equal(reservedAt, attempt.GetDateTimeOffset("ReservedAt"));
        Assert.Equal(reservedAt.AddSeconds(-60), attempt.GetDateTimeOffset("JwtIssuedAt"));
        Assert.Equal(reservedAt.AddSeconds(120), attempt.GetDateTimeOffset("JwtExpiresAt"));
        var quota = (await f.Storage.Table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "quota", cancellationToken: Token)).Value;
        Assert.Equal(1, quota.GetInt64("Admissions"));
        Assert.Equal(8, quota.GetInt64("Reads"));
        Assert.Equal(1, quota.GetInt64("Signs"));
        Assert.Equal(1, quota.GetInt64("Mints"));
    }

    [Fact]
    public async Task Admission_conflict_budget_is_shared_by_verifier_and_runtime_without_duplicate_global_charges()
    {
        await using var f = await IssuanceFixture.CreateAsync(runtimeLifetime: TimeSpan.FromMinutes(6));
        var reservedAt = f.Clock.GetUtcNow();
        var batches = 0;
        var conflicts = 0;
        var transport = new ObservedTransport { Before = async message =>
        {
            if (!ObservedTransport.IsBatch(message)) return;
            // Two verifier-admission conflicts precede its successful third evaluation.
            // Runtime's first admission consumes the fourth shared evaluation and conflicts too.
            if (++batches is not (2 or 3 or 8)) return;
            var gate = await f.GateAsync();
            gate["ConcurrentObservation"] = ++conflicts;
            await f.Storage.Table.UpdateEntityAsync(gate, gate.ETag, TableUpdateMode.Replace, Token);
            f.Clock.Advance(TimeSpan.FromSeconds(1));
        }};
        Assert.Equal(503, (await f.IssueAsync(Token, transport)).Status);
        Assert.Equal(3, conflicts);
        Assert.Equal(8, batches);
        Assert.Equal(1, f.Signing.Calls);
        Assert.Equal(1, f.GitHub.Mints);
        Assert.Equal(0, f.GitHub.Reads);
        var verifier = Assert.Single(await f.AttemptsAsync());
        Assert.Equal("verification", verifier.GetString("Purpose"));
        Assert.Equal("known", verifier.GetString("State"));
        Assert.Equal(reservedAt, verifier.GetDateTimeOffset("ReservedAt"));
        Assert.Equal(reservedAt.AddSeconds(120), verifier.GetDateTimeOffset("JwtExpiresAt"));
        Assert.Equal(3, (await GlobalQuota(f)).GetInt64("Slots"));
        Assert.Equal(2, (await f.GateAsync()).GetInt64("OutstandingTokenCount"));
        Assert.False((await f.DeviceAsync()).ContainsKey("LastActivityAt"));
        var quota = (await f.Storage.Table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "quota", cancellationToken: Token)).Value;
        Assert.Equal(1, quota.GetInt64("Admissions"));
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("secret")]
    public async Task Changed_authority_during_admission_retry_cannot_inherit_the_original_authentication(string changed)
    {
        await using var f = await IssuanceFixture.CreateAsync(runtimeLifetime: TimeSpan.FromMinutes(6));
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        var lastActivity = (await f.DeviceAsync()).GetDateTimeOffset("LastActivityAt");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var batches = 0;
        var changedAuthority = false;
        var transport = new ObservedTransport { Before = async message =>
        {
            if (!ObservedTransport.IsBatch(message) || ++batches != 2) return;
            if (changed == "revoked")
                Assert.Equal(204, await new DeviceRevocation(f.Storage.Table, f.Options, f.Clock).RevokeAsync(f.Device.DeviceId, f.Device.Secret, Token));
            else
            {
                var device = await f.DeviceAsync();
                device["SecretHash"] = new string('0', 64);
                await f.Storage.Table.UpdateEntityAsync(device, device.ETag, TableUpdateMode.Replace, Token);
            }
            changedAuthority = true;
        }};
        Assert.NotEqual(200, (await f.IssueAsync(Token, transport)).Status);
        Assert.True(changedAuthority);
        Assert.Equal(8, f.GitHub.Reads);
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal(2, f.Signing.Calls);
        Assert.Equal(2, (await f.AttemptsAsync()).Count);
        Assert.Equal(4, (await GlobalQuota(f)).GetInt64("Slots"));
        Assert.Equal(lastActivity, (await f.DeviceAsync()).GetDateTimeOffset("LastActivityAt"));
    }

    [Theory]
    [InlineData(1, false, null)]
    [InlineData(1, true, null)]
    [InlineData(2, false, null)]
    [InlineData(2, true, "reserved")]
    [InlineData(3, false, "reserved")]
    [InlineData(3, true, "sending")]
    [InlineData(4, false, "settled")]
    [InlineData(4, true, "settled")]
    [InlineData(5, false, "settled")]
    [InlineData(5, true, "released")]
    public async Task Lost_Table_transport_never_refunds_reservations_or_replays_an_attempt(int stage, bool afterCommit, string? expectedState)
    {
        await using var f = await IssuanceFixture.CreateAsync(runtimeLifetime: TimeSpan.FromMinutes(6));
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var beforeIds = (await f.AttemptsAsync()).Select(a => a.RowKey).ToHashSet();
        var batches = 0;
        var injected = false;
        Task Observe(Azure.Core.HttpMessage message)
        {
            if (ObservedTransport.IsBatch(message) && ++batches == stage)
            {
                injected = true;
                throw new HttpRequestException("One lost Table transport outcome");
            }
            return Task.CompletedTask;
        }
        var transport = afterCommit ? new ObservedTransport { After = Observe } : new ObservedTransport { Before = Observe };
        Assert.Equal(503, (await f.IssueAsync(Token, transport)).Status);
        Assert.True(injected);
        var attempts = await f.AttemptsAsync();
        var added = attempts.Where(a => !beforeIds.Contains(a.RowKey)).ToList();
        TableEntity? preserved = null;
        if (expectedState is null) Assert.Empty(added);
        else
        {
            preserved = Assert.Single(added);
            Assert.Equal(expectedState, preserved.GetString("State"));
            Assert.Equal("runtime", preserved.GetString("Purpose"));
        }
        var slots = stage == 1 && !afterCommit ? 3 : 4;
        Assert.Equal(slots, (await GlobalQuota(f)).GetInt64("Slots"));
        Assert.Equal(slots, (await GlobalRows(f)).Count(e => e.RowKey.StartsWith("slot:", StringComparison.Ordinal)));
        Assert.Equal(stage >= 4 ? 3 : 2, f.GitHub.Mints);
        Assert.Equal(stage >= 4 && !(stage == 5 && afterCommit) ? 1 : 0, f.GitHub.Revokes);
        var activity = (await f.DeviceAsync()).GetDateTimeOffset("LastActivityAt");
        Assert.Equal(stage == 5 && afterCommit ? f.Clock.GetUtcNow() : f.Clock.GetUtcNow().AddMinutes(-1), activity);

        // A new issuer reloads encrypted credentials from disk and durable state; it owns a new operation.
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        if (preserved is not null)
        {
            var recovered = (await f.Storage.Table.GetEntityAsync<TableEntity>(preserved.PartitionKey, preserved.RowKey, cancellationToken: Token)).Value;
            Assert.Equal(preserved.ETag, recovered.ETag);
            Assert.Equal(preserved.GetDateTimeOffset("JwtExpiresAt"), recovered.GetDateTimeOffset("JwtExpiresAt"));
        }
        Assert.Equal(stage >= 4 ? 4 : 3, f.GitHub.Mints);
        Assert.Equal(1, f.GitHub.VerifierMints);
        Assert.Equal(slots + 1, (await GlobalQuota(f)).GetInt64("Slots"));
    }

    [Fact]
    public async Task Concurrent_verifier_acquisition_never_duplicates_the_inflight_mint()
    {
        await using var f = await IssuanceFixture.CreateAsync(runtimeLifetime: TimeSpan.FromMinutes(6));
        var signingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueSigning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Signing.Before = async () =>
        {
            if (f.Signing.Calls != 1) return;
            signingEntered.SetResult();
            await continueSigning.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        };
        var first = f.IssueAsync(Token);
        try
        {
            await signingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(503, (await f.IssueAsync(Token)).Status);
            Assert.Equal("reserved", Assert.Single(await f.AttemptsAsync()).GetString("State"));
            Assert.Equal(0, f.GitHub.Mints);
            Assert.Equal(2, (await GlobalQuota(f)).GetInt64("Slots"));
        }
        finally { continueSigning.TrySetResult(); }
        Assert.Equal(200, (await first).Status);
        Assert.Equal(1, f.GitHub.VerifierMints);
        Assert.Equal(2, f.GitHub.Mints);
    }

    [Fact]
    public async Task Revocation_after_admission_stops_a_late_worker_before_mint()
    {
        await using var f = await IssuanceFixture.CreateAsync(runtimeLifetime: TimeSpan.FromMinutes(6));
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var revoked = false;
        f.GitHub.Before = async request =>
        {
            if (f.GitHub.Reads == 12 && request.RequestUri!.AbsolutePath.Contains("/variables/", StringComparison.Ordinal))
            {
                Assert.Equal(204, await new DeviceRevocation(f.Storage.Table, f.Options, f.Clock).RevokeAsync(f.Device.DeviceId, f.Device.Secret, Token));
                revoked = true;
            }
            return null;
        };
        Assert.NotEqual(200, (await f.IssueAsync(Token)).Status);
        Assert.True(revoked);
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal("reserved", Assert.Single(await f.AttemptsAsync(), a => a.GetString("State") == "reserved").GetString("State"));
        Assert.Equal("closed", (await f.GateAsync()).GetString("State"));
        Assert.Equal(f.Clock.GetUtcNow().AddMinutes(-1), (await f.DeviceAsync()).GetDateTimeOffset("LastActivityAt"));
        Assert.Equal(4, (await GlobalQuota(f)).GetInt64("Slots"));
        Assert.Equal(401, (await f.IssueAsync(Token)).Status);
        Assert.Equal(2, f.GitHub.Mints);
    }

    [Fact]
    public async Task Repository_capacity_stays_bounded_across_restarts_and_minute_windows()
    {
        await using var f = await IssuanceFixture.CreateAsync(runtimeLifetime: TimeSpan.FromMinutes(6));
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(200, (await f.IssueAsync(Token)).Status);
            f.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.Equal(8, (await f.GateAsync()).GetInt64("OutstandingTokenCount"));
        Assert.Equal(7, (await f.AttemptsAsync()).Count);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(429, (await f.IssueAsync(Token)).Status);
            f.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.Equal(7, f.GitHub.Mints);
        Assert.Equal(7, f.Signing.Calls);
        Assert.Equal(7, (await f.AttemptsAsync()).Count);
        Assert.Equal(12, (await GlobalQuota(f)).GetInt64("Slots"));
    }

    [Fact]
    public async Task Global_capacity_and_fixed_window_cost_bound_a_restarted_request_storm()
    {
        await using var f = await IssuanceFixture.CreateAsync(runtimeLifetime: TimeSpan.FromMinutes(6));
        var limits = new IssuanceLimits(GlobalAttempts: 4);
        Assert.Equal(200, (await f.Issuer(limits: limits).IssueAsync(f.Device.DeviceId, f.Device.Secret, Token)).Status);
        // Model a historical device without the optional published-lease pointer.
        var legacy = await f.DeviceAsync();
        legacy.Remove("RuntimeAttempt");
        legacy.Remove("RuntimeExpiresAt");
        await f.Storage.Table.UpdateEntityAsync(legacy, legacy.ETag, TableUpdateMode.Replace, Token);
        // The verifier and first runtime consume this minute's repository signing budget.
        for (var i = 0; i < 12; i++)
            Assert.Equal(429, (await f.Issuer(limits: limits).IssueAsync(f.Device.DeviceId, f.Device.Secret, Token)).Status);
        var quota = await GlobalQuota(f);
        Assert.Equal(4, quota.GetInt64("Slots"));
        Assert.Equal(3, quota.GetInt64("Admissions"));
        Assert.Equal(2, (await f.AttemptsAsync()).Count);
        Assert.Equal(2, f.Signing.Calls);
        Assert.Equal(2, f.GitHub.Mints);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(429, (await f.Issuer(limits: limits).IssueAsync(f.Device.DeviceId, f.Device.Secret, Token)).Status);
        Assert.Equal(quota.ETag, (await GlobalQuota(f)).ETag);
        var rows = await GlobalRows(f);
        Assert.Equal(9, rows.Count); // Quota, four charged slots and four immutable ledgers.
        Assert.Equal(new[] { "slot:000", "slot:001", "slot:002", "slot:003" },
            rows.Where(e => e.RowKey.StartsWith("slot:", StringComparison.Ordinal)).Select(e => e.RowKey).Order());
        Assert.Equal(4, rows.Count(e => e.RowKey.StartsWith("operation:", StringComparison.Ordinal)));
        Assert.Equal("one", Assert.Single(rows, e => e.RowKey == "slot:000").GetString("OperationId"));
        Assert.Equal("outstanding", (await f.Storage.Table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "attempt:one", cancellationToken: Token)).Value.GetString("State"));
    }

    private static async Task<TableEntity> GlobalQuota(IssuanceFixture f) =>
        (await f.Storage.Table.GetEntityAsync<TableEntity>("global:issuance-v1", "quota", cancellationToken: Token)).Value;
    private static async Task<List<TableEntity>> GlobalRows(IssuanceFixture f)
    {
        var rows = new List<TableEntity>();
        await foreach (var row in f.Storage.Table.QueryAsync<TableEntity>(e => e.PartitionKey == "global:issuance-v1", cancellationToken: Token)) rows.Add(row);
        var slots = rows.Where(r => r.RowKey.StartsWith("slot:", StringComparison.Ordinal)).ToList();
        var ledgers = rows.Where(r => r.RowKey.StartsWith("operation:", StringComparison.Ordinal)).ToList();
        Assert.Equal(slots.Count, ledgers.Count);
        Assert.Equal(ledgers.Count, rows.Single(r => r.RowKey == "quota").GetInt64("RetainedIssuanceCount"));
        foreach (var slot in slots)
        {
            var ledger = Assert.Single(ledgers, r => r.RowKey == "operation:" + slot.GetString("OperationId"));
            Assert.Equal(slot.GetString("Generation"), ledger.GetString("Generation"));
            Assert.Equal(slot.GetDateTimeOffset("ReservedAt"), ledger.GetDateTimeOffset("ReservedAt"));
            Assert.Equal(slot.GetDateTimeOffset("ReservedAt")!.Value.AddHours(24), ledger.GetDateTimeOffset("PayloadPurgeAt"));
            Assert.Equal(4, ledger.GetInt64("RowCredits"));
        }
        Assert.Equal("attempt:one", Assert.Single(ledgers, r => r.RowKey == "operation:one").GetString("AttemptRowKey"));
        return rows;
    }

    private static async Task<GrantSnapshot> SeedCompetingRepository(IssuanceFixture f)
    {
        const string repository = "654321", partition = "repository:" + repository;
        static TableEntity Copy(TableEntity source, string partition)
        {
            var copy = new TableEntity(partition, source.RowKey);
            foreach (var (key, value) in source)
                if (key is not ("PartitionKey" or "RowKey" or "Timestamp" or "odata.etag")) copy[key] = value;
            copy["RepositoryId"] = repository;
            return copy;
        }
        var gate = Copy(await f.GateAsync(), partition);
        var device = Copy(await f.DeviceAsync(), partition);
        // This competitor has an enrollment but no inherited unknown attempt.
        // The primary repository's original unknown authority remains charged.
        gate["OutstandingTokenCount"] = 0L;
        device["OutstandingTokenCount"] = 0L;
        await f.Storage.ChargeOfflineEnrollmentAsync(repository, f.Device.DeviceId, f.Clock.GetUtcNow());
        device["Repository"] = "owner/competing";
        device["Marker"] = ApprovalMarker.Create(new(gate, device));
        await f.Storage.Table.SubmitTransactionAsync(new[]
        {
            new TableTransactionAction(TableTransactionActionType.Add, gate),
            new TableTransactionAction(TableTransactionActionType.Add, device)
        }, Token);
        return await f.Store().SnapshotAsync(partition, f.Device.DeviceId, Token);
    }
}
