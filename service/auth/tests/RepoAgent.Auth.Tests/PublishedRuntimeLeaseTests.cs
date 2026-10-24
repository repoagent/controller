using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

// Reuse of a published runtime GitHub token, decided by the core over the in-memory broker state.
public sealed class PublishedRuntimeLeaseTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Repeated_controller_restarts_reuse_the_published_lease_without_exhausting_capacity()
    {
        var f = IssuanceFixture.Create();
        var leases = new List<JsonElement>();
        for (var restart = 0; restart < 8; restart++)
        {
            var result = await f.IssueAsync(Token);
            Assert.Equal(200, result.Status);
            leases.Add(JsonSerializer.SerializeToElement(result.Body));
            // Each reuse is activity: it renews the enrollment as a release does.
            Assert.Equal(f.Clock.GetUtcNow(), f.Enrollment.LastActivityAt);
            Assert.Equal(f.Clock.GetUtcNow().AddDays(90), f.Enrollment.ExpiresAt);
            f.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.All(leases, lease =>
        {
            Assert.Equal(leases[0].GetProperty("token").GetString(), lease.GetProperty("token").GetString());
            Assert.Equal(leases[0].GetProperty("expiresAt").GetString(), lease.GetProperty("expiresAt").GetString());
        });
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal(2, f.Attempts().Count);
        Assert.Equal(3, f.Gate.OutstandingTokenCount);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public async Task Definite_admission_conflicts_retry_with_fixed_clocks_and_no_duplicate_charge(int stage, bool exhaust)
    {
        var f = IssuanceFixture.Create();
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        var reserved = DateTimeOffset.FromUnixTimeSeconds(f.Clock.GetUtcNow().ToUnixTimeSeconds() / 60 * 60 + 90);
        f.Clock.Advance(reserved - f.Clock.GetUtcNow());
        var batches = 0;
        var conflicts = 0;
        f.State.BeforeCommit = _ =>
        {
            if (++batches < stage || (!exhaust && conflicts > 0)) return Task.CompletedTask;
            ++conflicts;
            f.State.Change(stage == 1 ? IssuanceQuota.Key : IssuanceFixture.GateKey, _ => { });
            f.Clock.Advance(TimeSpan.FromSeconds(2));
            return Task.CompletedTask;
        };
        var result = await f.IssueAsync(Token);
        Assert.Equal(exhaust ? 503 : 200, result.Status);
        Assert.Equal(exhaust ? 4 : 1, conflicts);
        Assert.Equal(conflicts, f.State.Conflicts);
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal(exhaust ? 8 : 16, f.GitHub.Reads);
        Assert.Equal(3, f.Gate.OutstandingTokenCount);
        var quota = f.GlobalQuota;
        Assert.Equal(1, quota.Admissions);
        Assert.Equal(1, quota.Signs);
        Assert.Equal(0, quota.Mints);
        Assert.Equal(3, quota.Slots);
        if (!exhaust)
        {
            var enrollment = f.Enrollment;
            Assert.Equal(reserved, enrollment.RuntimeObservationReservedAt);
            Assert.Equal(reserved.AddSeconds(-60), enrollment.RuntimeObservationJwtIssuedAt);
            Assert.Equal(reserved.AddSeconds(120), enrollment.RuntimeObservationJwtExpiresAt);
        }
    }

    [Fact]
    public async Task Changed_approval_closes_the_grant_instead_of_republishing_an_old_lease()
    {
        var f = IssuanceFixture.Create();
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.GitHub.Marker = "changed";

        var result = await f.IssueAsync(Token);

        Assert.Equal(401, result.Status);
        Assert.Null(result.Body);
        Assert.Equal("revoked", f.Enrollment.State);
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal(0, f.GitHub.Revokes);
        Assert.Equal(3, f.Gate.OutstandingTokenCount);
    }

    [Fact]
    public async Task Concurrent_revocation_during_reuse_denies_publication_without_revoking_the_shared_token()
    {
        var f = IssuanceFixture.Create();
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.GitHub.BeforeAuthorize = async () => Assert.Equal(204, await f.Revocation().RevokeAsync(f.Device.DeviceId, f.Device.Secret, Token));

        var result = await f.IssueAsync(Token);

        Assert.NotEqual(200, result.Status);
        Assert.Null(result.Body);
        Assert.Equal("revoked", f.Enrollment.State);
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal(0, f.GitHub.Revokes);
        Assert.Equal("released", f.Attempt("runtime").State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_original_release_response_cannot_revoke_a_published_or_concurrently_reused_lease(bool concurrentReuse)
    {
        var f = IssuanceFixture.Create();
        var batches = 0;
        var lostRelease = false;
        IssuanceResult? competing = null;
        f.State.AfterCommit = async _ =>
        {
            // The first verifier uses four transactions; runtime release is the ninth.
            if (lostRelease || ++batches != 9) return;
            lostRelease = true;
            if (concurrentReuse)
            {
                f.Clock.Advance(TimeSpan.FromMinutes(1));
                competing = await f.IssueAsync(Token);
            }
            throw new BrokerStateUnavailable("Lost original release response");
        };
        var lost = await f.IssueAsync(Token);
        Assert.Equal(503, lost.Status);
        Assert.True(lostRelease);
        f.State.AfterCommit = null;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await f.IssueAsync(Token);
        Assert.Equal(200, recovered.Status);
        if (concurrentReuse)
        {
            Assert.Equal(200, competing!.Status);
            Assert.Equal(JsonSerializer.SerializeToElement(competing.Body).GetProperty("token").GetString(),
                JsonSerializer.SerializeToElement(recovered.Body).GetProperty("token").GetString());
        }
        Assert.Equal(0, f.GitHub.Revokes);
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal("released", f.Attempt("runtime").State);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Lost_reuse_commit_response_preserves_the_published_lease_for_a_fresh_request(int stage)
    {
        var f = IssuanceFixture.Create();
        var first = await f.IssueAsync(Token);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var batches = 0;
        f.State.AfterCommit = _ => ++batches == stage
            ? throw new BrokerStateUnavailable("Lost reuse transaction response") : Task.CompletedTask;
        var lost = await f.IssueAsync(Token);
        Assert.Equal(503, lost.Status);
        Assert.Null(lost.Body);
        Assert.Equal(stage, batches);
        f.State.AfterCommit = null;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await f.IssueAsync(Token);
        Assert.Equal(200, recovered.Status);
        Assert.Equal(JsonSerializer.SerializeToElement(first.Body).GetProperty("token").GetString(),
            JsonSerializer.SerializeToElement(recovered.Body).GetProperty("token").GetString());
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal(0, f.GitHub.Revokes);
        Assert.Equal(2, f.Attempts().Count);
    }

    [Fact]
    public async Task A_competing_reuse_publication_fences_the_older_observation()
    {
        var f = IssuanceFixture.Create();
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.GitHub.BeforeAuthorize = async () =>
        {
            if (f.GitHub.Authorizations != 3) return;
            entered.SetResult();
            await resume.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        };
        var stale = f.IssueAsync(Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        }
        finally { resume.TrySetResult(); }
        Assert.NotEqual(200, (await stale).Status);
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal(0, f.GitHub.Revokes);
        Assert.Equal(3, f.Gate.OutstandingTokenCount);
    }

    [Theory]
    [InlineData("State", "known")]
    [InlineData("State", "sending")]
    [InlineData("ScopeValid", false)]
    [InlineData("DeviceId", "different-device")]
    [InlineData("ApprovalKind", "repository-agent-variable")]
    [InlineData("PayloadRedacted", true)]
    [InlineData("CapacityReleased", true)]
    public async Task Invalid_published_authority_fails_closed_without_a_replacement_mint(string field, object value)
    {
        var f = IssuanceFixture.Create();
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        var attempt = f.Attempt("runtime");
        f.State.Change(IssuanceAttempt.Key(IssuanceFixture.RepositoryId, attempt.OperationId!), row => row[field] = value);
        f.Clock.Advance(TimeSpan.FromMinutes(1));

        var result = await f.IssueAsync(Token);

        Assert.NotEqual(200, result.Status);
        Assert.Null(result.Body);
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal(2, f.GitHub.Authorizations);
    }

    // The grant still accounts for the published token: the enrollment counts it, and the gate counts the verifier too.
    [Theory]
    [InlineData("enrollment", 0L)]
    [InlineData("gate", 2L)]
    public async Task Published_lease_the_grant_no_longer_accounts_for_fails_closed(string record, long outstanding)
    {
        var f = IssuanceFixture.Create();
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        f.State.Change(record == "gate" ? IssuanceFixture.GateKey : f.EnrollmentKey, row => row["OutstandingTokenCount"] = outstanding);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var commits = f.State.Commits;

        Assert.Equal(503, (await f.IssueAsync(Token)).Status);
        Assert.Equal(commits, f.State.Commits);
        Assert.Equal(2, f.GitHub.Mints);
    }

    // A published lease is reusable only while its global ledger and slot still charge exactly that attempt.
    [Theory]
    [InlineData("ledger", "RetirementState", "retiring")]
    [InlineData("ledger", "CapacityState", "released")]
    [InlineData("slot", "Generation", "0123456789abcdef0123456789abcdef")]
    [InlineData("slot", "GlobalSlot", 99L)]
    public async Task Published_lease_whose_capacity_no_longer_binds_it_fails_closed(string record, string field, object value)
    {
        var f = IssuanceFixture.Create();
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        var runtime = f.Attempt("runtime");
        f.State.Change(record == "ledger" ? IssuanceLedger.Key(runtime.OperationId!) : IssuanceSlot.Key(runtime.GlobalSlot!.Value),
            row => row[field] = value);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var commits = f.State.Commits;

        Assert.Equal(503, (await f.IssueAsync(Token)).Status);
        Assert.Equal(commits, f.State.Commits);
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal(2, f.GitHub.Authorizations);
    }

    // A release whose response was lost may still land. Before the token is revoked, a conditional rewrite of the attempt
    // fences that release, so a token being revoked is never published.
    [Fact]
    public async Task Release_landing_late_during_revocation_is_fenced_and_never_publishes_the_revoked_token()
    {
        var f = IssuanceFixture.Create();
        var batches = 0;
        StateTransaction? release = null;
        f.State.BeforeCommit = transaction =>
        {
            // The first verifier uses four transactions; runtime release is the ninth.
            if (++batches != 9) return Task.CompletedTask;
            release = transaction;
            throw new BrokerStateUnavailable("Release in flight");
        };
        Exception? late = null;
        f.GitHub.BeforeRevoke = async _ =>
        {
            f.State.BeforeCommit = null;
            late = await Record.ExceptionAsync(() => f.State.CommitAsync(release!, Token));
        };

        Assert.Equal(503, (await f.IssueAsync(Token)).Status);
        Assert.IsType<StateConflict>(late);
        Assert.Equal(1, f.GitHub.Revokes);
        Assert.Null(f.Enrollment.RuntimeAttempt);
        Assert.Equal("settled", f.Attempt("runtime").State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Near_expiry_or_settled_lease_uses_ordinary_issuance_without_extending_previous_clocks(bool settled)
    {
        var f = IssuanceFixture.Create();
        var first = await f.IssueAsync(Token);
        Assert.Equal(200, first.Status);
        var key = IssuanceAttempt.Key(IssuanceFixture.RepositoryId, f.Attempt("runtime").OperationId!);
        var expiry = f.State.Get(key)!.ExpiresAt;
        f.Clock.Advance(TimeSpan.FromMinutes(settled ? 1 : 56));
        if (settled)
        {
            var store = f.Store();
            var grant = await store.AuthenticateAsync(f.Device.DeviceId, f.Device.Secret, Token);
            await store.MarkRevokedAsync(grant, new(key, f.State.Get(key)!, f.State.Version(key)!), Token);
        }

        var result = await f.IssueAsync(Token);

        Assert.Equal(200, result.Status);
        Assert.NotEqual(JsonSerializer.SerializeToElement(first.Body).GetProperty("token").GetString(),
            JsonSerializer.SerializeToElement(result.Body).GetProperty("token").GetString());
        Assert.Equal(settled ? 3 : 4, f.GitHub.Mints);
        Assert.Equal(expiry, f.State.Get(key)!.ExpiresAt);
        Assert.Equal(settled ? 4 : 5, f.Gate.OutstandingTokenCount);
    }
}
