using System.Text.Json;
using Azure.Data.Tables;
using Azure.Identity;

namespace RepoAgent.Auth;

internal static class BrokerCleanupCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not ["cleanup", "--once"])
        {
            Console.Error.WriteLine("Usage: cleanup --once");
            return 2;
        }

        using var budget = new CleanupRequestBudget();
        BrokerOptions options;
        TableClient table;
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var connectionString = configuration["Broker:Cleanup:DevelopmentConnectionString"];
            var developmentStorage = connectionString is not null;
            if (developmentStorage && configuration["DOTNET_ENVIRONMENT"] != "Development")
                throw new InvalidOperationException("Development storage requires Development environment.");
            options = BrokerOptions.Read(configuration, developmentStorage);
            var clientOptions = new TableClientOptions
            {
                Transport = budget,
                Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(3) },
                Diagnostics = { IsLoggingContentEnabled = false }
            };
            if (developmentStorage)
            {
                if (!BrokerOptions.IsDevelopmentTableEndpoint(options.TableEndpoint))
                    throw new InvalidOperationException("Development storage requires a loopback endpoint.");
                table = new TableClient(connectionString, options.TableName, clientOptions);
                var expected = new Uri(options.TableEndpoint.AbsoluteUri.TrimEnd('/') + "/" + options.TableName);
                if (table.Uri != expected || !table.Uri.IsLoopback || table.Uri.UserInfo.Length != 0
                    || table.Uri.Query.Length != 0 || table.Uri.Fragment.Length != 0)
                    throw new InvalidOperationException("Development storage destination does not match.");
            }
            else
            {
                var identity = options.ManagedIdentityClientId is { } clientId
                    ? ManagedIdentityId.FromUserAssignedClientId(clientId) : ManagedIdentityId.SystemAssigned;
                table = new TableClient(options.TableEndpoint, options.TableName,
                    new ManagedIdentityCredential(identity), clientOptions);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            Console.Error.WriteLine("Invalid cleanup configuration.");
            return 2;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var cleanup = new BrokerCleanup(table, options, budget, TimeProvider.System);
        var exitCode = await cleanup.RunAsync(deadline.Token);
        Console.WriteLine(JsonSerializer.Serialize(cleanup.Summary()));
        return exitCode;
    }
}
