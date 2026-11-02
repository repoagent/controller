using System.Text.Json;

namespace RepoAgent.Auth;

// What the cleanup command runs over: the broker state, deployment epoch and request budget its configuration names, and
// the optional export of the run's observations.
public interface ICleanupHost
{
    // Throws ArgumentException, InvalidOperationException or FormatException when the configuration is invalid.
    CleanupComposition Open();

    // Exports the observations, or nothing when monitoring isn't configured. It throws when monitoring is malformed or fails.
    void Export(IReadOnlyList<CleanupObservation> observations, IReadOnlyList<Exception> exceptions);
}

public sealed record CleanupComposition(IBrokerState State, string DeploymentEpoch, ICleanupBudget Budget);

// One run's value of a cleanup metric, which has no dimensions.
public sealed record CleanupObservation(string Metric, double Value);

// `cleanup --once`: one cleanup run, its summary as the only line on stdout, and the run's exit code. Bad arguments or
// configuration exit 2 before the store is opened; optional telemetry never changes the result.
public sealed class CleanupCommand(ICleanupHost host, TextWriter output, TextWriter error, TimeProvider clock)
{
    // `cleanup --watch` repeats the run every five minutes, and only in the Development environment.
    public static bool Watches(IReadOnlyList<string> args, string? environment) =>
        args is ["cleanup", "--watch"] && environment == "Development";

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args is not ["cleanup", "--once"])
        {
            error.WriteLine("Usage: cleanup --once");
            return 2;
        }
        CleanupComposition composition;
        try { composition = host.Open(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            error.WriteLine("Invalid cleanup configuration.");
            return 2;
        }
        var exceptions = new List<Exception>();
        var cleanup = new BrokerCleanup(composition.State, composition.DeploymentEpoch, composition.Budget, clock, exceptions.Add);
        var exitCode = await cleanup.RunAsync(ct);
        var summary = JsonSerializer.SerializeToElement(cleanup.Summary());
        output.WriteLine(summary.GetRawText());
        try { host.Export(Observations(summary), exceptions); }
        catch (Exception) { } // Optional telemetry never changes the result or emits configuration details.
        return exitCode;
    }

    public static IReadOnlyList<CleanupObservation> Observations(JsonElement summary) =>
    [
        new("repoagent.auth.cleanup.completed", summary.GetProperty("incompleteCoverage").GetBoolean() ? 0 : 1),
        new("repoagent.auth.cleanup.failures", summary.GetProperty("failures").GetInt32()),
        new("repoagent.auth.cleanup.examined", summary.GetProperty("examinedRecords").GetInt32()),
        new("repoagent.auth.cleanup.changed", summary.GetProperty("changedRecords").GetInt32()),
        new("repoagent.auth.cleanup.conflicts", summary.GetProperty("conflicts").GetInt32()),
        new("repoagent.auth.cleanup.dispatches", summary.GetProperty("sdkTableDispatches").GetInt32())
    ];
}
