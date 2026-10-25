using System.Text.Json;

namespace RepoAgent.Auth;

// What the cleanup command runs over: the broker state, deployment epoch and request budget its configuration names, and
// the optional export of the run's observations.
public interface ICleanupHost
{
    // Throws ArgumentException, InvalidOperationException or FormatException when the configuration is invalid.
    CleanupComposition Open();

    // Exports the observations, or nothing when monitoring isn't configured. It throws when monitoring is malformed or fails.
    void Export(IReadOnlyList<CleanupObservation> observations);
}

public sealed record CleanupComposition(IBrokerState State, string DeploymentEpoch, ICleanupBudget Budget);

// One run's value of a cleanup metric, which has no dimensions.
public sealed record CleanupObservation(string Metric, double Value);

// `cleanup --once`: one cleanup run, its summary as the only line on stdout, and the run's exit code. Bad arguments or
// configuration exit 2 before the store is opened; optional telemetry never changes the result.
public sealed class CleanupCommand(ICleanupHost host, TextWriter output, TextWriter error, TimeProvider clock)
{
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
        var cleanup = new BrokerCleanup(composition.State, composition.DeploymentEpoch, composition.Budget, clock);
        var exitCode = await cleanup.RunAsync(ct);
        var summary = JsonSerializer.SerializeToElement(cleanup.Summary());
        output.WriteLine(summary.GetRawText());
        try { host.Export(Observations(summary)); }
        catch (Exception) { } // Optional telemetry never changes the result or emits configuration details.
        return exitCode;
    }

    // The summary as metrics: one completion, the failures and unknown deadlines, the oldest cursor and cycle ages, how many
    // cycle ages are unknown, the oldest observed overdue payload, and whether a cursor and an overdue payload are both at
    // least 15 minutes old. An age that no lane observed isn't recorded.
    public static IReadOnlyList<CleanupObservation> Observations(JsonElement summary)
    {
        static double? Number(JsonElement item) => item.ValueKind == JsonValueKind.Number ? item.GetDouble() : null;
        var ages = summary.GetProperty("cursorAndCycleAges").EnumerateArray().ToArray();
        var cursor = ages.Select(age => Number(age.GetProperty("cursorAgeSeconds"))).Max();
        var cycle = ages.Select(age => Number(age.GetProperty("cycleAgeSeconds"))).Max();
        var overdue = Number(summary.GetProperty("oldestObservedOverduePayloadAgeSeconds"));
        var observations = new List<CleanupObservation>();
        void Record(string name, double? value)
        {
            if (value is { } number) observations.Add(new("repoagent.auth.cleanup." + name, number));
        }
        Record("completed", 1);
        Record("failures", summary.GetProperty("failures").GetInt32());
        Record("unknown_deadlines", summary.GetProperty("unknownDeadlineCount").GetInt32());
        Record("cursor_age_max_seconds", cursor);
        Record("cycle_age_max_seconds", cycle);
        Record("cycle_age_unknown", ages.Count(age => age.GetProperty("cycleAgeSeconds").ValueKind == JsonValueKind.Null));
        Record("overdue_payload_age_max_seconds", overdue);
        Record("stale_cursor_with_overdue_observation", cursor >= 900 && overdue >= 900 ? 1 : 0);
        return observations;
    }
}
