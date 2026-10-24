using Xunit;

namespace RepoAgent.Auth.Tests;

// Issuance under competing writers, lost commit responses and exhausted capacity. A broker state commit is one Table
// transaction: BeforeCommit hooks run before it is applied and AfterCommit hooks after, so a hook that throws
// BrokerStateUnavailable loses the transaction before it is sent or loses the response of one that committed.
public sealed class IssuanceFaultTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    // Six-minute runtime leases enter the five-minute renewal margin at the next test minute.
    // These cases exercise new mints; published-lease reuse is covered separately.

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Competing_global_reservation_retries_before_observation_without_duplicate_charges(bool existingQuota)
    {
        var f = IssuanceFixture.Create(runtimeLifetime: TimeSpan.FromMinutes(6));
        // Keep the two-second contention delay inside one fixed UTC quota window.
        var midMinute = DateTimeOffset.FromUnixTimeSeconds(f.Clock.GetUtcNow().ToUnixTimeSeconds() / 60 * 60 + 90);
        f.Clock.Advance(midMinute - f.Clock.GetUtcNow());
        var competingGrant = await f.EnrollCompetitorAsync("654321", "owner/competing", Token);
        if (existingQuota)
        {
            Assert.Equal(200, (await f.IssueAsync(Token)).Status);
            f.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        var reservedAt = f.Clock.GetUtcNow();
        var priorAttempts = f.Attempts().Select(a => a.OperationId).ToHashSet();
        StoredAttempt? competingAttempt = null;
        var injected = false;
        f.State.BeforeCommit = async _ =>
        {
            if (injected) return;
            injected = true;
            // A real admission in another repository wins the shared global quota/slot.
            using var work = new IssuanceWork(f.Clock);
            competingAttempt = (await f.Store().ReserveAsync(competingGrant, "verification", work)).Attempt;
            f.Clock.Advance(TimeSpan.FromSeconds(2));
        };
        var result = await f.IssueAsync(Token);
        Assert.True(injected);
        Assert.Equal(1, f.State.Conflicts); // The global charge read the quota before the competitor; nothing was written.
        Assert.Equal("reserved", competingAttempt!.Value.State);
        Assert.Equal(200, result.Status);
        var slots = existingQuota ? 5 : 4;
        Assert.Equal(slots, f.GlobalQuota.Slots);
        Assert.Equal(existingQuota ? 2 : 3, f.GlobalQuota.Admissions);
        var slotRows = f.GlobalRows().Slots;
        Assert.Equal(slots, slotRows.Count);
        Assert.Single(slotRows, s => s.OperationId == competingAttempt.Value.OperationId);
        var admitted = f.Attempts().Where(a => !priorAttempts.Contains(a.OperationId)).ToList();
        Assert.Equal(existingQuota ? 1 : 2, admitted.Count);
        foreach (var attempt in admitted) Assert.Single(slotRows, s => s.OperationId == attempt.OperationId);
        var retried = Assert.Single(admitted, a => a.Purpose == (existingQuota ? "runtime" : "verification"));
        Assert.Equal(reservedAt, retried.ReservedAt);
        Assert.Equal(reservedAt.AddSeconds(-60), retried.JwtIssuedAt);
        Assert.Equal(reservedAt.AddSeconds(120), retried.JwtExpiresAt);
        Assert.Equal(existingQuota ? 3 : 2, f.GitHub.Authorizations);
        Assert.Equal(existingQuota ? 3 : 2, f.GitHub.Mints);
        Assert.Equal(existingQuota ? 16 : 8, f.GitHub.Reads);
        Assert.Equal(1, f.GitHub.VerifierMints);
    }

    [Theory]
    [InlineData("gate")]
    [InlineData("device")]
    public async Task Transient_admission_conflict_rereads_authority_before_first_approval_observation(string changed)
    {
        var f = IssuanceFixture.Create(runtimeLifetime: TimeSpan.FromMinutes(6));
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var reservedAt = f.Clock.GetUtcNow();
        var batches = 0;
        f.State.BeforeCommit = _ =>
        {
            // Conflict the repository admission before any approval read or provider mint.
            if (++batches == 2)
            {
                f.State.Change(changed == "gate" ? IssuanceFixture.GateKey : f.EnrollmentKey, _ => { });
                f.Clock.Advance(TimeSpan.FromSeconds(2));
            }
            return Task.CompletedTask;
        };
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        Assert.Equal(1, f.State.Conflicts);
        Assert.Equal(16, f.GitHub.Reads);
        Assert.Equal(3, f.GitHub.Mints);
        Assert.Equal(2, f.Attempts().Count(a => a.State == "released"));
        Assert.Equal(4, f.GlobalQuota.Slots);
        var slot = Assert.Single(f.GlobalRows().Slots, s => s.GlobalSlot == 3);
        var attempt = Assert.Single(f.Attempts(), a => a.OperationId == slot.OperationId);
        Assert.Equal(reservedAt, attempt.ReservedAt);
        Assert.Equal(reservedAt.AddSeconds(-60), attempt.JwtIssuedAt);
        Assert.Equal(reservedAt.AddSeconds(120), attempt.JwtExpiresAt);
        Assert.Equal(1, f.Quota.Admissions);
        Assert.Equal(8, f.Quota.Reads);
        Assert.Equal(1, f.Quota.Signs);
        Assert.Equal(1, f.Quota.Mints);
    }

    [Fact]
    public async Task Admission_conflict_budget_is_shared_by_verifier_and_runtime_without_duplicate_global_charges()
    {
        var f = IssuanceFixture.Create(runtimeLifetime: TimeSpan.FromMinutes(6));
        var reservedAt = f.Clock.GetUtcNow();
        var batches = 0;
        var conflicts = 0;
        f.State.BeforeCommit = _ =>
        {
            // Two verifier-admission conflicts precede its successful third evaluation.
            // Runtime's first admission consumes the fourth shared evaluation and conflicts too.
            if (++batches is 2 or 3 or 8)
            {
                ++conflicts;
                f.State.Change(IssuanceFixture.GateKey, _ => { });
                f.Clock.Advance(TimeSpan.FromSeconds(1));
            }
            return Task.CompletedTask;
        };
        Assert.Equal(503, (await f.IssueAsync(Token)).Status);
        Assert.Equal(3, conflicts);
        Assert.Equal(3, f.State.Conflicts);
        Assert.Equal(8, batches);
        Assert.Equal(1, f.GitHub.Authorizations);
        Assert.Equal(1, f.GitHub.Mints);
        Assert.Equal(0, f.GitHub.Reads);
        var verifier = Assert.Single(f.Attempts());
        Assert.Equal("verification", verifier.Purpose);
        Assert.Equal("known", verifier.State);
        Assert.Equal(reservedAt, verifier.ReservedAt);
        Assert.Equal(reservedAt.AddSeconds(120), verifier.JwtExpiresAt);
        Assert.Equal(3, f.GlobalQuota.Slots);
        Assert.Equal(2, f.Gate.OutstandingTokenCount);
        Assert.Null(f.Enrollment.LastActivityAt);
        Assert.Equal(1, f.Quota.Admissions);
    }

    [Theory]
    [InlineData("revoked", false)]
    [InlineData("secret", false)]
    [InlineData("revoked", true)]
    [InlineData("secret", true)]
    public async Task Changed_authority_during_admission_retry_cannot_inherit_the_original_authentication(string changed, bool reuse)
    {
        var f = IssuanceFixture.Create(runtimeLifetime: TimeSpan.FromMinutes(reuse ? 60 : 6));
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        var lastActivity = f.Enrollment.LastActivityAt;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var batches = 0;
        var changedAuthority = false;
        f.State.BeforeCommit = async _ =>
        {
            if (changedAuthority || ++batches != 2) return;
            changedAuthority = true;
            if (changed == "revoked")
                Assert.Equal(204, await f.Revocation().RevokeAsync(f.Device.DeviceId, f.Device.Secret, Token));
            else
                f.State.Change(f.EnrollmentKey, row => row["SecretHash"] = new string('0', 64));
        };
        Assert.NotEqual(200, (await f.IssueAsync(Token)).Status);
        Assert.True(changedAuthority);
        Assert.Equal(8, f.GitHub.Reads);
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal(2, f.GitHub.Authorizations);
        Assert.Equal(2, f.Attempts().Count);
        Assert.Equal(reuse ? 3 : 4, f.GlobalQuota.Slots);
        Assert.Equal(lastActivity, f.Enrollment.LastActivityAt);
    }

    [Theory]
    [InlineData(1, false, null)]
    [InlineData(1, true, null)]
    [InlineData(2, false, null)]
    [InlineData(2, true, "reserved")]
    [InlineData(3, false, "reserved")]
    [InlineData(3, true, "sending")]
    [InlineData(4, false, "settled")]
    [InlineData(4, true, "settled")]
    [InlineData(5, false, "settled")]
    [InlineData(5, true, "released")]
    public async Task Lost_Table_transport_never_refunds_reservations_or_replays_an_attempt(int stage, bool afterCommit, string? expectedState)
    {
        var f = IssuanceFixture.Create(runtimeLifetime: TimeSpan.FromMinutes(6));
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var beforeIds = f.Attempts().Select(a => a.OperationId).ToHashSet();
        var batches = 0;
        var injected = false;
        Task Lose(StateTransaction _)
        {
            if (++batches == stage)
            {
                injected = true;
                throw new BrokerStateUnavailable("One lost Table transport outcome");
            }
            return Task.CompletedTask;
        }
        if (afterCommit) f.State.AfterCommit = Lose;
        else f.State.BeforeCommit = Lose;
        Assert.Equal(503, (await f.IssueAsync(Token)).Status);
        Assert.True(injected);
        var added = f.Attempts().Where(a => !beforeIds.Contains(a.OperationId)).ToList();
        StateKey<IssuanceAttempt>? preservedKey = null;
        StateVersion? preservedVersion = null;
        if (expectedState is null) Assert.Empty(added);
        else
        {
            var preserved = Assert.Single(added);
            Assert.Equal(expectedState, preserved.State);
            Assert.Equal("runtime", preserved.Purpose);
            preservedKey = IssuanceAttempt.Key(IssuanceFixture.RepositoryId, preserved.OperationId!);
            preservedVersion = f.State.Version(preservedKey);
        }
        var slots = stage == 1 && !afterCommit ? 3 : 4;
        Assert.Equal(slots, f.GlobalQuota.Slots);
        Assert.Equal(slots, f.GlobalRows().Slots.Count);
        Assert.Equal(stage >= 4 ? 3 : 2, f.GitHub.Mints);
        Assert.Equal(stage >= 4 && !(stage == 5 && afterCommit) ? 1 : 0, f.GitHub.Revokes);
        Assert.Equal(stage == 5 && afterCommit ? f.Clock.GetUtcNow() : f.Clock.GetUtcNow().AddMinutes(-1), f.Enrollment.LastActivityAt);

        // A new issuer reloads encrypted credentials and durable state; it owns a new operation.
        f.State.BeforeCommit = f.State.AfterCommit = null;
        var jwtExpiresAt = preservedKey is null ? null : f.State.Get(preservedKey)!.JwtExpiresAt;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        if (preservedKey is not null)
        {
            Assert.Equal(preservedVersion, f.State.Version(preservedKey));
            Assert.Equal(jwtExpiresAt, f.State.Get(preservedKey)!.JwtExpiresAt);
        }
        Assert.Equal(stage >= 4 ? 4 : 3, f.GitHub.Mints);
        Assert.Equal(1, f.GitHub.VerifierMints);
        Assert.Equal(slots + 1, f.GlobalQuota.Slots);
    }

    // A Replace writes the whole row, so issuance must never act on, or rewrite, a row with a property no record maps.
    // Rows read before the first commit leave nothing written; the repository quota is read after the global charge,
    // which is never compensated.
    [Theory]
    [InlineData("locator", false, 0)]
    [InlineData("gate", false, 0)]
    [InlineData("enrollment", false, 0)]
    [InlineData("enrollment-quota", false, 0)]
    [InlineData("issuance-quota", false, 0)]
    [InlineData("candidate", false, 0)]
    [InlineData("occupied-slot", false, 0)]
    [InlineData("verifier-attempt", true, 0)]
    [InlineData("runtime-attempt", true, 0)]
    [InlineData("ledger", true, 0)]
    [InlineData("repository-quota", true, 1)]
    public async Task Row_with_an_unknown_property_answers_503_before_GitHub_work(string row, bool issued, int commits)
    {
        var f = IssuanceFixture.Create();
        if (issued) Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var runtime = issued ? f.Attempt("runtime") : null;
        StateKey key = row switch
        {
            "locator" => EnrollmentLocator.Key(f.Device.DeviceId),
            "gate" => IssuanceFixture.GateKey,
            "enrollment" => f.EnrollmentKey,
            "enrollment-quota" => EnrollmentQuota.Key,
            "issuance-quota" => IssuanceQuota.Key,
            "candidate" => CandidateRepository.Key(IssuanceFixture.RepositoryId),
            "occupied-slot" => IssuanceSlot.Key(0),
            "verifier-attempt" => IssuanceAttempt.Key(IssuanceFixture.RepositoryId, f.Gate.VerifierAttempt!),
            "runtime-attempt" => IssuanceAttempt.Key(IssuanceFixture.RepositoryId, runtime!.OperationId!),
            "ledger" => IssuanceLedger.Key(runtime!.OperationId!),
            _ => IssuanceFixture.QuotaKey,
        };
        f.State.Change(key, stored => stored["Unknown"] = "kept by no record");
        var (before, authorizations, mints) = (f.State.Commits, f.GitHub.Authorizations, f.GitHub.Mints);

        Assert.Equal(503, (await f.IssueAsync(Token)).Status);

        Assert.Equal(commits, f.State.Commits - before);
        Assert.Equal(authorizations, f.GitHub.Authorizations);
        Assert.Equal(mints, f.GitHub.Mints);
        Assert.Equal("kept by no record", f.State.Row(key)!["Unknown"]);
    }

    [Fact]
    public async Task Concurrent_verifier_acquisition_never_duplicates_the_inflight_mint()
    {
        var f = IssuanceFixture.Create(runtimeLifetime: TimeSpan.FromMinutes(6));
        var signingEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueSigning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.GitHub.BeforeAuthorize = async () =>
        {
            if (f.GitHub.Authorizations != 1) return;
            signingEntered.SetResult();
            await continueSigning.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        };
        var first = f.IssueAsync(Token);
        try
        {
            await signingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(503, (await f.IssueAsync(Token)).Status);
            Assert.Equal("reserved", Assert.Single(f.Attempts()).State);
            Assert.Equal(0, f.GitHub.Mints);
            Assert.Equal(2, f.GlobalQuota.Slots);
        }
        finally { continueSigning.TrySetResult(); }
        Assert.Equal(200, (await first).Status);
        Assert.Equal(1, f.GitHub.VerifierMints);
        Assert.Equal(2, f.GitHub.Mints);
    }

    [Fact]
    public async Task Revocation_after_admission_stops_a_late_worker_before_mint()
    {
        var f = IssuanceFixture.Create(runtimeLifetime: TimeSpan.FromMinutes(6));
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var revoked = false;
        f.GitHub.BeforeRead = async request =>
        {
            if (f.GitHub.Reads == 12 && request.StartsWith("variable ", StringComparison.Ordinal))
            {
                Assert.Equal(204, await f.Revocation().RevokeAsync(f.Device.DeviceId, f.Device.Secret, Token));
                revoked = true;
            }
        };
        Assert.NotEqual(200, (await f.IssueAsync(Token)).Status);
        Assert.True(revoked);
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Single(f.Attempts(), a => a.State == "reserved");
        Assert.Equal("closed", f.Gate.State);
        Assert.Equal(f.Clock.GetUtcNow().AddMinutes(-1), f.Enrollment.LastActivityAt);
        Assert.Equal(4, f.GlobalQuota.Slots);
        Assert.Equal(401, (await f.IssueAsync(Token)).Status);
        Assert.Equal(2, f.GitHub.Mints);
    }

    [Fact]
    public async Task Repository_capacity_stays_bounded_across_restarts_and_minute_windows()
    {
        var f = IssuanceFixture.Create(runtimeLifetime: TimeSpan.FromMinutes(6));
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(200, (await f.IssueAsync(Token)).Status);
            f.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.Equal(8, f.Gate.OutstandingTokenCount);
        Assert.Equal(7, f.Attempts().Count);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(429, (await f.IssueAsync(Token)).Status);
            f.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.Equal(7, f.GitHub.Mints);
        Assert.Equal(7, f.GitHub.Authorizations);
        Assert.Equal(7, f.Attempts().Count);
        Assert.Equal(12, f.GlobalQuota.Slots);
    }

    [Fact]
    public async Task Global_capacity_and_fixed_window_cost_bound_a_restarted_request_storm()
    {
        var f = IssuanceFixture.Create(runtimeLifetime: TimeSpan.FromMinutes(6));
        var limits = new IssuanceLimits(GlobalAttempts: 4);
        Assert.Equal(200, (await f.IssueAsync(Token, limits)).Status);
        // Model a historical device without the optional published-lease pointer.
        f.State.Change(f.EnrollmentKey, row =>
        {
            row.Remove("RuntimeAttempt");
            row.Remove("RuntimeExpiresAt");
        });
        // The verifier and first runtime consume this minute's repository signing budget.
        for (var i = 0; i < 12; i++)
            Assert.Equal(429, (await f.IssueAsync(Token, limits)).Status);
        Assert.Equal(4, f.GlobalQuota.Slots);
        Assert.Equal(3, f.GlobalQuota.Admissions);
        Assert.Equal(2, f.Attempts().Count);
        Assert.Equal(2, f.GitHub.Authorizations);
        Assert.Equal(2, f.GitHub.Mints);
        var quota = f.State.Version(IssuanceQuota.Key);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(429, (await f.IssueAsync(Token, limits)).Status);
        Assert.Equal(quota, f.State.Version(IssuanceQuota.Key));
        var (slots, ledgers) = f.GlobalRows();
        Assert.Equal(9, f.State.Rows(IssuanceCapacity.Scope).Count); // Quota, four charged slots and four immutable ledgers.
        Assert.Equal(["slot:000", "slot:001", "slot:002", "slot:003"],
            f.State.Rows(IssuanceCapacity.Scope).Select(row => row.Name).Where(name => name.StartsWith("slot:", StringComparison.Ordinal)));
        Assert.Equal(4, ledgers.Count);
        Assert.Equal("one", f.State.Get(IssuanceSlot.Key(0))!.OperationId);
        Assert.Equal(4, slots.Count);
        Assert.Equal("outstanding", f.State.Get(IssuanceAttempt.Key(IssuanceFixture.RepositoryId, "one"))!.State);
    }
}
