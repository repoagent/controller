using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class RetainedAuthorityObservationTests
{
    private const string Repository = "1432194081", Epoch = "configured-production", Owner = "private-owner";
    private const string Runtime = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Verifier = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly string[] GateReadPair = ["gate", "gate"];
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Closed_gate_and_revoked_owner_are_observed_through_exact_reads_without_any_settlement_conclusion()
    {
        var state = Fixture();
        var now = new FixedClock();
        var report = await new RetainedAuthorityObservation(state, Epoch, now).ObserveAsync(Token);
        Assert.Equal("closed", report.Gate.Fields!["observedState"]);
        Assert.Equal("revoked", report.Owner.Fields!["observedState"]);
        Assert.Equal("absent", report.Receipt.Fields!["reason"]);
        Assert.Equal("at-or-before-observed-time", report.RuntimeAttempt.Fields!["expiresAt"]);
        Assert.Equal(6, report.ReadCount);
        Assert.Equal("unchanged", report.GateObservation);
        Assert.False(report.SettlementProven);
        Assert.False(report.AllOutstandingAttemptsExamined);
        Assert.Equal(1, now.Reads);
        Assert.Equal(new[] { "gate", "device:" + Owner, "revocation:" + Owner, "attempt:" + Runtime, "attempt:" + Verifier, "gate" }, state.Keys);
        Assert.DoesNotContain(Owner, JsonSerializer.Serialize(report), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_gate_and_absent_pointers_are_distinct_and_never_create_followup_reads()
    {
        var state = new ReadOnlyState();
        var missing = await Observe(state);
        Assert.Equal(("unavailable", "missing", 1), (missing.State, missing.Gate.Read, missing.ReadCount));
        Assert.Equal("unavailable", missing.Owner.Read);
        state.Inner.Seed(RepositoryGate.Key(Repository), RepositoryGate.Vacant(Repository, Epoch));
        var vacant = await Observe(state);
        Assert.Equal("not-referenced", vacant.Owner.Read);
        Assert.Equal("not-referenced", vacant.Receipt.Read);
        Assert.Equal("not-referenced", vacant.RuntimeAttempt.Read);
        Assert.Equal("not-referenced", vacant.VerifierAttempt.Read);
        Assert.Equal(2, vacant.ReadCount);
    }

    [Fact]
    public async Task Missing_owner_does_not_hide_its_receipt_or_independently_referenced_verifier()
    {
        var state = Fixture();
        state.Inner.Remove(ControllerEnrollment.Key(Repository, Owner));
        var report = await Observe(state);
        Assert.Equal("missing", report.Owner.Read);
        Assert.Equal("present", report.Receipt.Read);
        Assert.Equal("present", report.VerifierAttempt.Read);
        Assert.Equal("unavailable", report.RuntimeAttempt.Read);
        Assert.Equal(5, report.ReadCount);
    }

    [Fact]
    public async Task Invalid_references_are_not_followed_and_scope_mismatches_are_never_trusted()
    {
        var state = Fixture();
        state.Inner.Seed(RepositoryGate.Key(Repository), new RepositoryGate
        { RepositoryId = Repository, Epoch = Epoch, OwnerEnrollmentId = "private/invalid", VerifierAttempt = "private-invalid" });
        var report = await Observe(state);
        Assert.Equal("invalid-reference", report.Owner.Read);
        Assert.Equal("invalid-reference", report.Receipt.Read);
        Assert.Equal("invalid-reference", report.VerifierAttempt.Read);
        Assert.Equal(GateReadPair, state.Keys);
        state = Fixture();
        state.Inner.Seed(RepositoryGate.Key(Repository), new RepositoryGate { RepositoryId = "another", OwnerEnrollmentId = Owner });
        report = await Observe(state);
        Assert.Equal("binding-mismatch", report.Gate.Read);
        Assert.Equal(1, report.ReadCount);
        state = Fixture();
        state.Inner.Seed(ControllerEnrollment.Key(Repository, Owner), new ControllerEnrollment
        { RepositoryId = "another", EnrollmentId = Owner, RuntimeAttempt = Runtime });
        report = await Observe(state);
        Assert.Equal("binding-mismatch", report.Owner.Read);
        Assert.DoesNotContain("attempt:" + Runtime, state.Keys);
    }

    [Fact]
    public async Task Shared_runtime_and_verifier_reference_is_read_once_and_cannot_prove_two_purposes()
    {
        var state = Fixture();
        var gate = (await state.Inner.ReadAsync(RepositoryGate.Key(Repository), Token))!.Value;
        gate.VerifierAttempt = Runtime;
        state.Inner.Seed(RepositoryGate.Key(Repository), gate);
        var report = await Observe(state);
        Assert.Equal("binding-mismatch", report.RuntimeAttempt.Read);
        Assert.Equal("binding-mismatch", report.VerifierAttempt.Read);
        Assert.NotNull(report.RuntimeAttempt.Fields);
        Assert.Equal("purpose-ambiguous", report.RuntimeAttempt.Fields["referenceBinding"]);
        Assert.Equal(1, state.Keys.Count(key => key == "attempt:" + Runtime));
        Assert.Equal(5, report.ReadCount);
    }

    [Fact]
    public async Task Unknown_fields_old_epochs_and_expired_clocks_remain_partial_observations_not_zero_or_settlement()
    {
        var state = Fixture();
        var gate = (await state.Inner.ReadAsync(RepositoryGate.Key(Repository), Token))!.Value;
        gate.Epoch = "old-production";
        gate.State = "DO_NOT_EXPORT";
        gate.OutstandingTokenCount = -1;
        gate.PendingCount = null;
        state.Inner.Seed(RepositoryGate.Key(Repository), gate);
        var owner = (await state.Inner.ReadAsync(ControllerEnrollment.Key(Repository, Owner), Token))!.Value;
        owner.Acknowledged = null;
        owner.ControllerSecretHash = owner.Challenge = owner.Marker = owner.RuntimeObservation = "DO_NOT_EXPORT";
        owner.ExpiresAt = FixedClock.Now.AddHours(1);
        state.Inner.Seed(ControllerEnrollment.Key(Repository, Owner), owner);
        var attempt = (await state.Inner.ReadAsync(IssuanceAttempt.Key(Repository, Runtime), Token))!.Value;
        attempt.ProtectedToken = attempt.Generation = attempt.PendingId = "DO_NOT_EXPORT";
        attempt.State = "settled";
        attempt.SettlementReason = "DO_NOT_EXPORT";
        attempt.JwtExpiresAt = FixedClock.Now.AddMinutes(-1);
        attempt.CapacityReleased = null;
        state.Inner.Seed(IssuanceAttempt.Key(Repository, Runtime), attempt);
        var report = await Observe(state);
        Assert.Equal("partial", report.State);
        Assert.Equal("mismatch", report.Gate.EpochRelation);
        Assert.Equal("unknown", report.Gate.Fields!["observedState"]);
        Assert.Null(report.Gate.Fields["outstandingTokenCount"]);
        Assert.Null(report.Gate.Fields["pendingCount"]);
        Assert.Null(report.Owner.Fields!["acknowledged"]);
        Assert.Null(report.RuntimeAttempt.Fields!["capacityReleased"]);
        Assert.Equal("after-observed-time", report.Owner.Fields["expiresAt"]);
        Assert.Equal("at-or-before-observed-time", report.RuntimeAttempt.Fields["jwtExpiresAt"]);
        Assert.Equal("settled", report.RuntimeAttempt.Fields["observedState"]);
        Assert.False(report.SettlementProven);
        var json = JsonSerializer.Serialize(report);
        foreach (var secret in new[] { "DO_NOT_EXPORT", Owner, Runtime, Verifier, Epoch, "old-production" })
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task Provider_failure_at_each_read_is_bounded_and_never_exports_exception_text(int failedRead)
    {
        foreach (var failure in new Exception[] { new BrokerStateUnavailable("DO_NOT_EXPORT"), new BrokerRecordUnreadable("DO_NOT_EXPORT"), new OperationCanceledException("DO_NOT_EXPORT") })
        {
            var state = Fixture();
            state.Inner.BeforeRead = _ => state.Keys.Count == failedRead ? throw failure : Task.CompletedTask;
            var report = await Observe(state);
            Assert.NotEqual("observed", report.State);
            Assert.InRange(report.ReadCount, 1, 6);
            Assert.DoesNotContain("DO_NOT_EXPORT", JsonSerializer.Serialize(report), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gate_change_or_disappearance_marks_mixed_observations_without_retry(bool missing)
    {
        var state = Fixture();
        state.Inner.BeforeRead = _ =>
        {
            if (state.Keys.Count == 6)
            {
                if (missing) state.Inner.Remove(RepositoryGate.Key(Repository));
                else state.Inner.Seed(RepositoryGate.Key(Repository), RepositoryGate.Vacant(Repository, Epoch));
            }
            return Task.CompletedTask;
        };
        var report = await Observe(state);
        Assert.Equal("partial", report.State);
        Assert.Equal(missing ? "missing-on-reread" : "changed", report.GateObservation);
        Assert.Equal(6, report.ReadCount);
        Assert.False(report.SettlementProven);
    }

    [Fact]
    public async Task One_total_controlled_deadline_bounds_even_a_store_that_ignores_cancellation()
    {
        var state = Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.Inner.BeforeRead = _ => release.Task;
        var timers = new TimerClock(new FixedClock());
        var pending = new RetainedAuthorityObservation(state, Epoch, timers).ObserveAsync(Token);
        try
        {
            Assert.Equal(TimeSpan.FromSeconds(20), Assert.Single(timers.Due));
            timers.Fire();
            var report = await pending.WaitAsync(Token);
            Assert.Equal("unavailable", report.State);
            Assert.Equal(1, report.ReadCount);
            Assert.Single(state.Keys);
        }
        finally
        {
            release.TrySetResult();
            await pending.WaitAsync(Token);
        }
    }

    [Theory]
    [InlineData(null, "missing")]
    [InlineData("private/invalid", "invalid")]
    public async Task Missing_or_invalid_epochs_are_explicit_unknown_binding_not_current_epoch(string? epoch, string relation)
    {
        var state = Fixture();
        var gate = (await state.Inner.ReadAsync(RepositoryGate.Key(Repository), Token))!.Value;
        gate.Epoch = epoch;
        state.Inner.Seed(RepositoryGate.Key(Repository), gate);
        var report = await Observe(state);
        Assert.Equal(relation, report.Gate.EpochRelation);
        Assert.Equal(relation, report.Owner.EpochRelation);
        Assert.Equal("partial", report.State);
        Assert.DoesNotContain("private/invalid", JsonSerializer.Serialize(report), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("runtime", "repository")]
    [InlineData("runtime", "operation")]
    [InlineData("runtime", "enrollment")]
    [InlineData("runtime", "purpose")]
    [InlineData("verification", "repository")]
    [InlineData("verification", "operation")]
    [InlineData("verification", "enrollment")]
    [InlineData("verification", "purpose")]
    public async Task Every_attempt_binding_is_checked_before_its_fields_are_projected(string purpose, string changed)
    {
        var state = Fixture();
        var key = IssuanceAttempt.Key(Repository, purpose == "runtime" ? Runtime : Verifier);
        var attempt = (await state.Inner.ReadAsync(key, Token))!.Value;
        switch (changed)
        {
            case "repository": attempt.RepositoryId = "DO_NOT_EXPORT"; break;
            case "operation": attempt.OperationId = "DO_NOT_EXPORT"; break;
            case "enrollment": attempt.EnrollmentId = "DO_NOT_EXPORT"; break;
            default: attempt.Purpose = "DO_NOT_EXPORT"; break;
        }
        state.Inner.Seed(key, attempt);
        var report = await Observe(state);
        Assert.Equal("binding-mismatch", (purpose == "runtime" ? report.RuntimeAttempt : report.VerifierAttempt).Read);
        Assert.DoesNotContain("DO_NOT_EXPORT", JsonSerializer.Serialize(report), StringComparison.Ordinal);
        Assert.Equal(6, report.ReadCount);
    }

    [Fact]
    public async Task Verifier_without_owner_is_observed_with_explicitly_uncheckable_owner_association()
    {
        var state = Fixture();
        var gate = (await state.Inner.ReadAsync(RepositoryGate.Key(Repository), Token))!.Value;
        gate.OwnerEnrollmentId = null;
        state.Inner.Seed(RepositoryGate.Key(Repository), gate);
        var report = await Observe(state);
        Assert.Equal("not-referenced", report.Owner.Read);
        Assert.Equal("not-checkable", report.VerifierAttempt.Fields!["ownerBinding"]);
        Assert.False(report.VerifierAttempt.Complete);
        Assert.Equal("partial", report.State);
        Assert.Equal(3, report.ReadCount);
    }

    private static Task<RetainedAuthorityReport> Observe(ReadOnlyState state) =>
        new RetainedAuthorityObservation(state, Epoch, new FixedClock()).ObserveAsync(Token);

    private static ReadOnlyState Fixture()
    {
        var state = new ReadOnlyState();
        state.Inner.Seed(RepositoryGate.Key(Repository), new RepositoryGate
        {
            RepositoryId = Repository,
            Epoch = Epoch,
            State = "closed",
            OwnerEnrollmentId = Owner,
            VerifierAttempt = Verifier,
            Sequence = 4,
            OutstandingTokenCount = 1,
            PendingCount = 0
        });
        state.Inner.Seed(ControllerEnrollment.Key(Repository, Owner), new ControllerEnrollment
        {
            RepositoryId = Repository,
            EnrollmentId = Owner,
            Epoch = Epoch,
            State = "revoked",
            RuntimeAttempt = Runtime,
            Acknowledged = true,
            PayloadRedacted = false,
            OutstandingTokenCount = 1
        });
        state.Inner.Seed(RevocationReceipt.Key(Repository, Owner), new RevocationReceipt
        { RepositoryId = Repository, EnrollmentId = Owner, Epoch = Epoch });
        foreach (var (operation, purpose) in new[] { (Runtime, "runtime"), (Verifier, "verification") })
            state.Inner.Seed(IssuanceAttempt.Key(Repository, operation), new IssuanceAttempt
            {
                RepositoryId = Repository,
                EnrollmentId = Owner,
                Epoch = Epoch,
                OperationId = operation,
                Purpose = purpose,
                State = "known",
                RepositoryCharged = true,
                ScopeValid = true,
                CapacityReleased = false,
                PayloadRedacted = false,
                ExpiresAt = FixedClock.Now.AddMinutes(-1)
            });
        return state;
    }

    private sealed class FixedClock : TimeProvider
    {
        internal static readonly DateTimeOffset Now = new(2026, 10, 28, 21, 0, 0, TimeSpan.Zero);
        internal int Reads { get; private set; }
        public override DateTimeOffset GetUtcNow() { Reads++; return Now; }
    }

    private sealed class ReadOnlyState : IBrokerState
    {
        internal InMemoryBrokerState Inner { get; } = new();
        internal List<string> Keys { get; } = [];
        public Task<Versioned<T>?> ReadAsync<T>(StateKey<T> key, CancellationToken ct) where T : BrokerRecord, new()
        {
            Assert.Equal("repository:" + Repository, key.Scope);
            Keys.Add(key.Name);
            Assert.InRange(Keys.Count, 1, 6);
            return Inner.ReadAsync(key, ct);
        }
        public Task<IReadOnlyList<StateVersion?>> CommitAsync(StateTransaction transaction, CancellationToken ct) =>
            throw new InvalidOperationException("Observation attempted a write");
        public Task<StatePage<T>> ListAsync<T>(StateRange<T> range, string after, string? continuation, CancellationToken ct)
            where T : BrokerRecord, new() => throw new InvalidOperationException("Observation attempted a list");
    }
}
