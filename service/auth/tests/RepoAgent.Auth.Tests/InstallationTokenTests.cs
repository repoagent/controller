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
        Assert.Equal(2, f.Enrollment.OutstandingTokenCount);
        // Every repository transaction advances the gate: three for the verifier, four for the runtime token.
        Assert.Equal(14, f.Gate.Sequence);
        Assert.Equal(f.Clock.GetUtcNow().AddDays(90), f.Enrollment.ExpiresAt);
    }

    // Who may receive a GitHub token is decided before any GitHub work or write. A row that contradicts the deployment,
    // the key it was read under or its approval contract is corrupt (503); anything else that is not this controller's
    // active grant is 401.
    [Theory]
    [InlineData("enrollment-id", 401)]
    [InlineData("secret", 401)]
    [InlineData("locator-enrollment", 401)]
    [InlineData("retained-owner", 401)]
    [InlineData("runtime-version", 401)]
    [InlineData("unacknowledged", 401)]
    [InlineData("expired", 401)]
    [InlineData("redacted", 503)]
    [InlineData("other-deployment", 503)]
    [InlineData("gate-repository", 503)]
    [InlineData("grant-repository", 503)]
    [InlineData("agent-with-environment", 503)]
    public async Task Invalid_authority_is_denied_before_GitHub_work_or_a_write(string scenario, int status)
    {
        var f = IssuanceFixture.Create();
        var (enrollmentId, secret, runtimeVersion) = (f.Device.DeviceId, f.Device.Secret, 1);
        var other = Guid.NewGuid().ToString("N");
        string Marker(ApprovalContract approval, string repositoryId, string epoch, bool environment) => ApprovalMarker.Create(approval,
            repositoryId, "owner/project", 789, enrollmentId, new string('a', 43), "polling", epoch, "APPROVAL",
            environment ? "approval/environment" : null, environment ? 987 : null);
        switch (scenario)
        {
            case "enrollment-id": enrollmentId += "."; break;
            case "secret": secret = new string('s', 43); break;
            case "locator-enrollment": f.State.Change(EnrollmentLocator.Key(enrollmentId), row => row["DeviceId"] = other); break;
            case "retained-owner": f.State.Change(IssuanceFixture.GateKey, row => row["OwnerDeviceId"] = other); break;
            case "runtime-version": runtimeVersion = 2; break;
            case "unacknowledged": f.State.Change(f.EnrollmentKey, row => row["Acknowledged"] = false); break;
            case "expired": f.State.Change(f.EnrollmentKey, row => row["ExpiresAt"] = f.Clock.GetUtcNow()); break;
            case "redacted": f.State.Change(f.EnrollmentKey, row => row["PayloadRedacted"] = true); break;
            case "gate-repository": f.State.Change(IssuanceFixture.GateKey, row => row["RepositoryId"] = "654321"); break;
            case "other-deployment":
                // A consistent grant of another deployment, its marker included.
                f.State.Change(IssuanceFixture.GateKey, row => row["Epoch"] = "old-epoch");
                f.State.Change(f.EnrollmentKey, row =>
                {
                    row["Epoch"] = "old-epoch";
                    row["Marker"] = Marker(ApprovalContract.Legacy, IssuanceFixture.RepositoryId, "old-epoch", true);
                });
                break;
            case "grant-repository":
                // A consistent grant of another repository, its marker included, under this repository's keys.
                f.State.Change(IssuanceFixture.GateKey, row => row["RepositoryId"] = "654321");
                f.State.Change(f.EnrollmentKey, row =>
                {
                    row["RepositoryId"] = "654321";
                    row["Marker"] = Marker(ApprovalContract.Legacy, "654321", IssuanceFixture.Epoch, true);
                });
                break;
            case "agent-with-environment":
                // A repository-variable enrollment never names an approval environment.
                f.State.Change(f.EnrollmentKey, row =>
                {
                    row["ApprovalKind"] = ApprovalContract.Agent.Kind;
                    row["SetupVersion"] = ApprovalContract.Agent.SetupVersion;
                    row["PolicyVersion"] = ApprovalContract.Agent.Policy;
                    row["Marker"] = Marker(ApprovalContract.Agent, IssuanceFixture.RepositoryId, IssuanceFixture.Epoch, false);
                });
                break;
        }
        var (reads, commits) = (f.State.Reads, f.State.Commits);

        var result = await f.Issuer().IssueAsync(enrollmentId, secret, Token, runtimeVersion);

        Assert.Equal(status, result.Status);
        Assert.Null(result.Body);
        Assert.Equal(commits, f.State.Commits);
        Assert.Equal(0, f.GitHub.Authorizations);
        if (scenario == "enrollment-id") Assert.Equal(reads, f.State.Reads);
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
        // Closing left the revocation receipt, so the controller's own revocation of the closed grant still succeeds.
        Assert.Equal(204, await f.Revocation().RevokeAsync(f.Device.DeviceId, f.Device.Secret, Token));
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
        Assert.Null(f.Attempt("runtime").ProtectedToken);
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
        var runtime = f.Attempt("runtime");
        Assert.Equal("settled", runtime.State);
        Assert.False(runtime.ScopeValid);
        // Only the admission observation read GitHub; an out-of-scope token is never observed for release.
        Assert.Equal(4, f.GitHub.Reads);
    }

    [Fact]
    public async Task Excess_verifier_scope_is_revoked_and_settled_before_any_runtime_work()
    {
        var f = IssuanceFixture.Create();
        f.GitHub.MintAnswer = (mint, token) => GitHubRead.Found(mint.Verifier ? token with
        {
            Permissions = new Dictionary<string, string?>(token.Permissions!) { ["contents"] = "write" }
        } : token);
        Assert.Equal(503, (await f.IssueAsync(Token)).Status);
        Assert.Equal(1, f.GitHub.Revokes);
        var verifier = f.Attempt("verification");
        Assert.Equal("settled", verifier.State);
        Assert.False(verifier.ScopeValid);
        Assert.Equal(1, f.GitHub.Mints);
    }

    // Only a known verifier with exactly its scope may observe approval. Anything else is unresolved and denies before
    // any charge.
    [Theory]
    [InlineData("State", "sending")]
    [InlineData("ScopeValid", false)]
    public async Task Unusable_verifier_denies_before_any_charge(string field, object value)
    {
        var f = IssuanceFixture.Create();
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        f.State.Change(IssuanceAttempt.Key(IssuanceFixture.RepositoryId, f.Gate.VerifierAttempt!), row => row[field] = value);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var commits = f.State.Commits;
        Assert.Equal(503, (await f.IssueAsync(Token)).Status);
        Assert.Equal(commits, f.State.Commits);
        Assert.Equal(2, f.GitHub.Mints);
    }

    [Fact]
    public async Task Recovered_verifier_that_is_not_a_bearer_value_is_never_sent_to_GitHub()
    {
        var f = IssuanceFixture.Create();
        Assert.Equal(200, (await f.IssueAsync(Token)).Status);
        var operation = f.Gate.VerifierAttempt!;
        var purpose = ApprovalContract.Legacy.TokenPurpose(IssuanceFixture.RepositoryId, IssuanceFixture.Epoch, operation, "verification");
        f.State.Change(IssuanceAttempt.Key(IssuanceFixture.RepositoryId, operation),
            row => row["ProtectedToken"] = f.Secrets.Protect(purpose, "verifier token"));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(503, (await f.IssueAsync(Token)).Status);
        Assert.Equal(8, f.GitHub.Reads);
    }

    [Fact]
    public async Task Enrollment_expiring_before_the_send_claim_is_never_sent()
    {
        var f = IssuanceFixture.Create();
        f.State.Change(f.EnrollmentKey, row => row["ExpiresAt"] = f.Clock.GetUtcNow().AddSeconds(5));
        f.GitHub.BeforeAuthorize = () =>
        {
            // The runtime admission is decided; the enrollment expires while its App JWT is signed.
            if (f.GitHub.Authorizations == 2) f.Clock.Advance(TimeSpan.FromSeconds(6));
            return Task.CompletedTask;
        };
        Assert.Equal(401, (await f.IssueAsync(Token)).Status);
        Assert.Equal(1, f.GitHub.Mints);
        Assert.Equal("reserved", f.Attempt("runtime").State);
    }

    // The mint accepted a token just outside the five-minute renewal margin; it crosses the margin while the release
    // observation reads GitHub, so it is revoked rather than published.
    [Fact]
    public async Task Runtime_token_crossing_the_renewal_margin_before_release_is_revoked_instead_of_released()
    {
        var f = IssuanceFixture.Create(runtimeLifetime: TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(3)));
        f.GitHub.BeforeRead = _ =>
        {
            if (f.GitHub.Reads == 5) f.Clock.Advance(TimeSpan.FromSeconds(4));
            return Task.CompletedTask;
        };
        Assert.Equal(503, (await f.IssueAsync(Token)).Status);
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal(1, f.GitHub.Revokes);
        Assert.Equal("settled", f.Attempt("runtime").State);
        Assert.Null(f.Enrollment.RuntimeAttempt);
    }
}
