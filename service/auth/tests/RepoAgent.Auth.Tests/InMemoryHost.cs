using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace RepoAgent.Auth.Tests;

// The broker host with an in-memory broker state in place of the Table adapter, so no request reaches a store. The
// Table settings are only there for startup validation.
internal static class InMemoryHost
{
    public static WebApplicationFactory<Program> Create(IBrokerState state, Dictionary<string, string?>? settings = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            var configured = new Dictionary<string, string?>
            {
                ["Broker:DeploymentEpoch"] = InMemoryBroker.Epoch,
                ["Broker:TableEndpoint"] = "https://example.table.core.windows.net",
                ["Broker:TableName"] = "brokerstate"
            };
            foreach (var setting in settings ?? []) configured[setting.Key] = setting.Value;
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(configured));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBrokerState>();
                services.AddSingleton(state);
            });
        });
}
