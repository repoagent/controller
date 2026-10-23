using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class InstallationTokenTests
{
    [Fact]
    public async Task Acknowledged_device_receives_exact_v1_token_with_durable_encrypted_accounting()
    {
        await using var f = await IssuanceFixture.CreateAsync();
        await using var host = f.Storage.CreateHost().WithWebHostBuilder(builder => builder.ConfigureServices(s => s.AddSingleton(f.Issuer())));
        using var client = host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/devices/{f.Device.DeviceId}/installation-token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", f.Device.Secret);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        Assert.Equal(9, body.EnumerateObject().Count());
        Assert.Equal(1, body.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(IssuancePolicy.AppId, body.GetProperty("appId").GetInt64());
        Assert.Equal(123456, body.GetProperty("repositoryId").GetInt64());
        Assert.Equal("runtime-token-2", body.GetProperty("token").GetString());
        Assert.Equal("write", body.GetProperty("permissions").GetProperty("discussions").GetString());
        Assert.Single(body.GetProperty("permissions").EnumerateObject());
        var attempts = await f.AttemptsAsync();
        Assert.Equal(2, attempts.Count);
        Assert.Equal("released", Assert.Single(attempts, a => a.GetString("Purpose") == "runtime").GetString("State"));
        Assert.Equal("known", Assert.Single(attempts, a => a.GetString("Purpose") == "verification").GetString("State"));
        Assert.DoesNotContain("runtime-token", JsonSerializer.Serialize(attempts));
        Assert.DoesNotContain("verifier-token", JsonSerializer.Serialize(attempts));
        Assert.Equal(8, f.GitHub.Reads);
        Assert.Equal(2, f.Signing.Calls);
        Assert.Equal(3, (await f.GateAsync()).GetInt64("OutstandingTokenCount"));
        Assert.Equal(f.Clock.GetUtcNow().AddDays(90), (await f.DeviceAsync()).GetDateTimeOffset("ExpiresAt"));
    }

    [Fact]
    public async Task Restart_recovers_verifier_but_reads_fresh_approval_for_each_issuance()
    {
        await using var f = await IssuanceFixture.CreateAsync();
        Assert.Equal(200, (await f.IssueAsync(TestContext.Current.CancellationToken)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(200, (await f.IssueAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, f.GitHub.VerifierMints);
        Assert.Equal(2, f.GitHub.Mints);
        Assert.Equal(16, f.GitHub.Reads);
        Assert.Equal(2, (await f.AttemptsAsync()).Count);
    }

    [Theory]
    [InlineData("State", "revoked")]
    [InlineData("Epoch", "old-epoch")]
    [InlineData("Profile", "contents")]
    [InlineData("Marker", "copied-unbound-marker")]
    public async Task Invalid_grant_denies_before_signing_or_GitHub_work(string property, string value)
    {
        await using var f = await IssuanceFixture.CreateAsync();
        var device = await f.DeviceAsync();
        device[property] = value;
        await f.Storage.Table.UpdateEntityAsync(device, device.ETag, TableUpdateMode.Replace, TestContext.Current.CancellationToken);
        Assert.NotEqual(200, (await f.IssueAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(0, f.GitHub.Mints);
        Assert.Equal(0, f.Signing.Calls);
    }

    [Fact]
    public async Task Changed_marker_closes_monotonically_and_old_marker_restoration_cannot_revive()
    {
        await using var f = await IssuanceFixture.CreateAsync();
        var original = f.GitHub.Marker;
        f.GitHub.Marker = "changed";
        Assert.NotEqual(200, (await f.IssueAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal("closed", (await f.GateAsync()).GetString("State"));
        Assert.Equal("revoked", (await f.DeviceAsync()).GetString("State"));
        f.GitHub.Marker = original;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.NotEqual(200, (await f.IssueAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, f.GitHub.Mints);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(302)]
    public async Task Unverifiable_approval_denies_without_fabricating_withdrawal(int status)
    {
        await using var f = await IssuanceFixture.CreateAsync();
        f.GitHub.Before = request => Task.FromResult<HttpResponseMessage?>(request.RequestUri!.AbsolutePath.Contains("/variables/", StringComparison.Ordinal)
            ? new((HttpStatusCode)status) : null);
        Assert.NotEqual(200, (await f.IssueAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal("active", (await f.GateAsync()).GetString("State"));
        Assert.False((await f.DeviceAsync()).ContainsKey("LastActivityAt"));
        Assert.Equal(1, f.GitHub.Mints);
    }

    [Fact]
    public async Task Revoke_after_runtime_mint_prevents_release_and_revokes_known_token()
    {
        await using var f = await IssuanceFixture.CreateAsync();
        f.GitHub.Before = async request =>
        {
            if (request.Method == HttpMethod.Post && f.GitHub.Mints == 2)
                Assert.Equal(204, await new DeviceRevocation(f.Storage.Table, f.Options, f.Clock).RevokeAsync(f.Device.DeviceId, f.Device.Secret, TestContext.Current.CancellationToken));
            return null;
        };
        Assert.NotEqual(200, (await f.IssueAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal("closed", (await f.GateAsync()).GetString("State"));
        Assert.False((await f.DeviceAsync()).ContainsKey("LastActivityAt"));
        Assert.Equal(1, f.GitHub.Revokes);
        Assert.Equal("settled", Assert.Single(await f.AttemptsAsync(), a => a.GetString("Purpose") == "runtime").GetString("State"));
    }

    [Fact]
    public async Task Release_before_revoke_remains_accounted()
    {
        await using var f = await IssuanceFixture.CreateAsync();
        Assert.Equal(200, (await f.IssueAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(204, await new DeviceRevocation(f.Storage.Table, f.Options, f.Clock).RevokeAsync(f.Device.DeviceId, f.Device.Secret, TestContext.Current.CancellationToken));
        Assert.Equal("released", Assert.Single(await f.AttemptsAsync(), a => a.GetString("Purpose") == "runtime").GetString("State"));
        Assert.Equal(3, (await f.GateAsync()).GetInt64("OutstandingTokenCount"));
    }

    [Fact]
    public async Task Lost_verifier_mint_response_quarantines_acquisition_across_restart_and_JWT_expiry()
    {
        await using var f = await IssuanceFixture.CreateAsync();
        f.GitHub.Before = request => request.Method == HttpMethod.Post
            ? throw new HttpRequestException("secret-bearing-upstream-response") : Task.FromResult<HttpResponseMessage?>(null);
        Assert.Equal(503, (await f.IssueAsync(TestContext.Current.CancellationToken)).Status);
        f.Clock.Advance(TimeSpan.FromMinutes(10));
        f.GitHub.Before = null;
        Assert.Equal(503, (await f.IssueAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, f.GitHub.Mints);
        Assert.Equal("sending", Assert.Single(await f.AttemptsAsync()).GetString("State"));
    }

    [Fact]
    public async Task Caller_disconnect_after_send_does_not_cancel_accounting()
    {
        await using var f = await IssuanceFixture.CreateAsync();
        using var caller = new CancellationTokenSource();
        f.GitHub.Before = request =>
        {
            if (request.Method == HttpMethod.Post && f.GitHub.Mints == 2) caller.Cancel();
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        Assert.Equal(200, (await f.IssueAsync(caller.Token)).Status);
        Assert.Equal("released", Assert.Single(await f.AttemptsAsync(), a => a.GetString("Purpose") == "runtime").GetString("State"));
        Assert.Equal(2, f.GitHub.Mints);
    }

    [Fact]
    public async Task Delayed_signing_cannot_refresh_reserved_JWT_or_send_after_deadline()
    {
        await using var f = await IssuanceFixture.CreateAsync();
        f.Signing.Before = () => { f.Clock.Advance(TimeSpan.FromSeconds(121)); return Task.CompletedTask; };
        Assert.Equal(503, (await f.IssueAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(0, f.GitHub.Mints);
        var attempt = Assert.Single(await f.AttemptsAsync());
        Assert.Equal(attempt.GetDateTimeOffset("ReservedAt")!.Value.AddSeconds(120), attempt.GetDateTimeOffset("JwtExpiresAt"));
        Assert.Equal("reserved", attempt.GetString("State"));
    }

    [Fact]
    public async Task Stale_admission_observation_denies_and_revokes_minted_authority()
    {
        await using var f = await IssuanceFixture.CreateAsync();
        f.GitHub.Before = request =>
        {
            if (request.Method == HttpMethod.Post && f.GitHub.Mints == 2) f.Clock.Advance(TimeSpan.FromSeconds(11));
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        Assert.Equal(503, (await f.IssueAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, f.GitHub.Revokes);
        Assert.False((await f.DeviceAsync()).ContainsKey("LastActivityAt"));
    }

    [Fact]
    public async Task Excess_runtime_scope_is_protected_then_revoked_without_release()
    {
        await using var f = await IssuanceFixture.CreateAsync();
        f.GitHub.Before = request => Task.FromResult<HttpResponseMessage?>(request.Method == HttpMethod.Post && f.GitHub.Mints == 2
            ? GitHubTransport.Json(new { token = "excess-token", expires_at = DateTimeOffset.UtcNow.AddHours(1),
                repository_selection = "selected", permissions = new { discussions = "write", contents = "read" },
                repositories = new[] { new { id = 123456, full_name = "owner/project" } } }, HttpStatusCode.Created) : null);
        Assert.Equal(503, (await f.IssueAsync(TestContext.Current.CancellationToken)).Status);
        Assert.Equal(1, f.GitHub.Revokes);
        Assert.Equal("settled", Assert.Single(await f.AttemptsAsync(), a => a.GetString("Purpose") == "runtime").GetString("State"));
    }
}
