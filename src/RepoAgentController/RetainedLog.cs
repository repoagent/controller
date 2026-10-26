using System.Text;
using System.Text.RegularExpressions;

namespace RepoAgentController;

/// <summary>
/// Keeps redacted controller log lines across restarts in size-bounded rotating files beside the state.
/// Disk use never exceeds <c>files × fileBytes</c>; stdout output is unchanged.
/// </summary>
sealed partial class RetainedLog : IDisposable
{
    public const long DefaultFileBytes = 1024 * 1024, MinimumFileBytes = 1024, MaximumFileBytes = 1024L * 1024 * 1024;
    public const int DefaultFiles = 5, MaximumFiles = 100;
    const string Active = "controller.log";
    readonly string directory;
    readonly long fileBytes;
    readonly int files;
    readonly TimeProvider clock;
    readonly Lock gate = new();
    FileStream? current;

    public static string DirectoryFor(string statePath) => statePath + ".logs";

    public static void Validate(long fileBytes, int files)
    {
        if (fileBytes is < MinimumFileBytes or > MaximumFileBytes) throw new ArgumentException($"--log-file-bytes must be between {MinimumFileBytes} and {MaximumFileBytes}");
        if (files is < 1 or > MaximumFiles) throw new ArgumentException($"--log-files must be between 1 and {MaximumFiles}");
    }

    /// <summary>Opens the retained log in <paramref name="directory"/>; its own error line carries <paramref name="clock"/>'s time.</summary>
    public RetainedLog(string directory, long fileBytes, int files, TimeProvider clock)
    {
        Validate(fileBytes, files);
        this.directory = directory; this.fileBytes = fileBytes; this.files = files; this.clock = clock;
        GitHubAppCredentials.SecureDirectory(directory);
        // A lowered retention setting also bounds files left by an earlier, larger setting: surplus and oversized archives
        // are deleted, and an oversized active file keeps its newest whole lines.
        foreach (var (index, path) in Rotated(directory))
            if (index >= files || new FileInfo(path).Length > fileBytes) File.Delete(path);
        var active = Path.Combine(directory, Active);
        if (File.Exists(active) && new FileInfo(active).Length > fileBytes) KeepNewest(active, fileBytes);
        current = Open(FileMode.Append);
    }

    public void Append(LogEntry entry)
    {
        var bytes = Encoding.UTF8.GetBytes(Bounded(entry.Line) + "\n");
        lock (gate)
        {
            if (current is null) return;
            try
            {
                if (current.Length > 0 && current.Length + bytes.Length > fileBytes) Rotate();
                current.Write(bytes);
                current.Flush();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Stdout remains the operational record; never recurse into Log from its own sink. A failed rotation has
                // already closed the active file.
                current?.Dispose(); current = null;
                Console.WriteLine(new LogEntry(clock.GetUtcNow().ToUniversalTime(), "retained-log-error", $"retention stopped for this lifetime: {error.Message}").Line);
            }
        }
    }

    /// <summary>Retained text from oldest to newest.</summary>
    public static string Read(string directory)
    {
        if (!Directory.Exists(directory)) return "";
        var text = new StringBuilder();
        foreach (var (_, path) in Rotated(directory).OrderByDescending(file => file.Index)) text.Append(File.ReadAllText(path));
        var active = Path.Combine(directory, Active);
        if (File.Exists(active)) text.Append(File.ReadAllText(active));
        return text.ToString();
    }

    // Truncate rather than exceed the per-file bound with one oversized message.
    string Bounded(string line)
    {
        var limit = (int)Math.Min(fileBytes - 1, int.MaxValue);
        if (Encoding.UTF8.GetByteCount(line) <= limit) return line;
        const string Marker = " <truncated>";
        var length = Math.Min(line.Length, limit - Marker.Length);
        while (length > 0 && Encoding.UTF8.GetByteCount(line.AsSpan(0, length)) > limit - Marker.Length) length--;
        if (length > 0 && char.IsHighSurrogate(line[length - 1])) length--;
        return line[..length] + Marker;
    }

    void Rotate()
    {
        current!.Dispose(); current = null;
        if (files == 1) { current = Open(FileMode.Create); return; }
        File.Delete(Path.Combine(directory, Name(files - 1)));
        for (var index = files - 2; index >= 0; index--)
        {
            var source = Path.Combine(directory, Name(index));
            if (File.Exists(source)) File.Move(source, Path.Combine(directory, Name(index + 1)), true);
        }
        current = Open(FileMode.Create);
    }

    // Moves the newest whole lines that fit in fileBytes to the start of the file through a bounded buffer, then truncates.
    static void KeepNewest(string path, long fileBytes)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        // Kept text starts after the line break that ends the newest line not wholly within the last fileBytes.
        file.Position = file.Length - fileBytes - 1;
        while (file.ReadByte() is not (-1 or '\n')) { }
        var buffer = new byte[64 * 1024];
        long kept = 0;
        for (int count; (count = file.Read(buffer)) > 0; kept += count)
        {
            var next = file.Position;
            file.Position = kept;
            file.Write(buffer, 0, count);
            file.Position = next;
        }
        file.SetLength(kept);
    }

    FileStream Open(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(Path.Combine(directory, Active), options);
    }

    static string Name(int index) => index == 0 ? Active : $"controller.{index}.log";

    static IEnumerable<(int Index, string Path)> Rotated(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "controller.*.log"))
            if (RotatedName().Match(Path.GetFileName(path)) is { Success: true } match && int.TryParse(match.Groups[1].Value, out var index))
                yield return (index, path);
    }

    [GeneratedRegex(@"\Acontroller\.([1-9][0-9]{0,2})\.log\z")]
    private static partial Regex RotatedName();

    public void Dispose()
    {
        lock (gate) { current?.Dispose(); current = null; }
    }
}
