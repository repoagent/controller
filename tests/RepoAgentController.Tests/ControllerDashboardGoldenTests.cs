using Xunit;
using static RepoAgentController.Tests.BaselineReleaseCheckTests;

namespace RepoAgentController.Tests;

// The dashboard Discussion's title and body, byte for byte, as each publication hands them to the outbox: a running controller
// with every section filled, then its graceful stop, then a later lifetime's stop after a failure. The status reads the status
// record, Discussion state, settings and checkout fakes. ControllerDashboardGolden.txt holds representative complete publications.
public sealed class ControllerDashboardGoldenTests
{
    const string Category = "Controller status", Repository = "owner/repository", Url = "https://github.com/owner/repository/discussions/";
    static readonly DateTimeOffset Start = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
    readonly SteppedClock clock = new(Start);
    readonly InMemoryDiscussions github = new();
    // The settings and the outbox, as the controller's state files hold both.
    readonly InMemoryControllerState state = new();
    readonly InMemoryDiscussionStore discussions = new();
    readonly InMemoryStatusRecord record = new();
    readonly InMemoryCheckoutWork checkout = new();
    readonly InMemoryBaselineReleases published = new();

    static Dictionary<string, string> Golden()
    {
        using var stream = typeof(ControllerDashboardGoldenTests).Assembly.GetManifestResourceStream("ControllerDashboardGolden.txt")!;
        using var reader = new StreamReader(stream);
        var sections = reader.ReadToEnd().Split("--- ", StringSplitOptions.RemoveEmptyEntries);
        return sections.ToDictionary(section => section[..section.IndexOf(" ---\n", StringComparison.Ordinal)],
            section => section[(section.IndexOf(" ---\n", StringComparison.Ordinal) + 5)..]);
    }

    static ObservedInput Input(string id) => new(Start, """{"kind":"comment.created","subject":{"id":"ID"}}""".Replace("ID", id), null);

    static WaitingInput Waiting(int inputs, DeliveryRetry? retry = null)
    {
        var waiting = new WaitingInput("{}") { Retry = retry };
        for (var input = 1; input <= inputs; input++) waiting.Inputs.Add(Input("DC_" + input));
        return waiting;
    }

    void Save()
    {
        var saved = new DiscussionState(Repository) { AgentLogin = "test-app[bot]" };
        saved.Discussions["D1"] = new(Url + 1) { SessionId = "S1", Running = new("T1", Start.AddMinutes(-30)), Waiting = Waiting(1) };
        saved.Discussions["D2"] = new(Url + 2) { SessionId = "S2", Pending = new("send", Start.AddMinutes(-1), ["DC_2"], 1), Waiting = Waiting(1) };
        saved.Discussions["D3"] = new(Url + 3) { SessionId = "S3", Waiting = Waiting(2, new(2, Start.AddMinutes(5))) };
        saved.Discussions["D4"] = new(Url + 4) { SessionId = "S4", Waiting = Waiting(3) };
        saved.Discussions["D5"] = new(Url + 5) { SessionId = "S5" };
        saved.Discussions["D6"] = new("javascript:alert(1)") { Waiting = Waiting(1) };
        discussions.WriteDiscussions(saved);
        state.WriteSettings(new(new Dictionary<string, SettingValue>
        {
            [Bridge.ActiveSessionSetting] = new(SettingState.Configured, "1") { Since = Start },
            ["REPOAGENT_MODEL"] = new(SettingState.ReadFailed, "gpt-5.5", "HTTP 503") { Since = Start },
            ["REPOAGENT_REASONING"] = new(SettingState.Invalid, null, "must be one of low, medium, high"),
            ["REPOAGENT_ENROLLMENT_CATEGORIES"] = new(SettingState.Absent, null)
        }));
        // The managed checkout holds new turns for a refresh; it records no agent turn.
        checkout.Work = new(new HashSet<string>(), true);
    }

    ControllerStatus Open(string mode) => new(Repository, mode, record, discussions, state, checkout, clock);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2000", Justification = "ControllerDashboard owns and asynchronously disposes its BaselineReleases.")]
    ControllerDashboard Dashboard(ControllerStatus status, GitHubOutbox outbox) => new(Category, status, outbox,
            new BaselineReleases(published, _ => Task.FromResult<string?>(Inventory("running")), clock, new RecordingLog(), text => text), clock, new RecordingLog(), text => text);

    // The last publication handed to the outbox, once it names the newest release; each publication arms the next heartbeat.
    async Task<DashboardOperation> Publication(string release)
    {
        while (state.Outbox?.Operations.OfType<DashboardOperation>().SingleOrDefault() is not { } publication || !publication.Body.Contains(release, StringComparison.Ordinal))
            await clock.Armed();
        return state.Outbox!.Operations.OfType<DashboardOperation>().Single();
    }

    // Each lifetime starts its own outbox over the saved state, as a restarted controller does.
    GitHubOutbox Outbox() => new(Repository, github, InMemoryDiscussions.App, state, clock, new RecordingLog());

    static void Log(ControllerStatus status, int seconds, string kind, string message) => status.Record(new(Start.AddSeconds(seconds), kind, message));

    [Fact]
    public async Task The_dashboard_title_and_body_present_a_heartbeat_overview_while_running_and_at_each_kind_of_stop()
    {
        Save();
        var current = Release(101, image: "running");
        var newest = Release(103, gh: "2.103.1", codex: "0.164.0");
        published.Publish(current);
        published.Publish(Release(102));
        published.Publish(newest);
        var actual = new Dictionary<string, string>();

        var status = Open("polling");
        Log(status, 0, "ready", "repository=owner/repository actor=test-app[bot] mode=polling");
        Log(status, 0, "app-token-refreshed", "installation=234");
        Log(status, 0, "codex-auth-supplied", "account credentials installed");
        Log(status, 0, "forwarded", Url + "1 session=S1 changes=1 sources=DC_7 waiting=0 latest=" + Url + "1#discussioncomment-7");
        // Eleven failures: the dashboard lists the newest ten, newest first.
        for (var failure = 1; failure <= 8; failure++) Log(status, 0, "dispatch-error", $"failure {failure}");
        Log(status, 0, "delivery-retry", Url + "3 session=S3 attempt=2: delivery refused `the input`\n  with   spacing");
        Log(status, 1, "poll-error", "GitHub said " + new string('x', 320));
        Log(status, 2, "app-refresh-error", "GitHub App credential renewal failed: HTTP 401 Bad credentials @someone [link](https://example.org)");
        Log(status, 10, "session-created", Url + "4 session=S4");
        status.Running(["S1", "S9"]); // S9's Discussion record is forgotten while its turn runs.
        github.Category(Category);
        await using (var dashboard = Dashboard(status, Outbox()))
        {
            dashboard.Start(TestContext.Current.CancellationToken);
            var running = await Publication(newest.Tag);
            actual["running"] = running.Title + "\n" + running.Body;
            clock.Advance(TimeSpan.FromSeconds(15));
        }
        actual["stopped"] = github.Dashboards.Single().Title + "\n" + github.Dashboards.Single().Body;
        status.Dispose();

        clock.Advance(TimeSpan.FromMinutes(1));
        var later = Open("webhook");
        Log(later, 75, "ready", "repository=owner/repository actor=test-app[bot] mode=webhook");
        Log(later, 75, "codex-auth-failed", "turn failed: unexpected status 401 Unauthorized");
        await using (var dashboard = Dashboard(later, Outbox()))
        {
            dashboard.Start(TestContext.Current.CancellationToken);
            await Publication(newest.Tag);
            dashboard.Failed("Repository webhook registration failed:\n `hooks` returned HTTP 422");
        }
        actual["failed"] = github.Dashboards.Single().Title + "\n" + github.Dashboards.Single().Body;
        later.Dispose();

        var golden = Golden();
        Assert.Equal(golden.Keys, actual.Keys);
        foreach (var (name, text) in golden) Assert.Equal(text, actual[name]);
    }
}
