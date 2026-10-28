using Aspire.Hosting.Testing;
using Xunit;

namespace RepoAgent.AppHostTests;

public sealed class SharedAppHostTests
{
    [Fact]
    public async Task Saved_enrollment_refuses_randomized_broker_ports()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.RepoAgent_Auth_AppHost>(
            [
                "DcpPublisher:RandomizePorts=true",
                "Controller:Repository=repoagent/e2e-composition",
                "Controller:BrokerOrigin=https://localhost:7443",
                "Controller:AppId=1",
                $"Controller:Workspace={Path.GetTempPath()}",
                $"Controller:StateDirectory={Path.Combine(Path.GetTempPath(), "repoagent-composition-state")}",
                "Parameters:controller-enrollment={}",
                "SharedSigningKeyUri=https://example.vault.azure.net/keys/signing/version"
            ], TestContext.Current.CancellationToken);
        });
        Assert.Contains("saved, stable broker origin", error.Message, StringComparison.Ordinal);
    }
    [Fact]
    public async Task A_configured_controller_is_part_of_the_shared_AppHost()
    {
        using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.RepoAgent_Auth_AppHost>(
        [
            "DcpPublisher:RandomizePorts=false",
            "Controller:Repository=repoagent/e2e-composition",
            "Controller:BrokerOrigin=https://localhost:7443",
            "Controller:AppId=1",
            $"Controller:Workspace={Path.GetTempPath()}",
            $"Controller:StateDirectory={Path.Combine(Path.GetTempPath(), "repoagent-composition-state")}",
            "Parameters:controller-enrollment={}",
            "SharedSigningKeyUri=https://example.vault.azure.net/keys/signing/version"
        ], TestContext.Current.CancellationToken);

        Assert.Contains("controller", builder.Resources.Select(resource => resource.Name));
    }
}
