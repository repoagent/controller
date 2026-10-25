using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>Operator status over the files beside one state path: the status record, the state file, the settings record and
/// the refresh journal.</summary>
static class StatusFiles
{
    public static string PathFor(string statePath) => statePath + ".status.json";

    /// <summary>Starts this lifetime's status for <paramref name="statePath"/>.</summary>
    public static ControllerStatus Open(string statePath, string repository, string mode, TimeProvider clock)
    {
        var files = new ControllerStateFiles(statePath);
        return new(repository, mode, new StatusRecordFile(statePath, clock), files, files, new CheckoutJournalFile(statePath), clock);
    }

    /// <summary>The last recorded status of a running or stopped controller, read without taking its lease.</summary>
    public static OperatorStatus? Read(string statePath, TimeProvider clock)
    {
        var files = new ControllerStateFiles(statePath);
        return ControllerStatus.Read(new StatusRecordFile(statePath, clock), files, files, new CheckoutJournalFile(statePath), clock);
    }

    // A file that is missing or cannot be read as a JSON object reads as none.
    internal static JsonObject? ReadObject(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}

/// <summary>The <see cref="IStatusRecord"/> adapter: <c>.status.json</c> beside the state file, private to its owner.</summary>
sealed class StatusRecordFile(string statePath, TimeProvider clock) : IStatusRecord
{
    readonly string path = StatusFiles.PathFor(statePath);

    public RuntimeStatus? Read()
    {
        // A key repeated in the record reads only when it is reached, so the whole record then reads as none.
        try { return StatusFiles.ReadObject(path) is { } record ? OperatorStatusJson.ReadRecord(record) : null; }
        catch (ArgumentException) { return null; }
    }

    public void Write(RuntimeStatus status)
    {
        var temporary = path + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options)) file.Write(System.Text.Encoding.UTF8.GetBytes(OperatorStatusJson.Text(OperatorStatusJson.Record(status))));
            File.Move(temporary, path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Status is diagnostic; its failure must not stop work. Never recurse into Log from its observer.
            Console.WriteLine($"{clock.GetUtcNow():O} status-error {error.Message}");
        }
    }
}

/// <summary>The <see cref="ICheckoutWork"/> adapter: the managed checkout's refresh journal beside the state file, read leniently.</summary>
sealed class CheckoutJournalFile(string statePath) : ICheckoutWork
{
    readonly string path = statePath + ".refresh.json";

    public CheckoutWork? Read()
    {
        try
        {
            if (StatusFiles.ReadObject(path) is not { } journal) return null;
            // The journal names each admitted turn's agent session by its persisted key, thread.
            var sessions = journal["active"] is JsonObject active
                ? active.Select(entry => (entry.Value as JsonObject)?["thread"].OptionalText()).OfType<string>().ToHashSet(StringComparer.Ordinal)
                : [];
            return new(sessions, journal["blocked"] is JsonValue blocked && blocked.TryGetValue<bool>(out var held) ? held : null);
        }
        catch (ArgumentException) { return null; }
    }
}
