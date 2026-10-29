using Aspire.Hosting.ApplicationModel;
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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task A_configured_controller_forwards_only_enabled_repository_settings(bool? repositorySettings)
    {
        var configuration = new List<string>
        {
            "DcpPublisher:RandomizePorts=false",
            "Controller:Repository=repoagent/e2e-composition",
            "Controller:BrokerOrigin=https://localhost:7443",
            "Controller:AppId=1",
            $"Controller:Workspace={Path.GetTempPath()}",
            $"Controller:StateDirectory={Path.Combine(Path.GetTempPath(), "repoagent-composition-state")}",
            "Parameters:controller-enrollment={}",
            "SharedSigningKeyUri=https://example.vault.azure.net/keys/signing/version"
        };
        if (repositorySettings is not null) configuration.Add($"Controller:RepositorySettings={repositorySettings}");
        using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.RepoAgent_Auth_AppHost>(
            [.. configuration], TestContext.Current.CancellationToken);

        var controller = Assert.Single(builder.Resources, resource => resource.Name == "controller");
        var arguments = new List<object>();
        var context = new CommandLineArgsCallbackContext(arguments, controller, TestContext.Current.CancellationToken);
        foreach (var annotation in controller.Annotations.OfType<CommandLineArgsCallbackAnnotation>())
            await annotation.Callback(context);

        Assert.Equal(repositorySettings == true ? 1 : 0, arguments.Count(argument => argument as string == "--repository-settings"));
    }
}
