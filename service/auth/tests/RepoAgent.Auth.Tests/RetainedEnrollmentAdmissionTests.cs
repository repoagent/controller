using System.Net;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class RetainedEnrollmentAdmissionTests
{
    [Fact]
    public async Task Retained_enrollment_limit_denies_new_start_without_charging_identical_retry()
    {
        await using var f = await EnrollmentFixture.CreateAsync(settings: new()
        {
            ["Broker:RetainedEnrollmentLimit"] = "1"
        });
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        var request = new
        {
            idempotencyId = Guid.NewGuid().ToString("N"), repositoryId = 123456,
            repository = "owner/project", variable = "APPROVAL", profile = "polling"
        };
        using var first = await f.Send("/v2/enrollments", request);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var retry = await f.Send("/v2/enrollments", request);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await retry.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, f.GitHub.VerifierMints);

        // Remove the independent per-minute mint ceiling as a reason for denial.
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var second = await f.Send("/v2/enrollments", new
        {
            idempotencyId = Guid.NewGuid().ToString("N"), repositoryId = 123456,
            repository = "owner/project", variable = "APPROVAL_SECOND", profile = "polling"
        });
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);

        // Fresh reads expose any leaked reservation or repository publication on denial.
        var rows = new List<TableEntity>();
        await foreach (var row in f.Storage.Table.QueryAsync<TableEntity>(cancellationToken: TestContext.Current.CancellationToken))
            rows.Add(row);
        Assert.Equal("pending:" + request.idempotencyId,
            Assert.Single(rows, row => row.PartitionKey == "global:enrollment-v1"
                && row.RowKey.StartsWith("pending:", StringComparison.Ordinal)).RowKey);
        Assert.Equal("pending:" + request.idempotencyId,
            Assert.Single(rows, row => row.PartitionKey == RevocationFixture.Partition
                && row.RowKey.StartsWith("pending:", StringComparison.Ordinal)).RowKey);
        Assert.Equal(1, f.GitHub.VerifierMints);
    }
}
