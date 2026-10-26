using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

public sealed class BrokerAuthenticationTests
{
    [Fact]
    public async Task Startup_takes_enrollment_from_a_named_environment_variable_that_children_never_inherit()
    {
        using var fixture = new Fixture(clone: true);
        var variable = "REPOAGENT_TEST_ENROLLMENT_" + Guid.NewGuid().ToString("N");
        var childEnvironment = Path.Combine(fixture.Directory, "gh-environment");
        File.WriteAllText(fixture.Gh, $"#!/bin/sh\nenv >> '{childEnvironment}'\nif [ \"$1\" = api ]; then cat >/dev/null; printf '%s' '{{\"data\":{{\"viewer\":{{\"login\":\"test-app[bot]\"}}}}}}'; fi\n");
        File.Delete(fixture.EnrollmentFile); // No file copy exists anywhere.
        Environment.SetEnvironmentVariable(variable, fixture.Enrollment.ToJsonString());
        try
        {
            Assert.Equal(0, await fixture.Start("--enrollment-environment", variable));
            Assert.Single(fixture.Transport.Requests); // The transport accepts only the enrolled device secret.
            Assert.Null(Environment.GetEnvironmentVariable(variable));
            var inherited = File.ReadAllText(childEnvironment);
            Assert.Contains("GH_TOKEN=opaque-token-1", inherited);
            Assert.DoesNotContain(variable, inherited);
            Assert.DoesNotContain(fixture.Enrollment["deviceSecret"]!.GetValue<string>(), inherited);
            Assert.DoesNotContain(fixture.Enrollment["deviceSecret"]!.GetValue<string>(), string.Concat(
                System.IO.Directory.EnumerateFiles(fixture.Directory, "*", SearchOption.AllDirectories).Select(File.ReadAllText)));
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("mismatched")]
    [InlineData("with-file")]
    public async Task Missing_or_mismatched_environment_enrollment_denies_startup_before_broker_contact(string fault)
    {
        using var fixture = new Fixture(clone: true);
        var variable = "REPOAGENT_TEST_ENROLLMENT_" + Guid.NewGuid().ToString("N");
        if (fault == "mismatched") fixture.Enrollment["repository"] = "owner/other";
        Environment.SetEnvironmentVariable(variable, fault switch { "missing" => null, "empty" => "", _ => fixture.Enrollment.ToJsonString() });
        try { Assert.Equal(1, await fixture.Start("--enrollment-environment", variable, keepFile: fault == "with-file")); }
        finally { Environment.SetEnvironmentVariable(variable, null); }
        Assert.Empty(fixture.Transport.Requests);
        // Documented retained logs beside --state carry the operator diagnostic.
        var retained = string.Concat(System.IO.Directory.EnumerateFiles(fixture.State + ".logs").Select(File.ReadAllText));
        Assert.DoesNotContain(fixture.Enrollment["deviceSecret"]!.GetValue<string>(), retained);
        Assert.Contains(fault switch
        {
            "mismatched" => "CLI App/repository differs from enrollment",
            "with-file" => "exactly one of --enrollment-file or --enrollment-environment",
            _ => $"enrollment environment variable {variable} is missing or empty",
        }, retained);
    }

    [Fact]
    public async Task Broker_CLI_rejects_an_insecure_origin_without_requiring_a_PEM()
    {
        using var fixture = new Fixture();
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { typeof(GitHubAppCredentials).Assembly.Location, "--repo", "owner/repository", "--app-id", "123", "--broker-url", "http://credentials.example.org", "--enrollment-file", "/missing/enrollment.json", "--check-auth", "--state", fixture.State })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.NotEqual(0, process.ExitCode);
        var diagnostic = await output + await error;
        Assert.DoesNotContain("Unrecognized", diagnostic);
        Assert.Contains("HTTPS origin", diagnostic);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_startup_accepts_the_enrolled_bot_without_a_PEM_and_leaves_no_usable_credential(bool webhooks)
    {
        using var fixture = new Fixture(webhooks);
        fixture.SeedCache(); // An earlier controller version persisted its token here.
        Assert.Equal(0, await fixture.Start());
        Assert.Single(fixture.Transport.Requests);
        Assert.False(File.Exists(fixture.LegacyTokenFile));
        Assert.False(File.Exists(fixture.LegacyTokenFile + ".tmp"));
        // The checked lifetime has ended: its wrapper neither reuses that lease nor falls back to ambient sign-in.
        var result = await fixture.InvokeWrapper();
        Assert.NotEqual(0, result.Exit);
        Assert.DoesNotContain("gh-invoked", result.Output);
        Assert.DoesNotContain("opaque-token-1", result.Output);
    }

    [Fact]
    public async Task Wrong_actual_viewer_removes_prior_and_temporary_credentials_and_disables_existing_wrapper()
    {
        using var fixture = new Fixture();
        fixture.SeedCache();
        fixture.Viewer("human-user");
        Assert.Equal(1, await fixture.Start());
        Assert.False(File.Exists(fixture.LegacyTokenFile));
        Assert.False(File.Exists(fixture.LegacyTokenFile + ".tmp"));
        var result = await fixture.InvokeWrapper();
        Assert.NotEqual(0, result.Exit);
        Assert.DoesNotContain("gh-invoked", result.Output);
    }

    [Fact]
    public async Task Losing_the_state_lock_preserves_the_active_owners_credential()
    {
        using var fixture = new Fixture();
        using var owner = new FileStream(fixture.State + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var active = fixture.Credentials();
        await active.Initialize(null, TestContext.Current.CancellationToken);
        fixture.SeedCache();
        Assert.Equal(1, await fixture.Start());
        Assert.Contains("prior-token", File.ReadAllText(fixture.LegacyTokenFile));
        Assert.Single(fixture.Transport.Requests); // Only the active owner's acquisition.
        var served = await fixture.InvokeWrapper();
        Assert.Equal(0, served.Exit);
        Assert.Contains("gh-invoked", served.Output);
        Assert.Contains("opaque-token-1", served.Output);
    }

    [Fact]
    public async Task State_exclusion_does_not_depend_on_best_effort_runtime_locking()
    {
        using var fixture = new Fixture();
        var token = "{\"token\":\"prior-token\",\"expiresAt\":\"2099-01-01T00:00:00Z\"}";
        using (var owner = new FileStream(fixture.State + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            fixture.SeedCache();
            var denied = await Contender();
            Assert.Equal(1, denied.Exit);
            Assert.True(File.Exists(fixture.LegacyTokenFile), "A losing contender cleared the active owner's cache");
            Assert.Equal(token, File.ReadAllText(fixture.LegacyTokenFile));
            Assert.Equal("prior-temporary-token", File.ReadAllText(fixture.LegacyTokenFile + ".tmp"));
            Assert.DoesNotContain("Unrecognized", denied.Output);
            if (!OperatingSystem.IsWindows()) Assert.Contains("errno=", denied.Output);
        }
        // A fresh process can acquire after explicit disposal and reach the missing-key refusal.
        var released = await Contender();
        Assert.Equal(1, released.Exit);
        Assert.False(File.Exists(fixture.LegacyTokenFile));
        Assert.False(File.Exists(fixture.LegacyTokenFile + ".tmp"));
        Assert.Contains("missing-key.pem", released.Output);

        async Task<(int Exit, string Output)> Contender()
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
            start.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1";
            foreach (var argument in new[] { typeof(GitHubAppCredentials).Assembly.Location, "--repo", "owner/repository", "--app-id", "123", "--private-key", Path.Combine(fixture.Directory, "missing-key.pem"), "--check-auth", "--state", fixture.State })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                return (process.ExitCode, await output + await error);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            }
        }
    }

    [Fact]
    public async Task Invalid_state_after_lock_acquisition_clears_previous_credentials()
    {
        using var fixture = new Fixture();
        fixture.SeedCache();
        File.WriteAllText(fixture.State, "{}");
        Assert.Equal(1, await fixture.Start());
        Assert.False(File.Exists(fixture.LegacyTokenFile));
    }

    [Fact]
    public async Task Existing_wrapper_observes_renewal_then_requires_a_fresh_lifetime_after_failure()
    {
        using var fixture = new Fixture();
        using var credentials = fixture.Credentials();
        await credentials.Initialize(null, TestContext.Current.CancellationToken);
        Assert.Contains("opaque-token-1", (await fixture.InvokeWrapper()).Output);
        fixture.Clock.Advance(TimeSpan.FromMinutes(56));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => credentials.GetToken(TestContext.Current.CancellationToken)));
        Assert.Equal(2, fixture.Transport.Requests.Count);
        Assert.Contains("opaque-token-2", (await fixture.InvokeWrapper()).Output);
        fixture.Clock.Advance(TimeSpan.FromMinutes(56));
        fixture.Transport.Status = HttpStatusCode.Unauthorized;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => credentials.GetToken(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("DO-NOT-LOG", error.ToString());
        var failed = await fixture.InvokeWrapper();
        Assert.NotEqual(0, failed.Exit);
        Assert.DoesNotContain("gh-invoked", failed.Output);
        fixture.Transport.Status = HttpStatusCode.OK;
        var requests = fixture.Transport.Requests.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => credentials.GetToken(TestContext.Current.CancellationToken));
        Assert.Equal(requests, fixture.Transport.Requests.Count);
        using var restarted = fixture.Credentials();
        await restarted.Initialize(null, TestContext.Current.CancellationToken);
        Assert.Equal(0, (await fixture.InvokeWrapper()).Exit);
    }

    [Theory]
    [InlineData("appId")]
    [InlineData("installationId")]
    [InlineData("repositoryId")]
    [InlineData("appSlug")]
    [InlineData("repository")]
    [InlineData("protocolVersion")]
    [InlineData("permissions")]
    [InlineData("permission-level")]
    [InlineData("token")]
    [InlineData("whitespace-token")]
    [InlineData("expiresAt")]
    [InlineData("near-expiry")]
    [InlineData("overlong-expiry")]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    [InlineData("wrong-type")]
    [InlineData("redirect")]
    public async Task Rejected_lease_cannot_reach_a_native_launch_including_on_renewal(string defect)
    {
        using var fixture = new Fixture();
        using var credentials = fixture.Credentials();
        await credentials.Initialize(null, TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(56));
        fixture.Transport.Defect = defect;
        await Assert.ThrowsAsync<InvalidOperationException>(() => credentials.GetToken(TestContext.Current.CancellationToken));
        var result = await fixture.InvokeWrapper();
        Assert.NotEqual(0, result.Exit);
        Assert.DoesNotContain("gh-invoked", result.Output);
    }

    [Fact]
    public async Task Stalled_broker_body_times_out_clears_credentials_and_allows_a_fresh_start()
    {
        using var fixture = new Fixture();
        using var credentials = fixture.Credentials();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await credentials.Initialize(null, cancellation.Token);
        Assert.Contains("opaque-token-1", (await fixture.InvokeWrapper()).Output);
        fixture.Clock.Advance(TimeSpan.FromMinutes(56));
        using var stalledBody = new StalledBody();
        fixture.Transport.Body = stalledBody;
        var renewal = credentials.GetToken(cancellation.Token);
        try
        {
            await stalledBody.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            fixture.Clock.Advance(TimeSpan.FromSeconds(60));
            // Real time is only a failure watchdog; the acquisition deadline uses the fixture clock.
            var error = await Record.ExceptionAsync(() => renewal.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.False(cancellation.IsCancellationRequested);
            var failed = await fixture.InvokeWrapper();
            Assert.NotEqual(0, failed.Exit);
            Assert.DoesNotContain("gh-invoked", failed.Output);
            Assert.DoesNotContain("human-token", failed.Output);

            fixture.Transport.Body = null;
            await Assert.ThrowsAsync<InvalidOperationException>(() => credentials.GetToken(cancellation.Token));
            using var restarted = fixture.Credentials();
            await restarted.Initialize(null, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var recovered = await fixture.InvokeWrapper();
            Assert.Equal(0, recovered.Exit);
            Assert.Contains("opaque-token-3", recovered.Output);
            Assert.DoesNotContain("human-token", recovered.Output);
        }
        finally
        {
            cancellation.Cancel();
            await Record.ExceptionAsync(() => renewal.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        }
    }

    [Theory]
    [InlineData("githubHost", "other.example")]
    [InlineData("brokerOrigin", "https://other.example")]
    [InlineData("brokerOrigin", "https://credentials.example.org/path")]
    [InlineData("brokerOrigin", "https://user@credentials.example.org")]
    [InlineData("deviceId", "../other")]
    [InlineData("deviceSecret", "short")]
    [InlineData("appSlug", "..")]
    [InlineData("repository", "owner/..")]
    public async Task Rejected_enrollment_never_transmits_its_secret(string field, string value)
    {
        using var fixture = new Fixture();
        fixture.Enrollment[field] = value;
        fixture.SaveEnrollment();
        fixture.SeedCache();
        Assert.Equal(1, await fixture.Start());
        Assert.Empty(fixture.Transport.Requests);
        Assert.False(File.Exists(fixture.LegacyTokenFile));
    }

    [Theory]
    [InlineData("--app-id", "124")]
    [InlineData("--app-id", "client-id")]
    [InlineData("--repo", "owner/other")]
    [InlineData("--installation-id", "999")]
    [InlineData("--private-key", "/must-not-read.pem")]
    [InlineData("--broker-url", "https://other.example")]
    public async Task CLI_selection_must_agree_before_broker_contact(string option, string value)
    {
        using var fixture = new Fixture();
        Assert.Equal(1, await fixture.Start(option, value));
        Assert.Empty(fixture.Transport.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expected_immutable_repository_accepts_only_the_enrolled_identity(bool webhooks)
    {
        using var matching = new Fixture(webhooks, clone: true);
        Assert.Equal(0, await matching.Start("--repository-id", "345"));

        using var recreated = new Fixture(webhooks, clone: true);
        recreated.Enrollment["repositoryId"] = 999; // Same name, App and installation; different immutable identity.
        recreated.SaveEnrollment();
        recreated.SeedCache();
        Assert.Equal(1, await recreated.Start("--repository-id", "345"));
        Assert.Empty(recreated.Transport.Requests);
        Assert.False(File.Exists(recreated.LegacyTokenFile));
        Assert.False(File.Exists(recreated.LegacyTokenFile + ".tmp"));
        Assert.NotEqual(0, (await recreated.InvokeWrapper()).Exit);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("-1", true)]
    [InlineData("345", false)]
    public async Task Expected_repository_identity_requires_positive_broker_clone_selection(string id, bool clone)
    {
        using var fixture = new Fixture(clone: clone);
        Assert.Equal(1, await fixture.Start("--repository-id", id));
        Assert.Empty(fixture.Transport.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runtime_permission_profile_must_match_enrollment(bool webhooks)
    {
        using var fixture = new Fixture(webhooks);
        if (webhooks) fixture.Enrollment["permissions"]!.AsObject().Remove("repository_hooks");
        else fixture.Enrollment["permissions"]!["repository_hooks"] = "write";
        fixture.SaveEnrollment();
        Assert.Equal(1, await fixture.Start());
        Assert.Empty(fixture.Transport.Requests);
    }

    [Fact]
    public async Task Implicit_metadata_read_is_allowed_on_enrollment_and_lease()
    {
        using var fixture = new Fixture();
        fixture.Enrollment["permissions"]!["metadata"] = "read";
        fixture.SaveEnrollment();
        Assert.Equal(0, await fixture.Start());
    }

    [Theory]
    [InlineData(UnixFileMode.GroupRead)]
    [InlineData(UnixFileMode.UserExecute)]
    public async Task Unsafe_enrollment_file_is_rejected_before_contacting_the_broker(UnixFileMode extraMode)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        File.SetUnixFileMode(fixture.EnrollmentFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | extraMode);
        Assert.Equal(1, await fixture.Start());
        Assert.Empty(fixture.Transport.Requests);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    public async Task V1_file_rejects_Contents_before_any_broker_contact(string access)
    {
        using var fixture = new Fixture();
        fixture.Enrollment["permissions"]!["contents"] = access;
        fixture.SaveEnrollment();
        Assert.Equal(1, await fixture.Start());
        Assert.Empty(fixture.Transport.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clone_consumer_selects_v2_and_renewal_keeps_the_repository_bound_Git_credential(bool webhooks)
    {
        using var fixture = new Fixture(webhooks, clone: true);
        using var credentials = fixture.Credentials();
        await credentials.Initialize(null, TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Transport.Requests.Count);
        fixture.Clock.Advance(TimeSpan.FromMinutes(56));
        await credentials.GetToken(TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.Transport.Requests.Count);
        var renewed = await fixture.InvokeGitHelper("owner/repository.git");
        Assert.Equal(0, renewed.Exit);
        Assert.Equal("username=x-access-token\npassword=opaque-token-2\n\n", renewed.Output);
        var other = await fixture.InvokeGitHelper("owner/other.git");
        Assert.NotEqual(0, other.Exit);
        Assert.DoesNotContain("opaque-token-", other.Output);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("policyVersion")]
    [InlineData("protocolVersion")]
    [InlineData("permissions")]
    [InlineData("appId")]
    [InlineData("repositoryId")]
    [InlineData("repository")]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("redirect")]
    public async Task Clone_lease_denial_clears_cache_without_retrying_v1(string defect)
    {
        using var fixture = new Fixture(clone: true);
        using var credentials = fixture.Credentials();
        await credentials.Initialize(null, TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(56));
        fixture.Transport.Defect = defect;
        await Assert.ThrowsAsync<InvalidOperationException>(() => credentials.GetToken(TestContext.Current.CancellationToken));
        var denied = await fixture.InvokeGitHelper("owner/repository.git");
        Assert.NotEqual(0, denied.Exit);
        Assert.Equal("quit=true\n\n", denied.Output);
        Assert.Equal(2, fixture.Transport.Requests.Count);
        Assert.All(fixture.Transport.Requests, uri => Assert.Contains("/v2/", uri.AbsolutePath));
    }

    [Theory]
    [InlineData("repository-agent-clone-approval-v2")]
    [InlineData("repository-actions-clone-approval-v3")]
    [InlineData("repository-agent-clone-approval-v4")]
    public async Task Clone_lease_must_preserve_the_enrolled_approval_policy(string policy)
    {
        using var fixture = new Fixture(clone: true, clonePolicy: policy);
        using var credentials = fixture.Credentials();
        await credentials.Initialize(null, TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(56));
        await credentials.GetToken(TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(56));
        fixture.Transport.Defect = "other-clone-policy";
        await Assert.ThrowsAsync<InvalidOperationException>(() => credentials.GetToken(TestContext.Current.CancellationToken));
        var denied = await fixture.InvokeGitHelper("owner/repository.git");
        Assert.NotEqual(0, denied.Exit);
        Assert.Equal("quit=true\n\n", denied.Output);
        Assert.Equal(3, fixture.Transport.Requests.Count);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("policyVersion")]
    [InlineData("protocolVersion")]
    public async Task Clone_file_requires_explicit_matching_family_before_contact(string field)
    {
        using var fixture = new Fixture(clone: true);
        fixture.Enrollment.Remove(field);
        fixture.SaveEnrollment();
        Assert.Throws<InvalidOperationException>(() => fixture.Credentials());
        Assert.Empty(fixture.Transport.Requests);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Settings_consumer_selects_v3_only_with_explicit_repository_settings(bool webhooks, bool clone)
    {
        using var fixture = new Fixture(webhooks, clone, settings: true);
        Assert.Equal(0, await fixture.Start());
        Assert.Equal("/v3/devices/device-1/installation-token", Assert.Single(fixture.Transport.Requests).AbsolutePath);

        // Without the explicit selection the same file is never consumed as a frozen profile.
        fixture.RepositorySettings = false;
        Assert.Equal(1, await fixture.Start());
        Assert.Single(fixture.Transport.Requests);
        Assert.Contains("repository-settings profile requires --repository-settings", fixture.RetainedLog);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Frozen_enrollments_still_refuse_repository_settings_before_broker_contact(bool webhooks, bool clone)
    {
        using var fixture = new Fixture(webhooks, clone) { RepositorySettings = true };
        Assert.Equal(1, await fixture.Start());
        Assert.Empty(fixture.Transport.Requests);
        Assert.Contains("--repository-settings requires a controller enrollment with a repository-settings profile", fixture.RetainedLog);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task A_retired_v3_settings_enrollment_is_refused_before_broker_contact_and_names_re_enrollment(bool clone, bool repositorySettings)
    {
        // Setup v3 kept settings in Actions variables (ADR 0007, 0008). ADR 0012 retires it: its token cannot read Agent variables.
        using var fixture = new Fixture(clone: clone, settings: true) { RepositorySettings = repositorySettings };
        fixture.Enrollment["policyVersion"] = "repository-actions-settings-approval-v3";
        fixture.Enrollment["permissions"]!.AsObject().Remove("agent_variables");
        fixture.Enrollment["permissions"]!["actions_variables"] = "read";
        fixture.SaveEnrollment();

        Assert.Equal(1, await fixture.Start());

        Assert.Empty(fixture.Transport.Requests);
        Assert.Contains("v3 repository-settings enrollment is retired", fixture.RetainedLog);
        Assert.Contains("enroll afresh", fixture.RetainedLog);
    }

    [Theory]
    [InlineData("profile", "polling-clone")]
    [InlineData("policyVersion", "repository-actions-clone-approval-v3")]
    [InlineData("protocolVersion", "2")]
    [InlineData("without-variables", "")]
    [InlineData("variables-write", "write")]
    [InlineData("actions-variables", "read")]
    [InlineData("policyVersion", "repository-agent-clone-approval-v4")]
    public void Settings_file_must_name_its_exact_family_and_grants_before_contact(string field, string value)
    {
        using var fixture = new Fixture(clone: true, settings: true);
        switch (field)
        {
            case "without-variables": fixture.Enrollment["permissions"]!.AsObject().Remove("agent_variables"); break;
            case "variables-write": fixture.Enrollment["permissions"]!["agent_variables"] = value; break;
            case "actions-variables": fixture.Enrollment["permissions"]!["actions_variables"] = value; break;
            case "protocolVersion": fixture.Enrollment[field] = int.Parse(value); break;
            default: fixture.Enrollment[field] = value; break;
        }
        fixture.SaveEnrollment();
        Assert.Throws<InvalidOperationException>(() => fixture.Credentials());
        Assert.Empty(fixture.Transport.Requests);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("policyVersion")]
    [InlineData("frozen-policy")]
    [InlineData("protocolVersion")]
    [InlineData("without-variables")]
    [InlineData("variables-write")]
    [InlineData("actions-variables")]
    [InlineData("permissions")]
    public async Task Settings_lease_must_repeat_the_enrolled_family_and_exact_grants_on_renewal(string defect)
    {
        using var fixture = new Fixture(clone: true, settings: true);
        using var credentials = fixture.Credentials();
        await credentials.Initialize(null, TestContext.Current.CancellationToken);
        var current = await fixture.InvokeGitHelper("owner/repository.git");
        Assert.Equal("username=x-access-token\npassword=opaque-token-1\n\n", current.Output);
        fixture.Clock.Advance(TimeSpan.FromMinutes(56));
        fixture.Transport.Defect = defect;
        await Assert.ThrowsAsync<InvalidOperationException>(() => credentials.GetToken(TestContext.Current.CancellationToken));
        var denied = await fixture.InvokeGitHelper("owner/repository.git");
        Assert.NotEqual(0, denied.Exit);
        Assert.Equal("quit=true\n\n", denied.Output);
        Assert.Equal(2, fixture.Transport.Requests.Count);
        Assert.All(fixture.Transport.Requests, uri => Assert.Contains("/v3/", uri.AbsolutePath));
    }

    internal sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "broker-tests-" + Guid.NewGuid().ToString("N"));
        public string State => Path.Combine(Directory, "state.json");
        public string CredentialDirectory => State + ".github-app";
        public string LegacyTokenFile => Path.Combine(CredentialDirectory, "installation-token.json");
        public string EnrollmentFile => Path.Combine(Directory, "enrollment.json");
        public string Gh => Path.Combine(Directory, "gh-stub");
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
        public JsonObject Enrollment { get; }
        public Transport Transport { get; }
        readonly HttpClient http;
        readonly bool webhooks;
        readonly bool clone;
        readonly string wrapper;

        // The CLI's explicit --repository-settings selection; it defaults to whether the enrolled profile is a repository-settings profile.
        public bool RepositorySettings { get; set; }

        public Fixture(bool webhooks = false, bool clone = false, string clonePolicy = "repository-agent-clone-approval-v2", bool settings = false)
        {
            this.webhooks = webhooks;
            this.clone = clone;
            RepositorySettings = settings;
            System.IO.Directory.CreateDirectory(Directory);
            Enrollment = JsonNode.Parse("""
                {"protocolVersion":1,"brokerOrigin":"https://credentials.example.org","githubHost":"github.com","deviceId":"device-1","deviceSecret":"abcdefghijklmnopqrstuvwxyz_0123456789-ABCDEFG","appId":123,"appSlug":"test-app","installationId":234,"repositoryId":345,"repository":"owner/repository","permissions":{"discussions":"write"}}
                """)!.AsObject();
            if (webhooks) Enrollment["permissions"]!["repository_hooks"] = "write";
            if (settings)
            {
                Enrollment["protocolVersion"] = 3;
                Enrollment["profile"] = (webhooks ? "webhook" : "polling") + (clone ? "-clone" : "") + "-settings";
                Enrollment["policyVersion"] = "repository-agent-settings-approval-v4";
                if (clone) Enrollment["permissions"]!["contents"] = "read";
                Enrollment["permissions"]!["agent_variables"] = "read";
            }
            else if (clone)
            {
                Enrollment["protocolVersion"] = 2;
                Enrollment["profile"] = webhooks ? "webhook-clone" : "polling-clone";
                Enrollment["policyVersion"] = clonePolicy;
                Enrollment["permissions"]!["contents"] = "read";
            }
            SaveEnrollment();
            Transport = new Transport(Clock, Enrollment);
            http = new HttpClient(Transport);
            Viewer("test-app[bot]");
            wrapper = AppGitHubCommand.InstallWrapper(Path.Combine(CredentialDirectory, "bin"), Gh, CredentialDirectory);
        }

        public void SaveEnrollment()
        {
            File.WriteAllText(EnrollmentFile, Enrollment.ToJsonString());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(EnrollmentFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        public void Viewer(string login)
        {
            File.WriteAllText(Gh, "#!/bin/sh\nif [ \"$1\" = api ]; then cat >/dev/null; printf '%s' '{\"data\":{\"viewer\":{\"login\":\"" + login + "\"}}}'; else printf '%s\\n' gh-invoked \"$GH_TOKEN\"; fi\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void SeedCache()
        {
            File.WriteAllText(LegacyTokenFile, "{\"token\":\"prior-token\",\"expiresAt\":\"2099-01-01T00:00:00Z\"}");
            File.WriteAllText(LegacyTokenFile + ".tmp", "prior-temporary-token");
        }

        public HttpClient Http => http;

        public GitHubAppCredentials Credentials() => GitHubAppCredentials.FromBroker(http, "https://credentials.example.org/", EnrollmentFile, "123", "OWNER/Repository", CredentialDirectory,
            new RuntimeProfile(webhooks, clone, RepositorySettings), Clock);

        public Task<int> Start(string? option = null, string? value = null, bool keepFile = false)
        {
            var args = new List<string> { "--repo", "owner/repository", "--app-id", "123", "--state", State, "--broker-url", "https://credentials.example.org", "--enrollment-file", EnrollmentFile, "--check-auth" };
            if (option == "--enrollment-environment" && !keepFile) args.RemoveRange(args.IndexOf("--enrollment-file"), 2);
            if (clone) args.Add("--clone");
            if (RepositorySettings) args.Add("--repository-settings");
            if (webhooks) args.AddRange(["--webhook-url", "https://controller.example.org"]);
            if (option is not null)
            {
                var index = args.IndexOf(option);
                if (index >= 0) args[index + 1] = value!;
                else args.AddRange([option, value!]);
            }
            return BridgeCommand.Run(args.ToArray(), http, Gh);
        }

        public async Task<(int Exit, string Output)> InvokeWrapper()
        {
            var start = new ProcessStartInfo(wrapper) { RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("capture");
            start.Environment["GH_TOKEN"] = "human-token";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            return (process.ExitCode, await output + await error);
        }

        // The real controller Git credential-helper entry, as Git invokes it for an HTTPS repository path.
        public async Task<(int Exit, string Output)> InvokeGitHelper(string path)
        {
            var apphost = Path.Combine(AppContext.BaseDirectory, "RepoAgentController");
            var start = File.Exists(apphost) ? new ProcessStartInfo(apphost) : new ProcessStartInfo("dotnet")
            {
                ArgumentList = { Path.Combine(AppContext.BaseDirectory, "RepoAgentController.dll") }
            };
            foreach (var argument in new[] { "git-helper", CredentialDirectory, "get" }) start.ArgumentList.Add(argument);
            start.RedirectStandardInput = start.RedirectStandardOutput = true;
            using var process = Process.Start(start)!;
            await process.StandardInput.WriteAsync($"protocol=https\nhost=github.com\npath={path}\n\n");
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            return (process.ExitCode, await output);
        }
        public string RetainedLog => string.Concat(System.IO.Directory.EnumerateFiles(State + ".logs").Select(File.ReadAllText));
        public void Dispose() { http.Dispose(); System.IO.Directory.Delete(Directory, true); }
    }

    // Transport fixtures prove local consumer behavior, not provider compatibility or actual GitHub scope.
    internal sealed class Transport(FakeTimeProvider clock, JsonObject enrollment) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string? Defect { get; set; }
        public Stream? Body { get; set; }
        public TaskCompletionSource? Requested { get; set; }
        public Task? ResponseGate { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Requested?.TrySetResult();
            if (ResponseGate is not null) await ResponseGate.WaitAsync(cancellationToken);
            Assert.Equal($"https://credentials.example.org/v{enrollment["protocolVersion"]!.GetValue<int>()}/devices/device-1/installation-token", request.RequestUri!.AbsoluteUri);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Null(request.Content);
            Assert.Equal("application/json", request.Headers.Accept.Single().MediaType);
            Assert.Equal(enrollment["deviceSecret"]!.GetValue<string>(), request.Headers.Authorization!.Parameter);
            var result = enrollment.DeepClone().AsObject();
            foreach (var name in new[] { "brokerOrigin", "githubHost", "deviceId", "deviceSecret" }) result.Remove(name);
            result["token"] = "opaque-token-" + Requests.Count;
            result["expiresAt"] = clock.GetUtcNow().AddHours(1).ToString("O");
            switch (Defect)
            {
                case "appId": case "installationId": case "repositoryId": result[Defect] = 999; break;
                case "protocolVersion": result[Defect] = enrollment["protocolVersion"]!.GetValue<int>() == 1 ? 2 : 1; break;
                case "profile": result[Defect] = "webhook-clone"; break;
                case "policyVersion": result[Defect] = "repository-agent-approval-v2"; break;
                case "other-clone-policy":
                    result["policyVersion"] = result["policyVersion"]!.GetValue<string>() == "repository-agent-clone-approval-v2"
                        ? "repository-actions-clone-approval-v3" : "repository-agent-clone-approval-v2";
                    break;
                case "appSlug": result[Defect] = "other-app"; break;
                case "repository": result[Defect] = "owner/other"; break;
                case "permissions": result["permissions"]!["contents"] = "write"; break;
                case "frozen-policy": result["policyVersion"] = "repository-actions-clone-approval-v3"; break;
                case "without-variables": result["permissions"]!.AsObject().Remove("agent_variables"); break;
                case "variables-write": result["permissions"]!["agent_variables"] = "write"; break;
                case "actions-variables": result["permissions"]!["actions_variables"] = "read"; break;
                case "permission-level": result["permissions"]!["discussions"] = "read"; break;
                case "token": result["token"] = ""; break;
                case "whitespace-token": result["token"] = "token with space"; break;
                case "expiresAt": result["expiresAt"] = "tomorrow"; break;
                case "near-expiry": result["expiresAt"] = clock.GetUtcNow().AddMinutes(5).ToString("O"); break;
                case "overlong-expiry": result["expiresAt"] = clock.GetUtcNow().AddMinutes(66).ToString("O"); break;
                case "missing": result.Remove("repositoryId"); break;
                case "unknown": result["extra"] = "DO-NOT-LOG"; break;
                case "wrong-type": result["appId"] = "123"; break;
            }
            var body = result.ToJsonString();
            if (Defect == "duplicate") body = "{\"appId\":123," + body[1..];
            if (Defect == "malformed") body = "{DO-NOT-LOG";
            if (Defect == "oversized") body = new string('x', 65537);
            var status = Defect == "redirect" ? HttpStatusCode.TemporaryRedirect : Status;
            if (Defect == "transport") throw new HttpRequestException("transport unavailable");
            return new HttpResponseMessage(status) { Content = Body is not null ? new StreamContent(Body) : new StringContent(status == HttpStatusCode.OK ? body : "DO-NOT-LOG") };
        }
    }

    internal sealed class StalledBody : Stream
    {
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var pending = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            Reading.TrySetResult();
            await pending;
            return 0;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
