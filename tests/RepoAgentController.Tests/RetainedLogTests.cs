using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

// The retained log's own files in a private temporary directory; stdout is not captured.
public sealed class RetainedLogTests : IDisposable
{
    static readonly DateTimeOffset At = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
    readonly string directory = Path.Combine(Path.GetTempPath(), "retained-log-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_rotation_that_fails_stops_retention_for_the_lifetime_without_failing_the_logging_caller()
    {
        using var log = new RetainedLog(directory, 1024, 2);
        for (var line = 0; line < 8; line++) log.Append(Entry(line));
        // The next line rotates, which cannot replace the oldest retained file.
        Directory.CreateDirectory(Path.Combine(directory, "controller.1.log"));

        for (var line = 8; line < 12; line++) log.Append(Entry(line));

        Assert.Equal(Lines(0, 8), RetainedLog.Read(directory));
    }

    // A 128-byte line with its newline, so eight fill a 1,024-byte file.
    static LogEntry Entry(int line) => new(At, "test", $"line {line:D3} " + new string('x', 79));

    static string Lines(int first, int count) => string.Concat(Enumerable.Range(first, count).Select(line => Entry(line).Line + "\n"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
