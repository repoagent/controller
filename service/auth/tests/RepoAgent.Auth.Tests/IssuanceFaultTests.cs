using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class IssuanceFaultTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("gate")]
    [InlineData("device")]
    public async Task Transient_admission_conflict_rereads_authority_before_first_approval_observation(string changed)
    {
        await using var f = await IssuanceFixture.CreateAsync();
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var batches = 0;
        var transport = new ObservedTransport { Before = async message =>
        {
            // Conflict the repository admission before any approval read or provider mint.
            if (!ObservedTransport.IsBatch(message) || ++batches != 2) return;
            var row = changed == "gate" ? await f.GateAsync() : await f.DeviceAsync();
            row["ConcurrentObservation"] = "one harmless competing write";
            await f.Storage.Table.UpdateEntityAsync(row, row.ETag, TableUpdateMode.Replace, Token);
        }};
        Assert.Equal(200, (await f.IssueAsync(Token, transport)).Status);
        Assert.Equal(16, f.GitHub.Reads);
        Assert.Equal(3, f.GitHub.Mints);
        Assert.Equal(2, (await f.AttemptsAsync()).Count(a => a.GetString("State") == "released"));
    }

    [Theory]
    [InlineData(1, false, null)]
    [InlineData(1, true, null)]
    [InlineData(2, false, null)]
    [InlineData(2, true, "reserved")]
    [InlineData(3, false, "reserved")]
    [InlineData(3, true, "sending")]
    [InlineData(4, false, "settled")]
    [InlineData(4, true, "known")]
    [InlineData(5, false, "settled")]
    [InlineData(5, true, "released")]
    public async Task Lost_Table_transport_never_refunds_reservations_or_replays_an_attempt(int stage, bool afterCommit, string? expectedState)
    {
        await using var f = await IssuanceFixture.CreateAsync();
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
        var slots = stage == 1 && !afterCommit ? 2 : 3;
        Assert.Equal(slots, (await GlobalQuota(f)).GetInt64("Slots"));
        Assert.Equal(slots, (await GlobalRows(f)).Count(e => e.RowKey.StartsWith("slot:", StringComparison.Ordinal)));
        Assert.Equal(stage >= 4 ? 3 : 2, f.GitHub.Mints);
        Assert.Equal(stage >= 4 ? 1 : 0, f.GitHub.Revokes);
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
        await using var f = await IssuanceFixture.CreateAsync();
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
            Assert.Equal(1, (await GlobalQuota(f)).GetInt64("Slots"));
        }
        finally { continueSigning.TrySetResult(); }
        Assert.Equal(200, (await first).Status);
        Assert.Equal(1, f.GitHub.VerifierMints);
        Assert.Equal(2, f.GitHub.Mints);
    }

    [Fact]
    public async Task Revocation_after_admission_stops_a_late_worker_before_mint()
    {
        await using var f = await IssuanceFixture.CreateAsync();
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
        Assert.Equal(3, (await GlobalQuota(f)).GetInt64("Slots"));
        Assert.Equal(401, (await f.IssueAsync(Token)).Status);
        Assert.Equal(2, f.GitHub.Mints);
    }

    [Fact]
    public async Task Repository_capacity_stays_bounded_across_restarts_and_minute_windows()
    {
        await using var f = await IssuanceFixture.CreateAsync();
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
        Assert.Equal(11, (await GlobalQuota(f)).GetInt64("Slots"));
    }

    [Fact]
    public async Task Global_capacity_and_fixed_window_cost_bound_a_restarted_request_storm()
    {
        await using var f = await IssuanceFixture.CreateAsync();
        var limits = new IssuanceLimits(GlobalAttempts: 4);
        Assert.Equal(200, (await f.Issuer(limits: limits).IssueAsync(f.Device.DeviceId, f.Device.Secret, Token)).Status);
        // The verifier and first runtime consume this minute's repository signing budget.
        for (var i = 0; i < 12; i++)
            Assert.Equal(429, (await f.Issuer(limits: limits).IssueAsync(f.Device.DeviceId, f.Device.Secret, Token)).Status);
        var quota = await GlobalQuota(f);
        Assert.Equal(4, quota.GetInt64("Slots"));
        Assert.Equal(4, quota.GetInt64("Admissions"));
        Assert.Equal(2, (await f.AttemptsAsync()).Count);
        Assert.Equal(2, f.Signing.Calls);
        Assert.Equal(2, f.GitHub.Mints);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(429, (await f.Issuer(limits: limits).IssueAsync(f.Device.DeviceId, f.Device.Secret, Token)).Status);
        Assert.Equal(quota.ETag, (await GlobalQuota(f)).ETag);
        var rows = await GlobalRows(f);
        Assert.Equal(5, rows.Count);
        Assert.Equal(new[] { "quota", "slot:000", "slot:001", "slot:002", "slot:003" }, rows.Select(e => e.RowKey).Order());
    }

    private static async Task<TableEntity> GlobalQuota(IssuanceFixture f) =>
        (await f.Storage.Table.GetEntityAsync<TableEntity>("global:issuance-v1", "quota", cancellationToken: Token)).Value;
    private static async Task<List<TableEntity>> GlobalRows(IssuanceFixture f)
    {
        var rows = new List<TableEntity>();
        await foreach (var row in f.Storage.Table.QueryAsync<TableEntity>(e => e.PartitionKey == "global:issuance-v1", cancellationToken: Token)) rows.Add(row);
        return rows;
    }
}
