using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

// When redaction is due and what it keeps. Payload deadlines are inclusive and come from the original clocks: the
// pending deadline, a controller enrollment's expiry or first terminal time, and an attempt's reservation. A repeated
// run cannot move them. Redaction never settles a known or unknown GitHub token, never resets an old-epoch gate, and
// leaves every charge and count as it was.
public sealed partial class PayloadRedactionTests
{
    [Fact]
    public async Task Enrollment_deadline_is_inclusive_and_repeated_cleanup_cannot_rebase_it()
    {
        var f = new InMemoryBroker();
        var (id, _) = await StartAgent(f);
        var ledger = Retained.EnrollmentLedger(f, id).Value;
        var due = ledger.PayloadPurgeAt!.Value;
        f.Clock.Advance(due.AddTicks(-1) - f.Clock.GetUtcNow());
        await RedactEnrollment(f, id, ledger);
        Assert.True(f.State.Row(EnrollmentLedger.Key(id))!.ContainsKey("RecoveryHash"));
        Assert.True(f.State.Row(PendingKey(id))!.ContainsKey("RecoveryHash"));
        var before = f.Rows();
        f.Clock.Advance(TimeSpan.FromTicks(1));
        await RedactEnrollment(f, id, ledger);
        Assert.False(f.State.Row(PendingKey(id))!.ContainsKey("RecoveryHash"));
        var first = f.State.Get(PendingKey(id))!;
        Assert.Equal(ledger.Deadline, first.FirstTerminalAt);
        f.Clock.Advance(TimeSpan.FromDays(31));
        await RedactEnrollment(f, id, ledger);
        var later = f.State.Get(PendingKey(id))!;
        Assert.Equal(first.FirstTerminalAt, later.FirstTerminalAt);
        Assert.Equal(due, later.PayloadPurgeAt);
        Assert.False(f.State.Row(PendingKey(id))!.ContainsKey("NextWorkAt"));
        Assert.False(f.State.Row(EnrollmentLedger.Key(id))!.ContainsKey("BodyHash"));
        AssertCharges(before, f.Rows());
    }

    [Fact]
    public async Task Active_acknowledgment_survives_audit_compaction_without_activity_extension()
    {
        var f = new InMemoryBroker();
        var j = await Retained.ActiveAgent(f);
        var ledger = Retained.EnrollmentLedger(f, j.Id).Value;
        var key = EnrollmentKey(j.Device);
        var initial = f.State.Row(key)!;
        var before = f.Rows();
        f.Clock.Advance(TimeSpan.FromDays(31));
        await RedactEnrollment(f, j.Id, ledger);
        Assert.Equal(HttpStatusCode.OK, (await f.Acknowledge(j.Device, j.Completion, j.Secret, 2)).StatusCode);
        var current = f.State.Row(key)!;
        foreach (var field in new[] { "SecretHash", "Marker", "Challenge", "CompletionId", "PendingId", "LastActivityAt", "ExpiresAt" })
            Assert.Equal(initial[field], current[field]);
        var pending = f.State.Row(PendingKey(j.Id))!;
        Assert.False(pending.ContainsKey("ProtectedCompletion"));
        Assert.False(pending.ContainsKey("RecoveryHash"));
        Assert.False(pending.ContainsKey("NextWorkAt"));
        Assert.Equal(j.Completion, pending["CompletionId"]);
        AssertCharges(before, f.Rows());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoked_or_inactive_secret_uses_original_terminal_clock(bool revoke)
    {
        var f = new InMemoryBroker();
        var j = await Retained.ActiveAgent(f);
        var ledger = Retained.EnrollmentLedger(f, j.Id).Value;
        var key = EnrollmentKey(j.Device);
        DateTimeOffset terminal;
        if (revoke)
        {
            Assert.Equal(HttpStatusCode.NoContent, (await f.Revoke(j.Device, j.Secret)).StatusCode);
            terminal = f.State.Get(key)!.FirstTerminalAt!.Value;
            f.Clock.Advance(TimeSpan.FromHours(1));
            Assert.Equal(HttpStatusCode.NoContent, (await f.Revoke(j.Device, j.Secret)).StatusCode);
        }
        else terminal = f.State.Get(key)!.ExpiresAt!.Value;
        var before = f.Rows();
        f.Clock.Advance(terminal.AddHours(24).AddTicks(-1) - f.Clock.GetUtcNow());
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Issue(j.Device, j.Secret)).StatusCode);
        await RedactEnrollment(f, j.Id, ledger);
        Assert.True(f.State.Row(key)!.ContainsKey("SecretHash"));
        f.Clock.Advance(TimeSpan.FromTicks(1));
        await RedactEnrollment(f, j.Id, ledger);
        Assert.False(f.State.Row(key)!.ContainsKey("SecretHash"));
        Assert.Equal(terminal, f.State.Get(key)!.FirstTerminalAt);
        f.Clock.Advance(TimeSpan.FromDays(2));
        await RedactEnrollment(f, j.Id, ledger);
        Assert.Equal(terminal, f.State.Get(key)!.FirstTerminalAt);
        AssertCharges(before, f.Rows());
    }

    [Theory]
    [InlineData("invalid-expiry")]
    [InlineData("missing-fallback")]
    [InlineData("unknown")]
    public async Task Issuance_fallback_redacts_without_settling_known_or_unknown_authority(string fault)
    {
        var f = new InMemoryBroker();
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        f.GitHub.BeforeRevoke = _ => throw new GitHubUnavailable("Revocation unavailable");
        if (fault == "unknown") f.GitHub.BeforeMint = _ => throw new GitHubUnavailable("Unknown provider outcome");
        // GitHub's answer carries the token, but neither a readable expiry nor any scope.
        else f.GitHub.MintAnswer = (_, _) => GitHubRead.Found(new MintedGitHubToken("known-invalid-expiry", null, null, null, null));
        var id = Guid.NewGuid().ToString("N");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(Retained.AgentRequest(id), 2)).StatusCode);
        var rows = f.Rows();
        var stored = Assert.Single(rows, r => r.Name.StartsWith("attempt:", StringComparison.Ordinal));
        var ledger = Assert.Single(IssuanceLedgers(rows));
        var attemptKey = Retained.Attempt(ledger);
        Assert.Equal(attemptKey.Name, stored.Name);
        var attempt = stored.Row;
        Assert.Equal(fault == "unknown" ? "sending" : "known", attempt["State"]);
        if (fault != "unknown")
        {
            Assert.True(attempt.ContainsKey("ProtectedToken"));
            Assert.Equal(DateTimeOffset.MaxValue, stored.Read<IssuanceAttempt>().ExpiresAt);
            Assert.False(stored.Read<IssuanceAttempt>().ScopeValid);
        }
        if (fault == "missing-fallback")
        {
            f.State.Change(attemptKey, row =>
            {
                row.Remove("ReservedAt");
                row.Remove("PayloadPurgeAt");
            });
            attempt = f.State.Row(attemptKey)!;
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(Retained.AgentRequest(id), 2)).StatusCode);
            Assert.Equal(1, f.GitHub.Mints);
        }
        else
        {
            var due = ledger.ReservedAt!.Value.AddHours(24);
            f.Clock.Advance(due.AddTicks(-1) - f.Clock.GetUtcNow());
            await RedactIssuance(f, ledger);
            var notDue = f.State.Row(attemptKey)!;
            Assert.Equal(attempt.ContainsKey("ProtectedToken"), notDue.ContainsKey("ProtectedToken"));
            Assert.False(notDue.ContainsKey("PayloadRedacted"));
            f.Clock.Advance(TimeSpan.FromTicks(1));
        }
        var before = f.Rows();
        await RedactIssuance(f, ledger);
        var after = f.State.Row(attemptKey)!;
        Assert.False(after.ContainsKey("ProtectedToken"));
        Assert.Equal(true, after["PayloadRedacted"]);
        foreach (var field in attempt.Keys.Where(k => k != "ProtectedToken"))
            Assert.Equal(attempt[field], after[field]);
        Assert.False(after.ContainsKey("SettledAt"));
        AssertCharges(before, f.Rows());
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotEqual(HttpStatusCode.OK, (await f.Start(Retained.AgentRequest(id), 2)).StatusCode);
        Assert.Equal(1, f.GitHub.Mints);
    }

    [Fact]
    public async Task Due_old_epoch_issuance_is_redacted_behind_current_owner_without_resetting_gate()
    {
        var f = new InMemoryBroker();
        var j = await Retained.ActiveAgent(f);
        var ledger = Assert.Single(IssuanceLedgers(f.Rows()));
        var attemptKey = Retained.Attempt(ledger);
        var attempt = f.State.Row(attemptKey)!;
        Assert.True(attempt.ContainsKey("ProtectedToken"));
        Assert.Equal(j.Device, f.Gate.OwnerEnrollmentId);
        f.State.Change(GateKey, row => row["Epoch"] = "new-deployment");
        var gate = f.State.Row(GateKey)!;
        f.Clock.Advance(ledger.PayloadPurgeAt!.Value - f.Clock.GetUtcNow());
        var before = f.Rows();
        await RedactIssuance(f, ledger);
        var redacted = f.State.Row(attemptKey)!;
        Assert.False(redacted.ContainsKey("ProtectedToken"));
        Assert.Equal(attempt["State"], redacted["State"]);
        var current = f.State.Row(GateKey)!;
        foreach (var field in new[] { "Epoch", "OwnerDeviceId", "State", "OutstandingTokenCount", "PendingCount" })
            Assert.Equal(gate[field], current[field]);
        AssertCharges(before, f.Rows());
    }

    private static readonly StateKey<RepositoryGate> GateKey = RepositoryGate.Key(Retained.Repository);
    private static StateKey<PendingControllerEnrollment> PendingKey(string id) => PendingControllerEnrollment.Key(Retained.Repository, id);
    private static StateKey<ControllerEnrollment> EnrollmentKey(string enrollmentId) => ControllerEnrollment.Key(Retained.Repository, enrollmentId);

    // Started, with a minute passed so that the next operation may claim new work.
    private static async Task<(string Id, string Pending)> StartAgent(InMemoryBroker f)
    {
        var started = await Retained.StartAgent(f);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        return started;
    }

    private static Task RedactEnrollment(InMemoryBroker f, string id, EnrollmentLedger ledger) =>
        f.Redaction().RedactEnrollmentAsync(id, ledger.Generation!, Token);

    private static Task RedactIssuance(InMemoryBroker f, IssuanceLedger ledger) =>
        f.Redaction().RedactIssuanceAsync(ledger.OperationId!, ledger.Generation!, Token);

    // The retained issuance ledgers among the rows.
    private static List<IssuanceLedger> IssuanceLedgers(IEnumerable<StoredRow> rows) =>
        [.. rows.Where(r => r.Scope == IssuanceCapacity.Scope && r.Name.StartsWith("operation:", StringComparison.Ordinal))
            .Select(r => r.Read<IssuanceLedger>())];

    // Every row is still there with the same charges and counts, and no row was added.
    private static void AssertCharges(IReadOnlyList<StoredRow> before, IReadOnlyList<StoredRow> after)
    {
        foreach (var row in before)
        {
            var current = Assert.Single(after, r => r.Key == row.Key);
            foreach (var field in row.Row.Keys.Where(k => k.EndsWith("Count", StringComparison.Ordinal)
                || k is "RowCredits" or "GlobalSlot" or "Slots" or "Admissions" or "Reads" or "Signs" or "Mints"))
                Assert.Equal(row.Row[field], current.Row[field]);
        }
        Assert.Equal(before.Count, after.Count);
    }
}
