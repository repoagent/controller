using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

// A setup start whose verifier GitHub token reclamation settled as unsent before the send claim committed: the claim
// loses, and the same enrollment retries with one fresh verifier only on positive unsent proof while it is live.
// Decided by the core over the in-memory broker state and a manual clock.
public sealed class UnsentEnrollmentRecoveryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("polling")]
    [InlineData("polling-clone")]
    public async Task Same_enrollment_recovers_after_cleanup_fences_an_unsent_verifier(string profile)
    {
        var f = new InMemoryBroker();
        var (id, request, ledger) = await FencedEnrollment(f, profile);
        var pendingKey = PendingControllerEnrollment.Key(Retained.Repository, id);
        var before = f.State.Get(pendingKey)!;
        var old = f.State.Get(Retained.Attempt(ledger))!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));

        var retry = await f.Start(request, 2);

        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        f.GitHub.Marker = retry.Json.GetProperty("marker").GetString()!;
        var pending = retry.Json.GetProperty("pendingId").GetString()!;
        Assert.Equal(before.PendingId, pending);
        var after = f.State.Get(pendingKey)!;
        Assert.Equal((before.EnrollmentId, before.Challenge, before.Deadline, before.CreatedAt, before.Epoch, before.Generation),
            (after.EnrollmentId, after.Challenge, after.Deadline, after.CreatedAt, after.Epoch, after.Generation));
        Assert.NotEqual(old.OperationId, after.Attempt);
        var audit = f.State.Get(Retained.Attempt(ledger))!;
        Assert.Equal("unsent", audit.SettlementReason);
        Assert.Equal(old.JwtExpiresAt, audit.JwtExpiresAt);
        Assert.True(audit.CapacityReleased);
        Assert.Equal(2L, f.State.Get(IssuanceQuota.Key)!.RetainedIssuanceCount);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var completion = await f.Complete(pending, 2);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var enrollment = completion.Json.GetProperty("enrollment");
        var enrollmentId = enrollment.GetProperty("deviceId").GetString()!;
        var secret = enrollment.GetProperty("deviceSecret").GetString()!;
        Assert.Equal(before.EnrollmentId, enrollmentId);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await f.Acknowledge(enrollmentId, completion.Json.GetProperty("completionId").GetString()!, secret, 2)).StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(enrollmentId, secret, profile == "polling-clone" ? 2 : 1)).StatusCode);
    }

    [Theory]
    [InlineData("capacity")]
    [InlineData("reason")]
    [InlineData("send")]
    [InlineData("clock")]
    [InlineData("expired")]
    public async Task Retrying_settled_verifier_requires_positive_unsent_proof_and_live_enrollment(string barrier)
    {
        var f = new InMemoryBroker();
        var (id, request, ledger) = await FencedEnrollment(f);
        if (barrier != "expired")
            f.State.Change(Retained.Attempt(ledger), row =>
            {
                if (barrier == "capacity") Assert.True(row.Remove("CapacityReleased"));
                if (barrier == "reason") row["SettlementReason"] = "revoked";
                if (barrier == "send") row["SendClaimedAt"] = row["ReservedAt"];
                if (barrier == "clock") Assert.True(row.Remove("SettledAt"));
            });
        var pendingKey = PendingControllerEnrollment.Key(Retained.Repository, id);
        var pending = f.State.Version(pendingKey);
        Assert.NotNull(pending);
        f.Clock.Advance(TimeSpan.FromMinutes(barrier == "expired" ? 15 : 1));

        var retry = await f.Start(request, 2);

        Assert.Equal(barrier == "expired" ? HttpStatusCode.Gone : HttpStatusCode.ServiceUnavailable, retry.StatusCode);
        Assert.Equal(0, f.GitHub.Mints);
        Assert.Single(Retained.IssuanceLedgers(f));
        Assert.Equal(pending, f.State.Version(pendingKey));
    }

    [Fact]
    public async Task Concurrent_unsent_retries_cannot_publish_two_replacement_verifiers()
    {
        var f = new InMemoryBroker();
        var (_, request, _) = await FencedEnrollment(f);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        // The first retry is held at its first commit that carries the unsent settlement, until the second retry won.
        var pause = new CommitPause(f.State, t => t.Writes("SettlementReason", "unsent"));
        var stale = f.Start(request, 2);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            Assert.Equal(HttpStatusCode.OK, (await f.Start(request, 2)).StatusCode);
            pause.Release.TrySetResult();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await stale).StatusCode);
            Assert.Equal(1, f.GitHub.Mints);
            Assert.Equal(2, Retained.IssuanceLedgers(f).Count);
            Assert.Equal(HttpStatusCode.OK, (await f.Start(request, 2)).StatusCode);
            Assert.Equal(1, f.GitHub.Mints);
        }
        finally
        {
            pause.Release.TrySetResult();
            await stale;
        }
    }

    // A setup v2 start held at its verifier send claim while reclamation settles the verifier as unsent: the claim then
    // loses, nothing is minted, and the start answers 503.
    private static async Task<(string Id, RepositoryVariableEnrollmentRequest Request, IssuanceLedger Ledger)> FencedEnrollment(
        InMemoryBroker f, string profile = "polling")
    {
        CloneEnrollmentContractTests.ConfigureGitHub(f.GitHub, profile);
        var id = Guid.NewGuid().ToString("N");
        var request = Retained.AgentRequest(id, profile);
        var pause = new CommitPause(f.State, t => t.Writes("State", "sending"));
        var writer = f.Start(request, 2);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            var ledger = Assert.Single(Retained.IssuanceLedgers(f)).Value;
            await Retained.ReclaimIssuance(f, ledger);
            pause.Release.TrySetResult();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await writer).StatusCode);
            Assert.Equal(0, f.GitHub.Mints);
            return (id, request, ledger);
        }
        finally
        {
            pause.Release.TrySetResult();
            await writer;
            // The next calls run as a restarted process would, without the spent pause.
            f.State.BeforeCommit = null;
        }
    }
}
