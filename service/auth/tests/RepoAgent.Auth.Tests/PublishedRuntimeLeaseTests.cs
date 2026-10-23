using System.Text.Json;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class PublishedRuntimeLeaseTests
{
    [Fact]
    public async Task Repeated_controller_restarts_reuse_the_published_lease_without_exhausting_capacity()
    {
        await using var f = await IssuanceFixture.CreateAsync();
        var leases = new List<JsonElement>();
        for (var restart = 0; restart < 8; restart++)
        {
            var result = await f.IssueAsync(TestContext.Current.CancellationToken);
            Assert.Equal(200, result.Status);
            leases.Add(JsonSerializer.SerializeToElement(result.Body));
            f.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.All(leases, lease =>
        {
            Assert.Equal(leases[0].GetProperty("token").GetString(), lease.GetProperty("token").GetString());
            Assert.Equal(leases[0].GetProperty("expiresAt").GetString(), lease.GetProperty("expiresAt").GetString());
        });
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal(2, (await f.AttemptsAsync()).Count);
        Assert.Equal(3, (await f.GateAsync()).GetInt64("OutstandingTokenCount"));
    }

    [Fact]
    public async Task Changed_approval_closes_the_grant_instead_of_republishing_an_old_lease()
    {
        await using var f = await IssuanceFixture.CreateAsync();
        Assert.Equal(200, (await f.IssueAsync(TestContext.Current.CancellationToken)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.GitHub.Marker = "changed";

        var result = await f.IssueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(401, result.Status);
        Assert.Null(result.Body);
        Assert.Equal("revoked", (await f.DeviceAsync()).GetString("State"));
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal(0, f.GitHub.Revokes);
        Assert.Equal(3, (await f.GateAsync()).GetInt64("OutstandingTokenCount"));
    }

    [Fact]
    public async Task Concurrent_revocation_during_reuse_denies_publication_without_revoking_the_shared_token()
    {
        await using var f = await IssuanceFixture.CreateAsync();
        Assert.Equal(200, (await f.IssueAsync(TestContext.Current.CancellationToken)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.Signing.Before = async () => Assert.Equal(204, await new DeviceRevocation(f.Storage.Table, f.Options, f.Clock)
            .RevokeAsync(f.Device.DeviceId, f.Device.Secret, TestContext.Current.CancellationToken));

        var result = await f.IssueAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(200, result.Status);
        Assert.Null(result.Body);
        Assert.Equal("revoked", (await f.DeviceAsync()).GetString("State"));
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal(0, f.GitHub.Revokes);
        Assert.Equal("released", Assert.Single(await f.AttemptsAsync(), a => a.GetString("Purpose") == "runtime").GetString("State"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Lost_reuse_commit_response_preserves_the_published_lease_for_a_fresh_request(int stage)
    {
        await using var f = await IssuanceFixture.CreateAsync();
        var first = await f.IssueAsync(TestContext.Current.CancellationToken);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var batches = 0;
        var transport = new ObservedTransport { After = message =>
        {
            if (ObservedTransport.IsBatch(message) && ++batches == stage)
                throw new HttpRequestException("Lost reuse transaction response");
            return Task.CompletedTask;
        }};
        var lost = await f.IssueAsync(TestContext.Current.CancellationToken, transport);
        Assert.Equal(503, lost.Status);
        Assert.Null(lost.Body);
        Assert.Equal(stage, batches);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await f.IssueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(200, recovered.Status);
        Assert.Equal(JsonSerializer.SerializeToElement(first.Body).GetProperty("token").GetString(),
            JsonSerializer.SerializeToElement(recovered.Body).GetProperty("token").GetString());
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal(0, f.GitHub.Revokes);
        Assert.Equal(2, (await f.AttemptsAsync()).Count);
    }

    [Fact]
    public async Task A_competing_reuse_publication_fences_the_older_observation()
    {
        await using var f = await IssuanceFixture.CreateAsync();
        Assert.Equal(200, (await f.IssueAsync(TestContext.Current.CancellationToken)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Signing.Before = async () =>
        {
            if (f.Signing.Calls != 3) return;
            entered.SetResult();
            await resume.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        };
        var stale = f.IssueAsync(TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(200, (await f.IssueAsync(TestContext.Current.CancellationToken)).Status);
        }
        finally { resume.TrySetResult(); }
        Assert.NotEqual(200, (await stale).Status);
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal(0, f.GitHub.Revokes);
        Assert.Equal(3, (await f.GateAsync()).GetInt64("OutstandingTokenCount"));
    }

    [Theory]
    [InlineData("State", "known")]
    [InlineData("State", "sending")]
    [InlineData("ScopeValid", false)]
    [InlineData("DeviceId", "different-device")]
    public async Task Invalid_published_authority_fails_closed_without_a_replacement_mint(string field, object value)
    {
        await using var f = await IssuanceFixture.CreateAsync();
        Assert.Equal(200, (await f.IssueAsync(TestContext.Current.CancellationToken)).Status);
        var attempt = Assert.Single(await f.AttemptsAsync(), a => a.GetString("Purpose") == "runtime");
        attempt[field] = value;
        await f.Storage.Table.UpdateEntityAsync(attempt, attempt.ETag, TableUpdateMode.Replace, TestContext.Current.CancellationToken);
        f.Clock.Advance(TimeSpan.FromMinutes(1));

        var result = await f.IssueAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(200, result.Status);
        Assert.Null(result.Body);
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal(2, f.Signing.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Near_expiry_or_settled_lease_uses_ordinary_issuance_without_extending_previous_clocks(bool settled)
    {
        await using var f = await IssuanceFixture.CreateAsync();
        var first = await f.IssueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(200, first.Status);
        var previous = Assert.Single(await f.AttemptsAsync(), a => a.GetString("Purpose") == "runtime");
        var expiry = previous.GetDateTimeOffset("ExpiresAt");
        f.Clock.Advance(TimeSpan.FromMinutes(settled ? 1 : 56));
        if (settled)
        {
            var grant = await f.Store().AuthenticateAsync(f.Device.DeviceId, f.Device.Secret, TestContext.Current.CancellationToken);
            await f.Store().MarkRevokedAsync(grant, previous, TestContext.Current.CancellationToken);
        }

        var result = await f.IssueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(200, result.Status);
        Assert.NotEqual(JsonSerializer.SerializeToElement(first.Body).GetProperty("token").GetString(),
            JsonSerializer.SerializeToElement(result.Body).GetProperty("token").GetString());
        Assert.Equal(settled ? 3 : 4, f.GitHub.Mints);
        var original = (await f.Storage.Table.GetEntityAsync<TableEntity>(previous.PartitionKey, previous.RowKey,
            cancellationToken: TestContext.Current.CancellationToken)).Value;
        Assert.Equal(expiry, original.GetDateTimeOffset("ExpiresAt"));
        Assert.Equal(settled ? 4 : 5, (await f.GateAsync()).GetInt64("OutstandingTokenCount"));
    }
}
