using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

// A retired allocation leaves no rows behind, and its retained charge is refunded once, so a later admission can use the
// capacity it held: a positive issuance settled by revocation, and an enrollment admitted but never published or minted.
public sealed class RetainedRetirementTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Retired_positive_issuance_deletes_its_rows_and_reuses_retained_capacity()
    {
        var f = new InMemoryBroker { Retained = new(128, 256, 1) };
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        f.GitHub.MintAnswer = (_, token) => GitHubRead.Found(token with { Value = "invalid-scope-token", OnlyListedRepositories = false });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(Retained.AgentRequest(Guid.NewGuid().ToString("N")), 2)).StatusCode);
        Assert.Equal(1, f.GitHub.Revokes);
        var ledger = Assert.Single(Retained.IssuanceLedgers(f)).Value;
        var ledgerKey = IssuanceLedger.Key(ledger.OperationId!);
        var attemptKey = Retained.Attempt(ledger);
        var settled = f.State.Get(attemptKey);
        Assert.NotNull(settled);
        Assert.Equal("settled", settled.State);
        var resolvedAt = settled.SettledAt!.Value;
        await Retained.ReclaimIssuance(f, ledger); // Settlement and retirement are deliberately separate invocations.
        Assert.Equal("released", f.State.Get(ledgerKey)!.CapacityState);
        Assert.Equal(0L, f.State.Get(IssuanceQuota.Key)!.Slots);
        Assert.Equal(1L, f.State.Get(IssuanceQuota.Key)!.RetainedIssuanceCount);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Start(Retained.AgentRequest(Guid.NewGuid().ToString("N")), 2)).StatusCode);
        Assert.Equal(1, f.GitHub.Mints);

        f.Clock.Advance(resolvedAt.AddDays(30) - f.Clock.GetUtcNow());
        await Retained.ReclaimIssuance(f, ledger);

        AssertAbsent(f, ledgerKey);
        AssertAbsent(f, attemptKey);
        AssertAbsent(f, ReleaseProof.Issuance(Retained.Repository, ledger.OperationId!));
        Assert.Equal(0L, f.State.Get(IssuanceQuota.Key)!.RetainedIssuanceCount);
        await Retained.ReclaimIssuance(f, ledger); // A duplicate cannot refund another allocation.
        f.GitHub.MintAnswer = null; // GitHub mints in scope again.
        Assert.Equal(HttpStatusCode.OK, (await f.Start(Retained.AgentRequest(Guid.NewGuid().ToString("N")), 2)).StatusCode);
        Assert.Equal(1L, f.State.Get(IssuanceQuota.Key)!.RetainedIssuanceCount);
    }

    [Fact]
    public async Task Retired_new_never_minted_enrollment_deletes_its_rows_and_reuses_retained_capacity()
    {
        var f = new InMemoryBroker { Retained = new(128, 1, 4096) };
        var committed = false;
        f.State.AfterCommit = _ =>
        {
            if (committed) return Task.CompletedTask;
            committed = true;
            throw new BrokerStateUnavailable("Lost committed enrollment admission response before mint admission");
        };
        var id = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(Retained.AgentRequest(id), 2)).StatusCode);
        Assert.True(committed);
        f.State.AfterCommit = null;
        var ledger = Retained.EnrollmentLedger(f, id).Value;
        var pendingKey = PendingControllerEnrollment.Key(Retained.Repository, id);
        AssertAbsent(f, pendingKey);
        Assert.Empty(Retained.IssuanceLedgers(f));
        Assert.Equal(0, f.GitHub.Mints);
        f.Clock.Advance(ledger.Deadline!.Value - f.Clock.GetUtcNow());
        await f.Reclamation().ReclaimEnrollmentAsync(id, ledger.Generation!, Token);
        Assert.Equal(0L, f.State.Get(EnrollmentQuota.Key)!.Count);
        Assert.Equal(1L, f.State.Get(EnrollmentQuota.Key)!.RetainedEnrollmentCount);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Start(Retained.AgentRequest(Guid.NewGuid().ToString("N")), 2)).StatusCode);
        Assert.Equal(0, f.GitHub.Mints);
        f.Clock.Advance(TimeSpan.FromDays(30));

        await f.Reclamation().ReclaimEnrollmentAsync(id, ledger.Generation!, Token);

        AssertAbsent(f, EnrollmentLedger.Key(id));
        AssertAbsent(f, pendingKey);
        AssertAbsent(f, ReleaseProof.Enrollment(Retained.Repository, id));
        var quota = f.State.Get(EnrollmentQuota.Key)!;
        Assert.Equal(0L, quota.RetainedEnrollmentCount);
        Assert.Equal(1L, quota.CandidateCount);
        await f.Reclamation().ReclaimEnrollmentAsync(id, ledger.Generation!, Token);
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        Assert.Equal(HttpStatusCode.OK, (await f.Start(Retained.AgentRequest(Guid.NewGuid().ToString("N")), 2)).StatusCode);
        Assert.Equal(1L, f.State.Get(EnrollmentQuota.Key)!.RetainedEnrollmentCount);
    }

    private static void AssertAbsent(InMemoryBroker f, StateKey key) =>
        Assert.True(f.State.Row(key) is null, $"Retirement must physically delete {key}");
}
