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

    [Fact]
    public async Task Startup_keeps_the_exact_original_journal_and_Discussion_bytes_before_normalizing_a_pending_delivery()
    {
        const string original = "{ \"version\":1, \"root\":\"/workspace/repository\", \"owner\":\"0123456789abcdef0123456789abcdef\", \"repositoryId\":345, \"requested\":4, \"completed\":3, \"active\":{}, \"blocked\":false, \"running\":1 }\n";
        const string originalState = "{\"version\":1,\"repository\":\"owner/repository\",\"discussions\":{}}\n";
        Record(original);
        File.WriteAllText(statePath, originalState);

        await using (Open(pendingDiscussion: true)) { }

        var snapshot = Assert.Single(Directory.GetDirectories(statePath + ".checkout-evidence"));
        Assert.Equal(original, File.ReadAllText(Path.Combine(snapshot, "refresh.json")));
        Assert.Equal(originalState, File.ReadAllText(Path.Combine(snapshot, "state.json")));
        Assert.True(File.Exists(Path.Combine(snapshot, "complete")));
        Assert.True(new RefreshJournalFile(statePath).Read()!.Blocked);
        Assert.Equal(1, new RefreshJournalFile(statePath).Read()!.Running);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(snapshot, "state.json")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(snapshot));
        }
    }

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

        using var reader = new StreamReader(golden);
        Assert.Equal(reader.ReadToEnd(), string.Join("\n", written) + "\n");
    }

    const string Bound = "\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345";
    const string Settled = "\"requested\":4,\"completed\":4,\"active\":{}";
    const string Entry = "{\"thread\":\"S1\",\"turn\":\"\",\"since\":3,\"confirmed\":false}";
    static string Journal(string keys) => "{" + keys + "}";

    // Journals in forms no writer leaves, with what a start makes of each: the failure it stops with, or the journal it writes
    // and the reason it records once an agent turn it cannot match starts.
    public static TheoryData<string, string?, string> Damaged() => new()
    {
        // f742e2b stopped with JsonReaderException.
        { "not JSON", "{",
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with NullReferenceException.
        { "null", "null",
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "a list", "[]",
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "a number", "5",
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "text", "\"x\"",
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "version text", Journal($"\"version\":\"1\",\"root\":\"{Root}\",\"owner\":\"{Owner}\",\"repositoryId\":345,{Settled}"),
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "version 1.0", Journal($"\"version\":1.0,\"root\":\"{Root}\",\"owner\":\"{Owner}\",\"repositoryId\":345,{Settled}"),
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        { "version 2", Journal($"\"version\":2,\"root\":\"{Root}\",\"owner\":\"{Owner}\",\"repositoryId\":345,{Settled}"),
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        { "version missing", Journal($"\"root\":\"{Root}\",\"owner\":\"{Owner}\",\"repositoryId\":345,{Settled}"),
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "root a number", Journal($"\"version\":1,\"root\":5,\"owner\":\"{Owner}\",\"repositoryId\":345,{Settled}"),
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "owner a number", Journal($"\"version\":1,\"root\":\"{Root}\",\"owner\":5,\"repositoryId\":345,{Settled}"),
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "repositoryId text", Journal($"\"version\":1,\"root\":\"{Root}\",\"owner\":\"{Owner}\",\"repositoryId\":\"345\",{Settled}"),
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "repositoryId 345.0", Journal($"\"version\":1,\"root\":\"{Root}\",\"owner\":\"{Owner}\",\"repositoryId\":345.0,{Settled}"),
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with NullReferenceException.
        { "requested missing", Journal($"{Bound},\"completed\":4,\"active\":{{}}"),
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with NullReferenceException.
        { "requested null", Journal($"{Bound},\"requested\":null,\"completed\":4,\"active\":{{}}"),
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "requested text", Journal($"{Bound},\"requested\":\"4\",\"completed\":4,\"active\":{{}}"),
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with NullReferenceException.
        { "completed missing", Journal($"{Bound},\"requested\":4,\"active\":{{}}"),
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        { "active a list", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":[]"),
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "blocked text", Journal($"{Bound},{Settled},\"blocked\":\"true\""),
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "blocked 1", Journal($"{Bound},{Settled},\"blocked\":1"),
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "running text", Journal($"{Bound},{Settled},\"running\":\"0\""),
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with InvalidOperationException.
        { "running 1.5", Journal($"{Bound},{Settled},\"running\":1.5"),
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b stopped with ArgumentException.
        { "a key repeated", Journal($"{Bound},{Settled},\"blocked\":false,\"blocked\":true"),
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b wrote the keys in the order read.
        { "blocked missing", Journal($"{Bound},{Settled},\"running\":0"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":false,\"running\":0} | \"Unmatched native lifecycle event\"" },
        // f742e2b wrote the keys in the order read.
        { "applying empty", Journal($"{Bound},{Settled},\"blocked\":false,\"running\":0,\"applying\":{{}}"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\",\"applying\":{}} | \"Unmatched native lifecycle event\"" },
        // f742e2b stopped with ArgumentException.
        { "active repeated", Journal($"{Bound},{Settled},\"active\":{{}},\"blocked\":false,\"running\":0"),
            "IOException: Refresh journal binding or format differs; manual recovery required" },
        // f742e2b wrote the keys in the order read.
        { "applying with a key repeated", Journal($"{Bound},{Settled},\"blocked\":false,\"running\":0,\"applying\":{{\"generation\":1,\"generation\":2}}"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\",\"applying\":{\"generation\":1,\"generation\":2}} | \"Unmatched native lifecycle event\"" },
        { "blocked and running missing", Journal($"{Bound},{Settled}"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":false,\"running\":0} | \"Unmatched native lifecycle event\"" },
        // f742e2b wrote the keys in the order read.
        { "running null", Journal($"{Bound},{Settled},\"running\":null"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":false,\"running\":0} | \"Unmatched native lifecycle event\"" },
        // f742e2b wrote the keys in the order read.
        { "keys in another order", Journal($"\"reason\":\"r\",\"running\":0,\"blocked\":false,\"active\":{{}},\"requested\":4,\"completed\":4,{Bound}"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":false,\"running\":0,\"reason\":\"r\"} | \"Unmatched native lifecycle event\"" },
        // f742e2b wrote the keys in the order read.
        { "unknown key among them", Journal($"{Bound},\"later\":1,{Settled},\"blocked\":false,\"running\":0"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":false,\"running\":0,\"later\":1} | \"Unmatched native lifecycle event\"" },
        { "reason not text", Journal($"{Bound},{Settled},\"blocked\":false,\"running\":0,\"reason\":5"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":false,\"running\":0,\"reason\":5} | \"Unmatched native lifecycle event\"" },
        // f742e2b kept "applying": null.
        { "applying null", Journal($"{Bound},{Settled},\"blocked\":false,\"running\":0,\"applying\":null"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":false,\"running\":0} | \"Unmatched native lifecycle event\"" },
        // f742e2b wrote the keys in the order read.
        { "applying a number", Journal($"{Bound},{Settled},\"blocked\":false,\"running\":0,\"applying\":5"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\",\"applying\":5} | \"Unmatched native lifecycle event\"" },
        // f742e2b recorded the error reading this admission as the reason.
        { "admission null", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":null}},\"blocked\":true,\"running\":0"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":null},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Unmatched native lifecycle event\"" },
        // f742e2b recorded the error reading this admission as the reason.
        { "admission a number", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":5}},\"blocked\":true,\"running\":0"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":5},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Unmatched native lifecycle event\"" },
        // f742e2b recorded the error reading this admission as the reason.
        { "admission thread a number", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":{{\"thread\":5,\"turn\":\"\",\"since\":3,\"confirmed\":false}}}},\"blocked\":true,\"running\":0"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":{\"thread\":5,\"turn\":\"\",\"since\":3,\"confirmed\":false}},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Unmatched native lifecycle event\"" },
        // f742e2b recorded the error reading this admission as the reason.
        { "admission key repeated", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":{{\"thread\":\"S1\",\"thread\":\"S2\",\"turn\":\"\",\"since\":3,\"confirmed\":false}}}},\"blocked\":true,\"running\":0"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":{\"thread\":\"S1\",\"thread\":\"S2\",\"turn\":\"\",\"since\":3,\"confirmed\":false}},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Unmatched native lifecycle event\"" },
        // f742e2b placed the turn with the awaiting admission, then recorded the error reading the other as the reason
        // (Object reference not set to an instance of an object.).
        { "admission null beside one awaiting the turn", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":{Entry},\"b\":null}},\"blocked\":false,\"running\":0"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":{\"thread\":\"S1\",\"turn\":\"\",\"since\":3,\"confirmed\":false},\"b\":null},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Native activity or checkout update is uncertain; inspect state before recovery\"" },
        // f742e2b placed the turn with the awaiting admission, then recorded the error reading the other as the reason
        // (The node must be of type 'JsonObject'.).
        { "admission a number beside one awaiting the turn", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":{Entry},\"b\":5}},\"blocked\":false,\"running\":0"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":{\"thread\":\"S1\",\"turn\":\"\",\"since\":3,\"confirmed\":false},\"b\":5},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Native activity or checkout update is uncertain; inspect state before recovery\"" },
        // f742e2b wrote every admission back as read, and recorded the error reading them as the reason.
        { "an admission ID repeated", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":{Entry},\"b\":{Entry},\"a\":5}},\"blocked\":true,\"running\":0"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":{\"thread\":\"S1\",\"turn\":\"\",\"since\":3,\"confirmed\":false},\"b\":{\"thread\":\"S1\",\"turn\":\"\",\"since\":3,\"confirmed\":false},\"a\":5},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Native activity or checkout update is uncertain; inspect state before recovery\"" },
        // f742e2b stopped with ArgumentException: without a recorded block, it counted the admissions.
        { "an admission ID repeated, unblocked", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":{Entry},\"a\":{Entry}}},\"blocked\":false,\"running\":0"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":{\"thread\":\"S1\",\"turn\":\"\",\"since\":3,\"confirmed\":false},\"a\":{\"thread\":\"S1\",\"turn\":\"\",\"since\":3,\"confirmed\":false}},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Native activity or checkout update is uncertain; inspect state before recovery\"" },
        { "admission since text", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":{{\"thread\":\"S1\",\"turn\":\"\",\"since\":\"3\",\"confirmed\":false}}}},\"blocked\":true,\"running\":0"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":{\"thread\":\"S1\",\"turn\":\"\",\"since\":\"3\",\"confirmed\":false}},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Native activity or checkout update is uncertain; inspect state before recovery\"" },
        { "admission unconfirmed false", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":{{\"thread\":\"S1\",\"turn\":\"\",\"since\":3,\"confirmed\":false,\"unconfirmed\":false}}}},\"blocked\":true,\"running\":0"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":{\"thread\":\"S1\",\"turn\":\"\",\"since\":3,\"confirmed\":false,\"unconfirmed\":false}},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Native activity or checkout update is uncertain; inspect state before recovery\"" },
    };

    // A running count no writer leaves, beside a record that blocks the start before it: a recorded block, an admission, a refresh
    // applying, or all three. f742e2b read the count only when nothing before it blocked, so each of these started blocked and
    // rewrote the count to 0. Recovery now preserves its invalid representation instead. With nothing before it, the count stops the start (the
    // "running text" and "running 1.5" rows).
    public static TheoryData<string, string?, string> RunningNoWriterLeavesBehindABlock()
    {
        var rows = new TheoryData<string, string?, string>();
        foreach (var running in new[] { "\"0\"", "1.5", "true", "{}", "[]", "3000000000" })
        {
            rows.Add($"running {running} with a recorded block", Journal($"{Bound},{Settled},\"blocked\":true,\"running\":{running}"),
                "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Unmatched native lifecycle event\"".Replace("\"running\":0", "\"running\":" + running, StringComparison.Ordinal));
            rows.Add($"running {running} with an admission", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":{Entry}}},\"blocked\":false,\"running\":{running}"),
                "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":{\"thread\":\"S1\",\"turn\":\"\",\"since\":3,\"confirmed\":false}},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Native activity or checkout update is uncertain; inspect state before recovery\"".Replace("\"running\":0", "\"running\":" + running, StringComparison.Ordinal));
            rows.Add($"running {running} with a refresh applying", Journal($"{Bound},{Settled},\"blocked\":false,\"running\":{running},\"applying\":{{\"previous\":\"1111111111111111111111111111111111111111\",\"target\":\"2222222222222222222222222222222222222222\",\"generation\":4}}"),
                "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\",\"applying\":{\"previous\":\"1111111111111111111111111111111111111111\",\"target\":\"2222222222222222222222222222222222222222\",\"generation\":4}} | \"Unmatched native lifecycle event\"".Replace("\"running\":0", "\"running\":" + running, StringComparison.Ordinal));
            rows.Add($"running {running} with all three", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":{Entry}}},\"blocked\":true,\"running\":{running},\"applying\":{{\"previous\":\"1111111111111111111111111111111111111111\",\"target\":\"2222222222222222222222222222222222222222\",\"generation\":4}}"),
                "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":{\"thread\":\"S1\",\"turn\":\"\",\"since\":3,\"confirmed\":false}},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\",\"applying\":{\"previous\":\"1111111111111111111111111111111111111111\",\"target\":\"2222222222222222222222222222222222222222\",\"generation\":4}} | \"Native activity or checkout update is uncertain; inspect state before recovery\"".Replace("\"running\":0", "\"running\":" + running, StringComparison.Ordinal));
        }
        return rows;
    }


    [Theory]
    [MemberData(nameof(Damaged))]
    [MemberData(nameof(RunningNoWriterLeavesBehindABlock))]
    public async Task A_journal_in_a_form_no_writer_leaves_is_preserved_or_refused(string form, string? journal, string expected)
    {
        Assert.NotEmpty(form);
        if (journal is not null) Record(journal);
        string outcome;
        try
        {
            await using var refresh = Open();
            outcome = File.ReadAllText(JournalPath);
            refresh.Observe(new AgentTurnStarted(1, "S1", "T9"));
            outcome += " | " + JsonNode.Parse(File.ReadAllText(JournalPath))!["reason"]?.ToJsonString();
        }
        catch (Exception error) { outcome = error.GetType().Name + ": " + error.Message; }
        Assert.Equal(expected, outcome);
    }

    // Journals the controller's own writers leave, whose bytes differ from what f742e2b wrote for the same path: the fixed key
    // order moves the reason, after "blocked" and "running" and before "applying". Each is the same JSON as f742e2b wrote. The
    // f742e2b bytes are what f742e2b's writers left on the same path, each start over the journal the rest of the path left. The
    // last two rows start over a journal f742e2b left; f742e2b's own next start kept its order.
    // What f742e2b's writers left after an unsafe failure once applying began, at a first start after earlier sessions, and at a
    // start over a journal holding an applying record.
    const string LeftByFault = """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1},"reason":"boom"}""";
    const string LeftByBlockedStart = """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":0,"completed":0,"active":{},"reason":"Native activity or checkout update is uncertain; inspect state before recovery","blocked":true,"running":0}""";
    const string LeftByStartOverApplying = """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1},"reason":"Native activity or checkout update is uncertain; inspect state before recovery"}""";
    // What this controller writes at the same two starts.
    const string WrittenByBlockedStart = """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":0,"completed":0,"active":{},"blocked":true,"running":0,"reason":"Native activity or checkout update is uncertain; inspect state before recovery"}""";
    const string WrittenByStartOverApplying = """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"reason":"Native activity or checkout update is uncertain; inspect state before recovery","applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1}}""";
    const string ThenAStart = ", then a start", ThenARequest = ", then a request";

    public static TheoryData<string, string, string> Written() => new()
    {
        { "a first start after earlier sessions", LeftByBlockedStart, WrittenByBlockedStart },
        { "a first start after earlier sessions, then a start", LeftByBlockedStart, WrittenByBlockedStart },
        { "a first start after earlier sessions, then a start, then a start", LeftByBlockedStart, WrittenByBlockedStart },
        { "a first start with a Discussion delivery pending", LeftByBlockedStart, WrittenByBlockedStart },
        { "a first start with a Discussion delivery pending, then a start", LeftByBlockedStart, WrittenByBlockedStart },
        // The one row whose bytes match f742e2b's: the journal a process killed mid-refresh leaves, which the next row starts over.
        { "the applying record saved before applying",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":false,"running":0,"applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1}}""",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":false,"running":0,"applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1}}""" },
        { "the applying record saved before applying, then a start", LeftByStartOverApplying, WrittenByStartOverApplying },
        { "an unsafe failure after applying began",
            LeftByFault,
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"reason":"boom","applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1}}""" },
        { "an unsafe failure after applying began, then a start", LeftByStartOverApplying, WrittenByStartOverApplying },
        { "an unsafe failure after applying began, then a start, then a start", LeftByStartOverApplying, WrittenByStartOverApplying },
        { "a deferral after applying began",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1},"reason":"fetch refused: changed"}""",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"reason":"fetch refused: changed","applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1}}""" },
        { "a deferral after applying began, then a start", LeftByStartOverApplying, WrittenByStartOverApplying },
        { "a deferral after applying began, then a start, then a start", LeftByStartOverApplying, WrittenByStartOverApplying },
        { "a stop while applying",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1},"reason":"Refresh interrupted while applying"}""",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"reason":"Refresh interrupted while applying","applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1}}""" },
        { "a stop while applying, then a start", LeftByStartOverApplying, WrittenByStartOverApplying },
        { "a lost transport saved while applying",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1},"reason":"Native transport disconnected; completion unknown"}""",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"reason":"Native transport disconnected; completion unknown","applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1}}""" },
        { "a lost transport saved while applying, then a start", LeftByStartOverApplying, WrittenByStartOverApplying },
        { "a lost transport while applying",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1},"reason":"Native refresh became uncertain"}""",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"reason":"Native refresh became uncertain","applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1}}""" },
        { "a lost transport while applying, then a start", LeftByStartOverApplying, WrittenByStartOverApplying },
        { "an unmatched turn event saved while applying",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1},"reason":"Unmatched native lifecycle event"}""",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"reason":"Unmatched native lifecycle event","applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1}}""" },
        { "an unmatched turn event saved while applying, then a start", LeftByStartOverApplying, WrittenByStartOverApplying },
        { "a malformed turn identity saved while applying",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1},"reason":"Malformed native lifecycle identity"}""",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"reason":"Malformed native lifecycle identity","applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1}}""" },
        { "a running worker that finds the block a deferral after applying left",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1},"reason":"Checkout/native state uncertain; manual recovery required"}""",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"reason":"Checkout/native state uncertain; manual recovery required","applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1}}""" },
        { "a running worker that finds the block a deferral after applying left, then a start", LeftByStartOverApplying, WrittenByStartOverApplying },
        { "an unsafe failure after applying began, then a request",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":2,"completed":0,"active":{},"blocked":true,"running":0,"applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1},"reason":"boom"}""",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":2,"completed":0,"active":{},"blocked":true,"running":0,"reason":"boom","applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1}}""" },
        { "a first start after earlier sessions, then a request",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"reason":"Native activity or checkout update is uncertain; inspect state before recovery","blocked":true,"running":0}""",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"reason":"Native activity or checkout update is uncertain; inspect state before recovery"}""" },
        { "the next start after f742e2b's unsafe failure after applying", LeftByStartOverApplying, WrittenByStartOverApplying },
        { "the next start after f742e2b's first start after earlier sessions", LeftByBlockedStart, WrittenByBlockedStart },
    };

    [Theory]
    [MemberData(nameof(Written))]
    public async Task A_journal_a_writer_leaves_holds_what_f742e2b_wrote_in_the_fixed_key_order(string path, string f742e2b, string written)
    {
        Assert.Equal(written, await Write(path));
        Assert.Equal(Canonical(JsonNode.Parse(f742e2b)), Canonical(JsonNode.Parse(written)));
    }

    // The journal the writer path leaves: a start, or a refresh that began applying and then failed, deferred, stopped, lost its
    // agent transport or saw a turn event it could not place, at the idle boundary an admission takes; or a running worker that
    // finds a block. The paths ending "saved while applying" end where a process killed mid-refresh leaves the journal: with the
    // applying record, the lost transport or the turn event's fault saved before the checkout returned. A path ending
    // ", then a request" requests a refresh over the journal the rest of the path left, in the same lifetime. A path ending
    // ", then a start" starts again over it, as the controller does after its process ends. A next start begins over the journal
    // f742e2b left.
    async Task<string> Write(string path)
    {
        if (path.EndsWith(ThenAStart, StringComparison.Ordinal))
        {
            Record(await Write(path[..^ThenAStart.Length]));
            await using (Open(priorSessions: true)) { }
            return File.ReadAllText(JournalPath);
        }
        var thenRequest = path.EndsWith(ThenARequest, StringComparison.Ordinal);
        if (thenRequest) path = path[..^ThenARequest.Length];
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        CheckoutRefresh? refresh = null;
        string? saved = null;
        Task<bool> Applying(Action<string, string> beforeApply, Exception? then)
        {
            beforeApply("1111111111111111111111111111111111111111", "2222222222222222222222222222222222222222");
            saved = File.ReadAllText(JournalPath);
            return then is null ? Task.FromResult(false) : Task.FromException<bool>(then);
        }
        Refreshing Observing(AgentRuntimeEvent change) => new((apply, _) =>
        {
            var applied = Applying(apply, null);
            refresh!.Observe(change);
            saved = File.ReadAllText(JournalPath);
            return applied;
        });
        IRepositoryCheckout? checkout = path switch
        {
            "an unsafe failure after applying began" or "the applying record saved before applying" =>
                new Refreshing((apply, _) => Applying(apply, new IOException("boom"))),
            "a deferral after applying began" or "a running worker that finds the block a deferral after applying left" =>
                new Refreshing((apply, _) => Applying(apply, new CheckoutDeferred("fetch refused: changed"))),
            "a stop while applying" => new Refreshing((apply, ct) => { _ = Applying(apply, null); stopping.Cancel(); return Task.FromCanceled<bool>(ct); }),
            "a lost transport while applying" or "a lost transport saved while applying" => Observing(new AgentRuntimeDisconnected(1)),
            "an unmatched turn event saved while applying" => Observing(new AgentTurnStarted(1, "S9", "T9")),
            "a malformed turn identity saved while applying" => Observing(new AgentTurnStarted(1, "", "T9")),
            _ => null
        };
        var next = path.StartsWith("the next start", StringComparison.Ordinal);
        if (next) Record(path.EndsWith("unsafe failure after applying", StringComparison.Ordinal) ? LeftByFault : LeftByBlockedStart);
        var log = new RecordingLog();
        await using (refresh = Open(priorSessions: next || path == "a first start after earlier sessions", path == "a first start with a Discussion delivery pending", checkout, log))
        {
            if (path == "a running worker that finds the block a deferral after applying left")
            {
                // The start's refresh defers once applying began; the worker, woken by the start's request, then finds the block.
                await refresh.Start(new AgentSessions(new FakeAgentRuntime(), log, refresh), TimeSpan.FromMinutes(5), stopping.Token);
                await log.Written(2);
            }
            else if (checkout is not null)
            {
                refresh.Request();
                await Assert.ThrowsAnyAsync<Exception>(async () => (await refresh.Admit(stopping.Token)).Dispose());
            }
            if (thenRequest) refresh.Request();
        }
        return path.EndsWith("saved while applying", StringComparison.Ordinal) || path == "the applying record saved before applying"
            ? saved! : File.ReadAllText(JournalPath);
    }

    [Fact]
    public async Task A_start_writes_the_journal_private_and_refuses_one_left_mid_replacement_open_to_others_or_through_a_link()
    {
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        const string Unsafe = "Managed checkout blocked: private state is missing or unsafe; preserve this workspace and inspect it before retrying";
        await using (Open()) { }
        if (!OperatingSystem.IsWindows()) Assert.Equal(Private, File.GetUnixFileMode(JournalPath));

        Directory.CreateDirectory(JournalPath + ".tmp");
        Assert.Equal("Incomplete refresh journal; preserve it for manual recovery", Assert.Throws<IOException>(() => Open()).Message);
        Directory.Delete(JournalPath + ".tmp");

        if (!OperatingSystem.IsWindows()) // Only Unix records who may read the journal.
        {
            File.SetUnixFileMode(JournalPath, Private | UnixFileMode.GroupRead);
            Assert.Equal(Unsafe, Assert.Throws<IOException>(() => Open()).Message);
            File.SetUnixFileMode(JournalPath, Private);
        }

        File.Move(JournalPath, JournalPath + ".target");
        File.CreateSymbolicLink(JournalPath, JournalPath + ".target");
        Assert.Equal("Managed checkout blocked: symbolic links are not supported in managed paths; preserve this workspace and inspect it before retrying",
            Assert.Throws<IOException>(() => Open()).Message);
    }

    // The journal is private to its owner, as its writer leaves it.
    void Record(string journal)
    {
        File.WriteAllText(JournalPath, journal);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(JournalPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    CheckoutRefresh Open(bool priorSessions = false, bool pendingDiscussion = false, IRepositoryCheckout? checkout = null, RecordingLog? log = null) =>
        new(checkout ?? new UnusedCheckout(), new(Root, Owner, 345, "refs/heads/main"), new RefreshJournalFile(statePath), priorSessions, pendingDiscussion,
            new SteppedClock(DateTimeOffset.UnixEpoch), log ?? new RecordingLog());

    // A checkout whose refresh is scripted.
    sealed class Refreshing(Func<Action<string, string>, CancellationToken, Task<bool>> refresh) : IRepositoryCheckout
    {
        public Task<bool> Refresh(Action<string, string> beforeApply, CancellationToken ct) => refresh(beforeApply, ct);
    }

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
