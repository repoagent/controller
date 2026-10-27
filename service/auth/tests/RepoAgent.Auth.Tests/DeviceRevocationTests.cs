using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace RepoAgent.Auth.Tests;

// A controller's self-revocation, decided by the core over the in-memory broker state and a manual clock.
public sealed class DeviceRevocationTests
{
    private const string Epoch = "test-epoch", Repository = "123456", Partition = "repository:" + Repository, Id = "enrollment-1";
    private static readonly string Secret = new('s', 43);
    private static readonly StateKey<RepositoryGate> GateKey = RepositoryGate.Key(Repository);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly InMemoryBrokerState state = new();
    private readonly IssuanceClock clock = new();

    private static StateKey<EnrollmentLocator> LocatorKey(string id = Id) => EnrollmentLocator.Key(id);
    private static StateKey<ControllerEnrollment> EnrollmentKey(string id = Id) => ControllerEnrollment.Key(Repository, id);
    private static StateKey<RevocationReceipt> ReceiptKey(string id = Id) => RevocationReceipt.Key(Repository, id);
    private Task<int> RevokeAsync(string? secret, int runtimeVersion = 1, string id = Id) =>
        new DeviceRevocation(state, Epoch, clock).RevokeAsync(id, secret, Token, runtimeVersion);
    private Task<int> RevokeAsync() => RevokeAsync(Secret);

    // The rows a controller enrollment from before approval contracts has, with one outstanding GitHub token and its
    // issuance attempt, which revocation must never touch.
    private void Enroll(string id = Id, string? secret = null, Action<ControllerEnrollment>? shape = null)
    {
        state.Seed(LocatorKey(id), new EnrollmentLocator { RepositoryId = Repository, EnrollmentId = id, PendingId = Repository + "_pending" });
        state.Seed(GateKey, new RepositoryGate
        {
            RepositoryId = Repository,
            Epoch = Epoch,
            OwnerEnrollmentId = id,
            State = "active",
            Sequence = 7,
            OutstandingTokenCount = 1
        });
        var enrollment = new ControllerEnrollment
        {
            RepositoryId = Repository,
            EnrollmentId = id,
            Epoch = Epoch,
            ControllerSecretHash = Hash(secret ?? Secret),
            ExpiresAt = clock.GetUtcNow().AddHours(1),
            State = "active",
            OutstandingTokenCount = 1
        };
        shape?.Invoke(enrollment);
        state.Seed(EnrollmentKey(id), enrollment);
        state.Put(Partition, "attempt:one", new Dictionary<string, object> { ["DeviceId"] = id, ["State"] = "outstanding" });
    }

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));

    private static void Approve(ControllerEnrollment enrollment, ApprovalContract contract, string profile)
    {
        (enrollment.ApprovalKind, enrollment.SetupVersion, enrollment.PolicyVersion, enrollment.Profile) = (contract.Kind, contract.SetupVersion, contract.Policy, profile);
    }

    private void AssertUncommitted((StateVersion? Gate, StateVersion? Enrollment, StateVersion? Attempt) before)
    {
        Assert.Equal(before, Versions());
        Assert.Null(state.Row(ReceiptKey()));
    }

    private (StateVersion? Gate, StateVersion? Enrollment, StateVersion? Attempt) Versions() =>
        (state.Version(GateKey), state.Version(EnrollmentKey()), state.Version(Partition, "attempt:one"));

    [Theory]
    [InlineData("active")]
    [InlineData("unacknowledged")]
    public async Task Correct_secret_closes_bound_grant_and_preserves_outstanding_authority(string lifecycle)
    {
        Enroll(shape: enrollment => enrollment.State = lifecycle);
        state.Change(GateKey, row => row["State"] = lifecycle);
        var attempt = state.Version(Partition, "attempt:one");
        // Revocation adds only FirstTerminalAt and removes nothing.
        var gateFields = state.Row(GateKey)!.Keys.Order().ToList();
        var enrollmentFields = state.Row(EnrollmentKey())!.Keys.Append("FirstTerminalAt").Order().ToList();

        Assert.Equal(204, await RevokeAsync());

        Assert.Equal(gateFields, state.Row(GateKey)!.Keys.Order());
        Assert.Equal(enrollmentFields, state.Row(EnrollmentKey())!.Keys.Order());
        var (gate, enrollment, receipt) = (state.Get(GateKey)!, state.Get(EnrollmentKey())!, state.Get(ReceiptKey())!);
        Assert.Equal("closed", gate.State);
        Assert.Equal(8L, gate.Sequence);
        Assert.Equal(1L, gate.OutstandingTokenCount);
        Assert.Equal("revoked", enrollment.State);
        Assert.Equal(1L, enrollment.OutstandingTokenCount);
        Assert.Equal(clock.GetUtcNow(), enrollment.FirstTerminalAt);
        Assert.Equal((Repository, Id, Epoch), (receipt.RepositoryId, receipt.EnrollmentId, receipt.Epoch));
        Assert.Equal(clock.GetUtcNow(), receipt.RevokedAt);
        Assert.Equal(attempt, state.Version(Partition, "attempt:one"));
        Assert.Equal(1, state.Commits);
    }

    [Fact]
    public async Task Every_stored_field_of_the_revoked_records_survives_the_revocation()
    {
        Enroll(shape: enrollment =>
        {
            Approve(enrollment, ApprovalContract.Legacy, "polling");
            (enrollment.Repository, enrollment.Variable, enrollment.Challenge, enrollment.Marker) = ("owner/project", "APPROVAL", new string('c', 43), "{}");
            (enrollment.Environment, enrollment.EnvironmentId, enrollment.InstallationId) = ("approval/environment", 987, 789);
            (enrollment.AppId, enrollment.AppSlug, enrollment.GitHubHost) = (IssuancePolicy.AppId, IssuancePolicy.AppSlug, "github.com");
            (enrollment.PendingId, enrollment.CompletionId, enrollment.Acknowledged, enrollment.PayloadRedacted) = ("123456_p", "c", true, false);
            (enrollment.LastActivityAt, enrollment.FirstTerminalAt) = (clock.GetUtcNow().AddDays(-1), clock.GetUtcNow().AddDays(-1));
            (enrollment.RuntimeAttempt, enrollment.RuntimeExpiresAt, enrollment.RuntimeObservation) = ("r", clock.GetUtcNow().AddMinutes(30), "o");
            enrollment.RuntimeObservationReservedAt = enrollment.RuntimeObservationJwtIssuedAt = enrollment.RuntimeObservationJwtExpiresAt = clock.GetUtcNow();
        });
        state.Change(GateKey, row => (row["PendingCount"], row["VerifierAttempt"]) = (1L, "v"));
        var (gateBefore, enrollmentBefore) = (Sorted(state.Row(GateKey)!), Sorted(state.Row(EnrollmentKey())!));

        Assert.Equal(204, await RevokeAsync());

        var (gateAfter, enrollmentAfter) = (Sorted(state.Row(GateKey)!), Sorted(state.Row(EnrollmentKey())!));
        Assert.Equal((8, 33), (gateBefore.Count, enrollmentBefore.Count));
        (gateBefore["State"], gateBefore["Sequence"], enrollmentBefore["State"]) = ("closed", 8L, "revoked");
        Assert.Equal(gateBefore, gateAfter);
        Assert.Equal(enrollmentBefore, enrollmentAfter);
    }

    private static SortedDictionary<string, object> Sorted(Dictionary<string, object> row) => new(row, StringComparer.Ordinal);

    [Fact]
    public async Task Concurrent_and_later_revocations_commit_once_and_reuse_the_receipt()
    {
        var secret = new string('a', 128);
        Enroll("x", secret);
        var attempt = state.Version(Partition, "attempt:one");

        var statuses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => RevokeAsync(secret, id: "x"))));
        var receipt = state.Version(ReceiptKey("x"));
        var retried = await RevokeAsync(secret, id: "x");

        Assert.All(statuses, status => Assert.Equal(204, status));
        Assert.Equal(204, retried);
        Assert.NotNull(receipt);
        Assert.Equal(receipt, state.Version(ReceiptKey("x")));
        var (gate, enrollment) = (state.Get(GateKey)!, state.Get(EnrollmentKey("x"))!);
        Assert.Equal(("closed", 8L, 1L), (gate.State!, gate.Sequence!.Value, gate.OutstandingTokenCount!.Value));
        Assert.Equal(("revoked", 1L), (enrollment.State!, enrollment.OutstandingTokenCount!.Value));
        Assert.Equal(attempt, state.Version(Partition, "attempt:one"));
    }

    [Fact]
    public async Task Revocation_committed_between_authority_reads_is_reconciled_without_another_write()
    {
        Enroll();
        var competing = true;
        state.BeforeRead = async key =>
        {
            if (!competing || key.Name != "device:" + Id) return;
            competing = false;
            Assert.Equal(204, await RevokeAsync());
        };

        Assert.Equal(204, await RevokeAsync());

        Assert.False(competing);
        Assert.Equal(1, state.Commits);
        Assert.Equal(("closed", 8L), (state.Get(GateKey)!.State!, state.Get(GateKey)!.Sequence!.Value));
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("missing-secret")]
    [InlineData("short-secret")]
    [InlineData("invalid-enrollment-id")]
    [InlineData("device-binding")]
    [InlineData("enrollment-binding")]
    [InlineData("gate-binding")]
    [InlineData("gate-owner")]
    [InlineData("device-epoch")]
    [InlineData("gate-epoch")]
    [InlineData("expired")]
    [InlineData("expires-during-revocation")]
    [InlineData("expired-with-unknown-contract")]
    [InlineData("locator-binding")]
    [InlineData("orphan-locator")]
    [InlineData("missing-locator")]
    [InlineData("missing-device")]
    [InlineData("missing-gate")]
    public async Task Invalid_authority_is_denied_without_receipt(string scenario)
    {
        Enroll();
        switch (scenario)
        {
            case "device-binding": state.Change(EnrollmentKey(), row => row["RepositoryId"] = "987"); break;
            case "enrollment-binding": state.Change(EnrollmentKey(), row => row["DeviceId"] = "other"); break;
            case "gate-binding": state.Change(GateKey, row => row["RepositoryId"] = "987"); break;
            case "gate-owner": state.Change(GateKey, row => row["OwnerDeviceId"] = "other"); break;
            case "device-epoch": state.Change(EnrollmentKey(), row => row["Epoch"] = "old"); break;
            case "gate-epoch": state.Change(GateKey, row => row["Epoch"] = "old"); break;
            case "expired": state.Change(EnrollmentKey(), row => row["ExpiresAt"] = clock.GetUtcNow().AddHours(-1)); break;
            case "expired-with-unknown-contract":
                state.Change(EnrollmentKey(), row => (row["ExpiresAt"], row["ApprovalKind"]) = (clock.GetUtcNow().AddHours(-1), "unknown"));
                break;
            case "locator-binding": state.Change(LocatorKey(), row => row["DeviceId"] = "999"); break;
            case "orphan-locator": state.Change(LocatorKey(), row => row["RepositoryId"] = "999"); break;
            case "missing-locator": state.Remove(LocatorKey()); break;
            case "missing-device": state.Remove(EnrollmentKey()); break;
            case "missing-gate": state.Remove(GateKey); break;
            case "expires-during-revocation":
                state.BeforeRead = key =>
                {
                    if (key.Name.StartsWith("revocation:", StringComparison.Ordinal)) clock.Advance(TimeSpan.FromHours(2));
                    return Task.CompletedTask;
                };
                break;
        }
        var before = Versions();
        var secret = scenario switch { "wrong-secret" => new string('Z', 43), "missing-secret" => null, "short-secret" => "short", _ => Secret };

        Assert.Equal(401, await RevokeAsync(secret, id: scenario == "invalid-enrollment-id" ? "not an id" : Id));

        Assert.Equal(0, state.Commits);
        AssertUncommitted(before);
        if (scenario is "missing-secret" or "short-secret" or "invalid-enrollment-id") Assert.Equal(0, state.Reads);
    }

    [Theory]
    [InlineData("historical", 1, 204)]
    [InlineData("historical", 2, 401)]
    [InlineData("historical", 3, 401)]
    [InlineData("policy-only", 1, 204)]
    [InlineData("environment", 1, 204)]
    [InlineData("environment", 2, 401)]
    [InlineData("agent-clone", 2, 204)]
    [InlineData("agent-clone", 1, 401)]
    [InlineData("actions-settings", 3, 204)]
    [InlineData("actions-settings", 1, 401)]
    [InlineData("settings-with-frozen-profile", 3, 401)]
    [InlineData("unknown-kind", 1, 503)]
    [InlineData("agent-with-environment", 2, 503)]
    [InlineData("kind-without-profile", 3, 503)]
    [InlineData("kind-without-profile", 1, 401)]
    public async Task Approval_contract_selects_the_runtime_version_that_may_revoke(string contract, int runtimeVersion, int status)
    {
        Enroll(shape: enrollment =>
        {
            switch (contract)
            {
                case "policy-only": (enrollment.PolicyVersion, enrollment.Profile) = (IssuancePolicy.Version, "polling"); break;
                case "environment":
                    Approve(enrollment, ApprovalContract.Legacy, "polling");
                    (enrollment.Environment, enrollment.EnvironmentId) = ("approval/environment", 987);
                    break;
                case "agent-clone": Approve(enrollment, ApprovalContract.AgentClone, "polling-clone"); break;
                case "actions-settings": Approve(enrollment, ApprovalContract.ActionsSettings, "polling-settings"); break;
                case "settings-with-frozen-profile": Approve(enrollment, ApprovalContract.ActionsSettings, "polling"); break;
                case "unknown-kind": Approve(enrollment, ApprovalContract.Legacy with { Kind = "unknown" }, "polling"); break;
                case "agent-with-environment":
                    Approve(enrollment, ApprovalContract.AgentClone, "polling-clone");
                    enrollment.Environment = "approval/environment";
                    break;
                case "kind-without-profile": Approve(enrollment, ApprovalContract.ActionsSettings, "polling-settings"); enrollment.Profile = null; break;
            }
        });
        var before = Versions();

        Assert.Equal(status, await RevokeAsync(Secret, runtimeVersion));

        if (status == 204) Assert.Equal("revoked", state.Get(EnrollmentKey())!.State);
        else AssertUncommitted(before);
    }

    [Theory]
    [InlineData("string-sequence")]
    [InlineData("int32-sequence")]
    [InlineData("int32-pending-count")]
    [InlineData("nonfinite-sequence")]
    [InlineData("negative-sequence")]
    [InlineData("secret-hash")]
    [InlineData("missing-field")]
    [InlineData("string-time")]
    [InlineData("inconsistent-state")]
    [InlineData("unknown-enrollment-state")]
    [InlineData("vacant-gate")]
    [InlineData("unknown-enrollment-property")]
    [InlineData("unknown-gate-property")]
    [InlineData("unknown-locator-property")]
    [InlineData("noncanonical-locator")]
    public async Task Corrupt_authority_returns_unavailable_without_commit(string scenario)
    {
        Enroll();
        switch (scenario)
        {
            case "string-sequence": state.Change(GateKey, row => row["Sequence"] = "7"); break;
            case "int32-sequence": state.Change(GateKey, row => row["Sequence"] = 7); break;
            case "int32-pending-count": state.Change(GateKey, row => row["PendingCount"] = 1); break;
            case "nonfinite-sequence": state.Change(GateKey, row => row["Sequence"] = double.NaN); break;
            case "negative-sequence": state.Change(GateKey, row => row["Sequence"] = -1L); break;
            case "secret-hash": state.Change(EnrollmentKey(), row => row["SecretHash"] = "invalid"); break;
            case "missing-field": state.Change(EnrollmentKey(), row => row.Remove("ExpiresAt")); break;
            case "string-time": state.Change(EnrollmentKey(), row => row["ExpiresAt"] = clock.GetUtcNow().AddHours(1).ToString("O")); break;
            case "inconsistent-state": state.Change(GateKey, row => row["State"] = "unknown"); break;
            case "unknown-enrollment-state": state.Change(EnrollmentKey(), row => row["State"] = "unknown"); break;
            case "vacant-gate": state.Change(GateKey, row => row.Remove("OwnerDeviceId")); break;
            case "unknown-enrollment-property": state.Change(EnrollmentKey(), row => row["Unrecognized"] = "x"); break;
            case "unknown-gate-property": state.Change(GateKey, row => row["ConcurrentWriter"] = "x"); break;
            case "unknown-locator-property": state.Change(LocatorKey(), row => row["Unrecognized"] = "x"); break;
            case "noncanonical-locator": state.Change(LocatorKey(), row => row["RepositoryId"] = "0123456"); break;
        }
        var before = Versions();

        Assert.Equal(503, await RevokeAsync());

        Assert.Equal(0, state.Commits);
        AssertUncommitted(before);
        Assert.InRange(state.Reads, 1, 4); // Corrupt records fail at once and never spend the retry budget.
    }

    [Fact]
    public async Task Expired_revoked_enrollment_cannot_replay_its_receipt()
    {
        Enroll();
        Assert.Equal(204, await RevokeAsync());
        state.Change(EnrollmentKey(), row => row["ExpiresAt"] = clock.GetUtcNow().AddHours(-1));

        Assert.Equal(401, await RevokeAsync());

        Assert.Equal("closed", state.Get(GateKey)!.State);
    }

    [Theory]
    [InlineData("enrollment")]
    [InlineData("gate")]
    public async Task Conflict_rechecks_authority_and_rolls_back_every_revocation_write(string changedRecord)
    {
        Enroll();
        var changed = false;
        state.BeforeCommit = _ =>
        {
            if (changed) return Task.CompletedTask;
            changed = true;
            if (changedRecord == "enrollment") state.Change(EnrollmentKey(), row => row["ExpiresAt"] = clock.GetUtcNow().AddHours(-1));
            else state.Change(GateKey, row => row["OwnerDeviceId"] = "replacement");
            return Task.CompletedTask;
        };

        Assert.Equal(401, await RevokeAsync());

        Assert.True(changed);
        Assert.Equal(1, state.Commits);
        Assert.Equal(("active", 7L), (state.Get(GateKey)!.State!, state.Get(GateKey)!.Sequence!.Value));
        Assert.Equal("active", state.Get(EnrollmentKey())!.State);
        Assert.Null(state.Row(ReceiptKey()));
    }

    [Fact]
    public async Task Lost_success_response_is_reconciled_by_authenticated_retry_and_never_written_again()
    {
        Enroll();
        var lost = false;
        state.AfterCommit = _ =>
        {
            if (lost) return Task.CompletedTask;
            lost = true;
            throw new BrokerStateUnavailable("Lost response after the commit.");
        };
        var attempt = state.Version(Partition, "attempt:one");

        Assert.Equal(503, await RevokeAsync());
        var receipt = state.Version(ReceiptKey());
        Assert.Equal(204, await RevokeAsync());

        Assert.True(lost);
        Assert.NotNull(receipt);
        Assert.Equal(receipt, state.Version(ReceiptKey()));
        Assert.Equal(1, state.Commits);
        Assert.Equal(8L, state.Get(GateKey)!.Sequence);
        Assert.Equal(attempt, state.Version(Partition, "attempt:one"));
    }

    [Fact]
    public async Task Persistent_conflicts_exhaust_bounded_work_without_partial_commit()
    {
        Enroll();
        // Another writer touches the gate before every commit: the same row, a new version.
        state.BeforeCommit = _ =>
        {
            state.Change(GateKey, _ => { });
            return Task.CompletedTask;
        };

        Assert.Equal(503, await RevokeAsync());

        Assert.Equal(4, state.Commits);
        Assert.Equal(("active", 7L), (state.Get(GateKey)!.State!, state.Get(GateKey)!.Sequence!.Value));
        Assert.Equal("active", state.Get(EnrollmentKey())!.State);
        Assert.Null(state.Row(ReceiptKey()));
    }

    [Theory]
    [InlineData("locator")]
    [InlineData("gate")]
    [InlineData("device:")]
    [InlineData("revocation:")]
    [InlineData("commit")]
    public async Task Unavailable_state_fails_closed_without_commit(string failing)
    {
        Enroll();
        var before = Versions();
        if (failing == "commit") state.BeforeCommit = _ => throw new BrokerStateUnavailable("Storage unavailable.");
        else state.BeforeRead = key => key.Name.StartsWith(failing, StringComparison.Ordinal)
            ? throw new BrokerStateUnavailable("Storage unavailable.") : Task.CompletedTask;

        Assert.Equal(503, await RevokeAsync());

        AssertUncommitted(before);
    }

    [Fact]
    public async Task Closed_grant_without_its_receipt_is_unresolved()
    {
        Enroll();
        Assert.Equal(204, await RevokeAsync());
        state.Remove(ReceiptKey());

        Assert.Equal(503, await RevokeAsync());

        Assert.Equal(1, state.Commits);
        Assert.Equal("closed", state.Get(GateKey)!.State);
        Assert.Null(state.Row(ReceiptKey()));
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("enrollment")]
    [InlineData("epoch")]
    [InlineData("revoked-at")]
    public async Task Receipt_that_does_not_bind_the_closed_grant_is_unavailable(string field)
    {
        Enroll();
        Assert.Equal(204, await RevokeAsync());
        state.Change(ReceiptKey(), row =>
        {
            if (field == "revoked-at") row.Remove("RevokedAt");
            else row[field switch { "repository" => "RepositoryId", "enrollment" => "DeviceId", _ => "Epoch" }] = "other";
        });

        Assert.Equal(503, await RevokeAsync());

        Assert.Equal(1, state.Commits);
    }

    [Theory]
    [InlineData("split-states")]
    [InlineData("unacknowledged-gate")]
    [InlineData("receipt-while-active")]
    public async Task Gate_and_enrollment_that_disagree_never_commit(string scenario)
    {
        Enroll();
        if (scenario == "split-states") state.Change(EnrollmentKey(), row => row["State"] = "revoked");
        if (scenario == "unacknowledged-gate") state.Change(GateKey, row => row["State"] = "unacknowledged");
        if (scenario == "receipt-while-active") state.Seed(ReceiptKey(), new RevocationReceipt { RepositoryId = Repository, EnrollmentId = Id, Epoch = Epoch, RevokedAt = clock.GetUtcNow() });
        var gate = state.Version(GateKey);

        Assert.Equal(503, await RevokeAsync());

        Assert.Equal(0, state.Commits);
        Assert.Equal(gate, state.Version(GateKey));
    }

    [Fact]
    public async Task First_terminal_clock_already_recorded_is_kept()
    {
        var first = clock.GetUtcNow().AddDays(-1);
        Enroll(shape: enrollment => enrollment.FirstTerminalAt = first);

        Assert.Equal(204, await RevokeAsync());

        Assert.Equal(first, state.Get(EnrollmentKey())!.FirstTerminalAt);
        Assert.Equal(clock.GetUtcNow(), state.Get(ReceiptKey())!.RevokedAt);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_other_cancellation_is_unavailable()
    {
        Enroll();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DeviceRevocation(state, Epoch, clock).RevokeAsync(Id, Secret, cancelled.Token));
        state.BeforeRead = _ => throw new OperationCanceledException();
        Assert.Equal(503, await RevokeAsync());

        Assert.Equal(0, state.Commits);
    }
}
