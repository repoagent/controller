using System.Net;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed partial class AzuriteReclamationTests
{
    [Fact]
    public async Task Retired_positive_issuance_deletes_its_rows_and_reuses_retained_capacity()
    {
        await using var f = await EnrollmentFixture.CreateAsync(settings: new() { ["Broker:RetainedIssuanceLimit"] = "1" });
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        f.GitHub.MintAnswer = (_, token) => GitHubRead.Found(token with { Value = "invalid-scope-token", OnlyListedRepositories = false });
        using var failedScope = await f.Send("/v2/enrollments", Request(Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failedScope.StatusCode);
        Assert.Equal(1, f.GitHub.Revokes);
        var ledger = Assert.Single(await IssuanceLedgers(f));
        var attemptKey = ledger.GetString("AttemptRowKey")!;
        var settled = await Read(f, RevocationFixture.Partition, attemptKey);
        Assert.Equal("settled", settled.GetString("State"));
        var resolvedAt = settled.GetDateTimeOffset("SettledAt")!.Value;
        await Reclaim(f, ledger); // Settlement and retirement are deliberately separate invocations.
        Assert.Equal("released", (await Read(f, ledger.PartitionKey, ledger.RowKey)).GetString("CapacityState"));
        Assert.Equal(0L, (await Read(f, "global:issuance-v1", "quota")).GetInt64("Slots"));
        Assert.Equal(1L, (await Read(f, "global:issuance-v1", "quota")).GetInt64("RetainedIssuanceCount"));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var full = await f.Send("/v2/enrollments", Request(Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);
        Assert.Equal(1, f.GitHub.Mints);

        f.Clock.Advance(resolvedAt.AddDays(30) - f.Clock.GetUtcNow());
        await Reclaim(f, ledger);

        await AssertAbsent(f, ledger.PartitionKey, ledger.RowKey);
        await AssertAbsent(f, RevocationFixture.Partition, attemptKey);
        await AssertAbsent(f, RevocationFixture.Partition, "release:" + ledger.GetString("OperationId"));
        Assert.Equal(0L, (await Read(f, "global:issuance-v1", "quota")).GetInt64("RetainedIssuanceCount"));
        await Reclaim(f, ledger); // A duplicate cannot refund another allocation.
        f.GitHub.MintAnswer = null; // GitHub mints in scope again.
        using var replacement = await f.Send("/v2/enrollments", Request(Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
        Assert.Equal(1L, (await Read(f, "global:issuance-v1", "quota")).GetInt64("RetainedIssuanceCount"));
    }

    [Fact]
    public async Task Retired_new_never_minted_enrollment_deletes_its_rows_and_reuses_retained_capacity()
    {
        var committed = false;
        var lostResponse = new ObservedTransport { After = message =>
        {
            if (ObservedTransport.IsBatch(message) && !committed)
            {
                committed = true;
                throw new HttpRequestException("Lost committed enrollment admission response before mint admission");
            }
            return Task.CompletedTask;
        }};
        await using var f = await EnrollmentFixture.CreateAsync(lostResponse,
            settings: new() { ["Broker:RetainedEnrollmentLimit"] = "1" });
        var id = Guid.NewGuid().ToString("N");
        using var interrupted = await f.Send("/v2/enrollments", Request(id));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, interrupted.StatusCode);
        Assert.True(committed);
        var ledger = await Read(f, "global:enrollment-v1", "pending:" + id);
        await AssertAbsent(f, RevocationFixture.Partition, ledger.RowKey);
        Assert.Empty(await IssuanceLedgers(f));
        Assert.Equal(0, f.GitHub.Mints);
        // This is a newly admitted allocation. Do not stamp legacy fixtures with the new version.
        await f.RestartAsync();
        f.Clock.Advance(ledger.GetDateTimeOffset("Deadline")!.Value - f.Clock.GetUtcNow());
        await Cleanup(f).ReclaimEnrollmentAsync(id, ledger.GetString("Generation")!, Token);
        Assert.Equal(0L, (await Read(f, "global:enrollment-v1", "quota")).GetInt64("Count"));
        Assert.Equal(1L, (await Read(f, "global:enrollment-v1", "quota")).GetInt64("RetainedEnrollmentCount"));
        using var full = await f.Send("/v2/enrollments", Request(Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);
        Assert.Equal(0, f.GitHub.Mints);
        f.Clock.Advance(TimeSpan.FromDays(30));

        await Cleanup(f).ReclaimEnrollmentAsync(id, ledger.GetString("Generation")!, Token);

        await AssertAbsent(f, ledger.PartitionKey, ledger.RowKey);
        await AssertAbsent(f, RevocationFixture.Partition, ledger.RowKey);
        await AssertAbsent(f, RevocationFixture.Partition, "release:" + ledger.RowKey);
        var quota = await Read(f, "global:enrollment-v1", "quota");
        Assert.Equal(0L, quota.GetInt64("RetainedEnrollmentCount"));
        Assert.Equal(1L, quota.GetInt64("CandidateCount"));
        await Cleanup(f).ReclaimEnrollmentAsync(id, ledger.GetString("Generation")!, Token);
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        using var replacement = await f.Send("/v2/enrollments", Request(Guid.NewGuid().ToString("N")));
        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);
        Assert.Equal(1L, (await Read(f, "global:enrollment-v1", "quota")).GetInt64("RetainedEnrollmentCount"));
    }
}
