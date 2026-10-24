using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace RepoAgentController.Tests;

// BaselineReleases in-process: the core's comparison of this controller's image with the published baseline releases, what it
// claims after a failed read, and what it logs. Release metadata parsing is the GitHub adapter's.
public sealed class BaselineReleaseCheckTests
{
    static readonly DateTimeOffset Start = new(2026, 10, 24, 10, 0, 0, TimeSpan.Zero);
    readonly FakeTimeProvider clock = new(Start);
    readonly InMemoryBaselineReleases published = new();
    readonly RecordingLog log = new();

    BaselineReleases Check(string? inventory, bool startupToolUpdates = false, TimeProvider? time = null) =>
        new(published, _ => Task.FromResult(inventory), startupToolUpdates, time ?? clock, log);

    /// <summary>A baseline release as the release publisher describes it; its image inventory digest is derived from <paramref name="image"/>.</summary>
    internal static BaselineRelease Release(long run, int attempt = 1, string? image = null, string gh = "2.102.0", string codex = "0.163.0") =>
        new($"baseline-{run}.{attempt}", run, attempt, "ghcr.io/repoagent/controller@sha256:" + Digest($"manifest-{run}.{attempt}"),
            Inventory(image ?? $"image-{run}.{attempt}"), Digest($"commit-{run}")[..40], gh, codex);

    internal static string Inventory(string image) => Digest("inventory-" + image);

    static string Digest(string seed) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));

    [Fact]
    public async Task Newer_baseline_release_is_offered_with_how_many_are_newer_while_the_running_release_claims_no_update()
    {
        var ct = TestContext.Current.CancellationToken;
        var current = Release(101, image: "running");
        published.Publish(current);
        var releases = Check(Inventory("running"));

        await releases.Check(ct);
        Assert.Equal(new ReleaseNotice(Start, current, current, 0, null), releases.Current);

        // Two releases are published before the next check; only the newest is offered, counting both.
        clock.Advance(TimeSpan.FromHours(1));
        published.Publish(Release(102));
        var newest = Release(103, gh: "2.103.1", codex: "0.164.0");
        published.Publish(newest);
        await releases.Check(ct);
        Assert.Equal(new ReleaseNotice(Start.AddHours(1), current, newest, 2, null), releases.Current);

        Assert.Equal([
            ("release-check", "running=baseline-101.1 newest=baseline-101.1 newer=0 identity=known"),
            ("release-check", "running=baseline-101.1 newest=baseline-103.1 newer=2 identity=known")], log.Entries);
    }

    [Fact]
    public async Task Publication_run_and_attempt_decide_which_release_is_newer_not_the_listing_order()
    {
        // A re-run release job creates its release last, so GitHub lists an older run or attempt first.
        var running = Release(102, image: "running");
        var rerun = Release(102, attempt: 2);
        var newest = Release(103);
        foreach (var release in new[] { newest, rerun, running, Release(101, attempt: 3) }) published.Publish(release);
        var releases = Check(Inventory("running"));

        await releases.Check(TestContext.Current.CancellationToken);

        Assert.Equal(new ReleaseNotice(Start, running, newest, 2, null), releases.Current);
    }

    [Fact]
    public async Task A_failed_check_claims_no_release_until_a_later_check_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var current = Release(101, image: "running");
        var newer = Release(102);
        published.Publish(current);
        published.Publish(newer);
        published.Fail(new IOException("GitHub returned HTTP 503"));
        var releases = Check(Inventory("running"));

        await releases.Check(ct);
        Assert.Equal(new ReleaseNotice(Start, null, null, 0, null, "GitHub returned HTTP 503"), releases.Current);

        clock.Advance(TimeSpan.FromHours(1));
        published.Fail(null);
        await releases.Check(ct);
        Assert.Equal(new ReleaseNotice(Start.AddHours(1), current, newer, 1, null), releases.Current);

        // A later failure forgets what the earlier check found instead of claiming it again.
        clock.Advance(TimeSpan.FromHours(1));
        published.Fail(new IOException("GitHub returned HTTP 502"));
        await releases.Check(ct);
        Assert.Equal(new ReleaseNotice(Start.AddHours(2), null, null, 0, null, "GitHub returned HTTP 502"), releases.Current);

        Assert.Equal([
            ("release-check-error", "GitHub returned HTTP 503; the next check is in about an hour"),
            ("release-check", "running=baseline-101.1 newest=baseline-102.1 newer=1 identity=known"),
            ("release-check-error", "GitHub returned HTTP 502; the next check is in about an hour")], log.Entries);
    }

    [Fact]
    public async Task An_unreadable_image_inventory_fails_the_check_like_an_unreadable_release_list()
    {
        published.Publish(Release(101));
        var releases = new BaselineReleases(published, _ => throw new FileNotFoundException("Could not find file '/opt/repoagent/image-inventory.json'."),
            false, clock, log);

        await releases.Check(TestContext.Current.CancellationToken);

        Assert.Equal(new ReleaseNotice(Start, null, null, 0, null, "Could not find file '/opt/repoagent/image-inventory.json'."), releases.Current);
        Assert.Equal(0, published.Reads);
    }

    [Theory]
    [InlineData("derived image", "its image inventory matches none of the newest published baseline releases, so it is a derived, local, unreleased or much older image")]
    [InlineData("native run", "this controller was not started with a baseline image inventory, for example in a native or development run")]
    [InlineData("startup tool updates", "startup tool updates replace the image's gh and Codex, possibly with versions that no release records")]
    public async Task Uncertain_image_identity_is_reported_instead_of_a_replacement(string image, string reason)
    {
        var baseline = Release(101, image: "baseline");
        var newer = Release(102, gh: "2.103.1");
        published.Publish(baseline);
        published.Publish(newer);
        var releases = image switch
        {
            // Composed with a derived Dockerfile, for example: its inventory differs from every published one.
            "derived image" => Check(Inventory("derived")),
            // The image is baseline-101.1, but the runtime replaced its gh and Codex with versions no release records.
            "startup tool updates" => Check(Inventory("baseline"), startupToolUpdates: true),
            _ => Check(null)
        };

        await releases.Check(TestContext.Current.CancellationToken);

        var matched = image == "startup tool updates";
        Assert.Equal(new ReleaseNotice(Start, matched ? baseline : null, newer, matched ? 1 : 0, reason), releases.Current);
        Assert.Equal([("release-check", $"running={(matched ? "baseline-101.1" : "unidentified")} newest=baseline-102.1 newer={(matched ? 1 : 0)} identity=uncertain")],
            log.Entries);
    }

    [Fact]
    public async Task Without_a_published_baseline_release_none_is_newest()
    {
        var releases = Check(Inventory("running"));

        await releases.Check(TestContext.Current.CancellationToken);

        Assert.Equal(new ReleaseNotice(Start, null, null, 0,
            "its image inventory matches none of the newest published baseline releases, so it is a derived, local, unreleased or much older image"),
            releases.Current);
        Assert.Equal([("release-check", "running=unidentified newest=none newer=0 identity=uncertain")], log.Entries);
    }

    [Fact]
    public async Task Every_check_is_published_but_an_unchanged_outcome_is_logged_once()
    {
        var ct = TestContext.Current.CancellationToken;
        published.Publish(Release(101, image: "running"));
        var releases = Check(Inventory("running"));
        var changes = 0;
        releases.Changed += () => changes++;

        await releases.Check(ct);
        clock.Advance(TimeSpan.FromHours(1));
        await releases.Check(ct);

        Assert.Equal(2, changes);
        Assert.Equal(Start.AddHours(1), releases.Current!.CheckedAt);
        Assert.Single(log.Entries);
    }

    [Fact]
    public async Task Releases_are_checked_at_start_and_then_hourly_until_disposed()
    {
        var time = new SteppedClock(Start);
        published.Publish(Release(101, image: "running"));
        var releases = Check(Inventory("running"), time: time);

        releases.Start(TestContext.Current.CancellationToken);
        await time.Armed();
        Assert.Equal(1, published.Reads);
        Assert.Equal(Start, releases.Current!.CheckedAt);

        time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(1, published.Reads);
        time.Advance(TimeSpan.FromMinutes(1));
        await time.Armed();
        Assert.Equal(2, published.Reads);
        Assert.Equal(Start.AddHours(1), releases.Current!.CheckedAt);

        await releases.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(2, published.Reads);
    }
}
