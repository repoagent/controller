using System.Text.Json;
using Azure.Data.Tables;

namespace RepoAgent.Auth;

internal static class BrokerCleanupCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args is ["cleanup", "--watch"] && Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") == "Development")
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
        if (args is not ["cleanup", "--once"])
        {
            Console.Error.WriteLine("Usage: cleanup --once");
            return 2;
        }

        using var budget = new CleanupRequestBudget();
        BrokerOptions options;
        TableClient table;
        IConfiguration configuration;
        try
        {
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
            table = BrokerStorage.Create(configuration, initial["DOTNET_ENVIRONMENT"], clientOptions);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            Console.Error.WriteLine("Invalid cleanup configuration.");
            return 2;
        }

        var cleanup = new BrokerCleanup(new TableBrokerState(table), options.DeploymentEpoch, budget, TimeProvider.System);
        var exitCode = await cleanup.RunAsync(CancellationToken.None);
        var summary = JsonSerializer.SerializeToElement(cleanup.Summary());
        Console.WriteLine(summary.GetRawText());
        try
        {
            if (BrokerTelemetry.CreateOptions(configuration, options.ManagedIdentityClientId) is { } telemetry)
                BrokerTelemetry.ExportCleanupOnce(summary, telemetry);
        }
        catch (Exception) { } // Optional telemetry never changes the CLI result or emits configuration details.
        return exitCode;
    }
}
