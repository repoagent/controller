using Azure.Data.Tables;
using Azure.Identity;

namespace RepoAgent.Auth;

internal static class BrokerStorage
{
    internal static BrokerOptions Options(IConfiguration configuration, string? environment) =>
        BrokerSettings.Options(name => configuration[name], environment);

    internal static TableClient Create(IConfiguration configuration, string? environment, TableClientOptions clientOptions)
    {
        var connection = BrokerSettings.DevelopmentConnection(name => configuration[name], environment);
        var options = Options(configuration, environment);
        if (connection is not null)
        {
            var table = new TableClient(connection, options.TableName, clientOptions);
            options.RequireEmulatedTable(table.Uri);
            return table;
        }
        var identity = options.ManagedIdentityClientId is { } clientId
            ? ManagedIdentityId.FromUserAssignedClientId(clientId) : ManagedIdentityId.SystemAssigned;
        return new TableClient(options.TableEndpoint, options.TableName, new ManagedIdentityCredential(identity), clientOptions);
    }
}
