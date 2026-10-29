using System.CommandLine;
using System.Text.Json;

namespace RepoAgentController;

static class ActivityCommand
{
    public static void Add(RootCommand root)
    {
        var state = new Option<FileInfo>("--state") { DefaultValueFactory = _ => new(".bridge-state.json") };
        var discussion = new Option<string?>("--discussion") { Description = "Mapped Discussion number or exact repository URL; omit to list mapped sessions" };
        var limit = new Option<int>("--limit") { DefaultValueFactory = _ => 100, Description = "Items per history page (1..100)" };
        var cursor = new Option<string?>("--cursor") { Description = "Opaque continuation printed by an earlier page in this controller/runtime lifetime" };
        var follow = new Option<bool>("--follow") { Description = "Follow existing session activity until cancellation, a gap or runtime restart" };
        var format = new Option<string>("--format") { DefaultValueFactory = _ => "text", Description = "Output text or deliberate local JSON lines" };
        var command = new Command("activity", "Privately read or follow the running controller's mapped agent sessions; never starts or steers work")
        { state, discussion, limit, cursor, follow, format };
        command.SetAction(async (result, ct) =>
        {
            var selected = result.GetValue(discussion);
            var output = result.GetValue(format);
            if (output is not ("text" or "json") || result.GetValue(limit) is < 1 or > 100 ||
                (selected is null && (result.GetValue(follow) || result.GetValue(cursor) is not null)))
            { Console.Error.WriteLine("Choose --format text|json, --limit 1..100 and a --discussion for --follow or --cursor."); return 1; }
            try
            {
                await foreach (var frame in ActivityClient.Read(result.GetValue(state)!.FullName, selected, result.GetValue(limit),
                    result.GetValue(cursor), result.GetValue(follow), ct))
                {
                    if (output == "json") Console.WriteLine(JsonSerializer.Serialize(frame, ActivityJson.Default.ActivityFrame));
                    else Print(frame, Console.Out);
                    if (frame.Kind == "gap") return 1;
                }
                return 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 0; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException or JsonException or OperationCanceledException or ArgumentException)
            { Console.Error.WriteLine("Activity unavailable: the controller must be running with a private local endpoint and an unambiguous mapped Discussion. Read a new snapshot after a gap or restart."); return 1; }
        });
        root.Subcommands.Add(command);
    }

    internal static void Print(ActivityFrame frame, TextWriter output)
    {
        if (frame.Inventory is { } inventory)
        {
            output.WriteLine($"Mapped sessions ({inventory.Sessions.Count}; omitted {inventory.Omitted}), lifetime={inventory.Lifetime}");
            foreach (var session in inventory.Sessions) output.WriteLine($"{session.Discussion} session={session.SessionId}");
        }
        if (frame.Snapshot is { } snapshot)
        {
            output.WriteLine($"{snapshot.Session.Discussion} session={snapshot.Session.SessionId} runtime={snapshot.Epoch} lifetime={snapshot.Lifetime}");
            output.WriteLine("Read evidence records provider-classified operations; it does not establish complete reading or compliance.");
            foreach (var item in snapshot.Items) Item(item);
            if (snapshot.SuppressedItems != 0) output.WriteLine($"Suppressed private/unsupported source items: {snapshot.SuppressedItems}");
            if (snapshot.Incomplete) output.WriteLine("History is incomplete; this page is not proof of complete session activity.");
            if (snapshot.Notice is { } notice) output.WriteLine(notice);
            if (snapshot.NextCursor is { } next) output.WriteLine($"Older page: --cursor {next}");
            else output.WriteLine("No older continuation reported by the runtime.");
        }
        if (frame.Item is { } update) { output.WriteLine($"UPDATE sequence={frame.Sequence}"); Item(update); }
        if (frame.Kind == "gap") output.WriteLine($"GAP: {frame.Notice} Select the Discussion again for a new snapshot.");
        void Item(ActivityItem item)
        {
            output.WriteLine($"[{item.Kind} {item.State}] turn={item.TurnId} item={item.Id}");
            if (item.Name is { Length: > 0 } name) output.WriteLine(name);
            if (item.Arguments is { } arguments) output.WriteLine($"Arguments: {arguments}");
            if (item.Text is { } text) output.WriteLine(text);
            if (item.Result is { } result) output.WriteLine($"Result: {result}");
            if (item.ExitCode is { } exit) output.WriteLine($"Exit code: {exit}");
            if (item.Reads is { } reads) foreach (var read in reads) output.WriteLine($"Read evidence: {read.Path}");
            if (item.Truncated) output.WriteLine("[text truncated]");
            if (item.Incomplete) output.WriteLine("[item incomplete or unsupported]");
        }
    }
}
