using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>
/// The <see cref="IRefreshJournal"/> adapter: <c>.refresh.json</c> beside the state file, private to its owner and replaced
/// whole. A journal left mid-replacement, or on a path through a symbolic link, is refused for manual recovery.
/// </summary>
sealed class RefreshJournalFile(string statePath) : IRefreshJournal
{
    readonly string path = statePath + ".refresh.json";

    public RefreshJournal? Read()
    {
        ManagedCheckout.CheckPath(path);
        if (Path.Exists(path + ".tmp")) throw new IOException("Incomplete refresh journal; preserve it for manual recovery");
        if (!File.Exists(path)) return null;
        var text = ManagedCheckout.ReadPrivate(path);
        try { return Parse(text); }
        catch (ArgumentException) { throw new IOException(RefreshJournal.Unusable); } // A key repeated in the journal or among its admissions.
    }

    public void Write(RefreshJournal journal) => ManagedCheckout.SavePrivate(path, Serialize(journal));

    static readonly string[] Keys = ["version", "root", "owner", "repositoryId", "requested", "completed", "active", "blocked", "running", "reason", "applying"];
    static readonly string[] AdmissionKeys = ["thread", "turn", "since", "confirmed", "unconfirmed"];

    /// <summary>
    /// The journal in <paramref name="text"/>. Each key the controller decides from must hold its writer's type; a missing
    /// <c>blocked</c> or <c>running</c> reads as false or 0. A <c>running</c> of another type is left for the start to refuse,
    /// which it does only when nothing before it blocks. An admission, a reason or a refresh applying in a form no writer
    /// leaves is kept as read, and so is every key this controller does not write.
    /// </summary>
    internal static RefreshJournal Parse(string text)
    {
        JsonObject? read = null;
        try { read = JsonNode.Parse(text) as JsonObject; } catch (JsonException) { }
        if (read is not { } journal || Value<int>(journal["version"]) != 1 || Text(journal["root"]) is not { } root ||
            Text(journal["owner"]) is not { } owner || Value<long>(journal["repositoryId"]) is not { } repositoryId ||
            Value<long>(journal["requested"]) is not { } requested || Value<long>(journal["completed"]) is not { } completed ||
            journal["active"] is not JsonObject active ||
            (journal["blocked"] is null ? false : Value<bool>(journal["blocked"])) is not { } blocked)
            throw new IOException(RefreshJournal.Unusable);
        var running = journal["running"] is null ? 0 : Value<int>(journal["running"]);
        var result = new RefreshJournal(root, owner, repositoryId)
        {
            // Running before RunningUnreadable, which setting Running clears.
            Requested = requested,
            Completed = completed,
            Blocked = blocked,
            Running = running ?? 0,
            RunningUnreadable = running is null,
            Unknown = [.. journal.Where(key => !Keys.Contains(key.Key)).Select(key => new UnknownKey(key.Key, Encode(key.Value)))]
        };
        if (journal.ContainsKey("reason"))
        {
            if (Text(journal["reason"]) is { } reason) result.Reason = reason;
            else result.ReasonAsRead = Encode(journal["reason"]);
        }
        if (journal["applying"] is { } applying) result.Applying = new("", "", 0) { AsRead = Encode(applying) };
        foreach (var (id, admission) in Admissions(active, text)) result.Active[id] = Admission(admission);
        return result;
    }

    // Each admission by its ID, as read. JsonObject refuses an ID read twice, so such a map is read again from the text; the ID
    // keeps its first place and its last admission. The journal still starts blocked, as it did before it was typed.
    static IEnumerable<(string Id, JsonNode? Admission)> Admissions(JsonObject active, string text)
    {
        try { return [.. active.Select(item => (item.Key, item.Value))]; }
        catch (ArgumentException)
        {
            using var document = JsonDocument.Parse(text);
            return [.. document.RootElement.GetProperty("active").EnumerateObject().Select(item => (item.Name, JsonNode.Parse(item.Value.GetRawText())))];
        }
    }

    // An admission as its writer leaves it: thread, turn and since, whether it is confirmed, and unconfirmed only when true.
    // Any other form is written back as read; its session and turn still match lifecycle events where they are text, and it is
    // confirmed or unconfirmed only where it says true, as the journal read such an admission before it was typed.
    static RefreshAdmission Admission(JsonNode? read)
    {
        var asRead = Encode(read);
        try
        {
            if (read is not JsonObject admission) return new(0) { AsRead = asRead };
            var (thread, turn, since, confirmed) = (Text(admission["thread"]), Text(admission["turn"]), Value<long>(admission["since"]), Value<bool>(admission["confirmed"]));
            var unconfirmed = Value<bool>(admission["unconfirmed"]) == true;
            if (thread is not null && turn is not null && since is { } order && confirmed is { } held && (unconfirmed || !admission.ContainsKey("unconfirmed")))
                return new(order)
                {
                    Thread = thread,
                    Turn = turn,
                    Confirmed = held,
                    Unconfirmed = unconfirmed,
                    Unknown = [.. admission.Where(key => !AdmissionKeys.Contains(key.Key)).Select(key => new UnknownKey(key.Key, Encode(key.Value)))]
                };
            return new(since ?? 0) { AsRead = asRead, Thread = thread ?? "", Turn = turn ?? "", Confirmed = confirmed == true, Unconfirmed = unconfirmed };
        }
        catch (ArgumentException) { return new(0) { AsRead = asRead }; } // A key repeated in the admission.
    }

    static T? Value<T>(JsonNode? node) where T : struct => node is JsonValue value && value.TryGetValue<T>(out var read) ? read : null;
    static string? Text(JsonNode? node) => node.OptionalText();

    internal static string Serialize(RefreshJournal journal)
    {
        var active = new JsonObject();
        foreach (var (id, admission) in journal.Active) active[id] = Admission(admission);
        var json = Json.Object(("version", 1), ("root", journal.Root), ("owner", journal.Owner), ("repositoryId", journal.RepositoryId),
            ("requested", journal.Requested), ("completed", journal.Completed), ("active", active), ("blocked", journal.Blocked), ("running", journal.Running));
        if (journal.Reason is { } reason) json["reason"] = reason;
        else if (journal.ReasonAsRead is { } asRead) json["reason"] = Decode(asRead);
        if (journal.Applying is { } applying)
            json["applying"] = applying.AsRead is { } recorded ? Decode(recorded)
                : Json.Object(("previous", applying.Previous), ("target", applying.Target), ("generation", applying.Generation));
        foreach (var key in journal.Unknown) json[key.Name] = Decode(key.Json);
        return json.ToJsonString();
    }

    static JsonNode? Admission(RefreshAdmission admission)
    {
        if (admission.AsRead is { } read) return Decode(read);
        var json = Json.Object(("thread", admission.Thread), ("turn", admission.Turn), ("since", admission.Since), ("confirmed", admission.Confirmed));
        if (admission.Unconfirmed) json["unconfirmed"] = true;
        foreach (var key in admission.Unknown) json[key.Name] = Decode(key.Json);
        return json;
    }

    static string Encode(JsonNode? node) => node?.ToJsonString() ?? "null";
    static JsonNode? Decode(string json) => JsonNode.Parse(json);
}
