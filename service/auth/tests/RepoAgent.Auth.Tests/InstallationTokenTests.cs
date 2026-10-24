using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

// GitHub token issuance, decided by the core over the in-memory broker state, GitHub fake and a manual clock.
public sealed class InstallationTokenTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Acknowledged_device_receives_exact_v1_token_with_durable_encrypted_accounting()
    {
        var f = IssuanceFixture.Create();
        var result = await f.IssueAsync(Token);
        Assert.Equal(200, result.Status);
        var body = JsonSerializer.SerializeToElement(result.Body);
        Assert.Equal(9, body.EnumerateObject().Count());
        Assert.Equal(1, body.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(IssuancePolicy.AppId, body.GetProperty("appId").GetInt64());
        Assert.Equal(123456, body.GetProperty("repositoryId").GetInt64());
        Assert.Equal("runtime-token-2", body.GetProperty("token").GetString());
        Assert.Equal("write", body.GetProperty("permissions").GetProperty("discussions").GetString());
        Assert.Single(body.GetProperty("permissions").EnumerateObject());
        Assert.Equal(2, f.Attempts().Count);
        Assert.Equal("released", f.Attempt("runtime").State);
        Assert.Equal("known", f.Attempt("verification").State);
        var stored = JsonSerializer.Serialize(f.State.Rows("repository:" + IssuanceFixture.RepositoryId).Select(row => row.Row));
        Assert.DoesNotContain("runtime-token", stored);
        Assert.DoesNotContain("verifier-token", stored);
        Assert.Equal(8, f.GitHub.Reads);
        Assert.Equal(2, f.GitHub.Authorizations);
        Assert.Equal(3, f.Gate.OutstandingTokenCount);
        Assert.Equal(f.Clock.GetUtcNow().AddDays(90), f.Enrollment.ExpiresAt);
    }

    [Fact]
    public async Task Restart_recovers_verifier_but_reads_fresh_approval_for_each_issuance()
    {
        var f = IssuanceFixture.Create();
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        Assert.Equal(1, f.GitHub.VerifierMints);
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal(16, f.GitHub.Reads);
        Assert.Equal(2, f.Attempts().Count);
    }

    [Theory]
    [InlineData("State", "revoked")]
    [InlineData("Epoch", "old-epoch")]
    [InlineData("Profile", "contents")]
    [InlineData("Marker", "copied-unbound-marker")]
    public async Task Invalid_grant_denies_before_signing_or_GitHub_work(string property, string value)
    {
        var f = IssuanceFixture.Create();
        f.State.Change(f.EnrollmentKey, row => row[property] = value);
        Assert.NotEqual(200, (await f.IssueAsync(Token)).Status);
        Assert.Equal(0, f.GitHub.Mints);
        Assert.Equal(0, f.GitHub.Authorizations);
    }

    [Fact]
    public async Task Changed_marker_closes_monotonically_and_old_marker_restoration_cannot_revive()
    {
        var f = IssuanceFixture.Create();
        var original = f.GitHub.Marker;
        f.GitHub.Marker = "changed";
        Assert.NotEqual(200, (await f.IssueAsync(Token)).Status);
        Assert.Equal("closed", f.Gate.State);
        Assert.Equal("revoked", f.Enrollment.State);
        f.GitHub.Marker = original;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotEqual(200, (await f.IssueAsync(Token)).Status);
        Assert.Equal(1, f.GitHub.Mints);
    }

    // Which answers are unverifiable is the core's rule (ApprovalObserverTests); this is the issuer's outcome.
    [Fact]
    public async Task Unverifiable_approval_denies_without_fabricating_withdrawal()
    {
        var f = IssuanceFixture.Create();
        f.GitHub.VariableAnswer = _ => new GitHubRead<GitHubVariable>.Refused();
        Assert.NotEqual(200, (await f.IssueAsync(Token)).Status);
        Assert.Equal("active", f.Gate.State);
        Assert.Null(f.Enrollment.LastActivityAt);
        Assert.Equal(1, f.GitHub.Mints);
    }

    [Fact]
    public async Task Revoke_after_runtime_mint_prevents_release_and_revokes_known_token()
    {
        var f = IssuanceFixture.Create();
        f.GitHub.BeforeMint = async mint =>
        {
            if (mint.Number == 2) Assert.Equal(204, await f.Revocation().RevokeAsync(f.Device.DeviceId, f.Device.Secret, Token));
        };
        Assert.NotEqual(200, (await f.IssueAsync(Token)).Status);
        Assert.Equal("closed", f.Gate.State);
        Assert.Null(f.Enrollment.LastActivityAt);
        Assert.Equal(1, f.GitHub.Revokes);
        Assert.Equal("settled", f.Attempt("runtime").State);
    }

    [Fact]
    public async Task Release_before_revoke_remains_accounted()
    {
        var f = IssuanceFixture.Create();
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        Assert.Equal(204, await f.Revocation().RevokeAsync(f.Device.DeviceId, f.Device.Secret, Token));
        Assert.Equal("released", f.Attempt("runtime").State);
        Assert.Equal(3, f.Gate.OutstandingTokenCount);
    }

    [Fact]
    public async Task Lost_verifier_mint_response_quarantines_acquisition_across_restart_and_JWT_expiry()
    {
        var f = IssuanceFixture.Create();
        f.GitHub.BeforeMint = _ => throw new GitHubUnavailable("secret-bearing-upstream-response");
        Assert.Equal(503, (await f.IssueAsync(Token)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        f.GitHub.BeforeMint = null;
        Assert.Equal(503, (await f.IssueAsync(Token)).Status);
        Assert.Equal(1, f.GitHub.Mints);
        Assert.Equal("sending", Assert.Single(f.Attempts()).State);
    }

    [Fact]
    public async Task Caller_disconnect_after_send_does_not_cancel_accounting()
    {
        var f = IssuanceFixture.Create();
        using var caller = new CancellationTokenSource();
        f.GitHub.BeforeMint = mint =>
        {
            if (mint.Number == 2) caller.Cancel();
            return Task.CompletedTask;
        };
        Assert.Equal(200, (await f.IssueAsync(caller.Token)).Status);
        Assert.Equal("released", f.Attempt("runtime").State);
        Assert.Equal(2, f.GitHub.Mints);
    }

    [Fact]
    public async Task Delayed_signing_cannot_refresh_reserved_JWT_or_send_after_deadline()
    {
        var f = IssuanceFixture.Create();
        f.GitHub.BeforeAuthorize = () => { f.Clock.Advance(TimeSpan.FromSeconds(121)); return Task.CompletedTask; };
        Assert.Equal(503, (await f.IssueAsync(Token)).Status);
        Assert.Equal(0, f.GitHub.Mints);
        var attempt = Assert.Single(f.Attempts());
        Assert.Equal(attempt.ReservedAt!.Value.AddSeconds(120), attempt.JwtExpiresAt);
        Assert.Equal("reserved", attempt.State);
    }

    [Fact]
    public async Task Stale_admission_observation_denies_and_revokes_minted_authority()
    {
        var f = IssuanceFixture.Create();
        f.GitHub.BeforeMint = mint =>
        {
            if (mint.Number == 2) f.Clock.Advance(TimeSpan.FromSeconds(11));
            return Task.CompletedTask;
        };
        Assert.Equal(503, (await f.IssueAsync(Token)).Status);
        Assert.Equal(1, f.GitHub.Revokes);
        Assert.Null(f.Enrollment.LastActivityAt);
    }

    [Fact]
    public async Task Excess_runtime_scope_is_protected_then_revoked_without_release()
    {
        var f = IssuanceFixture.Create();
        // Which reported scopes are out of scope is the core's rule (GitHubTokenMinterTests); this is the issuer's outcome.
        f.GitHub.MintAnswer = (mint, token) => GitHubRead.Found(mint.Number == 2 ? token with
        {
            Value = "excess-token", Permissions = new Dictionary<string, string?> { ["discussions"] = "write", ["contents"] = "read" }
        } : token);
        Assert.Equal(503, (await f.IssueAsync(Token)).Status);
        Assert.Equal(1, f.GitHub.Revokes);
        Assert.Equal("settled", f.Attempt("runtime").State);
    }
}
