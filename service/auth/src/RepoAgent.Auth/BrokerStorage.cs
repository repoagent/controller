using Azure.Data.Tables;
using Azure.Identity;

namespace RepoAgent.Auth;

internal static class BrokerStorage
{
    internal static string? DevelopmentConnection(IConfiguration configuration, string? environment)
    {
        var connection = configuration["Broker:DevelopmentConnectionString"]
            ?? configuration["Broker:Cleanup:DevelopmentConnectionString"];
        if (connection is not null && environment != "Development")
            throw new InvalidOperationException("Development storage requires Development environment.");
        return connection;
    }

    internal static BrokerOptions Options(IConfiguration configuration, string? environment) =>
        BrokerOptions.Read(configuration, DevelopmentConnection(configuration, environment) is not null);

    internal static TableClient Create(IConfiguration configuration, string? environment, TableClientOptions clientOptions)
    {
        var connection = DevelopmentConnection(configuration, environment);
        var options = BrokerOptions.Read(configuration, connection is not null);
        if (connection is not null)
        {
            if (!BrokerOptions.IsDevelopmentTableEndpoint(options.TableEndpoint))
                throw new InvalidOperationException("Development storage requires a loopback endpoint.");
            var table = new TableClient(connection, options.TableName, clientOptions);
            var expected = new Uri(options.TableEndpoint.AbsoluteUri.TrimEnd('/') + "/" + options.TableName);
            if (table.Uri != expected || !table.Uri.IsLoopback || table.Uri.UserInfo.Length != 0
                || table.Uri.Query.Length != 0 || table.Uri.Fragment.Length != 0)
                throw new InvalidOperationException("Development storage destination does not match.");
            return table;
        }
        var identity = options.ManagedIdentityClientId is { } clientId
            ? ManagedIdentityId.FromUserAssignedClientId(clientId) : ManagedIdentityId.SystemAssigned;
        return new TableClient(options.TableEndpoint, options.TableName, new ManagedIdentityCredential(identity), clientOptions);
    }
}
