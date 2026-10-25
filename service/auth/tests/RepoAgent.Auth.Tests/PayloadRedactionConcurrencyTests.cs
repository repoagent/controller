using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

// Redaction against a writer held mid-transaction. A CommitPause holds the writer's first matching transaction before
// its versions are checked; redaction then writes the gate with the next sequence, so the released writer conflicts.
// It can neither replace a tombstone nor restore a removed payload, and a late locator routes only.
public sealed partial class PayloadRedactionTests
{
    [Theory]
    [InlineData("pending")]
    [InlineData("enrollment-attempt")]
    [InlineData("runtime-attempt")]
    public async Task Paused_add_cannot_replace_original_charged_key_tombstone(string stage)
    {
        var f = new InMemoryBroker();
        var active = stage == "runtime-attempt" ? await Retained.ActiveAgent(f) : null;
        if (active is null) AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        var id = Guid.NewGuid().ToString("N");
        var pause = new CommitPause(f.State, t => t.Writes("State", stage == "pending" ? "discovering" : "reserved"));
        var writer = Task.Run(() => active is null ? f.Start(Retained.AgentRequest(id), 2)
            : f.Issue(active.Device, active.Secret), Token);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var before = f.Rows();
            string target, generation;
            DateTimeOffset due;
            Func<Task> redact;
            if (stage == "pending")
            {
                var ledger = Retained.EnrollmentLedger(f, id).Value;
                (target, generation, due) = (PendingKey(id).Name, ledger.Generation!, ledger.PayloadPurgeAt!.Value);
                redact = () => RedactEnrollment(f, id, ledger);
            }
            else
            {
                // The ledger whose attempt the held transaction adds.
                var ledger = Assert.Single(IssuanceLedgers(before),
                    l => !before.Any(r => r.Scope == InMemoryBroker.Partition && r.Name == l.AttemptRowKey));
                (target, generation, due) = (ledger.AttemptRowKey!, ledger.Generation!, ledger.PayloadPurgeAt!.Value);
                redact = () => RedactIssuance(f, ledger);
            }
            Assert.DoesNotContain(before, r => r.Scope == InMemoryBroker.Partition && r.Name == target);
            f.Clock.Advance(due - f.Clock.GetUtcNow());
            await redact();
            var tombstone = f.State.Row(InMemoryBroker.Partition, target)!;
            var version = f.State.Version(InMemoryBroker.Partition, target)!;
            Assert.Equal("redacted", tombstone["State"]);
            Assert.Equal(generation, tombstone["Generation"]);
            pause.Release.TrySetResult();
            Assert.NotEqual(HttpStatusCode.OK, (await writer).StatusCode);
            Assert.Equal(version, f.State.Version(InMemoryBroker.Partition, target));
            var retained = f.State.Row(InMemoryBroker.Partition, target)!;
            Assert.False(retained.ContainsKey("RecoveryHash"));
            Assert.False(retained.ContainsKey("ProtectedToken"));
            Assert.False(retained.ContainsKey("ProtectedCompletion"));
            var after = f.Rows();
            foreach (var row in before)
            {
                var current = Assert.Single(after, r => r.Key == row.Key);
                foreach (var field in row.Row.Keys.Where(k => k.EndsWith("Count", StringComparison.Ordinal)
                    || k is "RowCredits" or "Slots" or "GlobalSlot"))
                    Assert.Equal(row.Row[field], current.Row[field]);
            }
            Assert.DoesNotContain(after, r => r.Name.StartsWith("device:", StringComparison.Ordinal)
                && (active is null || r.Name != EnrollmentKey(active.Device).Name));
            Assert.Equal(active is null ? 0 : 1, f.GitHub.Mints);
        }
        finally
        {
            pause.Release.TrySetResult();
            await writer;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paused_known_ciphertext_or_device_completion_cannot_rehydrate_after_cleanup(bool completion)
    {
        var f = new InMemoryBroker();
        string id, pendingId = "";
        if (completion) (id, pendingId) = await StartAgent(f);
        else
        {
            AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
            id = Guid.NewGuid().ToString("N");
        }
        var pause = new CommitPause(f.State, t => t.Stores(completion ? "ProtectedCompletion" : "ProtectedToken"));
        var writer = Task.Run(() => completion ? f.Complete(pendingId, 2) : f.Start(Retained.AgentRequest(id), 2), Token);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var before = f.Rows();
            string target;
            if (completion)
            {
                var ledger = Retained.EnrollmentLedger(f, id).Value;
                f.Clock.Advance(ledger.PayloadPurgeAt!.Value - f.Clock.GetUtcNow());
                await RedactEnrollment(f, id, ledger);
                target = PendingKey(id).Name;
            }
            else
            {
                var ledger = Assert.Single(IssuanceLedgers(before));
                f.Clock.Advance(ledger.PayloadPurgeAt!.Value - f.Clock.GetUtcNow());
                await RedactIssuance(f, ledger);
                target = ledger.AttemptRowKey!;
            }
            pause.Release.TrySetResult();
            Assert.NotEqual(HttpStatusCode.OK, (await writer).StatusCode);
            var redacted = f.State.Row(InMemoryBroker.Partition, target)!;
            Assert.False(redacted.ContainsKey(completion ? "ProtectedCompletion" : "ProtectedToken"));
            Assert.Equal(true, redacted["PayloadRedacted"]);
            var after = f.Rows();
            Assert.DoesNotContain(after, r => r.Name.StartsWith("device:", StringComparison.Ordinal));
            AssertCharges(before, after);
        }
        finally
        {
            pause.Release.TrySetResult();
            await writer;
        }
    }

    [Fact]
    public async Task Late_locator_publication_remains_routing_only_and_charged_after_redaction()
    {
        var f = new InMemoryBroker();
        var start = await StartAgent(f);
        var ledger = Retained.EnrollmentLedger(f, start.Id).Value;
        var pause = new CommitPause(f.State, t => t.InScope(EnrollmentLocator.Key(ledger.EnrollmentId!).Scope));
        var writer = Task.Run(() => f.Complete(start.Pending, 2), Token);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var before = f.Rows();
            Assert.DoesNotContain(before, r => r.Scope.StartsWith("locator:", StringComparison.Ordinal));
            Assert.Single(before, r => r.Name.StartsWith("device:", StringComparison.Ordinal));
            f.Clock.Advance(ledger.PayloadPurgeAt!.Value - f.Clock.GetUtcNow());
            await RedactEnrollment(f, start.Id, ledger);
            pause.Release.TrySetResult();
            Assert.Equal(HttpStatusCode.Gone, (await writer).StatusCode);
            var after = f.Rows();
            var locator = Assert.Single(after, r => r.Scope.StartsWith("locator:", StringComparison.Ordinal));
            var route = locator.Read<EnrollmentLocator>();
            Assert.Equal(ledger.EnrollmentId, route.EnrollmentId);
            Assert.Equal(ledger.PendingId, route.PendingId);
            Assert.Equal(ledger.RepositoryId, route.RepositoryId);
            Assert.Equal(new[] { "DeviceId", "PendingId", "RepositoryId" }, locator.Row.Keys.Order(StringComparer.Ordinal));
            AssertCharges(before, [.. after.Where(r => r.Scope != locator.Scope)]);
            Assert.False(f.State.Row(PendingKey(start.Id))!.ContainsKey("ProtectedCompletion"));
            Assert.False(f.State.Row(EnrollmentKey(ledger.EnrollmentId!))!.ContainsKey("SecretHash"));
        }
        finally
        {
            pause.Release.TrySetResult();
            await writer;
        }
    }

    [Fact]
    public async Task Release_winning_cleanup_race_preserves_renewed_live_secret()
    {
        var f = new InMemoryBroker();
        var j = await Retained.ActiveAgent(f);
        var ledger = Retained.EnrollmentLedger(f, j.Id).Value;
        var key = EnrollmentKey(j.Device);
        var device = f.State.Row(key)!;
        var originalExpiry = f.State.Get(key)!.ExpiresAt!.Value;
        f.Clock.Advance(originalExpiry.AddMinutes(-1) - f.Clock.GetUtcNow());
        var releasePause = new CommitPause(f.State, t => t.Writes("State", "released"));
        var writer = Task.Run(() => f.Issue(j.Device, j.Secret), Token);
        CommitPause? cleanupPause = null;
        Task? cleanup = null;
        try
        {
            await releasePause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var before = f.Rows();
            f.Clock.Advance(originalExpiry.AddHours(24) - f.Clock.GetUtcNow());
            // The issuance writer is held, so redaction is the only writer this pause can match.
            cleanupPause = new CommitPause(f.State, t => t.Writes("PayloadRedacted", true));
            cleanup = Task.Run(() => RedactEnrollment(f, j.Id, ledger), Token);
            await cleanupPause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            releasePause.Release.TrySetResult();
            Assert.Equal(HttpStatusCode.OK, (await writer).StatusCode);
            cleanupPause.Release.TrySetResult();
            await Assert.ThrowsAsync<StateConflict>(() => cleanup);
            var renewed = f.State.Row(key)!;
            Assert.True(f.State.Get(key)!.ExpiresAt > f.Clock.GetUtcNow());
            Assert.Equal(device["SecretHash"], renewed["SecretHash"]);
            Assert.False(renewed.ContainsKey("PayloadRedacted"));
            AssertCharges(before, f.Rows());
        }
        finally
        {
            releasePause.Release.TrySetResult();
            cleanupPause?.Release.TrySetResult();
            await writer;
            if (cleanup is not null)
            {
                try { await cleanup; }
                catch (Exception ex) when (ex is StateConflict or BrokerStateUnavailable) { }
            }
        }
    }

    [Fact]
    public async Task Stale_runtime_token_replacement_cannot_restore_redacted_ciphertext()
    {
        var f = new InMemoryBroker();
        var j = await Retained.ActiveAgent(f);
        // The runtime attempt's known outcome, with its protected GitHub token.
        var pause = new CommitPause(f.State, t => t.Writes.Any(write => write.Row is { } row && row.ContainsKey("ProtectedToken")
            && Equals(row.GetValueOrDefault("Purpose"), "runtime") && Equals(row.GetValueOrDefault("State"), "known")));
        var writer = Task.Run(() => f.Issue(j.Device, j.Secret), Token);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var before = f.Rows();
            var ledger = Assert.Single(IssuanceLedgers(before), l => l.Purpose == "runtime");
            var attemptKey = Retained.Attempt(ledger);
            Assert.Equal("sending", f.State.Get(attemptKey)!.State);
            Assert.False(f.State.Row(attemptKey)!.ContainsKey("ProtectedToken"));
            f.Clock.Advance(ledger.PayloadPurgeAt!.Value - f.Clock.GetUtcNow());
            await RedactIssuance(f, ledger);
            pause.Release.TrySetResult();
            Assert.NotEqual(HttpStatusCode.OK, (await writer).StatusCode);
            var attempt = f.State.Row(attemptKey)!;
            Assert.False(attempt.ContainsKey("ProtectedToken"));
            Assert.Equal(true, attempt["PayloadRedacted"]);
            Assert.Equal("sending", attempt["State"]);
            Assert.False(attempt.ContainsKey("SettledAt"));
            AssertCharges(before, f.Rows());
        }
        finally
        {
            pause.Release.TrySetResult();
            await writer;
        }
    }
}
