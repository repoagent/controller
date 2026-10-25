using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

// Owner recovery for a controller enrollment that was completed but never acknowledged: once its deadline passed and
// every GitHub token it could hold is positively settled, reclamation hands the repository gate back, and nothing less
// releases it. Decided by the core over the in-memory broker state and a manual clock.
public sealed class ExpiredEnrollmentRecoveryTests
{
    private static readonly StateKey<RepositoryGate> GateKey = RepositoryGate.Key(Retained.Repository);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_unacknowledged_owner_allows_fresh_enrollment_after_positive_settlement(bool pendingAlreadyReleased)
    {
        var f = new InMemoryBroker();
        var old = await Retained.CompletedAgent(f);
        f.Clock.Advance(TimeSpan.FromMinutes(15));
        if (pendingAlreadyReleased) await Retained.ReclaimEnrollment(f, old.Id);
        await SettleVerifiers(f);

        await Retained.ReclaimEnrollment(f, old.Id);
        await Retained.ReclaimEnrollment(f, old.Id);

        // Exercise the consumer's actual recovery outcome, not just a vacant-gate projection.
        var replacement = await Retained.ActiveAgent(f);
        Assert.NotEqual(old.Device, replacement.Device);
        Assert.NotEqual(old.Secret, replacement.Secret);
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(replacement.Device, replacement.Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Issue(old.Device, old.Secret)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await f.Acknowledge(old.Device, old.Completion, old.Secret, 2)).StatusCode);

        // A delayed old cleanup visit must not close or release the replacement.
        var gate = f.State.Version(GateKey);
        await Retained.ReclaimEnrollment(f, old.Id);
        Assert.Equal(gate, f.State.Version(GateKey));
        Assert.Equal(replacement.Device, f.Gate.OwnerEnrollmentId);
        var retired = f.Enrollment(old.Device);
        Assert.Equal("revoked", retired.State);
        Assert.False(retired.Acknowledged);
        Assert.Equal(old.Device, f.State.Get(RevocationReceipt.Key(Retained.Repository, old.Device))!.EnrollmentId);
        var quota = f.State.Get(EnrollmentQuota.Key)!;
        Assert.Equal(2L, quota.RetainedEnrollmentCount);
        Assert.Equal(1L, quota.Count);
    }

    [Fact]
    public async Task Unknown_setup_send_still_blocks_owner_recovery_after_known_tokens_expire()
    {
        var f = new InMemoryBroker();
        var old = await Retained.CompletedAgent(f);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.GitHub.BeforeMint = _ => throw new GitHubUnavailable("Provider send outcome unknown");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(Retained.AgentRequest(Guid.NewGuid().ToString("N")), 2)).StatusCode);
        f.Clock.Advance(TimeSpan.FromDays(2));
        foreach (var ledger in Retained.IssuanceLedgers(f)) await Retained.ReclaimIssuance(f, ledger.Value);
        await Retained.ReclaimEnrollment(f, old.Id);

        await Retained.ReclaimEnrollment(f, old.Id);

        Assert.Equal(old.Device, f.Gate.OwnerEnrollmentId);
        Assert.Equal(1L, f.Gate.OutstandingTokenCount);
        Assert.Null(f.State.Row(RevocationReceipt.Key(Retained.Repository, old.Device)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Issue(old.Device, old.Secret)).StatusCode);
    }

    [Theory]
    [InlineData("acknowledged")]
    [InlineData("epoch")]
    [InlineData("pending-binding")]
    [InlineData("missing-device")]
    [InlineData("missing-release")]
    [InlineData("missing-acknowledgment")]
    [InlineData("app-binding")]
    [InlineData("unknown-property")]
    public async Task Owner_recovery_requires_never_activated_device_and_complete_current_binding_proofs(string barrier)
    {
        var f = new InMemoryBroker();
        var old = await Retained.CompletedAgent(f);
        if (barrier == "acknowledged")
        {
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(HttpStatusCode.OK, (await f.Acknowledge(old.Device, old.Completion, old.Secret, 2)).StatusCode);
        }
        f.Clock.Advance(TimeSpan.FromDays(91));
        await SettleVerifiers(f);
        await Retained.ReclaimEnrollment(f, old.Id);
        var device = ControllerEnrollment.Key(Retained.Repository, old.Device);
        if (barrier is "pending-binding" or "missing-acknowledgment" or "app-binding" or "unknown-property")
            f.State.Change(device, row =>
            {
                if (barrier == "pending-binding") row["PendingId"] = "123456_" + Guid.NewGuid().ToString("N");
                if (barrier == "missing-acknowledgment") Assert.True(row.Remove("Acknowledged"));
                // An enrollment of another App, which the deployment's binding does not encode.
                if (barrier == "app-binding") row["AppSlug"] = "another-app";
                // A property no record maps: the release reads the gate and the enrollment through their records.
                if (barrier == "unknown-property") row["Unrecognized"] = "kept by no record";
            });
        if (barrier == "missing-device") Assert.True(f.State.Remove(device));
        if (barrier == "missing-release") Assert.True(f.State.Remove(ReleaseProof.Enrollment(Retained.Repository, old.Id)));
        var before = f.State.Version(GateKey);
        Assert.NotNull(before);
        if (barrier == "epoch") f.DeploymentEpoch = "restored-epoch";

        if (barrier is "pending-binding" or "missing-device" or "missing-release" or "app-binding")
            await Assert.ThrowsAsync<IssuanceDenied>(() => Retained.ReclaimEnrollment(f, old.Id));
        else if (barrier == "unknown-property") await Assert.ThrowsAsync<BrokerRecordUnreadable>(() => Retained.ReclaimEnrollment(f, old.Id));
        else await Retained.ReclaimEnrollment(f, old.Id);

        Assert.Equal(before, f.State.Version(GateKey));
        Assert.Equal(old.Device, f.Gate.OwnerEnrollmentId);
    }

    [Fact]
    public async Task Stale_recovery_cannot_release_a_replacement_after_another_worker_recovers_the_owner()
    {
        var f = new InMemoryBroker();
        var old = await Retained.CompletedAgent(f);
        f.Clock.Advance(TimeSpan.FromMinutes(15));
        await Retained.ReclaimEnrollment(f, old.Id);
        await SettleVerifiers(f);
        // The first owner release to commit is the stale worker's; it is held until another worker released the owner
        // and a replacement took the gate.
        var pause = new CommitPause(f.State, t => t.Writes("State", "vacant"));
        var stale = Retained.ReclaimEnrollment(f, old.Id);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            await Retained.ReclaimEnrollment(f, old.Id);
            var replacement = await Retained.ActiveAgent(f);
            var gate = f.State.Version(GateKey);
            pause.Release.TrySetResult();

            await Assert.ThrowsAsync<StateConflict>(() => stale);

            Assert.Equal(gate, f.State.Version(GateKey));
            Assert.Equal(replacement.Device, f.Gate.OwnerEnrollmentId);
            Assert.Equal(HttpStatusCode.OK, (await f.Issue(replacement.Device, replacement.Secret)).StatusCode);
        }
        finally
        {
            pause.Release.TrySetResult();
            try { await stale; } catch (Exception ex) when (ex is StateConflict or BrokerStateUnavailable) { }
        }
    }

    // Past every verifier GitHub token's expiry and grace, with each issuance attempt reclaimed.
    private static async Task SettleVerifiers(InMemoryBroker f)
    {
        var ledgers = Retained.IssuanceLedgers(f);
        var expiry = ledgers.Max(ledger => f.State.Get(Retained.Attempt(ledger.Value))!.ExpiresAt!.Value.AddMinutes(5));
        if (expiry > f.Clock.GetUtcNow()) f.Clock.Advance(expiry - f.Clock.GetUtcNow());
        foreach (var ledger in ledgers) await Retained.ReclaimIssuance(f, ledger.Value);
    }
}
