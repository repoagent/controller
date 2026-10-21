using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RepoAgentController;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class AppAuthenticationTests
{
    [Theory]
    [InlineData("Bot", "discussion-agent-bridge", true)]
    [InlineData("Bot", "DISCUSSION-AGENT-BRIDGE", true)]
    [InlineData("Bot", "discussion-agent-bridge[bot]", true)]
    [InlineData("Bot", "DISCUSSION-AGENT-BRIDGE[BOT]", true)]
    [InlineData("User", "discussion-agent-bridge", false)]
    [InlineData("Bot", "another-app", false)]
    [InlineData("Bot", "discussion-agent-bridge-extra", false)]
    [InlineData(null, "discussion-agent-bridge", false)]
    [InlineData(null, null, false)]
    public async Task App_actor_identity_recognizes_the_observed_Bot_login_without_claiming_human_or_other_App_actors(
        string? actorType, string? login, bool expected)
    {
        using var fixture = new AppFixture();
        using var credentials = fixture.Credentials();
        await credentials.Initialize(null, TestContext.Current.CancellationToken);
        // GitHub returned this App's comment author as Bot + bare slug, while viewer has [bot].
        var actor = login is null ? null : JsonNode.Parse(JsonSerializer.Serialize(new { __typename = actorType, login }));

        Assert.Equal("discussion-agent-bridge[bot]", credentials.AgentLogin);
        Assert.Equal(expected, credentials.IsAgentActor(actor));
    }

    [Fact]
    public async Task App_authentication_signs_requests_and_limits_the_token_to_the_selected_repository()
    {
        using var fixture = new AppFixture();
        using var credentials = fixture.Credentials();
        await credentials.Initialize(null, TestContext.Current.CancellationToken);

        Assert.Equal("discussion-agent-bridge[bot]", credentials.AgentLogin);
        var request = Assert.Single(fixture.Provider.TokenRequests);
        Assert.Equal("gh-discussion-agent-bridge", Assert.Single(request["repositories"]!.AsArray())!.GetValue<string>());
        Assert.Equal("write", request["permissions"]!["discussions"]!.GetValue<string>());
        Assert.Single(request["permissions"]!.AsObject());
        Assert.Equal("installation-token-1", await credentials.GetToken(TestContext.Current.CancellationToken));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(credentials.TokenFile));
    }

    [Fact]
    public async Task Webhook_mode_preserves_approved_hook_permission_and_repository_scope_across_renewal()
    {
        using var fixture = new AppFixture();
        fixture.Provider.WebhookPermission = "write";
        using var credentials = fixture.Credentials(repositoryWebhooks: true);
        await credentials.Initialize(null, TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(56));
        await credentials.GetToken(TestContext.Current.CancellationToken);

        Assert.Equal(2, fixture.Provider.TokenRequests.Count);
        Assert.All(fixture.Provider.TokenRequests, request =>
        {
            Assert.Equal("gh-discussion-agent-bridge", Assert.Single(request["repositories"]!.AsArray())!.GetValue<string>());
            Assert.Equal("write", request["permissions"]!["discussions"]!.GetValue<string>());
            Assert.Equal("write", request["permissions"]!["repository_hooks"]!.GetValue<string>());
            Assert.Equal(2, request["permissions"]!.AsObject().Count);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("read")]
    public async Task Webhook_mode_rejects_an_unapproved_hook_grant_before_minting_a_token(string? permission)
    {
        using var fixture = new AppFixture();
        fixture.Provider.WebhookPermission = permission;
        using var credentials = fixture.Credentials(repositoryWebhooks: true);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => credentials.Initialize(null, TestContext.Current.CancellationToken));
        Assert.Contains("Webhooks: read and write", error.Message);
        Assert.Empty(fixture.Provider.TokenRequests);
        Assert.False(File.Exists(credentials.TokenFile));
    }

    [Fact]
    public async Task The_same_native_command_wrapper_uses_a_refreshed_token_after_the_old_one_expires()
    {
        using var fixture = new AppFixture();
        using var credentials = fixture.Credentials();
        await credentials.Initialize(null, TestContext.Current.CancellationToken);
        var wrapper = AppGitHubCommand.InstallWrapper(Path.Combine(fixture.Directory, "bin with 'quotes' and spaces"), fixture.Gh, credentials.TokenFile);

        var first = await Run(wrapper, ["api", "graphql", "--input", "a file with spaces.json"]);
        Assert.Equal(0, first.Exit);
        Assert.Contains("installation-token-1", first.Output);
        Assert.Contains("a file with spaces.json", first.Output);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var refresh = credentials.RefreshUntilCancelled(lifetime.Token);
        try
        {
            await fixture.Clock.WaitForDelay();
            fixture.Clock.Advance(TimeSpan.FromMinutes(56));
            await fixture.Clock.WaitForDelay();
            fixture.Clock.Advance(TimeSpan.FromMinutes(5));
            await fixture.Clock.WaitForDelay();
            var second = await Run(wrapper, ["api", "graphql"]);
            Assert.Equal(0, second.Exit);
            Assert.Contains("installation-token-2", second.Output);
            Assert.DoesNotContain("installation-token-1", second.Output);
            Assert.Contains("github.com", second.Output);
            Assert.DoesNotContain("otherhost/owner/repo", second.Output);
            Assert.Contains("GH_REPO=\n", second.Output);
            Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/.config/gh\n", second.Output);
        }
        finally
        {
            await lifetime.CancelAsync();
            await refresh.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Scheduled_refresh_failure_blocks_native_commands_until_the_next_scheduled_recovery()
    {
        using var fixture = new AppFixture();
        using var credentials = fixture.Credentials();
        await credentials.Initialize(null, TestContext.Current.CancellationToken);
        var wrapper = AppGitHubCommand.InstallWrapper(Path.Combine(fixture.Directory, "bin"), fixture.Gh, credentials.TokenFile);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var refresh = credentials.RefreshUntilCancelled(lifetime.Token);
        try
        {
            await fixture.Clock.WaitForDelay();
            fixture.Provider.RejectRefresh = true;
            fixture.Clock.Advance(TimeSpan.FromMinutes(56));
            await fixture.Clock.WaitForDelay();

            var blocked = await Run(wrapper, ["api", "user"], "ambient-human-token");
            Assert.NotEqual(0, blocked.Exit);
            Assert.DoesNotContain("gh-invoked", blocked.Output);
            Assert.False(File.Exists(credentials.TokenFile));

            fixture.Provider.RejectRefresh = false;
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            await fixture.Clock.WaitForDelay();
            var recovered = await Run(wrapper, ["api", "user"]);
            Assert.Equal(0, recovered.Exit);
            Assert.Contains("installation-token-3", recovered.Output);
            Assert.DoesNotContain("installation-token-1", recovered.Output);
        }
        finally
        {
            await lifetime.CancelAsync();
            await refresh.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Renewal_happens_before_expiry_and_a_failed_refresh_disables_native_publication()
    {
        using var fixture = new AppFixture();
        using var credentials = fixture.Credentials();
        await credentials.Initialize(null, TestContext.Current.CancellationToken);
        var wrapper = AppGitHubCommand.InstallWrapper(Path.Combine(fixture.Directory, "bin"), fixture.Gh, credentials.TokenFile);
        fixture.Clock.Advance(TimeSpan.FromMinutes(56));
        fixture.Provider.RejectRefresh = true;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => credentials.GetToken(TestContext.Current.CancellationToken));
        Assert.Contains("HTTP 401", error.Message);
        Assert.False(File.Exists(credentials.TokenFile));
        var result = await Run(wrapper, ["api", "user"]);
        Assert.NotEqual(0, result.Exit);
        Assert.DoesNotContain("installation-token-1", result.Output);
        Assert.DoesNotContain("gh-invoked", result.Output);
    }

    [Fact]
    public async Task An_expired_cache_cannot_launch_gh_with_ambient_user_credentials()
    {
        using var fixture = new AppFixture();
        var tokenFile = Path.Combine(fixture.Directory, "expired.json");
        await File.WriteAllTextAsync(tokenFile, JsonSerializer.Serialize(new { token = "expired-app-token", expiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }), TestContext.Current.CancellationToken);
        var wrapper = AppGitHubCommand.InstallWrapper(Path.Combine(fixture.Directory, "bin"), fixture.Gh, tokenFile);
        var result = await Run(wrapper, ["api", "user"], "ambient-human-token");
        Assert.NotEqual(0, result.Exit);
        Assert.DoesNotContain("gh-invoked", result.Output);
        Assert.DoesNotContain("ambient-human-token", result.Output);
        Assert.DoesNotContain("expired-app-token", result.Output);
    }

    [Fact]
    public async Task An_unexpected_installation_is_rejected_before_minting_a_token()
    {
        using var fixture = new AppFixture();
        using var credentials = fixture.Credentials();
        await Assert.ThrowsAsync<InvalidOperationException>(() => credentials.Initialize(123, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Provider.TokenRequests);
        Assert.False(File.Exists(credentials.TokenFile));
    }

    [Fact]
    public async Task An_installation_without_Discussions_write_permission_cannot_start()
    {
        using var fixture = new AppFixture();
        fixture.Provider.DiscussionPermission = "read";
        using var credentials = fixture.Credentials();
        await Assert.ThrowsAsync<InvalidOperationException>(() => credentials.Initialize(null, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Provider.TokenRequests);
    }

    [Fact]
    public async Task A_failed_provider_response_does_not_leak_its_body_or_fall_back_to_an_old_cache()
    {
        using var fixture = new AppFixture();
        using var credentials = fixture.Credentials();
        fixture.Provider.RejectRefresh = true;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => credentials.Initialize(null, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("DO-NOT-LOG", error.ToString());
        Assert.False(File.Exists(credentials.TokenFile));
    }

    [Fact]
    public void Missing_signing_key_invalidates_a_previous_process_credential()
    {
        using var fixture = new AppFixture();
        var directory = Path.Combine(fixture.Directory, "credentials");
        Directory.CreateDirectory(directory);
        var tokenFile = Path.Combine(directory, "installation-token.json");
        File.WriteAllText(tokenFile, JsonSerializer.Serialize(new { token = "old-process-token", expiresAt = DateTimeOffset.UtcNow.AddHours(1) }));
        using var http = new HttpClient();
        Assert.Throws<FileNotFoundException>(() => new GitHubAppCredentials(http, "some-app", Path.Combine(directory, "missing.pem"), "egil/gh-discussion-agent-bridge", directory));
        Assert.False(File.Exists(tokenFile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_clone_binds_immutable_repository_and_exact_scope_on_renewal(bool webhooks)
    {
        using var fixture = new AppFixture();
        fixture.Provider.WebhookPermission = webhooks ? "write" : null;
        using var credentials = fixture.Credentials(repositoryWebhooks: webhooks, clone: true);
        await credentials.Initialize(null, TestContext.Current.CancellationToken);
        fixture.Clock.Advance(TimeSpan.FromMinutes(56));
        await credentials.GetToken(TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.Provider.TokenRequests.Count);
        var first = fixture.Provider.TokenRequests[0];
        Assert.Equal("gh-discussion-agent-bridge", Assert.Single(first["repositories"]!.AsArray())!.GetValue<string>());
        var next = fixture.Provider.TokenRequests[1];
        Assert.False(next.AsObject().ContainsKey("repositories"));
        Assert.Equal(1234, Assert.Single(next["repository_ids"]!.AsArray())!.GetValue<long>());
        foreach (var request in fixture.Provider.TokenRequests)
        {
            Assert.Equal("read", request["permissions"]!["contents"]!.GetValue<string>());
            Assert.Equal(webhooks ? 3 : 2, request["permissions"]!.AsObject().Count);
        }
    }

    [Theory]
    [InlineData("multiple", false)]
    [InlineData("name", false)]
    [InlineData("contents-write", false)]
    [InlineData("extra-permission", false)]
    [InlineData("id", true)]
    [InlineData("multiple", true)]
    [InlineData("name", true)]
    [InlineData("contents-write", true)]
    [InlineData("extra-permission", true)]
    public async Task Direct_clone_never_publishes_a_provider_scope_mismatch(string defect, bool renewal)
    {
        using var fixture = new AppFixture();
        using var credentials = fixture.Credentials(clone: true);
        if (renewal)
        {
            await credentials.Initialize(null, TestContext.Current.CancellationToken);
            fixture.Clock.Advance(TimeSpan.FromMinutes(56));
        }
        fixture.Provider.CloneScopeDefect = defect;
        await Assert.ThrowsAsync<InvalidOperationException>(() => renewal
            ? credentials.GetToken(TestContext.Current.CancellationToken)
            : credentials.Initialize(null, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(credentials.TokenFile));
    }

    static async Task<(int Exit, string Output)> Run(string command, string[] arguments, string? ambientToken = null)
    {
        var start = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (ambientToken is not null) start.Environment["GH_TOKEN"] = ambientToken;
        start.Environment["GH_REPO"] = "otherhost/owner/repo";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        return (process.ExitCode, await output + await error);
    }

    sealed class AppFixture : IDisposable
    {
        readonly RSA key = RSA.Create(2048);
        readonly HttpClient http;
        readonly string privateKey;
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "bridge-app-" + Guid.NewGuid().ToString("N"));
        public Clock Clock { get; } = new();
        public Provider Provider { get; }
        public string Gh { get; }

        public AppFixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            privateKey = Path.Combine(Directory, "test-key.pem");
            File.WriteAllText(privateKey, key.ExportRSAPrivateKeyPem());
            Provider = new Provider(key, Clock);
            http = new HttpClient(Provider);
            Gh = Path.Combine(Directory, "gh-capture");
            File.WriteAllText(Gh, "#!/bin/sh\nprintf '%s\\n' gh-invoked \"$GH_TOKEN\" \"$GH_CONFIG_DIR\" \"$GH_HOST\" \"GH_REPO=$GH_REPO\" \"$@\"\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public GitHubAppCredentials Credentials(bool repositoryWebhooks = false, bool clone = false)
        {
            Provider.CloneScope = clone;
            return new(http, "Iv-test-client", privateKey, "egil/gh-discussion-agent-bridge", Path.Combine(Directory, "credentials"), Clock, repositoryWebhooks, clone);
        }
        public void Dispose() { http.Dispose(); key.Dispose(); System.IO.Directory.Delete(Directory, true); }
    }

    sealed class Clock : TimeProvider
    {
        readonly FakeTimeProvider time = new(DateTimeOffset.UtcNow);
        readonly SemaphoreSlim delays = new(0);
        public override DateTimeOffset GetUtcNow() => time.GetUtcNow();
        public override long GetTimestamp() => time.GetTimestamp();
        public override long TimestampFrequency => time.TimestampFrequency;
        public void Advance(TimeSpan duration) => time.Advance(duration);

        // Observe registration, not wall time: the next delay means due work has finished.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = time.CreateTimer(callback, state, dueTime, period);
            delays.Release();
            return timer;
        }

        public Task WaitForDelay() => delays.WaitAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
    }

    // An external transport stub for deterministic credential/error tests, not GitHub compatibility evidence.
    sealed class Provider(RSA key, Clock clock) : HttpMessageHandler
    {
        public List<JsonNode> TokenRequests { get; } = [];
        public bool RejectRefresh { get; set; }
        public bool CloneScope { get; set; }
        public string? CloneScopeDefect { get; set; }
        public string DiscussionPermission { get; set; } = "write";
        public string? WebhookPermission { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            var parts = request.Headers.Authorization!.Parameter!.Split('.');
            static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
            Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            var claims = JsonNode.Parse(Decode(parts[1]))!;
            Assert.Equal("Iv-test-client", claims["iss"]!.GetValue<string>());
            Assert.True(claims["iat"]!.GetValue<long>() < clock.GetUtcNow().ToUnixTimeSeconds());
            Assert.InRange(claims["exp"]!.GetValue<long>(), clock.GetUtcNow().ToUnixTimeSeconds() + 1, clock.GetUtcNow().ToUnixTimeSeconds() + 600);
            object result;
            switch (request.RequestUri.AbsolutePath)
            {
                case "/app": result = new { id = 5387625, slug = "discussion-agent-bridge" }; break;
                case "/repos/egil/gh-discussion-agent-bridge/installation":
                    result = new { id = 42, app_id = 5387625, permissions = new { discussions = DiscussionPermission, repository_hooks = WebhookPermission, contents = "read" } }; break;
                case "/app/installations/42/access_tokens":
                    TokenRequests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!);
                    if (RejectRefresh) return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("DO-NOT-LOG bearer-body") };
                    if (CloneScope)
                    {
                        var scope = TokenRequests[^1]["permissions"]!.DeepClone().AsObject();
                        var repos = new JsonArray(JsonSerializer.SerializeToNode(new { id = 1234, full_name = "egil/gh-discussion-agent-bridge" }));
                        if (CloneScopeDefect == "multiple") repos.Add(repos[0]!.DeepClone());
                        if (CloneScopeDefect == "id") repos[0]!["id"] = 9876;
                        if (CloneScopeDefect == "name") repos[0]!["full_name"] = "other/repository";
                        if (CloneScopeDefect == "contents-write") scope["contents"] = "write";
                        if (CloneScopeDefect == "extra-permission") scope["actions"] = "read";
                        result = new { token = "installation-token-" + TokenRequests.Count,
                            expires_at = clock.GetUtcNow().AddHours(1), repository_selection = "selected", permissions = scope, repositories = repos };
                    }
                    else result = new { token = "installation-token-" + TokenRequests.Count, expires_at = clock.GetUtcNow().AddHours(1) };
                    break;
                default: throw new InvalidOperationException("Unexpected authentication request");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json") };
        }
    }
}
