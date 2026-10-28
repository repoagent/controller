using System.Text;
using System.Text.Json;
using Azure.Data.Tables;
using Azure.Identity;

namespace RepoAgent.Auth;

// Internal operator command for the accepted production runtime only. This is not HTTP
// admission, a general Table viewer, a cleanup run or an authority to replace an enrollment.
internal static class BrokerAuthorityObservationCommand
{
    // Accepted nonsecret deployment40749418629, source4b7e9eb, 2026-10-28T19:05:50Z.
    private const string RuntimeClientId = "8016ef66-b1b7-4118-8de4-07814ebc9896";
    private const string TableEndpoint = "https://repoagentauth.table.core.windows.net/";
    private static readonly string[] ForbiddenConfiguration = ["Broker:DevelopmentSettingsFile", "Broker:DevelopmentConnectionString",
        "Broker:Cleanup:DevelopmentConnectionString", "Auth:DevelopmentBlobConnectionString", "Auth:DevelopmentSigningCredential", "Auth:DevelopmentVaultUri"];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is not ["--inspect-retained-authority"])
        {
            Console.Error.WriteLine("Usage: --inspect-retained-authority");
            return 2;
        }
        BrokerOptions options;
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            static bool Production(string? value) => value is null || string.Equals(value, "Production", StringComparison.OrdinalIgnoreCase);
            if (!Production(configuration["DOTNET_ENVIRONMENT"]) || !Production(configuration["ASPNETCORE_ENVIRONMENT"])
                || ForbiddenConfiguration.Any(name => configuration[name] is not null))
                throw new InvalidOperationException("Production configuration required.");
            options = BrokerOptions.Read(name => name is "Broker:TableEndpoint" or "Broker:TableName" or "Broker:DeploymentEpoch"
                or "Broker:ManagedIdentityClientId" ? configuration[name] : null);
            if (options.TableEndpoint.AbsoluteUri != TableEndpoint || options.TableName != "brokerstate"
                || !Guid.TryParse(options.ManagedIdentityClientId, out var identity) || identity != Guid.Parse(RuntimeClientId)
                || configuration["AZURE_CLIENT_ID"] is { } ambient
                    && (!Guid.TryParse(ambient, out var ambientId) || ambientId != identity))
                throw new InvalidOperationException("Accepted runtime identity and table required.");
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FormatException)
        {
            Console.Error.WriteLine("Retained authority inspection refused: invalid production configuration.");
            return 2;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Retained authority inspection unavailable; no provider details retained.");
            return 1;
        }
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var clientOptions = new TableClientOptions
            {
                Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(3) },
                Diagnostics = { IsLoggingContentEnabled = false }
            };
            // Constructors do not create the Table. Only the projection's bounded exact
            // ReadAsync calls run; no host, signing, key ring, telemetry or GitHub is composed.
            var table = new TableClient(options.TableEndpoint, options.TableName,
                new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(RuntimeClientId)), clientOptions);
            var observation = await new RetainedAuthorityObservation(new TableBrokerState(table), options.DeploymentEpoch,
                TimeProvider.System).ObserveAsync(budget.Token);
            var json = JsonSerializer.Serialize(new
            {
                schema = observation.Schema,
                compiledVersion = BrokerVersion.Version,
                compiledSource = BrokerVersion.Source,
                observation
            }, JsonOptions);
            if (Encoding.UTF8.GetByteCount(json) > 16 * 1024) throw new InvalidOperationException("Observation output limit.");
            Console.Out.WriteLine(json);
            return observation.State == "observed" ? 0 : 1;
        }
        catch (Exception)
        {
            // All provider and unexpected failures stay private, including inner exception
            // text. A failed observation must never fall through to normal HTTP startup.
            Console.Error.WriteLine("Retained authority inspection unavailable; no provider details retained.");
            return 1;
        }
    }
}
