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
    public async Task Real_startup_accepts_the_enrolled_bot_without_a_PEM(bool webhooks)
    {
        using var fixture = new Fixture(webhooks);
        Assert.Equal(0, await fixture.Start());
        var result = await fixture.InvokeWrapper();
        Assert.Equal(0, result.Exit);
        Assert.Contains("opaque-token-1", result.Output);
        Assert.DoesNotContain("human-token", result.Output);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fixture.TokenFile));
    }

    [Fact]
    public async Task Wrong_actual_viewer_removes_prior_and_temporary_credentials_and_disables_existing_wrapper()
    {
        using var fixture = new Fixture();
        fixture.SeedCache();
        fixture.Viewer("human-user");
        Assert.Equal(1, await fixture.Start());
        Assert.False(File.Exists(fixture.TokenFile));
        Assert.False(File.Exists(fixture.TokenFile + ".tmp"));
        var result = await fixture.InvokeWrapper();
        Assert.NotEqual(0, result.Exit);
        Assert.DoesNotContain("gh-invoked", result.Output);
    }

    [Fact]
    public async Task Losing_the_state_lock_preserves_the_active_owners_cache()
    {
        using var fixture = new Fixture();
        fixture.SeedCache();
        using var owner = new FileStream(fixture.State + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(1, await fixture.Start());
        Assert.Contains("prior-token", File.ReadAllText(fixture.TokenFile));
        Assert.Empty(fixture.Transport.Requests);
    }

    [Fact]
    public async Task Invalid_state_after_lock_acquisition_clears_previous_credentials()
    {
        using var fixture = new Fixture();
        fixture.SeedCache();
        File.WriteAllText(fixture.State, "{}");
        Assert.Equal(1, await fixture.Start());
        Assert.False(File.Exists(fixture.TokenFile));
    }

    [Fact]
    public async Task Existing_wrapper_observes_renewal_then_fails_closed_and_recovers_after_authorization()
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
        Assert.False(File.Exists(fixture.TokenFile));
        var failed = await fixture.InvokeWrapper();
        Assert.NotEqual(0, failed.Exit);
        Assert.DoesNotContain("gh-invoked", failed.Output);
        fixture.Transport.Status = HttpStatusCode.OK;
        await credentials.GetToken(TestContext.Current.CancellationToken);
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
        Assert.False(File.Exists(fixture.TokenFile));
        var result = await fixture.InvokeWrapper();
        Assert.NotEqual(0, result.Exit);
        Assert.DoesNotContain("gh-invoked", result.Output);
    }

    [Fact]
    public async Task Stalled_broker_body_times_out_clears_credentials_and_allows_retry()
    {
        using var fixture = new Fixture();
        using var credentials = fixture.Credentials();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await credentials.Initialize(null, cancellation.Token);
        Assert.Contains("opaque-token-1", (await fixture.InvokeWrapper()).Output);
        File.WriteAllText(fixture.TokenFile + ".tmp", "prior-temporary-token");
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
            Assert.False(File.Exists(fixture.TokenFile));
            Assert.False(File.Exists(fixture.TokenFile + ".tmp"));
            var failed = await fixture.InvokeWrapper();
            Assert.NotEqual(0, failed.Exit);
            Assert.DoesNotContain("gh-invoked", failed.Output);
            Assert.DoesNotContain("human-token", failed.Output);

            fixture.Transport.Body = null;
            await credentials.GetToken(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
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
        Assert.False(File.Exists(fixture.TokenFile));
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

    [Fact]
    public async Task Shared_enrollment_file_is_rejected_before_contacting_the_broker()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        File.SetUnixFileMode(fixture.EnrollmentFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
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
    public async Task Clone_consumer_selects_v2_and_renewal_preserves_exact_profile(bool webhooks)
    {
        using var fixture = new Fixture(webhooks, clone: true);
        using var credentials = fixture.Credentials();
        await credentials.Initialize(null, TestContext.Current.CancellationToken);
        Assert.Equal(1, fixture.Transport.Requests.Count);
        fixture.Clock.Advance(TimeSpan.FromMinutes(56));
        await credentials.GetToken(TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.Transport.Requests.Count);
        var cached = JsonNode.Parse(File.ReadAllText(fixture.TokenFile))!;
        Assert.Equal(webhooks ? "webhook-clone" : "polling-clone", cached["profile"]!.GetValue<string>());
        Assert.Equal("owner/repository", cached["repository"]!.GetValue<string>());
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
        Assert.False(File.Exists(fixture.TokenFile));
        Assert.Equal(2, fixture.Transport.Requests.Count);
        Assert.All(fixture.Transport.Requests, uri => Assert.Contains("/v2/", uri.AbsolutePath));
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

    internal sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "broker-tests-" + Guid.NewGuid().ToString("N"));
        public string State => Path.Combine(Directory, "state.json");
        public string CredentialDirectory => State + ".github-app";
        public string TokenFile => Path.Combine(CredentialDirectory, "installation-token.json");
        public string EnrollmentFile => Path.Combine(Directory, "enrollment.json");
        public string Gh => Path.Combine(Directory, "gh-stub");
        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
        public JsonObject Enrollment { get; }
        public Transport Transport { get; }
        readonly HttpClient http;
        readonly bool webhooks;
        readonly bool clone;
        readonly string wrapper;

        public Fixture(bool webhooks = false, bool clone = false)
        {
            this.webhooks = webhooks;
            this.clone = clone;
            System.IO.Directory.CreateDirectory(Directory);
            Enrollment = JsonNode.Parse("""
                {"protocolVersion":1,"brokerOrigin":"https://credentials.example.org","githubHost":"github.com","deviceId":"device-1","deviceSecret":"abcdefghijklmnopqrstuvwxyz_0123456789-ABCDEFG","appId":123,"appSlug":"test-app","installationId":234,"repositoryId":345,"repository":"owner/repository","permissions":{"discussions":"write"}}
                """)!.AsObject();
            if (webhooks) Enrollment["permissions"]!["repository_hooks"] = "write";
            if (clone)
            {
                Enrollment["protocolVersion"] = 2;
                Enrollment["profile"] = webhooks ? "webhook-clone" : "polling-clone";
                Enrollment["policyVersion"] = "repository-agent-clone-approval-v2";
                Enrollment["permissions"]!["contents"] = "read";
            }
            SaveEnrollment();
            Transport = new Transport(Clock, Enrollment);
            http = new HttpClient(Transport);
            Viewer("test-app[bot]");
            wrapper = AppGitHubCommand.InstallWrapper(Path.Combine(CredentialDirectory, "bin"), Gh, TokenFile);
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
            File.WriteAllText(TokenFile, "{\"token\":\"prior-token\",\"expiresAt\":\"2099-01-01T00:00:00Z\"}");
            File.WriteAllText(TokenFile + ".tmp", "prior-temporary-token");
        }

        public HttpClient Http => http;

        public GitHubAppCredentials Credentials() => GitHubAppCredentials.FromBroker(http, "https://credentials.example.org/", EnrollmentFile, "123", "OWNER/Repository", CredentialDirectory, Clock, webhooks, clone);

        public Task<int> Start(string? option = null, string? value = null)
        {
            var args = new List<string> { "--repo", "owner/repository", "--app-id", "123", "--state", State, "--broker-url", "https://credentials.example.org", "--enrollment-file", EnrollmentFile, "--check-auth" };
            if (clone) args.Add("--clone");
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
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            Requested?.TrySetResult();
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
                case "appSlug": result[Defect] = "other-app"; break;
                case "repository": result[Defect] = "owner/other"; break;
                case "permissions": result["permissions"]!["contents"] = "write"; break;
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
            return Defect == "transport" ? Task.FromException<HttpResponseMessage>(new HttpRequestException("transport unavailable"))
                : Task.FromResult(new HttpResponseMessage(status) { Content = Body is not null ? new StreamContent(Body) : new StringContent(status == HttpStatusCode.OK ? body : "DO-NOT-LOG") });
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
