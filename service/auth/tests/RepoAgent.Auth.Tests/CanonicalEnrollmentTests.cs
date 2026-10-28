using System.Net;
using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class CanonicalEnrollmentTests
{
    [Fact]
    public async Task Canonical_setup_uses_enrollment_identity_and_controller_secret_without_widening_authority()
    {
        var f = new InMemoryBroker();
        var github = ActionsVariableEnrollmentTests.Repository.Configure(f.GitHub);
        var request = ActionsVariableEnrollmentTests.Request();
        var start = await f.Start(request, 5);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var pendingId = start.Json.GetProperty("pendingId").GetString()!;
        var marker = start.Json.GetProperty("marker").GetString()!;
        var approval = JsonSerializer.Deserialize<JsonElement>(marker);
        var enrollmentId = approval.GetProperty("enrollmentId").GetString()!;
        Assert.False(approval.TryGetProperty("deviceId", out _));
        github.Agent[approval.GetProperty("variable").GetString()!] = marker;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var complete = await f.Complete(pendingId, 5);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var enrollment = complete.Json.GetProperty("enrollment");
        Assert.Equal(enrollmentId, enrollment.GetProperty("enrollmentId").GetString());
        var secret = enrollment.GetProperty("controllerSecret").GetString()!;
        Assert.False(enrollment.TryGetProperty("deviceSecret", out _));
        Assert.DoesNotContain(secret, marker);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await f.Acknowledge(enrollmentId,
            complete.Json.GetProperty("completionId").GetString()!, secret, 5)).StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(enrollmentId, secret)).StatusCode);
        Assert.Equal(new Dictionary<string, string> { ["discussions"] = "write" }, github.RuntimePermissions[^1]);
        Assert.Equal(HttpStatusCode.NoContent, (await f.Revoke(enrollmentId, secret)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Issue(enrollmentId, secret)).StatusCode);
    }
}
