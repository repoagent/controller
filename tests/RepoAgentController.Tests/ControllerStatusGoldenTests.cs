using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace RepoAgentController.Tests;

// The status output and the status record file, byte for byte, for files a controller wrote: the Discussion state, settings
// and refresh journal beside the state file, over two lifetimes. ControllerStatusGolden.txt holds what 1f36818 produced.
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
        // As CheckoutRefresh saves its journal: a native turn of S6 that the refresh journal records.
        File.WriteAllText(State + ".refresh.json", """{"version":1,"root":"/workspace/repository","owner":"owner","repositoryId":7,"requested":1,"completed":1,"active":{"1":{"thread":"S6","turn":"T6","since":1,"confirmed":true}},"blocked":true,"running":1}""");
    }

    void Log(ControllerStatus status, int seconds, string kind, string message) => status.Record(new(Start.AddSeconds(seconds), kind, message));

    [Fact]
    public void Status_and_its_record_are_the_bytes_1f36818_wrote_for_a_controller_written_state_over_two_lifetimes()
    {
        WriteFiles();
        var golden = Golden();
        var actual = new Dictionary<string, string>();

        var first = new ControllerStatus(State, Repository, "polling", clock);
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
        actual["live"] = ControllerStatus.Render(first.Snapshot());
        clock.Advance(TimeSpan.FromSeconds(10));
        first.Dispose();
        actual["record"] = File.ReadAllText(ControllerStatus.PathFor(State));
        clock.Advance(TimeSpan.FromSeconds(10));
        actual["stopped"] = ControllerStatus.Render(ControllerStatus.Read(State, clock)!);

        var second = new ControllerStatus(State, Repository, "webhook", clock);
        Log(second, 40, "app-refresh-error", "GitHub App installation token refresh failed: HTTP 401");
        Log(second, 41, "codex-auth-failed", "turn failed: unexpected status 401 Unauthorized");
        Log(second, 42, "fatal", "GitHub App installation token refresh failed: HTTP 401");
        actual["restarted"] = ControllerStatus.Render(second.Snapshot());
        second.Dispose();
        actual["failed"] = File.ReadAllText(ControllerStatus.PathFor(State));

        Assert.Equal(golden.Keys, actual.Keys);
        foreach (var (name, text) in golden) Assert.Equal(text, actual[name]);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
