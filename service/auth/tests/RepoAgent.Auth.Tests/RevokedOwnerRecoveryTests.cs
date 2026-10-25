using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

// Owner recovery for an acknowledged controller enrollment: only its explicit revocation, with every GitHub token it
// could hold positively settled and every durable proof matching, hands the repository gate back. Decided by the core
// over the in-memory broker state and a manual clock.
public sealed class RevokedOwnerRecoveryTests
{
    private static readonly StateKey<RepositoryGate> GateKey = RepositoryGate.Key(Retained.Repository);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("polling")]
    [InlineData("polling-clone")]
    public async Task Revoked_acknowledged_owner_releases_only_after_all_capabilities_settle(string profile)
    {
        var f = new InMemoryBroker();
        var old = await AcknowledgedOwner(f, profile);
        var version = profile == "polling-clone" ? 2 : 1;
        Assert.Equal(HttpStatusCode.NoContent, (await f.Revoke(old.Device, old.Secret, version)).StatusCode);
        var device = f.Enrollment(old.Device);
        var receipt = f.State.Get(ReceiptKey(old))!;
        await Retained.ReclaimEnrollment(f, old.Id);
        await Retained.ReclaimEnrollment(f, old.Id);
        Assert.Equal(old.Device, f.Gate.OwnerEnrollmentId);
        var (_, blocked) = await Retained.StartAgent(f);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Conflict, (await f.Complete(blocked, 2)).StatusCode);
        await SettleOwnerCapabilities(f);

        await Retained.ReclaimEnrollment(f, old.Id);

        var replacement = await Retained.ActiveAgent(f);
        Assert.NotEqual(old.Device, replacement.Device);
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(replacement.Device, replacement.Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Issue(old.Device, old.Secret, version)).StatusCode);
        var gate = f.State.Version(GateKey);
        await Retained.ReclaimEnrollment(f, old.Id);
        Assert.Equal(gate, f.State.Version(GateKey));
        var released = f.Enrollment(old.Device);
        Assert.Equal((device.ExpiresAt, device.LastActivityAt, device.FirstTerminalAt, device.Acknowledged, device.State),
            (released.ExpiresAt, released.LastActivityAt, released.FirstTerminalAt, released.Acknowledged, released.State));
        var audit = f.State.Get(ReceiptKey(old))!;
        Assert.Equal(receipt.RevokedAt, audit.RevokedAt);
        Assert.Equal("revoked-acknowledged", audit.Reason);
        Assert.NotNull(audit.OwnerReleasedAt);
        Assert.Equal(3L, f.State.Get(EnrollmentQuota.Key)!.RetainedEnrollmentCount);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("expired-active")]
    [InlineData("missing-receipt")]
    [InlineData("missing-release")]
    [InlineData("binding")]
    [InlineData("epoch")]
    public async Task Acknowledged_owner_release_requires_explicit_closure_and_matching_durable_proofs(string barrier)
    {
        var f = new InMemoryBroker();
        var old = await AcknowledgedOwner(f);
        if (barrier is not ("active" or "expired-active"))
            Assert.Equal(HttpStatusCode.NoContent, (await f.Revoke(old.Device, old.Secret)).StatusCode);
        await Retained.ReclaimEnrollment(f, old.Id);
        await SettleOwnerCapabilities(f);
        if (barrier == "expired-active") f.Clock.Advance(TimeSpan.FromDays(91));
        if (barrier == "missing-receipt") Assert.True(f.State.Remove(ReceiptKey(old)));
        if (barrier == "missing-release") Assert.True(f.State.Remove(ReleaseProof.Enrollment(Retained.Repository, old.Id)));
        if (barrier == "binding")
            f.State.Change(ControllerEnrollment.Key(Retained.Repository, old.Device), row => row["CompletionId"] = Guid.NewGuid().ToString("N"));
        var before = f.State.Version(GateKey);
        Assert.NotNull(before);
        if (barrier == "epoch") f.DeploymentEpoch = "restored-epoch";

        if (barrier is "missing-receipt" or "missing-release" or "binding")
            await Assert.ThrowsAsync<IssuanceDenied>(() => Retained.ReclaimEnrollment(f, old.Id));
        else await Retained.ReclaimEnrollment(f, old.Id);

        Assert.Equal(before, f.State.Version(GateKey));
        Assert.Equal(old.Device, f.Gate.OwnerEnrollmentId);
    }

    [Fact]
    public async Task Revoked_acknowledged_owner_with_unknown_runtime_send_cannot_be_replaced()
    {
        var f = new InMemoryBroker();
        f.GitHub.RuntimeLifetime = TimeSpan.FromMinutes(6);
        var old = await AcknowledgedOwner(f);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var normal = f.GitHub.BeforeMint;
        f.GitHub.BeforeMint = _ => throw new GitHubUnavailable("Uncertain provider response");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Issue(old.Device, old.Secret)).StatusCode);
        f.GitHub.BeforeMint = normal;
        Assert.Equal(HttpStatusCode.NoContent, (await f.Revoke(old.Device, old.Secret)).StatusCode);
        await Retained.ReclaimEnrollment(f, old.Id);
        await SettleOwnerCapabilities(f);

        await Retained.ReclaimEnrollment(f, old.Id);

        Assert.Equal(old.Device, f.Gate.OwnerEnrollmentId);
        Assert.Equal(1L, f.Gate.OutstandingTokenCount);
        var receipt = f.State.Row(ReceiptKey(old));
        Assert.NotNull(receipt);
        Assert.False(receipt.ContainsKey("OwnerReleasedAt"));
        var (_, replacement) = await Retained.StartAgent(f);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Conflict, (await f.Complete(replacement, 2)).StatusCode);
    }

    [Fact]
    public async Task Stale_acknowledged_owner_release_cannot_overwrite_a_fresh_owner()
    {
        var f = new InMemoryBroker();
        var old = await AcknowledgedOwner(f);
        Assert.Equal(HttpStatusCode.NoContent, (await f.Revoke(old.Device, old.Secret)).StatusCode);
        await Retained.ReclaimEnrollment(f, old.Id);
        await SettleOwnerCapabilities(f);
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
        }
        finally
        {
            pause.Release.TrySetResult();
            try { await stale; } catch (Exception ex) when (ex is StateConflict or BrokerStateUnavailable) { }
        }
    }

    private static StateKey<RevocationReceipt> ReceiptKey(Retained.Journey owner) => RevocationReceipt.Key(Retained.Repository, owner.Device);

    // An acknowledged controller enrollment that was issued one GitHub token, with a minute passed after each step.
    private static async Task<Retained.Journey> AcknowledgedOwner(InMemoryBroker f, string profile = "polling")
    {
        var owner = await Retained.ActiveAgent(f, profile);
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(owner.Device, owner.Secret, profile == "polling-clone" ? 2 : 1)).StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        return owner;
    }

    // Past every GitHub token's expiry and grace, with each issuance attempt reclaimed.
    private static async Task SettleOwnerCapabilities(InMemoryBroker f)
    {
        f.Clock.Advance(TimeSpan.FromMinutes(66));
        foreach (var ledger in Retained.IssuanceLedgers(f)) await Retained.ReclaimIssuance(f, ledger.Value);
    }
}
