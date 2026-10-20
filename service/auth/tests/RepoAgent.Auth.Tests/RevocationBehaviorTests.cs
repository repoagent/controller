using System.Net;
using System.Net.Http.Headers;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class RevocationBehaviorTests : IAsyncLifetime
{
    private readonly RevocationFixture fixture = new();
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    public ValueTask InitializeAsync() => fixture.InitializeAsync();
    public ValueTask DisposeAsync() => fixture.DisposeAsync();
    private Task<TableEntity> Read(string row) => Read(RevocationFixture.Partition, row);
    private async Task<TableEntity> Read(string partition, string row) =>
        (await fixture.Table.GetEntityAsync<TableEntity>(partition, row, cancellationToken: Token)).Value;
    private async Task Change(string row, Action<TableEntity> change)
    {
        var entity = await Read(row);
        change(entity);
        await fixture.Table.UpdateEntityAsync(entity, entity.ETag, Azure.Data.Tables.TableUpdateMode.Replace, Token);
    }
    private static async Task<HttpResponseMessage> Revoke(HttpClient client, EnrolledDevice device, string? secret = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/v1/devices/" + device.DeviceId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret ?? device.Secret);
        return await client.SendAsync(request, Token);
    }
    private async Task AssertUncommitted(EnrolledDevice device)
    {
        Assert.Equal("active", (await Read("gate")).GetString("State"));
        Assert.Equal("active", (await Read("device:" + device.DeviceId)).GetString("State"));
        Assert.False((await fixture.Table.GetEntityIfExistsAsync<TableEntity>(RevocationFixture.Partition,
            "revocation:" + device.DeviceId, cancellationToken: Token)).HasValue);
        Assert.Equal("outstanding", (await Read("attempt:one")).GetString("State"));
    }

    [Fact]
    public async Task Revocation_survives_new_host_and_concurrent_retries_without_new_receipts()
    {
        var device = await fixture.EnrollAsync("x", new string('a', 128));
        await using (var host = fixture.CreateHost())
        {
            using var client = host.CreateClient();
            var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Revoke(client, device)));
            foreach (var response in responses) { using (response) Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); }
        }
        var receipt = await Read("revocation:" + device.DeviceId);
        await using var restarted = fixture.CreateHost();
        using var freshClient = restarted.CreateClient();
        using var retry = await Revoke(freshClient, device);
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        Assert.Equal(receipt.ETag, (await Read("revocation:" + device.DeviceId)).ETag);
        Assert.Equal(8L, (await Read("gate")).GetInt64("Sequence"));
        Assert.Equal("closed", (await Read("gate")).GetString("State"));
        Assert.Equal("revoked", (await Read("device:" + device.DeviceId)).GetString("State"));
        Assert.Equal(1L, (await Read("gate")).GetInt64("OutstandingTokenCount"));
        Assert.Equal(1L, (await Read("device:" + device.DeviceId)).GetInt64("OutstandingTokenCount"));
        Assert.Equal("outstanding", (await Read("attempt:one")).GetString("State"));
    }

    [Fact]
    public async Task Concurrent_commit_between_authority_reads_is_reconciled()
    {
        var device = await fixture.EnrollAsync();
        await using var concurrentHost = fixture.CreateHost();
        using var concurrentClient = concurrentHost.CreateClient();
        var committed = false;
        var transport = new ObservedTransport { After = async message =>
        {
            var uri = Uri.UnescapeDataString(message.Request.Uri.ToUri().AbsoluteUri);
            if (committed || !uri.Contains("RowKey='gate'", StringComparison.Ordinal)) return;
            committed = true;
            using var other = await Revoke(concurrentClient, device);
            Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);
        }};
        await using var host = fixture.CreateHost(transport);
        using var client = host.CreateClient();
        using var response = await Revoke(client, device);
        Assert.True(committed);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(8L, (await Read("gate")).GetInt64("Sequence"));
        Assert.Equal("closed", (await Read("gate")).GetString("State"));
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("missing-secret")]
    [InlineData("short-secret")]
    [InlineData("device-binding")]
    [InlineData("gate-owner")]
    [InlineData("device-epoch")]
    [InlineData("gate-epoch")]
    [InlineData("expired")]
    [InlineData("locator-binding")]
    [InlineData("orphan-locator")]
    [InlineData("missing-locator")]
    [InlineData("missing-device")]
    [InlineData("missing-gate")]
    public async Task Invalid_authority_is_denied_without_receipt(string scenario)
    {
        var device = await fixture.EnrollAsync();
        switch (scenario)
        {
            case "device-binding": await Change("device:" + device.DeviceId, e => e["RepositoryId"] = "987"); break;
            case "gate-owner": await Change("gate", e => e["OwnerDeviceId"] = "other"); break;
            case "device-epoch": await Change("device:" + device.DeviceId, e => e["Epoch"] = "old"); break;
            case "gate-epoch": await Change("gate", e => e["Epoch"] = "old"); break;
            case "expired": await Change("device:" + device.DeviceId, e => e["ExpiresAt"] = DateTimeOffset.UtcNow.AddHours(-1)); break;
            case "missing-gate": await fixture.Table.DeleteEntityAsync(RevocationFixture.Partition, "gate", cancellationToken: Token); break;
            case "missing-device": await fixture.Table.DeleteEntityAsync(RevocationFixture.Partition, "device:" + device.DeviceId, cancellationToken: Token); break;
            case "missing-locator": await fixture.Table.DeleteEntityAsync("locator:" + device.DeviceId, "locator", cancellationToken: Token); break;
            case "locator-binding":
            case "orphan-locator":
                var locator = await Read("locator:" + device.DeviceId, "locator");
                locator[scenario == "locator-binding" ? "DeviceId" : "RepositoryId"] = "999";
                await fixture.Table.UpdateEntityAsync(locator, locator.ETag, TableUpdateMode.Replace, Token); break;
        }
        await using var host = fixture.CreateHost();
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/v1/devices/" + device.DeviceId);
        if (scenario != "missing-secret") request.Headers.Authorization = new("Bearer",
            scenario == "wrong-secret" ? new string('Z', 43) : scenario == "short-secret" ? "short" : device.Secret);
        using var response = await client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False((await fixture.Table.GetEntityIfExistsAsync<TableEntity>(RevocationFixture.Partition,
            "revocation:" + device.DeviceId, cancellationToken: Token)).HasValue);
        Assert.Equal("outstanding", (await Read("attempt:one")).GetString("State"));
    }

    [Theory]
    [InlineData("sequence")]
    [InlineData("nonfinite-sequence")]
    [InlineData("secret-hash")]
    [InlineData("missing-field")]
    [InlineData("inconsistent-state")]
    public async Task Corrupt_authority_returns_unavailable_without_commit(string scenario)
    {
        var device = await fixture.EnrollAsync();
        if (scenario == "sequence") await Change("gate", e => e["Sequence"] = "7");
        if (scenario == "nonfinite-sequence") await Change("gate", e => e["Sequence"] = double.NaN);
        if (scenario == "secret-hash") await Change("device:" + device.DeviceId, e => e["SecretHash"] = "invalid");
        if (scenario == "missing-field") await Change("device:" + device.DeviceId, e => e.Remove("ExpiresAt"));
        if (scenario == "inconsistent-state") await Change("gate", e => e["State"] = "unknown");
        var gateBefore = await Read("gate");
        var deviceBefore = await Read("device:" + device.DeviceId);
        await using var host = fixture.CreateHost();
        using var client = host.CreateClient();
        using var response = await Revoke(client, device);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(gateBefore.ETag, (await Read("gate")).ETag);
        Assert.Equal(deviceBefore.ETag, (await Read("device:" + device.DeviceId)).ETag);
        Assert.False((await fixture.Table.GetEntityIfExistsAsync<TableEntity>(RevocationFixture.Partition,
            "revocation:" + device.DeviceId, cancellationToken: Token)).HasValue);
    }

    [Fact]
    public async Task Expired_revoked_device_cannot_replay_its_receipt()
    {
        var device = await fixture.EnrollAsync();
        await using var host = fixture.CreateHost();
        using var client = host.CreateClient();
        using var first = await Revoke(client, device);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        await Change("device:" + device.DeviceId, e => e["ExpiresAt"] = DateTimeOffset.UtcNow.AddHours(-1));
        using var retry = await Revoke(client, device);
        Assert.Equal(HttpStatusCode.Unauthorized, retry.StatusCode);
        Assert.Equal("closed", (await Read("gate")).GetString("State"));
    }

    [Theory]
    [InlineData("device")]
    [InlineData("gate")]
    public async Task Conflict_rechecks_authority_and_rolls_back_every_revocation_write(string changedEntity)
    {
        var device = await fixture.EnrollAsync();
        var changed = false;
        var transport = new ObservedTransport { Before = async message =>
        {
            if (!ObservedTransport.IsBatch(message) || changed) return;
            changed = true;
            if (changedEntity == "device") await Change("device:" + device.DeviceId, e => e["ExpiresAt"] = DateTimeOffset.UtcNow.AddHours(-1));
            else await Change("gate", e => e["OwnerDeviceId"] = "replacement");
        }};
        await using var host = fixture.CreateHost(transport);
        using var client = host.CreateClient();
        using var response = await Revoke(client, device);
        Assert.True(changed);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertUncommitted(device);
        Assert.Equal(7L, (await Read("gate")).GetInt64("Sequence"));
    }

    [Fact]
    public async Task Lost_success_response_is_reconciled_by_authenticated_retry()
    {
        var device = await fixture.EnrollAsync();
        var dropped = false;
        var transport = new ObservedTransport { After = message =>
        {
            if (ObservedTransport.IsBatch(message) && !dropped && message.Response.Status == 202)
            {
                dropped = true;
                throw new HttpRequestException("Simulated response loss after real commit");
            }
            return Task.CompletedTask;
        }};
        await using (var host = fixture.CreateHost(transport))
        {
            using var client = host.CreateClient();
            using var lost = await Revoke(client, device);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, lost.StatusCode);
        }
        Assert.True(dropped);
        var receipt = await Read("revocation:" + device.DeviceId);
        await using var restarted = fixture.CreateHost();
        using var freshClient = restarted.CreateClient();
        using var retry = await Revoke(freshClient, device);
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        Assert.Equal(receipt.ETag, (await Read("revocation:" + device.DeviceId)).ETag);
        Assert.Equal(8L, (await Read("gate")).GetInt64("Sequence"));
        Assert.Equal("outstanding", (await Read("attempt:one")).GetString("State"));
    }

    [Fact]
    public async Task Persistent_conflicts_exhaust_bounded_work_without_partial_commit()
    {
        var device = await fixture.EnrollAsync();
        var transport = new ObservedTransport { Before = async message =>
        {
            if (ObservedTransport.IsBatch(message)) await Change("gate", e => e["ConcurrentWriter"] = Guid.NewGuid().ToString());
        }};
        await using var host = fixture.CreateHost(transport);
        using var client = host.CreateClient();
        using var response = await Revoke(client, device);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await AssertUncommitted(device);
    }

    [Fact]
    public async Task Transport_unavailability_fails_closed_without_commit()
    {
        var device = await fixture.EnrollAsync();
        var transport = new ObservedTransport { Before = _ => throw new HttpRequestException("Storage unavailable") };
        await using var host = fixture.CreateHost(transport);
        using var client = host.CreateClient();
        using var response = await Revoke(client, device);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(Token));
        await AssertUncommitted(device);
    }

    [Fact]
    public async Task Closed_grant_without_its_receipt_is_unresolved()
    {
        var device = await fixture.EnrollAsync();
        await using var host = fixture.CreateHost();
        using var client = host.CreateClient();
        using var first = await Revoke(client, device);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        await fixture.Table.DeleteEntityAsync(RevocationFixture.Partition, "revocation:" + device.DeviceId,
            cancellationToken: Token);
        using var retry = await Revoke(client, device);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, retry.StatusCode);
        Assert.Equal("closed", (await Read("gate")).GetString("State"));
        Assert.Equal("outstanding", (await Read("attempt:one")).GetString("State"));
    }

    [Fact]
    public async Task Missing_table_is_unavailable_not_invalid_credentials()
    {
        var device = await fixture.EnrollAsync();
        await fixture.Table.DeleteAsync(Token);
        try
        {
            await using var host = fixture.CreateHost();
            using var client = host.CreateClient();
            using var response = await Revoke(client, device);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally { await fixture.Table.CreateAsync(Token); }
    }
}
