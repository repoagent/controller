using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

// `cleanup --once` as the Container Apps job runs it: its arguments and configuration, the one summary line, the exit
// code, and the optional export of its observations.
public sealed class CleanupCommandTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("")]
    [InlineData("cleanup")]
    [InlineData("cleanup --watch")]
    [InlineData("cleanup --ONCE")]
    [InlineData("--once")]
    [InlineData("clean --once")]
    [InlineData("cleanup --once --watch")]
    public async Task Other_arguments_print_the_usage_and_exit_2_without_opening_the_store(string args)
    {
        var host = new Host(new InMemoryBroker());
        var run = await host.RunAsync(args.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal((2, "", "Usage: cleanup --once" + Environment.NewLine), (run.Exit, run.Output, run.Error));
        Assert.Equal((0, 0), (host.Opened, host.Exported));
    }

    [Theory]
    [InlineData("argument")]
    [InlineData("operation")]
    [InlineData("format")]
    public async Task Invalid_configuration_exits_2_before_the_store_is_read(string failure)
    {
        var f = new InMemoryBroker();
        var host = new Host(f)
        {
            Opening = () => throw (failure switch
            {
                "argument" => new ArgumentException("Secret-bearing detail"),
                "operation" => new InvalidOperationException("Secret-bearing detail"),
                _ => (Exception)new FormatException("Secret-bearing detail"),
            })
        };
        var run = await host.RunAsync(["cleanup", "--once"]);
        Assert.Equal((2, "", "Invalid cleanup configuration." + Environment.NewLine), (run.Exit, run.Output, run.Error));
        Assert.Equal((1, 0, 0), (host.Opened, host.Exported, f.State.Reads));
    }

    // Only the three configuration failures are invalid configuration; anything else is a defect and isn't reported as one.
    [Fact]
    public async Task Another_failure_to_open_is_not_reported_as_invalid_configuration()
    {
        var host = new Host(new InMemoryBroker()) { Opening = () => throw new IOException("Missing settings file.") };
        await Assert.ThrowsAsync<IOException>(() => host.RunAsync(["cleanup", "--once"]));
        Assert.Equal("", host.Error.ToString());
    }

    // Disabled telemetry exports nothing; malformed or failing telemetry throws. None of them changes the exit code or the
    // one summary line, and none writes to stderr. The export always gets the observations of the printed summary.
    [Theory]
    [InlineData("disabled", false)]
    [InlineData("malformed", false)]
    [InlineData("failing", false)]
    [InlineData("disabled", true)]
    [InlineData("malformed", true)]
    [InlineData("failing", true)]
    public async Task A_run_prints_its_summary_as_the_only_line_whatever_telemetry_does(string telemetry, bool storeFails)
    {
        var f = new InMemoryBroker();
        if (storeFails) f.State.BeforeRead = _ => throw new BrokerStateUnavailable("Store unavailable.");
        var host = new Host(f)
        {
            Exporting = telemetry switch
            {
                "malformed" => _ => throw new InvalidOperationException("Invalid monitoring configuration."),
                "failing" => _ => throw new TaskCanceledException("Ingestion timed out."),
                _ => _ => { },
            }
        };
        var run = await host.RunAsync(["cleanup", "--once"]);
        Assert.Equal(storeFails ? 1 : 0, run.Exit);
        Assert.Equal("", run.Error);
        var line = Assert.Single(run.Output.Split(Environment.NewLine)[..^1]);
        Assert.Equal(run.Output, line + Environment.NewLine);
        var summary = JsonSerializer.Deserialize<JsonElement>(line);
        Assert.Equal(storeFails ? 1 : 0, summary.GetProperty("failures").GetInt32());
        Assert.Equal(1, host.Exported);
        Assert.Equal(CleanupCommand.Observations(summary), host.Observations);
    }

    // The summary's monitored values as the cleanup-observation alert reads them. A stale cursor counts only with an overdue
    // payload, both at least 900 s old.
    [Theory]
    [InlineData(950d, 22000d, 900d, 1d)]
    [InlineData(899d, 1d, 900d, 0d)]
    [InlineData(900d, 1d, 899d, 0d)]
    [InlineData(900d, 1d, 900d, 1d)]
    [InlineData(null, null, null, 0d)]
    public void Observations_are_the_summarys_monitored_values_without_dimensions(double? cursor, double? cycle, double? overdue, double stale)
    {
        var summary = JsonSerializer.SerializeToElement(new
        {
            failures = 2, unknownDeadlineCount = 3, stopReason = "request-budget",
            cursorAndCycleAges = new[]
            {
                new { cursorAgeSeconds = cursor, cycleAgeSeconds = cycle },
                new { cursorAgeSeconds = cursor is null ? null : (double?)1, cycleAgeSeconds = (double?)null },
                new { cursorAgeSeconds = cursor is null ? null : (double?)1, cycleAgeSeconds = cycle is null ? null : (double?)1 }
            },
            oldestObservedOverduePayloadAgeSeconds = overdue
        });
        var expected = new List<CleanupObservation> { new("repoagent.auth.cleanup.completed", 1), new("repoagent.auth.cleanup.failures", 2),
            new("repoagent.auth.cleanup.unknown_deadlines", 3) };
        if (cursor is not null) expected.Add(new("repoagent.auth.cleanup.cursor_age_max_seconds", cursor.Value));
        if (cycle is not null) expected.Add(new("repoagent.auth.cleanup.cycle_age_max_seconds", cycle.Value));
        expected.Add(new("repoagent.auth.cleanup.cycle_age_unknown", cycle is null ? 3 : 1));
        if (overdue is not null) expected.Add(new("repoagent.auth.cleanup.overdue_payload_age_max_seconds", overdue.Value));
        expected.Add(new("repoagent.auth.cleanup.stale_cursor_with_overdue_observation", stale));

        Assert.Equal(expected, CleanupCommand.Observations(summary));
    }

    // The command's host over the in-memory broker: opening returns its state, epoch and a cleanup budget, and the export
    // keeps what it was given.
    private sealed class Host(InMemoryBroker f) : ICleanupHost
    {
        public int Opened { get; private set; }
        public int Exported { get; private set; }
        public IReadOnlyList<CleanupObservation>? Observations { get; private set; }
        public Func<CleanupComposition>? Opening { get; init; }
        public Action<IReadOnlyList<CleanupObservation>> Exporting { get; init; } = _ => { };
        public StringWriter Output { get; } = new();
        public StringWriter Error { get; } = new();

        public CleanupComposition Open()
        {
            Opened++;
            if (Opening is { } opening) return opening();
            var budget = new CleanupBudget(f.State);
            return new(budget, f.DeploymentEpoch, budget);
        }

        public void Export(IReadOnlyList<CleanupObservation> observations)
        {
            Exported++;
            Observations = observations;
            Exporting(observations);
        }

        public async Task<(int Exit, string Output, string Error)> RunAsync(string[] args)
        {
            var exit = await new CleanupCommand(this, Output, Error, f.Clock).RunAsync(args, Token);
            return (exit, Output.ToString(), Error.ToString());
        }
    }
}
