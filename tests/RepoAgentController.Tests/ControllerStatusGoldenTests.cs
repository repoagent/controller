using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace RepoAgentController.Tests;

// The status output and the status record file, byte for byte, for files a controller wrote: the Discussion state, settings
// and refresh journal beside the state file, over two lifetimes. ControllerStatusGolden.txt holds what 1f36818 produced.
// Not parallel: one test captures stdout.
[Collection("Managed checkout environment")]
public sealed class ControllerStatusGoldenTests : IDisposable
{
    const string Repository = "owner/repository", Url = "https://github.com/owner/repository/discussions/";
    static readonly DateTimeOffset Start = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    readonly string directory = Path.Combine(Path.GetTempPath(), "status-golden-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider clock = new(Start);
    string State => Path.Combine(directory, "bridge.json");

    static Dictionary<string, string> Golden()
    {
        using var stream = typeof(ControllerStatusGoldenTests).Assembly.GetManifestResourceStream("ControllerStatusGolden.txt")!;
        var sections = new StreamReader(stream).ReadToEnd().Split("--- ", StringSplitOptions.RemoveEmptyEntries);
        return sections.ToDictionary(section => section[..section.IndexOf(" ---\n", StringComparison.Ordinal)],
            section => section[(section.IndexOf(" ---\n", StringComparison.Ordinal) + 5)..].TrimEnd('\n'));
    }

    static ObservedInput Input(string id) => new(Start, """{"kind":"comment.created","subject":{"id":"ID"}}""".Replace("ID", id), null);

    void WriteFiles()
    {
        Directory.CreateDirectory(directory);
        var state = new DiscussionState(Repository) { AgentLogin = "test-app[bot]" };
        state.Discussions["D1"] = new(Url + 1) { SessionId = "S1" };
        var pending = new WaitingInput("{}");
        pending.Inputs.Add(Input("C2"));
        state.Discussions["D2"] = new(Url + 2) { SessionId = "S2", Waiting = pending, Pending = new("send", Start.AddMinutes(1), ["C2"], 1) };
        var retrying = new WaitingInput("{}") { Retry = new(2, Start.AddMinutes(5)) };
        retrying.Inputs.AddRange([Input("C3"), Input("C4")]);
        state.Discussions["D3"] = new(Url + 3) { SessionId = "S3", Waiting = retrying };
        var behind = new WaitingInput("{}");
        behind.Inputs.Add(Input("C5"));
        state.Discussions["D4"] = new(Url + 4) { SessionId = "S4", Running = new("T4", Start.AddMinutes(2)), Waiting = behind };
        var unstarted = new WaitingInput("{}") { ContextJson = "{}" };
        unstarted.Inputs.Add(Input("C6"));
        state.Discussions["D5"] = new(Url + 5) { Waiting = unstarted };
        state.Discussions["D6"] = new(Url + 6) { SessionId = "S6" };
        var files = new ControllerStateFiles(State);
        files.WriteDiscussions(state);
        files.WriteSettings(new(new Dictionary<string, SettingValue>
        {
            ["REPOAGENT_MAX_ACTIVE_SESSIONS"] = new(SettingState.Configured, "3") { Since = Start },
            ["REPOAGENT_MODEL"] = new(SettingState.Invalid, null, "must be a model name")
        }));
        // As CheckoutRefresh saves its journal when it starts over an earlier lifetime's admission: S6's native turn is still
        // recorded, so admission is blocked with its reason.
        File.WriteAllText(State + ".refresh.json", """{"version":1,"root":"/workspace/repository","owner":"owner","repositoryId":7,"requested":1,"completed":1,"active":{"6f1c2a9e0b7d4e5f8a3c1d2e4f6a8b0c":{"thread":"S6","turn":"T6","since":1,"confirmed":true}},"reason":"Native activity or checkout update is uncertain; inspect state before recovery","blocked":true,"running":1}""");
    }

    void Log(ControllerStatus status, int seconds, string kind, string message) => status.Record(new(Start.AddSeconds(seconds), kind, message));

    [Fact]
    public void Status_and_its_record_are_the_bytes_1f36818_wrote_for_a_controller_written_state_over_two_lifetimes()
    {
        WriteFiles();
        var golden = Golden();
        var actual = new Dictionary<string, string>();

        var first = StatusFiles.Open(State, Repository, "polling", clock);
        Log(first, 1, "ready", "repository=owner/repository actor=test-app[bot] mode=polling");
        Log(first, 2, "app-token-refreshed", "installation=7");
        Log(first, 3, "codex-auth-supplied", "account credentials installed");
        Log(first, 4, "session-created", Url + "1 session=S1");
        Log(first, 5, "forwarded", Url + "1 session=S1 changes=1 sources=C1 waiting=0 latest=" + Url + "1#discussioncomment-7");
        Log(first, 6, "delivery-retry", Url + "3 session=S3 attempt=2 redelivery=false inputs=2 sources=C3 retry-at=2026-10-25T09:05:00.0000000+00:00: native refused");
        Log(first, 7, "input-waiting", Url + "5 session= status=none waiting=1");
        Log(first, 8, "discovered", Url + "5");
        first.Running(["S9", "S4"]); // S9's Discussion record is forgotten while its turn runs.
        clock.Advance(TimeSpan.FromSeconds(10));
        actual["live"] = OperatorStatusJson.Text(first.Snapshot());
        Assert.Equal(actual["live"], OperatorStatusJson.Text(StatusFiles.Read(State, clock)!)); // As status reads it from the files.
        clock.Advance(TimeSpan.FromSeconds(10));
        first.Dispose();
        actual["record"] = File.ReadAllText(StatusFiles.PathFor(State));
        if (!OperatingSystem.IsWindows()) // The record holds failure messages; only its owner reads it.
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(StatusFiles.PathFor(State)));
        clock.Advance(TimeSpan.FromSeconds(10));
        actual["stopped"] = OperatorStatusJson.Text(StatusFiles.Read(State, clock)!);

        var second = StatusFiles.Open(State, Repository, "webhook", clock);
        clock.Advance(TimeSpan.FromSeconds(10));
        Log(second, 40, "app-refresh-error", "GitHub App installation token refresh failed: HTTP 401");
        clock.Advance(TimeSpan.FromSeconds(1));
        Log(second, 41, "codex-auth-failed", "turn failed: unexpected status 401 Unauthorized");
        clock.Advance(TimeSpan.FromSeconds(1));
        Log(second, 42, "fatal", "GitHub App installation token refresh failed: HTTP 401");
        actual["restarted"] = OperatorStatusJson.Text(second.Snapshot());
        second.Dispose();
        actual["failed"] = File.ReadAllText(StatusFiles.PathFor(State));

        Assert.Equal(golden.Keys, actual.Keys);
        foreach (var (name, text) in golden) Assert.Equal(text, actual[name]);
    }

    [Fact]
    public void A_status_record_that_cannot_be_written_is_reported_on_stdout_at_the_clock_time_and_the_lifetime_continues()
    {
        WriteFiles();
        Directory.CreateDirectory(StatusFiles.PathFor(State) + ".tmp"); // The record's temporary file cannot be created.
        var previous = Console.Out;
        var stdout = new StringWriter();
        Console.SetOut(TextWriter.Synchronized(stdout));
        string state;
        try
        {
            using var status = StatusFiles.Open(State, Repository, "polling", clock);
            Log(status, 1, "ready", "mode=polling");
            state = status.Snapshot().Controller.State;
        }
        finally { Console.SetOut(previous); }

        // Its start, ready and stop each fail to write the record.
        var errors = stdout.ToString().Split('\n').Where(line => line.Contains(" status-error ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, errors.Length);
        Assert.All(errors, line => Assert.Matches(@"^2026-10-25T09:00:00\.0000000\+00:00 status-error \S.*$", line));
        Assert.Equal("running", state);
        Assert.False(File.Exists(StatusFiles.PathFor(State)));
    }

    // Each damaged form that 1f36818's lenient JSON readers read differently from the typed readers that replaced them, and the
    // status record forms it read as they are still read: (file
    // beside the state file, dotted path in it, JSON put there or null to remove it, whether a running lifetime reads it, the
    // status parts it changes, only those parts as status now reports them). The pull request body lists what 1f36818 reported.
    public static TheoryData<string, string, string?, bool, string, string> DamagedForms => new()
    {
        // A state the controller cannot start from reports none of its sessions or its App login.
        { "", "version", "2", false, "actor sessions", """ null [] """ },
        { "", "repository", "5", false, "repository actor sessions", """ "owner/repository" null [] """ },
        { "", "agentLogin", "5", false, "actor sessions", """ null [] """ },
        { "", "repository", null, false, "repository", """ "" """ },
        // A record or key that once failed status reads.
        { "", "discussions.D1", """{"url":"x","url":"y","sources":{}}""", false, "work.sessions sessions.0.discussion", """ 4 "https://github.com/owner/repository/discussions/2" """ },
        { "", "discussions.D1.url", "5", false, "sessions.0.discussion", """ "" """ },
        { "", "discussions.D1.sessionId", "4", false, "sessions.0.session", """ null """ },
        { "", "discussions.D6.sessionId", "4", false, "work.sessions sessions.5.session", """ 4 null """ },
        { "", "discussions.D2.pending", "5", false, "sessions.1.state sessions.1.since sessions.1.operation", """ "pending" null null """ },
        { "", "discussions.D2.pending", """{"operation":"send","operation":"send"}""", false, "sessions.1.state sessions.1.since sessions.1.operation", """ "pending" null null """ },
        { "", "discussions.D3.waiting", "5", false, "work.retrying work.waiting sessions.2.state", """ 0 3 "idle" """ },
        { "", "discussions.D3.waiting", "[]", false, "work.retrying work.waiting sessions.2.state", """ 0 3 "idle" """ },
        { "", "discussions.D3.waiting.retry", "5", false, "work.retrying work.waiting sessions.2.state", """ 0 3 "idle" """ },
        // A delivery, retry or turn in a form no writer leaves reports none of its times or counts.
        { "", "discussions.D2.pending", """{"operation":5}""", false, "sessions.1.operation", """ null """ },
        { "", "discussions.D2.pending", """{"since":"soon","operation":"send"}""", false, "sessions.1.since sessions.1.operation", """ null null """ },
        { "", "discussions.D3.waiting.retry", """{"attempts":"x"}""", false, "sessions.2.attempts", """ null """ },
        { "", "discussions.D3.waiting.retry", """{"attempts":1}""", false, "sessions.2.attempts", """ null """ },
        { "", "discussions.D4.running", """{"turn":"T4","since":"soon"}""", true, "sessions.3.since", """ null """ },
        // Waiting input that cannot be read, such as a retry with no time, holds no retry and no inputs.
        { "", "discussions.D3.waiting.retry", """{"attempts":1,"after":"soon"}""", false, "work.retrying work.waiting sessions.2.state", """ 0 3 "idle" """ },
        { "", "discussions.D3.waiting.inputs", "5", false, "work.retrying work.waiting sessions.2.state", """ 0 3 "idle" """ },
        { "", "discussions.D3.waiting.inputs", null, false, "work.retrying work.waiting sessions.2.state", """ 0 3 "idle" """ },
        { "", "discussions.D3.waiting.inputs.0", "5", false, "work.retrying work.waiting sessions.2.state", """ 0 3 "idle" """ },
        { "", "discussions.D3.waiting.inputs.0.change", null, false, "work.retrying work.waiting sessions.2.state", """ 0 3 "idle" """ },
        // A delivery time in another form is reported in the form a writer leaves.
        { "", "discussions.D2.pending.since", "\"2026-10-25T09:01:00Z\"", false, "sessions.1.since", """ "2026-10-25T09:01:00.0000000\u002B00:00" """ },
        // A recorded controller that once failed status is reported as read.
        { ".status.json", "controller", "5", false, "controller", """ 5 """ },
        { ".status.json", "controller.state", "5", false, "controller.state", """ 5 """ },
        // A record with a repeated key reads as none, so the next lifetime starts; 1f36818 failed that start.
        { ".status.json", "", """{"version":1,"controller":{"state":"running"},"controller":{"state":"stopped"}}""", true, "controller.state failures", """ "running" [] """ },
        // Read as 1f36818 read them: recorded parts in a form no writer leaves are reported as read.
        { ".status.json", "authentication", "5", false, "authentication", """ 5 """ },
        { ".status.json", "failures", "5", false, "failures", """ 5 """ },
        { ".status.json", "failures", "[5]", false, "failures", """ [5] """ },
        { ".status.json", "failures.0", "null", false, "failures", """ [null] """ },
        { ".status.json", "failures.0.extra", "1", false, "failures.0.extra", """ 1 """ },
        { ".status.json", "latestActivity", "5", false, "latestActivity", """ 5 """ },
        // Settings are reported as the typed reader reads them, and the limit from that.
        { ".settings.json", "", "5", false, "settings work.limit", """ null 4 """ },
        { ".settings.json", "settings.REPOAGENT_MAX_ACTIVE_SESSIONS", "5", false, "settings.REPOAGENT_MAX_ACTIVE_SESSIONS work.limit", """ null 4 """ },
        { ".settings.json", "settings.REPOAGENT_MAX_ACTIVE_SESSIONS.since", null, false, "settings.REPOAGENT_MAX_ACTIVE_SESSIONS work.limit", """ null 4 """ },
        { ".settings.json", "settings.REPOAGENT_MAX_ACTIVE_SESSIONS.state", "\"weird\"", false, "settings.REPOAGENT_MAX_ACTIVE_SESSIONS.state", """ "pending" """ },
        { ".settings.json", "settings.REPOAGENT_MAX_ACTIVE_SESSIONS.value", "3", false, "settings.REPOAGENT_MAX_ACTIVE_SESSIONS.value", """ null """ },
        { ".settings.json", "settings.REPOAGENT_MAX_ACTIVE_SESSIONS.extra", "1", false, "settings.REPOAGENT_MAX_ACTIVE_SESSIONS.extra", """ null """ },
        { ".settings.json", "settings.REPOAGENT_MODEL.reason", "5", false, "settings.REPOAGENT_MODEL.reason", """ null """ },
        // A journal entry that cannot be read records no turn; admission that is not true or false is not reported.
        { ".refresh.json", "active.6f1c2a9e0b7d4e5f8a3c1d2e4f6a8b0c", "5", true, "work.running sessions.5.state", """ 1 "idle" """ },
        { ".refresh.json", "active.6f1c2a9e0b7d4e5f8a3c1d2e4f6a8b0c.thread", "5", true, "work.running sessions.5.state", """ 1 "idle" """ },
        { ".refresh.json", "blocked", "\"yes\"", true, "work.admissionBlocked", """ null """ },
    };

    [Theory]
    [MemberData(nameof(DamagedForms))]
    public void A_damaged_file_beside_the_state_file_is_reported_as_the_typed_readers_read_it(string suffix, string path, string? json, bool live,
        string parts, string expected)
    {
        WriteFiles();
        var first = StatusFiles.Open(State, Repository, "polling", clock);
        Log(first, 5, "forwarded", Url + "1 session=S1 changes=1 sources=C1 waiting=0 latest=" + Url + "1#discussioncomment-7");
        Log(first, 6, "delivery-retry", Url + "3 attempt=2: native refused");
        first.Running(["S9", "S4"]);
        first.Dispose();
        Damage(State + suffix, path, json);

        JsonObject status;
        if (live)
        {
            using var second = StatusFiles.Open(State, Repository, "polling", clock);
            Log(second, 30, "ready", "mode=polling");
            second.Running(["S4"]);
            status = OperatorStatusJson.Render(second.Snapshot());
        }
        else status = OperatorStatusJson.Render(StatusFiles.Read(State, clock)!);

        var reported = " " + string.Join(" ", parts.Split(' ').Select(part => part.Split('.').Aggregate((JsonNode?)status,
            (node, key) => node is JsonArray array ? array[int.Parse(key)] : node?[key])?.ToJsonString() ?? "null")) + " ";
        Assert.Equal(expected, reported);
    }

    // Replaces the JSON at the dotted path with json, or removes it when json is null; the empty path is the whole file.
    static void Damage(string file, string path, string? json)
    {
        if (path.Length == 0)
        {
            File.WriteAllText(file, json);
            return;
        }
        var root = JsonNode.Parse(File.ReadAllText(file))!;
        var keys = path.Split('.');
        var parent = keys[..^1].Aggregate(root, (node, key) => node is JsonArray array ? array[int.Parse(key)]! : node[key]!);
        // A placeholder value stands where the JSON goes, so a form no JsonNode holds, such as a repeated key, is written as given.
        if (json is null) parent.AsObject().Remove(keys[^1]);
        else if (parent is JsonArray array) array[int.Parse(keys[^1])] = "\u0001";
        else parent[keys[^1]] = "\u0001";
        File.WriteAllText(file, root.ToJsonString().Replace("\"\\u0001\"", json));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
