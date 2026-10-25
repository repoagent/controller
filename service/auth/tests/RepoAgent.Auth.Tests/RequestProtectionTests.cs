using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace RepoAgent.Auth.Tests;

// The host's request limits and credential grammar, over an enrolled controller in the in-memory broker state. "Before
// storage" means no call reaches the broker state port.
public sealed class RequestProtectionTests
{
    private readonly IssuanceFixture fixture = IssuanceFixture.Create();
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static async Task<HttpResponseMessage> Send(HttpClient client, EnrolledDevice device)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/v1/devices/" + device.DeviceId);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", device.Secret);
        return await client.SendAsync(request, Token);
    }

    [Fact]
    public async Task Enrollment_requires_recovery_possession_before_storage()
    {
        var state = new ObservedState(fixture.State);
        await using var host = InMemoryHost.Create(state);
        using var client = host.CreateClient();
        using var response = await client.PostAsync("/v1/enrollments", null, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, state.Calls);
    }

    [Fact]
    public async Task Exhausted_request_budget_rejects_before_storage_and_keeps_probes_available()
    {
        var device = fixture.Device;
        var state = new ObservedState(fixture.State);
        await using var host = InMemoryHost.Create(state, new() { ["Broker:RequestLimitPerMinute"] = "1" });
        using var client = host.CreateClient();
        using var first = await Send(client, device);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        var calls = state.Calls;
        using var rejected = await Send(client, device);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
        Assert.Equal(calls, state.Calls);
        using var live = await client.GetAsync("/health/live", Token);
        using var ready = await client.GetAsync("/health/ready", Token);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(calls, state.Calls);
    }

    [Fact]
    public async Task Concurrent_budget_rejects_without_storage_and_releases_permit_after_completion()
    {
        var device = fixture.Device;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new ObservedState(fixture.State) { Before = async ct =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        }};
        await using var host = InMemoryHost.Create(state, new() { ["Broker:ConcurrentRequestLimit"] = "1" });
        using var client = host.CreateClient();
        var pending = Send(client, device);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            using var rejected = await Send(client, device);
            Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.NotNull(rejected.Headers.RetryAfter);
            Assert.Equal(1, state.Calls);
        }
        finally { release.TrySetResult(); }
        using var first = await pending;
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        using var retry = await Send(client, device);
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
    }

    [Fact]
    public async Task Request_deadline_cancels_storage_work_and_returns_unavailable()
    {
        var device = fixture.Device;
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new ObservedState(fixture.State) { Before = async ct =>
        {
            try { await release.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        }};
        await using var host = InMemoryHost.Create(state, new() { ["Broker:RequestTimeoutSeconds"] = "1", ["Broker:ConcurrentRequestLimit"] = "1" });
        using var client = host.CreateClient();
        var pending = Send(client, device);
        try
        {
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
        finally
        {
            release.TrySetResult();
            using var completed = await pending.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
        Assert.Equal("active", fixture.Gate.State);
        using var retry = await Send(client, device);
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
    }

    [Fact]
    public async Task Credentials_and_request_body_rejections_do_not_contact_storage()
    {
        var device = fixture.Device;
        var state = new ObservedState(fixture.State);
        await using var host = InMemoryHost.Create(state);
        using var client = host.CreateClient();
        using var missing = await client.DeleteAsync("/v1/devices/" + device.DeviceId, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/v1/devices/" + device.DeviceId)
        { Content = new StringContent("body") };
        request.Headers.Authorization = new("Bearer", device.Secret);
        using var body = await client.SendAsync(request, Token);
        Assert.Equal(HttpStatusCode.BadRequest, body.StatusCode);
        Assert.Equal(0, state.Calls);
        using var mint = await client.PostAsync("/v1/devices/" + device.DeviceId + "/installation-token", null, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, mint.StatusCode);
        Assert.Equal(0, state.Calls);
    }
}
