using System.Net;
using System.Net.Http.Headers;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class DeviceRevocationTests : IClassFixture<RevocationFixture>
{
    private readonly RevocationFixture fixture;

    public DeviceRevocationTests(RevocationFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task Correct_secret_closes_bound_grant_and_preserves_outstanding_authority()
    {
        var device = await fixture.EnrollAsync();
        await using var host = fixture.CreateHost();
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/v1/devices/" + device.DeviceId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", device.Secret);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var fresh = fixture.CreateClient();
        var gate = (await fresh.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "gate", cancellationToken: TestContext.Current.CancellationToken)).Value;
        var grant = (await fresh.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "device:" + device.DeviceId, cancellationToken: TestContext.Current.CancellationToken)).Value;
        var receipt = (await fresh.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "revocation:" + device.DeviceId, cancellationToken: TestContext.Current.CancellationToken)).Value;
        var attempt = (await fresh.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "attempt:one", cancellationToken: TestContext.Current.CancellationToken)).Value;
        Assert.Equal("closed", gate.GetString("State"));
        Assert.Equal(8L, gate.GetInt64("Sequence"));
        Assert.Equal("revoked", grant.GetString("State"));
        Assert.Equal(1L, gate.GetInt64("OutstandingTokenCount"));
        Assert.Equal(1L, grant.GetInt64("OutstandingTokenCount"));
        Assert.Equal("outstanding", attempt.GetString("State"));
        Assert.Equal(device.DeviceId, receipt.GetString("DeviceId"));
        Assert.Equal(RevocationFixture.RepositoryId, receipt.GetString("RepositoryId"));
        Assert.Equal(RevocationFixture.Epoch, receipt.GetString("Epoch"));
        Assert.NotNull(receipt.GetDateTimeOffset("RevokedAt"));
    }
}
