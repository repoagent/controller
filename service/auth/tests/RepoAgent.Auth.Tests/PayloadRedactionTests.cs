using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

// Payload redaction, decided by the core over the in-memory broker state and a manual clock. Once a payload is due,
// redaction removes setup secret hashes, controller secret hashes, protected completions and protected GitHub tokens,
// and leaves every charge and count as it was. Redaction is built without any signing, provider or key dependency.
public sealed partial class PayloadRedactionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Abandoned_enrollment_loses_both_recovery_hashes_after_original_payload_deadline()
    {
        var f = new InMemoryBroker();
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        var id = Guid.NewGuid().ToString("N");
        var start = await f.Start(Retained.AgentRequest(id), 2);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var ledgerKey = EnrollmentLedger.Key(id);
        var pendingKey = PendingKey(id);
        var reservation = f.State.Get(ledgerKey)!;
        var pending = f.State.Get(pendingKey)!;
        var generation = reservation.Generation!;
        var deadline = reservation.Deadline!.Value;
        var purgeAt = deadline.AddHours(24);
        Assert.Equal(start.Json.GetProperty("pendingId").GetString(), reservation.PendingId);
        Assert.False(string.IsNullOrEmpty(generation));
        Assert.Equal(generation, pending.Generation);
        Assert.Equal(purgeAt, reservation.PayloadPurgeAt);
        Assert.Equal(purgeAt, pending.PayloadPurgeAt);
        Assert.True(f.State.Row(ledgerKey)!.ContainsKey("RecoveryHash"));
        Assert.True(f.State.Row(pendingKey)!.ContainsKey("RecoveryHash"));
        Assert.Equal(8, reservation.RowCredits);
        var quota = f.State.Get(EnrollmentQuota.Key)!;
        Assert.Equal(1, quota.RetainedEnrollmentCount);
        Assert.Equal(1, quota.CandidateCount);

        f.Clock.Advance(purgeAt.AddTicks(1) - f.Clock.GetUtcNow());
        await f.Redaction().RedactEnrollmentAsync(id, generation, Token);

        var redactedReservation = f.State.Get(ledgerKey)!;
        var redactedPending = f.State.Get(pendingKey)!;
        var retainedQuota = f.State.Get(EnrollmentQuota.Key)!;
        Assert.Equal(generation, redactedReservation.Generation);
        Assert.Equal(generation, redactedPending.Generation);
        Assert.Equal(deadline, redactedReservation.Deadline);
        Assert.Equal(deadline, redactedPending.Deadline);
        Assert.Equal(purgeAt, redactedReservation.PayloadPurgeAt);
        Assert.Equal(purgeAt, redactedPending.PayloadPurgeAt);
        Assert.Equal(8, redactedReservation.RowCredits);
        Assert.Equal(quota.RetainedEnrollmentCount, retainedQuota.RetainedEnrollmentCount);
        Assert.Equal(quota.CandidateCount, retainedQuota.CandidateCount);
        Assert.Multiple(
            () => Assert.False(f.State.Row(ledgerKey)!.ContainsKey("RecoveryHash"), "Global recovery hash must be physically absent after its original payload deadline."),
            () => Assert.False(f.State.Row(pendingKey)!.ContainsKey("RecoveryHash"), "Repository recovery hash must be physically absent after its original payload deadline."));
    }
}
