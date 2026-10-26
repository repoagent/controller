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

    const string Bound = "\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345";
    const string Settled = "\"requested\":4,\"completed\":4,\"active\":{}";
    const string Entry = "{\"thread\":\"S1\",\"turn\":\"\",\"since\":3,\"confirmed\":false}";
    static string Journal(string keys) => "{" + keys + "}";

    // Journals in forms no writer leaves, with what a start makes of each: the failure it stops with, or the journal it writes
    // and the reason it records once an agent turn it cannot match starts.
    public static TheoryData<string, string?, bool, string> Damaged() => new()
    {
        // f742e2b stopped with JsonReaderException.
        { "not JSON", "{", false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with NullReferenceException.
        { "null", "null", false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "a list", "[]", false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "a number", "5", false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "text", "\"x\"", false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "version text", Journal($"\"version\":\"1\",\"root\":\"{Root}\",\"owner\":\"{Owner}\",\"repositoryId\":345,{Settled}"), false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "version 1.0", Journal($"\"version\":1.0,\"root\":\"{Root}\",\"owner\":\"{Owner}\",\"repositoryId\":345,{Settled}"), false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        { "version 2", Journal($"\"version\":2,\"root\":\"{Root}\",\"owner\":\"{Owner}\",\"repositoryId\":345,{Settled}"), false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        { "version missing", Journal($"\"root\":\"{Root}\",\"owner\":\"{Owner}\",\"repositoryId\":345,{Settled}"), false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "root a number", Journal($"\"version\":1,\"root\":5,\"owner\":\"{Owner}\",\"repositoryId\":345,{Settled}"), false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        { "root another checkout", Journal($"\"version\":1,\"root\":\"/other\",\"owner\":\"{Owner}\",\"repositoryId\":345,{Settled}"), false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "owner a number", Journal($"\"version\":1,\"root\":\"{Root}\",\"owner\":5,\"repositoryId\":345,{Settled}"), false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "repositoryId text", Journal($"\"version\":1,\"root\":\"{Root}\",\"owner\":\"{Owner}\",\"repositoryId\":\"345\",{Settled}"), false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "repositoryId 345.0", Journal($"\"version\":1,\"root\":\"{Root}\",\"owner\":\"{Owner}\",\"repositoryId\":345.0,{Settled}"), false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with NullReferenceException.
        { "requested missing", Journal($"{Bound},\"completed\":4,\"active\":{{}}"), false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with NullReferenceException.
        { "requested null", Journal($"{Bound},\"requested\":null,\"completed\":4,\"active\":{{}}"), false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "requested text", Journal($"{Bound},\"requested\":\"4\",\"completed\":4,\"active\":{{}}"), false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        { "requested below completed", Journal($"{Bound},\"requested\":3,\"completed\":4,\"active\":{{}}"), false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with NullReferenceException.
        { "completed missing", Journal($"{Bound},\"requested\":4,\"active\":{{}}"), false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        { "completed negative", Journal($"{Bound},\"requested\":4,\"completed\":-1,\"active\":{{}}"), false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        { "active a list", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":[]"), false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "blocked text", Journal($"{Bound},{Settled},\"blocked\":\"true\""), false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "blocked 1", Journal($"{Bound},{Settled},\"blocked\":1"), false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "running text", Journal($"{Bound},{Settled},\"running\":\"0\""), false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "running 1.5", Journal($"{Bound},{Settled},\"running\":1.5"), false,
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        { "a key repeated", Journal($"{Bound},{Settled},\"blocked\":false,\"blocked\":true"), false,
            "ArgumentException: An item with the same key has already been added. Key: blocked (Parameter 'key')" },
        // f742e2b wrote the keys in the order read.
        { "blocked missing", Journal($"{Bound},{Settled},\"running\":0"), false,
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":false,\"running\":0} | \"Unmatched native lifecycle event\"" },
        // f742e2b wrote the keys in the order read.
        { "applying empty", Journal($"{Bound},{Settled},\"blocked\":false,\"running\":0,\"applying\":{{}}"), false,
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\",\"applying\":{}} | \"Unmatched native lifecycle event\"" },
        { "blocked and running missing", Journal($"{Bound},{Settled}"), false,
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":false,\"running\":0} | \"Unmatched native lifecycle event\"" },
        // f742e2b wrote the keys in the order read.
        { "running null", Journal($"{Bound},{Settled},\"running\":null"), false,
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":false,\"running\":0} | \"Unmatched native lifecycle event\"" },
        // f742e2b wrote the keys in the order read.
        { "running 1", Journal($"{Bound},{Settled},\"running\":1"), false,
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Unmatched native lifecycle event\"" },
        // f742e2b wrote the keys in the order read.
        { "keys in another order", Journal($"\"reason\":\"r\",\"running\":0,\"blocked\":false,\"active\":{{}},\"requested\":4,\"completed\":4,{Bound}"), false,
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":false,\"running\":0,\"reason\":\"r\"} | \"Unmatched native lifecycle event\"" },
        // f742e2b wrote the keys in the order read.
        { "unknown key among them", Journal($"{Bound},\"later\":1,{Settled},\"blocked\":false,\"running\":0"), false,
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":false,\"running\":0,\"later\":1} | \"Unmatched native lifecycle event\"" },
        // f742e2b wrote the keys in the order read.
        { "no journal after sessions", null, true,
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":0,\"completed\":0,\"active\":{},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Unmatched native lifecycle event\"" },
        { "reason not text", Journal($"{Bound},{Settled},\"blocked\":false,\"running\":0,\"reason\":5"), false,
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":false,\"running\":0,\"reason\":5} | \"Unmatched native lifecycle event\"" },
        // f742e2b kept "applying": null.
        { "applying null", Journal($"{Bound},{Settled},\"blocked\":false,\"running\":0,\"applying\":null"), false,
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":false,\"running\":0} | \"Unmatched native lifecycle event\"" },
        // f742e2b wrote the keys in the order read.
        { "applying a number", Journal($"{Bound},{Settled},\"blocked\":false,\"running\":0,\"applying\":5"), false,
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\",\"applying\":5} | \"Unmatched native lifecycle event\"" },
        // f742e2b recorded the error reading this admission as the reason.
        { "admission null", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":null}},\"blocked\":true,\"running\":0"), false,
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":null},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Unmatched native lifecycle event\"" },
        // f742e2b recorded the error reading this admission as the reason.
        { "admission a number", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":5}},\"blocked\":true,\"running\":0"), false,
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":5},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Unmatched native lifecycle event\"" },
        // f742e2b recorded the error reading this admission as the reason.
        { "admission thread a number", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":{{\"thread\":5,\"turn\":\"\",\"since\":3,\"confirmed\":false}}}},\"blocked\":true,\"running\":0"), false,
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":{\"thread\":5,\"turn\":\"\",\"since\":3,\"confirmed\":false}},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Unmatched native lifecycle event\"" },
        // f742e2b recorded the error reading this admission as the reason.
        { "admission key repeated", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":{{\"thread\":\"S1\",\"thread\":\"S2\",\"turn\":\"\",\"since\":3,\"confirmed\":false}}}},\"blocked\":true,\"running\":0"), false,
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":{\"thread\":\"S1\",\"thread\":\"S2\",\"turn\":\"\",\"since\":3,\"confirmed\":false}},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Unmatched native lifecycle event\"" },
        { "admission since text", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":{{\"thread\":\"S1\",\"turn\":\"\",\"since\":\"3\",\"confirmed\":false}}}},\"blocked\":true,\"running\":0"), false,
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":{\"thread\":\"S1\",\"turn\":\"\",\"since\":\"3\",\"confirmed\":false}},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Native activity or checkout update is uncertain; inspect state before recovery\"" },
        { "admission unconfirmed false", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":{{\"thread\":\"S1\",\"turn\":\"\",\"since\":3,\"confirmed\":false,\"unconfirmed\":false}}}},\"blocked\":true,\"running\":0"), false,
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":{\"thread\":\"S1\",\"turn\":\"\",\"since\":3,\"confirmed\":false,\"unconfirmed\":false}},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Native activity or checkout update is uncertain; inspect state before recovery\"" },
        { "admission unblocked", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":{Entry}}},\"blocked\":false,\"running\":0"), false,
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":{\"thread\":\"S1\",\"turn\":\"\",\"since\":3,\"confirmed\":false}},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Native activity or checkout update is uncertain; inspect state before recovery\"" },
    };

    [Theory]
    [MemberData(nameof(Damaged))]
    public async Task A_journal_in_a_form_no_writer_leaves_starts_as_pinned(string form, string? journal, bool priorSessions, string expected)
    {
        Assert.NotEmpty(form);
        if (journal is not null) Record(journal);
        string outcome;
        try
        {
            await using var refresh = Open(priorSessions);
            outcome = File.ReadAllText(JournalPath);
            refresh.Observe(new AgentTurnStarted(1, "S1", "T9"));
            outcome += " | " + JsonNode.Parse(File.ReadAllText(JournalPath))!["reason"]?.ToJsonString();
        }
        catch (Exception error) { outcome = error.GetType().Name + ": " + error.Message; }
        Assert.Equal(expected, outcome);
    }

    [Fact]
    public async Task A_start_writes_the_journal_private_and_refuses_one_left_mid_replacement_open_to_others_or_through_a_link()
    {
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        const string Unsafe = "Managed checkout blocked: private state is missing or unsafe; preserve this workspace and inspect it before retrying";
        await using (Open()) { }
        Assert.Equal(Private, File.GetUnixFileMode(JournalPath));

        Directory.CreateDirectory(JournalPath + ".tmp");
        Assert.Equal("Incomplete refresh journal; preserve it for manual recovery", Assert.Throws<IOException>(() => Open()).Message);
        Directory.Delete(JournalPath + ".tmp");

        File.SetUnixFileMode(JournalPath, Private | UnixFileMode.GroupRead);
        Assert.Equal(Unsafe, Assert.Throws<IOException>(() => Open()).Message);
        File.SetUnixFileMode(JournalPath, Private);

        File.Move(JournalPath, JournalPath + ".target");
        File.CreateSymbolicLink(JournalPath, JournalPath + ".target");
        Assert.Equal("Managed checkout blocked: symbolic links are not supported in managed paths; preserve this workspace and inspect it before retrying",
            Assert.Throws<IOException>(() => Open()).Message);
    }

    // The journal is private to its owner, as its writer leaves it.
    void Record(string journal)
    {
        File.WriteAllText(JournalPath, journal);
        File.SetUnixFileMode(JournalPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    CheckoutRefresh Open(bool priorSessions = false) => new(new UnusedCheckout(), new(Root, Owner, 345, "refs/heads/main"), new RefreshJournalFile(statePath),
        priorSessions, false, new SteppedClock(DateTimeOffset.UnixEpoch), new RecordingLog());

    // A checkout bound to the journal's root, owner and repository, without a workspace: these tests never refresh it.
    sealed class UnusedCheckout : IRepositoryCheckout
    {
        public Task<bool> Refresh(Action<string, string> beforeApply, CancellationToken ct) => throw new InvalidOperationException("Never refreshed");
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
