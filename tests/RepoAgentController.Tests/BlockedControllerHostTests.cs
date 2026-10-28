using System.Text.Json.Nodes;
using Xunit;

namespace RepoAgentController.Tests;

// Real Kestrel over the existing Core composition, without executable stand-ins. This does not exercise BridgeCommand's credential/bootstrap preconditions.
public sealed class BlockedControllerHostTests
{
    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2000", Justification = "The dashboard owns and asynchronously disposes the release check.")]
    public async Task Startup_uncertainty_serves_busy_HTTP_status_and_publishes_the_blocked_dashboard_without_agent_work()
    {
        var clock = new SteppedClock(DateTimeOffset.UnixEpoch);
        var state = new InMemoryControllerState();
        var discussions = new InMemoryDiscussionStore();
        var journals = new InMemoryRefreshJournal
        { Saved = new("/workspace/repository", "0123456789abcdef0123456789abcdef", 345) };
        using var status = new ControllerStatus("owner/repository", "polling", new InMemoryStatusRecord(), discussions, state, new JournalWork(journals), clock);
        var log = new StatusLog(status, clock);
        var runtime = new FakeAgentRuntime();
        var checkout = new ScriptedCheckout();
        await using var refresh = new CheckoutRefresh(checkout, new("/workspace/repository", "0123456789abcdef0123456789abcdef", 345, "refs/heads/main"),
            journals, false, true, clock, log);
        var sessions = new AgentSessions(runtime, log, refresh);

        await refresh.Start(sessions, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        var github = new InMemoryDiscussions();
        github.Category("Controller status");
        await using var outbox = new GitHubOutbox("owner/repository", github, InMemoryDiscussions.App, state, clock, log);
        await using var dashboard = new ControllerDashboard("Controller status", status, outbox,
            new BaselineReleaseCheck(new InMemoryBaselineReleases(), _ => Task.FromResult<string?>(null), clock, log, text => text), clock, log, text => text);
        dashboard.Start(TestContext.Current.CancellationToken);
        await outbox.Flush(TestContext.Current.CancellationToken);
        var port = ControllerHarness.FreePort();
        await using var server = await ControllerHttpServer.Start(port, () => StatusSnapshot.From(status.Snapshot()), null, TestContext.Current.CancellationToken);
        using var client = new HttpClient();

        var response = JsonNode.Parse(await client.GetStringAsync($"http://127.0.0.1:{port}/status", TestContext.Current.CancellationToken))!;

        Assert.Equal("busy", response["state"]!.GetValue<string>());
        Assert.True(response["work"]!["admissionBlocked"]!.GetValue<bool>());
        Assert.Equal(0, response["work"]!["running"]!.GetValue<int>());
        Assert.Contains("refresh-blocked", Assert.Single(github.Dashboards).Body);
        Assert.Contains("recover-checkout", github.Dashboards[0].Body);
        Assert.Equal(0, checkout.Refreshes);
        Assert.Empty(runtime.Requests);
    }

    sealed class JournalWork(IRefreshJournal journals) : ICheckoutWork
    {
        public CheckoutWork? Read() => new(new HashSet<string>(), journals.Read()?.Blocked);
    }

    sealed class StatusLog(ControllerStatus status, TimeProvider clock) : IControllerLog
    {
        public void Write(string kind, string message) => status.Record(new(clock.GetUtcNow(), kind, message));
    }
}
