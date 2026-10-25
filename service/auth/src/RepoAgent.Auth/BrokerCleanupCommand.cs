using Azure.Data.Tables;

namespace RepoAgent.Auth;

internal static class BrokerCleanupCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (CleanupCommand.Watches(args, Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")))
        {
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
            try
            {
                do
                {
                    var result = await RunAsync(["cleanup", "--once"]);
                    if (result != 0) return result;
                }
                while (await timer.WaitForNextTickAsync(stop.Token));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            return 0;
        }
        using var host = new Host();
        return await new CleanupCommand(host, Console.Out, Console.Error, TimeProvider.System).RunAsync(args, CancellationToken.None);
    }

    // The environment's configuration, the Table adapter over the counted transport, and Azure Monitor.
    private sealed class Host : ICleanupHost, IDisposable
    {
        private CleanupRequestBudget? budget;
        private IConfiguration? configuration;
        private BrokerOptions? options;

        public CleanupComposition Open()
        {
            budget = new CleanupRequestBudget();
            var builder = new ConfigurationBuilder().AddEnvironmentVariables();
            var initial = builder.Build();
            DevelopmentSettings.Add(builder, initial, initial["DOTNET_ENVIRONMENT"]);
            configuration = builder.Build();
            options = BrokerStorage.Options(configuration, initial["DOTNET_ENVIRONMENT"]);
            var clientOptions = new TableClientOptions
            {
                Transport = budget,
                Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(3) },
                Diagnostics = { IsLoggingContentEnabled = false }
            };
            var table = BrokerStorage.Create(configuration, initial["DOTNET_ENVIRONMENT"], clientOptions);
            return new(new TableBrokerState(table), options.DeploymentEpoch, budget);
        }

        public void Export(IReadOnlyList<CleanupObservation> observations)
        {
            if (BrokerTelemetry.CreateOptions(configuration!, options!.ManagedIdentityClientId) is { } telemetry)
                BrokerTelemetry.ExportCleanupOnce(observations, telemetry);
        }

        public void Dispose() => budget?.Dispose();
    }
}
