using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

// Reclamation releases an expired or unpublished pending enrollment's occupancy, never its retained charge.
public sealed class CapacityReclamationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Redacted_unpublished_pending_releases_only_its_global_occupancy()
    {
        var f = new InMemoryBroker();
        var committed = false;
        f.State.AfterCommit = _ =>
        {
            if (committed) return Task.CompletedTask;
            committed = true;
            throw new BrokerStateUnavailable("Lost global admission response before repository publication");
        };
        var id = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(Retained.AgentRequest(id), 2)).StatusCode);
        Assert.True(committed);
        f.State.AfterCommit = null;
        var pendingKey = PendingControllerEnrollment.Key(Retained.Repository, id);
        var ledger = Retained.EnrollmentLedger(f, id).Value;
        Assert.Null(f.State.Get(pendingKey));
        Assert.Null(f.State.Get(RepositoryGate.Key(Retained.Repository)));
        f.Clock.Advance(ledger.PayloadPurgeAt!.Value - f.Clock.GetUtcNow());

        // The real absent-target tombstone, not a test-authored approximation.
        await f.Redaction().RedactEnrollmentAsync(id, ledger.Generation!, Token);
        var tombstone = f.State.Row(pendingKey)!;
        Assert.Equal("redacted", tombstone["State"]);
        Assert.Equal(true, tombstone["PayloadRedacted"]);
        Assert.False(tombstone.ContainsKey("RepositoryCharged"));
        Assert.Equal(0L, f.Gate.PendingCount);
        Assert.Equal(0L, f.Gate.OutstandingTokenCount);

        await f.Reclamation().ReclaimEnrollmentAsync(id, ledger.Generation!, Token);
        await f.Reclamation().ReclaimEnrollmentAsync(id, ledger.Generation!, Token);

        var quota = f.State.Get(EnrollmentQuota.Key)!;
        Assert.Equal(0L, quota.Count);
        Assert.Equal(0L, f.State.Get(CandidateRepository.Key(Retained.Repository))!.PendingCount);
        Assert.Equal(0L, f.Gate.PendingCount);
        Assert.Equal(0L, f.Gate.OutstandingTokenCount);
        Assert.Equal(1L, quota.RetainedEnrollmentCount);
        Assert.Equal(1L, quota.CandidateCount);
        Assert.Equal(0, f.GitHub.Mints);
    }

    [Fact]
    public async Task Expired_pending_releases_occupancy_without_refunding_retained_or_token_capacity()
    {
        var f = new InMemoryBroker { Retained = new(128, 1, 4096) };
        var (id, pendingId) = await Retained.StartAgent(f);
        var ledger = Retained.EnrollmentLedger(f, id).Value;
        var quota = f.State.Get(EnrollmentQuota.Key)!;
        Assert.Equal(1L, quota.Count);
        Assert.Equal(1L, f.State.Get(CandidateRepository.Key(Retained.Repository))!.PendingCount);
        Assert.Equal(1L, f.Gate.PendingCount);
        Assert.Equal(1L, f.Gate.OutstandingTokenCount);
        Assert.Equal(1L, quota.RetainedEnrollmentCount);

        f.Clock.Advance(ledger.Deadline!.Value - f.Clock.GetUtcNow());
        Assert.Equal(HttpStatusCode.Gone, (await f.Complete(pendingId, 2)).StatusCode);

        await f.Reclamation().ReclaimEnrollmentAsync(id, ledger.Generation!, Token);

        quota = f.State.Get(EnrollmentQuota.Key)!;
        Assert.Equal(0L, quota.Count);
        Assert.Equal(0L, f.State.Get(CandidateRepository.Key(Retained.Repository))!.PendingCount);
        Assert.Equal(0L, f.Gate.PendingCount);
        Assert.Equal(1L, quota.RetainedEnrollmentCount);
        Assert.Equal(1L, f.Gate.OutstandingTokenCount);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Retained.HistoricalDiscovery(f, () => f.Start(Retained.AgentRequest(Guid.NewGuid().ToString("N")), 2))).StatusCode);
        Assert.Equal(1, f.GitHub.Mints);
    }
}
