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

    [Fact]
    public void A_restart_with_a_smaller_file_size_bounds_the_files_an_earlier_setting_retained()
    {
        using (var earlier = new RetainedLog(directory, 4096, 3))
            for (var line = 0; line < 84; line++) earlier.Append(Entry(line)); // Two full 32-line archives and 20 active lines.

        using (var log = new RetainedLog(directory, 1024, 3)) log.Append(Entry(84));

        Assert.All(Directory.GetFiles(directory), file => Assert.InRange(new FileInfo(file).Length, 1, 1024));
        // Oversized archives are dropped; the newest whole lines that fit the smaller size are kept.
        Assert.Equal(Lines(76, 9), RetainedLog.Read(directory));
    }

    [Fact]
    public void Retained_lines_survive_restarts_and_rotation_keeps_the_newest_within_the_bound()
    {
        for (var lifetime = 0; lifetime < 6; lifetime++)
        {
            using var log = new RetainedLog(directory, 1024, 2);
            for (var line = 0; line < 5; line++) log.Append(Entry(lifetime * 5 + line));
            if (lifetime == 1) Assert.Equal(Lines(0, 10), RetainedLog.Read(directory)); // The earlier lifetime's lines survived.
        }

        Assert.InRange(Directory.GetFiles(directory).Length, 1, 2);
        Assert.InRange(Directory.GetFiles(directory).Sum(file => new FileInfo(file).Length), 1, 2 * 1024);
        Assert.Equal(Lines(16, 14), RetainedLog.Read(directory)); // The oldest files rotated out; the newest archive and the active file remain.
    }

    // A 128-byte line with its newline, so eight fill a 1,024-byte file.
    static LogEntry Entry(int line) => new(At, "test", $"line {line:D3} " + new string('x', 79));

    static string Lines(int first, int count) => string.Concat(Enumerable.Range(first, count).Select(line => Entry(line).Line + "\n"));

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
