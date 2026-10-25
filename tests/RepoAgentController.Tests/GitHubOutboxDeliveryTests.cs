using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace RepoAgentController.Tests;

// The core GitHub outbox in-process: what it retains across lifetimes, when it retries, how it settles an uncertain write and
// what it logs. GitHub is the Discussions fake; how a GitHub response is classified is the adapter's.
public sealed class GitHubOutboxDeliveryTests
{
    const string Repository = "owner/repository", Category = "Controller status";
    const string Url = "https://github.com/owner/repository/discussions/1", Comment = Url + "#discussioncomment-7";
    static readonly DateTimeOffset Start = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
    readonly FakeTimeProvider clock = new(Start);
    readonly InMemoryDiscussions github = new();
    readonly InMemoryControllerState state = new();
    readonly RecordingLog log = new();

    GitHubOutbox Lifetime(TimeProvider? time = null) => new(Repository, github, state, time ?? clock, log);

    [Theory]
    [InlineData("retry-after", 61)]
    [InlineData("reset", 61)]
    [InlineData("unsignalled", 60)]
    public async Task A_rate_limit_pauses_every_retained_operation_until_the_signalled_time_even_across_a_restart(string signal, int seconds)
    {
        var ct = TestContext.Current.CancellationToken;
        github.Fail(signal switch
        {
            "retry-after" => new(GitHubFailure.RateLimited, "secondary rate limit", retryAfter: TimeSpan.FromSeconds(61)),
            "reset" => new(GitHubFailure.RateLimited, "API rate limit exceeded", resetAt: Start.AddSeconds(61)),
            _ => new(GitHubFailure.RateLimited, "Too many requests")
        });
        await using (var outbox = Lifetime())
        {
            outbox.Receipt("D1", Url, hasEyesReceipt: false);
            Assert.False(await outbox.Flush(ct));
            outbox.Receipt("C7", Comment, hasEyesReceipt: false); // Due now, but the pause covers the installation.
            Assert.False(await outbox.Flush(ct));
        }
        var due = Start.AddSeconds(seconds);
        var message = signal switch { "retry-after" => "secondary rate limit", "reset" => "API rate limit exceeded", _ => "Too many requests" };
        Assert.Equal([("receipt-error", $"{Url} subject=D1 attempt=1 RateLimited: {message}; retained for retry at {due:O}")], log.Entries);

        clock.Advance(TimeSpan.FromSeconds(seconds - 1));
        await using (var outbox = Lifetime()) Assert.False(await outbox.Flush(ct));
        Assert.Equal(["addReaction D1"], github.Calls);

        clock.Advance(TimeSpan.FromSeconds(1));
        await using (var outbox = Lifetime()) Assert.True(await outbox.Flush(ct));
        Assert.Equal(["addReaction D1", "addReaction D1", "addReaction C7"], github.Calls);
        Assert.True(github.HasEyes("D1"));
        Assert.True(github.HasEyes("C7"));
        Assert.Equal([("receipt", Url), ("receipt", Comment)], log.Entries.Skip(1));
        Assert.Empty(state.Outbox!.Operations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_uncertain_receipt_is_settled_from_GitHub_before_it_is_written_again(bool appliedBeforeTheResponseFailed)
    {
        var ct = TestContext.Current.CancellationToken;
        github.Fail(new(GitHubFailure.Transient, "HTTP 502", uncertain: true), appliedBeforeTheResponseFailed);
        await using (var outbox = Lifetime())
        {
            outbox.Receipt("D1", Url, hasEyesReceipt: false);
            Assert.False(await outbox.Flush(ct));
        }

        clock.Advance(TimeSpan.FromSeconds(15));
        await using (var outbox = Lifetime()) Assert.True(await outbox.Flush(ct));
        Assert.True(github.HasEyes("D1"));
        Assert.Equal(appliedBeforeTheResponseFailed ? ["addReaction D1", "reactionGroups D1"] : ["addReaction D1", "reactionGroups D1", "addReaction D1"],
            github.Calls);
        Assert.Equal(appliedBeforeTheResponseFailed ? ("receipt-reconciled", Url) : ("receipt", Url), log.Entries[^1]);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_in_the_background_after_a_doubling_delay()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new SteppedClock(Start);
        // The one failure GitHub classifies as transient and certain: the installation token was refused, and renewal may recover it.
        github.Fail(new(GitHubFailure.Transient, "HTTP 401: Bad credentials"));
        github.Fail(new(GitHubFailure.Transient, "HTTP 401: Bad credentials"));
        await using var outbox = Lifetime(time);
        outbox.Start(ct);

        outbox.Receipt("D1", Url, hasEyesReceipt: false);
        await time.Armed();
        time.Advance(TimeSpan.FromSeconds(15));
        await time.Armed();
        time.Advance(TimeSpan.FromSeconds(30));
        await log.Written(3); // Nothing remains retained, so the loop arms no timer to wait for.

        // A failure GitHub did not apply is written again without first reading the reaction.
        Assert.Equal(["addReaction D1", "addReaction D1", "addReaction D1"], github.Calls);
        Assert.Equal([
            ("receipt-error", $"{Url} subject=D1 attempt=1 Transient: HTTP 401: Bad credentials; retained for retry at {Start.AddSeconds(15):O}"),
            ("receipt-error", $"{Url} subject=D1 attempt=2 Transient: HTTP 401: Bad credentials; retained for retry at {Start.AddSeconds(45):O}"),
            ("receipt", Url)], log.Entries);
    }

    [Fact]
    public async Task A_receipt_the_App_already_left_is_reused_and_a_permanent_failure_is_logged_once_and_never_retried()
    {
        var ct = TestContext.Current.CancellationToken;
        github.Fail(new(GitHubFailure.Permanent, "FORBIDDEN: Resource not accessible by integration"));
        await using var outbox = Lifetime();
        outbox.Receipt("D1", Url, hasEyesReceipt: true);
        outbox.Receipt("C7", Comment, hasEyesReceipt: false);
        Assert.False(await outbox.Flush(ct));

        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.True(await outbox.Flush(ct));
        Assert.Equal(["addReaction C7"], github.Calls);
        Assert.Empty(state.Outbox!.Operations);
        Assert.Equal([("receipt-reused", Url), ("receipt-error", $"{Comment} subject=C7 attempt=1 Permanent: FORBIDDEN: Resource not accessible by integration; " +
            "permanent failure removed from retry; accepted input is not replayed")], log.Entries);
    }

    [Fact]
    public async Task The_dashboard_is_created_once_then_replaced_in_place_and_a_later_lifetime_finds_it_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var category = github.Category(Category);
        await using (var outbox = Lifetime())
        {
            outbox.Dashboard(Category, "Status", "first");
            Assert.True(await outbox.Flush(ct));
            outbox.Dashboard(Category, "Status", "second");
            Assert.True(await outbox.Flush(ct));
        }
        var dashboard = Assert.Single(github.Dashboards);
        Assert.Equal(("Status", "second"), (dashboard.Title, dashboard.Body));
        Assert.Equal([$"discussionCategories {Category}", $"discussions {category}", $"createDiscussion {category}", $"updateDiscussion {dashboard.Id}"], github.Calls);
        Assert.Equal([("dashboard-created", dashboard.Url)], log.Entries); // A routine update is not logged.

        await using (var outbox = Lifetime())
        {
            outbox.Dashboard(Category, "Attention", "third");
            Assert.True(await outbox.Flush(ct));
        }
        Assert.Equal(dashboard with { Title = "Attention", Body = "third" }, Assert.Single(github.Dashboards));
        Assert.Equal([$"discussionCategories {Category}", $"discussions {category}", $"updateDiscussion {dashboard.Id}"], github.Calls.Skip(4));
        Assert.Equal(("dashboard", dashboard.Url), log.Entries[^1]);
    }

    [Fact]
    public async Task A_dashboard_whose_creation_response_was_lost_is_found_instead_of_created_again()
    {
        var ct = TestContext.Current.CancellationToken;
        github.Category(Category);
        github.Fail(new(GitHubFailure.Transient, "dashboard creation returned no Discussion", uncertain: true), applied: true);
        await using var outbox = Lifetime();
        outbox.Dashboard(Category, "Status", "first");
        Assert.False(await outbox.Flush(ct));

        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.True(await outbox.Flush(ct));
        var dashboard = Assert.Single(github.Dashboards);
        Assert.Equal([
            ("dashboard-error", $"dashboard category '{Category}' attempt=1 Transient: dashboard creation returned no Discussion; retained for retry at {Start.AddSeconds(15):O}"),
            ("dashboard", dashboard.Url)], log.Entries);
    }

    [Fact]
    public async Task Dashboard_publications_superseded_during_a_rate_limit_are_written_once_as_the_latest_without_dropping_receipts()
    {
        var ct = TestContext.Current.CancellationToken;
        github.Category(Category);
        await using var outbox = Lifetime();
        outbox.Dashboard(Category, "Status", "heartbeat 0");
        Assert.True(await outbox.Flush(ct));
        var dashboard = Assert.Single(github.Dashboards);

        // GitHub's signalled wait spans four heartbeats; every one of them is retained, none is written.
        github.Fail(new(GitHubFailure.RateLimited, "secondary rate limit", retryAfter: TimeSpan.FromSeconds(120)));
        for (var heartbeat = 30; heartbeat <= 150; heartbeat += 30)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            outbox.Dashboard(Category, "Status", $"heartbeat {heartbeat}");
            if (heartbeat == 60) outbox.Receipt("D1", Url, hasEyesReceipt: false);
            Assert.Equal(heartbeat == 150, await outbox.Flush(ct));
        }
        Assert.Equal(dashboard with { Body = "heartbeat 150" }, Assert.Single(github.Dashboards));
        Assert.Equal([$"updateDiscussion {dashboard.Id}", $"updateDiscussion {dashboard.Id}", "addReaction D1"], github.Calls.Skip(3));
        Assert.Equal([
            ("dashboard-created", dashboard.Url),
            ("dashboard-error", $"{dashboard.Url} attempt=1 RateLimited: secondary rate limit; retained for retry at {Start.AddSeconds(150):O}"),
            ("dashboard", dashboard.Url), // A recovery is logged.
            ("receipt", Url)], log.Entries);
    }

    [Fact]
    public async Task A_missing_dashboard_category_is_reported_once_and_stops_dashboard_updates_until_a_restart_while_receipts_continue()
    {
        var ct = TestContext.Current.CancellationToken;
        const string Missing = "Missing category";
        await using (var outbox = Lifetime())
        {
            outbox.Dashboard(Missing, "Status", "first");
            Assert.False(await outbox.Flush(ct));
            for (var heartbeat = 0; heartbeat < 4; heartbeat++)
            {
                clock.Advance(TimeSpan.FromSeconds(30));
                outbox.Dashboard(Missing, "Status", "later");
                Assert.True(await outbox.Flush(ct));
            }
            outbox.Receipt("D1", Url, hasEyesReceipt: false);
            Assert.True(await outbox.Flush(ct));
        }
        Assert.Equal([$"discussionCategories {Missing}", "addReaction D1"], github.Calls);
        Assert.Equal([
            ("dashboard-error", $"dashboard category '{Missing}' attempt=1 Permanent: dashboard Discussion category '{Missing}' does not exist; " +
                "permanent failure removed from retry; dashboard updates stop until the controller restarts"),
            ("receipt", Url)], log.Entries);

        await using (var outbox = Lifetime())
        {
            outbox.Dashboard(Missing, "Status", "restarted");
            Assert.False(await outbox.Flush(ct));
        }
        Assert.Equal($"discussionCategories {Missing}", github.Calls[^1]);
    }

    [Fact]
    public async Task An_outbox_retained_for_another_repository_stops_startup_and_stays_as_it_was()
    {
        var retained = new OutboxState("owner/other", null, [new("receipt:D1", "receipt", "D1", Url, null, null, null, 0, Start, false, null)]);
        state.Outbox = retained;

        var error = Assert.Throws<InvalidOperationException>(() => Lifetime());
        Assert.Equal("GitHub outbox belongs to a different repository or unsupported version; preserve it for manual inspection", error.Message);
        Assert.Same(retained, state.Outbox);

        // The repository's name is compared as GitHub compares it, ignoring case.
        state.Outbox = retained with { Repository = "Owner/Repository" };
        await using var outbox = Lifetime();
        Assert.True(await outbox.Flush(TestContext.Current.CancellationToken));
        Assert.Equal(["addReaction D1"], github.Calls);
    }
}
