using Xunit;
using static RepoAgentController.Tests.ControllerHarness;

namespace RepoAgentController.Tests;

// The operator log line, byte for byte: its round-trip UTC time, kind and redacted message, as stdout, an observer and the
// retained log each receive it. Not parallel: stdout is captured; lines are picked out by a kind no other test writes.
[Collection("Managed checkout environment")]
public sealed class LogLineTests : IDisposable
{
    const string Time = @"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{7}\+00:00";
    readonly string directory = Path.Combine(Path.GetTempPath(), "log-line-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_log_entry_line_is_its_round_trip_utc_time_then_its_kind_then_its_message()
    {
        Assert.Equal("2026-10-24T10:00:00.1234567+00:00 test-kind a message  with `spacing`",
            new LogEntry(new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero).AddTicks(1234567), "test-kind", "a message  with `spacing`").Line);
        Assert.Equal("2026-10-24T08:00:00.0000000+00:00 test-kind multi\nline",
            new LogEntry(new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.FromHours(2)).ToUniversalTime(), "test-kind", "multi\nline").Line);
    }

    [Fact]
    public void A_written_line_reaches_stdout_the_flow_observer_and_the_retained_log_as_the_same_redacted_bytes()
    {
        var secret = "ghs_" + Guid.NewGuid().ToString("N");
        Log.Protect(secret);
        var observed = new List<LogEntry>();
        using var retained = new RetainedLog(directory, RetainedLog.DefaultFileBytes, 1);
        using var log = new LogCapture();
        using (Log.Observe(entry => { observed.Add(entry); retained.Append(entry); }))
            Log.Write("log-line-test", $"token {secret} refused; next attempt at 10:00");

        var entry = Assert.Single(observed);
        Assert.Equal(TimeSpan.Zero, entry.At.Offset);
        Assert.Equal(("log-line-test", "token <redacted> refused; next attempt at 10:00"), (entry.Kind, entry.Message));
        var line = Assert.Single(log.Lines, line => line.Contains(" log-line-test ", StringComparison.Ordinal));
        Assert.Matches($"^{Time} log-line-test token <redacted> refused; next attempt at 10:00$", line);
        Assert.Equal(entry.Line, line);
        Assert.Equal(line + "\n", RetainedLog.Read(directory));
    }

    [Fact]
    public void A_retained_log_that_cannot_write_reports_once_on_stdout_in_the_log_line_form_and_stops()
    {
        using var retained = new RetainedLog(directory, 1024, 2);
        var at = new DateTimeOffset(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
        for (var line = 0; line < 8; line++) retained.Append(new(at, "test", $"line {line:D3} " + new string('x', 79)));
        Directory.CreateDirectory(Path.Combine(directory, "controller.1.log")); // The next line's rotation cannot replace it.
        using var log = new LogCapture();

        retained.Append(new(at, "test", "rotates"));
        retained.Append(new(at, "test", "after"));

        var error = Assert.Single(log.Lines, line => line.Contains(" retained-log-error ", StringComparison.Ordinal));
        Assert.Matches($"^{Time} retained-log-error retention stopped for this lifetime: \\S.*$", error);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
