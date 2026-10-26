using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Xunit;

namespace RepoAgentController.Tests;

// The managed checkout's refresh journal, .refresh.json beside the state file, as the controller reads and writes it at start:
// a new journal, and a recorded one round-tripped with every key its writers produce and keys this controller does not write.
public sealed class RefreshJournalGoldenTests : IDisposable
{
    const string Root = "/workspace/repository", Owner = "0123456789abcdef0123456789abcdef";
    readonly string directory = Path.Combine(Path.GetTempPath(), "refresh-journal-" + Guid.NewGuid().ToString("N"));
    readonly string statePath;

    public RefreshJournalGoldenTests()
    {
        Directory.CreateDirectory(directory);
        statePath = Path.Combine(directory, "bridge.json");
    }

    string JournalPath => statePath + ".refresh.json";

    // As the refresh journal wrote it: keys in the order its writers add them, an admission per resolution its writers record
    // (bound, acknowledged and confirmed, unconfirmed) and one still unbound, a refresh applying, and a reason. Every object a
    // key could be added to carries one this controller does not write ("later"). With admissions recorded it reads as blocked.
    const string RecordedBlocked = """
        {"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":4,"completed":2,
        "active":{
        "a1":{"thread":"S1","turn":"T1","since":3,"confirmed":true,"later":1},
        "a2":{"thread":"S2","turn":"","since":5,"confirmed":false,"unconfirmed":true},
        "a3":{"thread":"","turn":"","since":6,"confirmed":false}},
        "blocked":true,"running":0,"reason":"Native activity or checkout update is uncertain; inspect state before recovery",
        "applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":4,"later":1},
        "later":{"version":2}}
        """;

    // A settled journal: every requested refresh completed, no admission outstanding, and the last deferral's reason kept.
    const string RecordedSettled = """
        {"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":7,"completed":7,
        "active":{},"blocked":false,"running":0,"reason":"authoritative default-branch lookup unavailable; retry pending","later":"é <x>"}
        """;

    [Theory]
    [InlineData(RecordedBlocked)]
    [InlineData(RecordedSettled)]
    public async Task A_recorded_journal_round_trips_every_key_its_writers_produce_and_keeps_keys_this_controller_does_not_write(string recorded)
    {
        Record(recorded);

        await using (Open()) { }

        Assert.Equal(Canonical(JsonNode.Parse(recorded)), Canonical(JsonNode.Parse(File.ReadAllText(JournalPath))));
    }

    [Fact]
    public async Task The_journal_is_written_with_its_keys_in_one_order_then_the_keys_this_controller_does_not_write()
    {
        // RefreshJournalWritten.txt holds, one per line, the journal a first start writes, then the two recorded journals as
        // written back at start.
        var written = new List<string>();
        await using (Open()) written.Add(File.ReadAllText(JournalPath));
        foreach (var recorded in new[] { RecordedBlocked, RecordedSettled })
        {
            Record(recorded);
            await using (Open()) written.Add(File.ReadAllText(JournalPath));
        }
        using var golden = typeof(RefreshJournalGoldenTests).Assembly.GetManifestResourceStream("RefreshJournalWritten.txt")!;

        Assert.Equal(new StreamReader(golden).ReadToEnd(), string.Join("\n", written) + "\n");
    }

    // The journal is private to its owner, as its writer leaves it.
    void Record(string journal)
    {
        File.WriteAllText(JournalPath, journal);
        File.SetUnixFileMode(JournalPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    CheckoutRefresh Open() => new(Checkout(), statePath, false, false);

    // A checkout bound to the journal's root, owner and repository, without a workspace: these tests never refresh it.
    static ManagedCheckout Checkout()
    {
        var checkout = (ManagedCheckout)RuntimeHelpers.GetUninitializedObject(typeof(ManagedCheckout));
        void Set(string field, object value) => typeof(ManagedCheckout).GetField(field,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(checkout, value);
        Set("<Root>k__BackingField", Root);
        Set("metadata", new JsonObject { ["owner"] = Owner });
        Set("repository", new RepositoryCheckout(345, "owner/project", "https://github.com/owner/project.git", "main"));
        return checkout;
    }

    // Keys in ordinal order at every level, so two documents compare by their keys and values alone.
    static string Canonical(JsonNode? node) => node switch
    {
        JsonObject value => "{" + string.Join(",", value.OrderBy(field => field.Key, StringComparer.Ordinal)
            .Select(field => JsonValue.Create(field.Key).ToJsonString() + ":" + Canonical(field.Value))) + "}",
        JsonArray items => "[" + string.Join(",", items.Select(Canonical)) + "]",
        null => "null",
        _ => node.ToJsonString()
    };

    public void Dispose() => Directory.Delete(directory, true);
}
