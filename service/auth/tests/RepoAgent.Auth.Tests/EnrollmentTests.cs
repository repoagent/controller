using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class EnrollmentTests
{
    [Fact]
    public async Task Lost_start_and_completion_responses_recover_one_device_and_only_ack_enables_runtime()
    {
        var f = new InMemoryBroker();
        var request = InMemoryBroker.Request();
        var start = await f.Start(request);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var pending = start.Json;
        var id = pending.GetProperty("pendingId").GetString()!;
        var marker = pending.GetProperty("marker").GetString()!;
        var duplicate = await f.Start(request);
        Assert.Equal(pending.ToString(), duplicate.Json.ToString());
        Assert.Equal(1, f.GitHub.VerifierMints);
        f.GitHub.Marker = marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var completion = await f.Complete(id);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var delivered = completion.Json;
        var envelope = delivered.GetProperty("enrollment");
        var device = envelope.GetProperty("deviceId").GetString()!;
        var secret = envelope.GetProperty("deviceSecret").GetString()!;
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Issue(device, secret)).StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var recovered = await f.Complete(id);
        Assert.Equal(delivered.ToString(), recovered.Json.ToString());
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var completionId = delivered.GetProperty("completionId").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await f.Acknowledge(device, completionId, secret)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Acknowledge(device, completionId, secret)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Complete(id)).StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(device, secret)).StatusCode);
        Assert.Equal(1, f.GitHub.VerifierMints);
    }

    [Fact]
    public async Task Pending_capacity_and_wrong_recovery_possession_never_allocate_more_provider_work()
    {
        var f = new InMemoryBroker();
        var request = InMemoryBroker.Request();
        Assert.Equal(HttpStatusCode.OK, (await f.Start(request)).StatusCode);
        var reads = f.GitHub.Reads;
        Assert.Equal(HttpStatusCode.Conflict, (await f.Start(request, new string('b', 43))).StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
        for (var i = 0; i < 3; i++)
        {
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(HttpStatusCode.OK, (await Retained.HistoricalDiscovery(f, () => f.Start(InMemoryBroker.Request()))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Retained.HistoricalDiscovery(f, () => f.Start(InMemoryBroker.Request()))).StatusCode);
        Assert.Equal(4, f.GitHub.VerifierMints);
    }
}
