using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

public sealed class ImagePublicationProfileTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Startup_requires_the_explicit_image_flag_and_never_adopts_frozen_grants(bool clone, bool settings)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new BrokerAuthenticationTests.Fixture(clone: clone, settings: settings, images: true);
        Assert.Equal(0, await fixture.Start());
        Assert.Single(fixture.Transport.Requests);
        Assert.True(File.Exists(Path.Combine(fixture.CredentialDirectory, "bin", "repoagent-assets")));
        fixture.Images = false;
        Assert.Equal(1, await fixture.Start());
        Assert.Single(fixture.Transport.Requests);
        using var frozen = new BrokerAuthenticationTests.Fixture(clone: clone, settings: settings) { Images = true };
        Assert.Equal(1, await frozen.Start());
        Assert.Empty(frozen.Transport.Requests);
    }

    [Theory]
    [InlineData("policyVersion", "repository-agent-settings-approval-v5")]
    [InlineData("profile", "polling-clone-images")]
    public async Task Changed_approval_binding_is_refused_before_any_broker_request(string field, string value)
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new BrokerAuthenticationTests.Fixture(images: true);
        fixture.Enrollment[field] = value;
        fixture.SaveEnrollment();
        Assert.Equal(1, await fixture.Start());
        Assert.Empty(fixture.Transport.Requests);
    }
}
