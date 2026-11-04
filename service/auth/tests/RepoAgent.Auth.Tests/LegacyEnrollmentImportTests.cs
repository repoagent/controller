using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class LegacyEnrollmentImportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [MemberData(nameof(CurrentBrokerTests.Profiles), MemberType = typeof(CurrentBrokerTests))]
    public async Task Every_frozen_profile_retains_its_original_binding_and_runtime_route(int setup, string profile)
    {
        var (f, id, secret) = await Imported(setup, profile);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(200, (await f.Issuer().IssueAsync(id, secret, Token, f.Row.Approval.RuntimeVersion)).Status);
        CurrentBrokerTests.AssertNoTokens(f);
    }

    [Fact]
    public async Task Unchanged_controller_secret_and_approval_renew_after_import_without_token_history()
    {
        var (f, id, secret) = await Imported();
        var originalHash = f.Row.ControllerSecretHash;
        var originalMarker = f.Row.Marker;
        var originalExpiry = f.Row.ExpiresAt;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(200, (await f.Issuer().IssueAsync(id, secret, Token)).Status);
        Assert.Equal(originalHash, f.Row.ControllerSecretHash);
        Assert.Equal(originalMarker, f.Row.Marker);
        Assert.True(f.Row.ExpiresAt > originalExpiry);
        Assert.Equal(3, f.State.All().Count);
        CurrentBrokerTests.AssertNoTokens(f);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("unacknowledged")]
    [InlineData("gate-owner")]
    [InlineData("receipt")]
    [InlineData("epoch")]
    [InlineData("marker")]
    [InlineData("hash")]
    [InlineData("counter")]
    public async Task Invalid_or_closed_legacy_authority_cannot_be_imported(string defect)
    {
        var (f, _, _) = await CurrentBrokerTests.Active();
        var (gate, enrollment, quota) = Source(f.Row);
        RevocationReceipt? receipt = null;
        switch (defect)
        {
            case "revoked": enrollment.State = "revoked"; break;
            case "expired": enrollment.ExpiresAt = f.Clock.GetUtcNow(); break;
            case "unacknowledged": enrollment.Acknowledged = false; break;
            case "gate-owner": gate.OwnerEnrollmentId = Guid.NewGuid().ToString("N"); break;
            case "receipt": receipt = new(); break;
            case "epoch": gate.Epoch = "other-epoch"; break;
            case "marker": enrollment.Marker += " "; break;
            case "hash": enrollment.ControllerSecretHash = "invalid"; break;
            case "counter": quota.Mints = null; break;
        }
        Assert.Throws<IssuanceDenied>(() => LegacyEnrollmentImport.FromActive(gate, enrollment, quota,
            InMemoryBroker.Epoch, f.Clock.GetUtcNow(), receipt));
    }

    [Fact]
    public async Task Import_preserves_original_expiry_and_only_current_work_counters()
    {
        var (f, _, _) = await CurrentBrokerTests.Active();
        var (gate, enrollment, quota) = Source(f.Row);
        quota.Window = f.Clock.GetUtcNow().ToUnixTimeSeconds() / 60;
        quota.Admissions = 3; quota.Reads = 7; quota.Signs = 1; quota.Mints = 2; quota.Slots = 8;
        enrollment.RuntimeAttempt = "old-operation-reference";
        var row = LegacyEnrollmentImport.FromActive(gate, enrollment, quota, InMemoryBroker.Epoch, f.Clock.GetUtcNow());
        Assert.Equal(enrollment.ExpiresAt, row.ExpiresAt);
        Assert.Equal(row.ExpiresAt, row.NextCleanupAt);
        Assert.Equal(quota.Window, row.Window);
        Assert.Equal(3, row.Admissions); Assert.Equal(7, row.Reads); Assert.Equal(1, row.Signs); Assert.Equal(2, row.Mints);
        Assert.DoesNotContain(row.ToRow().Values, value => value is string s && s == enrollment.RuntimeAttempt);
        Assert.Null(row.PendingId); Assert.Null(row.SetupSecretHash); Assert.Null(row.ProtectedCompletion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revocation_removes_legacy_routing_and_retries_succeed_even_after_a_lost_response(bool lostResponse)
    {
        var (f, id, secret) = await Imported();
        var marker = f.GitHub.Marker;
        if (lostResponse) f.State.AfterCommit = _ => throw new BrokerStateUnavailable("lost reply");
        Assert.Equal(lostResponse ? 503 : 204, await new ControllerSelfRevocation(f.State, InMemoryBroker.Epoch, f.Clock).RevokeAsync(id, secret, Token));
        f.State.AfterCommit = null;
        var commits = f.State.Commits;
        Assert.Equal(204, await new ControllerSelfRevocation(f.State, InMemoryBroker.Epoch, f.Clock).RevokeAsync(id, secret, Token));
        Assert.Equal(commits, f.State.Commits);
        Assert.Null(f.State.Get(LegacyEnrollmentLocator.Key(id)));
        Assert.Null(f.Row.ControllerSecretHash);
        f.GitHub.Marker = marker;
        Assert.Equal(401, (await f.Issuer().IssueAsync(id, secret, Token)).Status);
    }

    [Fact]
    public async Task Cleanup_removes_expired_legacy_routing_and_secret_in_the_same_transaction()
    {
        var (f, id, _) = await Imported();
        f.Clock.Advance(TimeSpan.FromDays(90));
        var cleanup = new BrokerCleanup(f.State, InMemoryBroker.Epoch, new Budget(), f.Clock);
        Assert.Equal(0, await cleanup.RunAsync(Token));
        Assert.Null(f.State.Get(LegacyEnrollmentLocator.Key(id)));
        Assert.Null(f.Row.ControllerSecretHash);
    }

    [Fact]
    public async Task Stale_or_misdirected_locator_never_authenticates_another_generation()
    {
        var (f, id, secret) = await Imported();
        var mints = f.GitHub.Mints;
        var replacement = f.Row;
        replacement.EnrollmentId = "123456_" + Guid.NewGuid().ToString("N");
        replacement.Marker = replacement.CreateMarker();
        f.State.Seed(CurrentRepository.Key(123456), replacement);
        Assert.Equal(401, (await f.Issuer().IssueAsync(id, secret, Token)).Status);
        Assert.Equal(mints, f.GitHub.Mints);
    }

    [Fact]
    public async Task Replacing_an_expired_import_deletes_its_locator_without_retaining_history()
    {
        var (f, id, secret) = await Imported();
        f.Clock.Advance(TimeSpan.FromDays(90));
        var setup = await f.Enrollment().StartRepositoryVariableAsync(CurrentBrokerTests.Request(), InMemoryBroker.SetupSecret, Token, 5);
        Assert.Equal(200, setup.Status);
        Assert.Null(f.State.Get(LegacyEnrollmentLocator.Key(id)));
        Assert.Equal(401, (await f.Issuer().IssueAsync(id, secret, Token)).Status);
        Assert.Equal(2, f.State.All().Count);
    }

    [Fact]
    public async Task A_competing_close_fences_imported_runtime_publication()
    {
        var (f, id, secret) = await Imported();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        f.GitHub.BeforeMint = async mint =>
        {
            if (!mint.Verifier) Assert.Equal(204, await new ControllerSelfRevocation(f.State, InMemoryBroker.Epoch, f.Clock).RevokeAsync(id, secret, Token));
        };
        Assert.Equal(503, (await f.Issuer().IssueAsync(id, secret, Token)).Status);
        Assert.Null(f.Row.ControllerSecretHash);
        Assert.Null(f.State.Get(LegacyEnrollmentLocator.Key(id)));
    }

    private static async Task<(InMemoryBroker Broker, string Id, string Secret)> Imported(int setup = 5, string profile = "polling")
    {
        var (f, _, secret) = await CurrentBrokerTests.Active(setup, profile);
        var (gate, enrollment, quota) = Source(f.Row);
        var row = LegacyEnrollmentImport.FromActive(gate, enrollment, quota, InMemoryBroker.Epoch, f.Clock.GetUtcNow());
        f.State.Seed(CurrentRepository.Key(123456), row);
        f.State.Seed(LegacyEnrollmentLocator.Key(row.EnrollmentId!), new() { RepositoryId = 123456 });
        f.GitHub.Marker = row.Marker!;
        f.GitHub.Actions["APPROVAL"] = row.Marker!;
        return (f, row.EnrollmentId!, secret);
    }

    private static (RepositoryGate Gate, ControllerEnrollment Enrollment, RepositoryQuota Quota) Source(CurrentRepository current)
    {
        var id = Guid.NewGuid().ToString("N");
        var marker = ApprovalMarker.Create(current.Approval, "123456", current.Repository!, current.InstallationId!.Value,
            id, current.Challenge, current.Profile!, current.Epoch!, current.Variable!, current.Environment, current.EnvironmentId);
        return (new() { RepositoryId = "123456", OwnerEnrollmentId = id, State = "active", Epoch = current.Epoch, Sequence = 1 },
            new()
            {
                RepositoryId = "123456",
                EnrollmentId = id,
                Repository = current.Repository,
                InstallationId = current.InstallationId,
                Profile = current.Profile,
                Variable = current.Variable,
                ApprovalKind = current.ApprovalKind,
                SetupVersion = current.SetupVersion,
                PolicyVersion = current.PolicyVersion,
                Challenge = current.Challenge,
                Marker = marker,
                Epoch = current.Epoch,
                AppId = IssuancePolicy.AppId,
                AppSlug = IssuancePolicy.AppSlug,
                GitHubHost = "github.com",
                State = "active",
                Acknowledged = true,
                ControllerSecretHash = current.ControllerSecretHash,
                CompletionId = current.CompletionId,
                ExpiresAt = current.ExpiresAt,
                Environment = current.Environment,
                EnvironmentId = current.EnvironmentId
            }, RepositoryQuota.New());
    }

    private sealed class Budget : ICleanupBudget
    {
        public int Dispatches => 0;
        public int BatchDispatches => 0;
    }
}
