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
        // f742e2b wrote every admission back as read, and recorded the error reading them as the reason.
        { "an admission ID repeated", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":{Entry},\"b\":{Entry},\"a\":5}},\"blocked\":true,\"running\":0"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":5,\"b\":{\"thread\":\"S1\",\"turn\":\"\",\"since\":3,\"confirmed\":false}},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Native activity or checkout update is uncertain; inspect state before recovery\"" },
        // f742e2b stopped with ArgumentException: without a recorded block, it counted the admissions.
        { "an admission ID repeated, unblocked", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":{Entry},\"a\":{Entry}}},\"blocked\":false,\"running\":0"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":{\"thread\":\"S1\",\"turn\":\"\",\"since\":3,\"confirmed\":false}},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Native activity or checkout update is uncertain; inspect state before recovery\"" },
        { "admission since text", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":{{\"thread\":\"S1\",\"turn\":\"\",\"since\":\"3\",\"confirmed\":false}}}},\"blocked\":true,\"running\":0"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":{\"thread\":\"S1\",\"turn\":\"\",\"since\":\"3\",\"confirmed\":false}},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Native activity or checkout update is uncertain; inspect state before recovery\"" },
        { "admission unconfirmed false", Journal($"{Bound},\"requested\":4,\"completed\":4,\"active\":{{\"a\":{{\"thread\":\"S1\",\"turn\":\"\",\"since\":3,\"confirmed\":false,\"unconfirmed\":false}}}},\"blocked\":true,\"running\":0"),
            "{\"version\":1,\"root\":\"/workspace/repository\",\"owner\":\"0123456789abcdef0123456789abcdef\",\"repositoryId\":345,\"requested\":4,\"completed\":4,\"active\":{\"a\":{\"thread\":\"S1\",\"turn\":\"\",\"since\":3,\"confirmed\":false,\"unconfirmed\":false}},\"blocked\":true,\"running\":0,\"reason\":\"Native activity or checkout update is uncertain; inspect state before recovery\"} | \"Native activity or checkout update is uncertain; inspect state before recovery\"" },
    };

    [Theory]
    [MemberData(nameof(Damaged))]
    public async Task A_journal_in_a_form_no_writer_leaves_starts_as_pinned(string form, string? journal, string expected)
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
    // f742e2b bytes are what f742e2b's writers left on the same path. The last two rows start over a journal f742e2b left; f742e2b's
    // own next start kept its order.
    // What f742e2b's writers left after an unsafe failure once applying began, and at a first start after earlier sessions.
    const string LeftByFault = """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1},"reason":"boom"}""";
    const string LeftByBlockedStart = """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":0,"completed":0,"active":{},"reason":"Native activity or checkout update is uncertain; inspect state before recovery","blocked":true,"running":0}""";

    public static TheoryData<string, string, string> Written() => new()
    {
        { "a first start after earlier sessions",
            LeftByBlockedStart,
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":0,"completed":0,"active":{},"blocked":true,"running":0,"reason":"Native activity or checkout update is uncertain; inspect state before recovery"}""" },
        { "a first start with a Discussion delivery pending",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":0,"completed":0,"active":{},"reason":"Native activity or checkout update is uncertain; inspect state before recovery","blocked":true,"running":0}""",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":0,"completed":0,"active":{},"blocked":true,"running":0,"reason":"Native activity or checkout update is uncertain; inspect state before recovery"}""" },
        { "an unsafe failure after applying began",
            LeftByFault,
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"reason":"boom","applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1}}""" },
        { "a deferral after applying began",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1},"reason":"fetch refused: changed"}""",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"reason":"fetch refused: changed","applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1}}""" },
        { "a stop while applying",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1},"reason":"Refresh interrupted while applying"}""",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"reason":"Refresh interrupted while applying","applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1}}""" },
        { "a lost transport while applying",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1},"reason":"Native refresh became uncertain"}""",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"reason":"Native refresh became uncertain","applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1}}""" },
        { "the next start after f742e2b's unsafe failure after applying",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1},"reason":"Native activity or checkout update is uncertain; inspect state before recovery"}""",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":1,"completed":0,"active":{},"blocked":true,"running":0,"reason":"Native activity or checkout update is uncertain; inspect state before recovery","applying":{"previous":"1111111111111111111111111111111111111111","target":"2222222222222222222222222222222222222222","generation":1}}""" },
        { "the next start after f742e2b's first start after earlier sessions",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":0,"completed":0,"active":{},"reason":"Native activity or checkout update is uncertain; inspect state before recovery","blocked":true,"running":0}""",
            """{"version":1,"root":"/workspace/repository","owner":"0123456789abcdef0123456789abcdef","repositoryId":345,"requested":0,"completed":0,"active":{},"blocked":true,"running":0,"reason":"Native activity or checkout update is uncertain; inspect state before recovery"}""" },
    };

    [Theory]
    [MemberData(nameof(Written))]
    public async Task A_journal_a_writer_leaves_holds_what_f742e2b_wrote_in_the_fixed_key_order(string path, string f742e2b, string written)
    {
        Assert.Equal(written, await Write(path));
        Assert.Equal(Canonical(JsonNode.Parse(f742e2b)), Canonical(JsonNode.Parse(written)));
    }

    // The journal the writer path leaves: a start, or a refresh that began applying and then failed, deferred, stopped or lost its
    // agent transport, at the idle boundary an admission takes. A next start begins over the journal f742e2b left.
    async Task<string> Write(string path)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        CheckoutRefresh? refresh = null;
        Task<bool> Applying(Action<string, string> beforeApply, Exception? then)
        {
            beforeApply("1111111111111111111111111111111111111111", "2222222222222222222222222222222222222222");
            return then is null ? Task.FromResult(false) : Task.FromException<bool>(then);
        }
        IRepositoryCheckout? checkout = path switch
        {
            "an unsafe failure after applying began" => new Refreshing((apply, _) => Applying(apply, new IOException("boom"))),
            "a deferral after applying began" => new Refreshing((apply, _) => Applying(apply, new CheckoutDeferred("fetch refused: changed"))),
            "a stop while applying" => new Refreshing((apply, ct) => { _ = Applying(apply, null); stopping.Cancel(); return Task.FromCanceled<bool>(ct); }),
            "a lost transport while applying" => new Refreshing((apply, _) => { var applied = Applying(apply, null); refresh!.Observe(new AgentRuntimeDisconnected(1)); return applied; }),
            _ => null
        };
        var next = path.StartsWith("the next start", StringComparison.Ordinal);
        if (next) Record(path.EndsWith("unsafe failure after applying", StringComparison.Ordinal) ? LeftByFault : LeftByBlockedStart);
        await using (refresh = Open(priorSessions: next || path == "a first start after earlier sessions", path == "a first start with a Discussion delivery pending", checkout))
        {
            if (checkout is not null)
            {
                refresh.Request();
                await Assert.ThrowsAnyAsync<Exception>(async () => (await refresh.Admit(stopping.Token)).Dispose());
            }
        }
        return File.ReadAllText(JournalPath);
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

    CheckoutRefresh Open(bool priorSessions = false, bool pendingDiscussion = false, IRepositoryCheckout? checkout = null) => new(checkout ?? new UnusedCheckout(),
        new(Root, Owner, 345, "refs/heads/main"), new RefreshJournalFile(statePath), priorSessions, pendingDiscussion, new SteppedClock(DateTimeOffset.UnixEpoch),
        new RecordingLog());

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
