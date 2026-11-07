using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

public sealed class StandardReadingTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Standard_reading_consumes_existing_protocols_and_rejects_write(bool clone, bool settings, bool images)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new BrokerAuthenticationTests.Fixture(clone: clone, settings: settings, images: images);
        if (!images)
        {
            fixture.Enrollment["enrollmentId"] = fixture.Enrollment["deviceId"]!.DeepClone();
            fixture.Enrollment["controllerSecret"] = fixture.Enrollment["deviceSecret"]!.DeepClone();
            fixture.Enrollment.Remove("deviceId");
            fixture.Enrollment.Remove("deviceSecret");
        }
        fixture.Enrollment["permissions"]!["issues"] = "read";
        fixture.Enrollment["permissions"]!["pull_requests"] = "read";
        if (images || settings || clone)
            fixture.Enrollment["policyVersion"] = images ? "repository-agent-images-read-approval-v5" : settings ? "repository-agent-settings-read-approval-v5" : "repository-agent-clone-read-approval-v5";
        fixture.SaveEnrollment();
        Assert.Equal(0, await fixture.Start());
        fixture.Enrollment["permissions"]!["pull_requests"] = "write";
        fixture.SaveEnrollment();
        Assert.Equal(1, await fixture.Start());
        Assert.Single(fixture.Transport.Requests);
    }
}
