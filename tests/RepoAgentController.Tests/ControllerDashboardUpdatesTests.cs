using System.Text;
using Xunit;
using static RepoAgentController.Tests.BaselineReleaseCheckTests;

namespace RepoAgentController.Tests;

// The dashboard's Updates section for each release check outcome, rendered in-process. Release links reach only
// repoagent/controller's release, package and documentation pages.
public sealed class ControllerDashboardUpdatesTests
{
    static readonly DateTimeOffset At = new(2026, 10, 24, 10, 0, 0, 250, TimeSpan.Zero);
    const string Checked = "2026-10-24T10:00:00Z";
    static readonly BaselineRelease Current = Release(101, image: "running");
    static readonly BaselineRelease Newest = Release(103, gh: "2.103.1", codex: "0.164.0");

    static string Updates(ReleaseNotice? notice)
    {
        var text = new StringBuilder();
        ControllerDashboard.Updates(text, notice);
        return text.ToString().ReplaceLineEndings("\n");
    }

    static string Link(BaselineRelease release) => $"[{release.Tag}](https://github.com/repoagent/controller/releases/tag/{release.Tag})";

    [Fact]
    public void Before_the_first_check_completes_nothing_is_claimed() =>
        Assert.Equal("### Updates\nThe baseline release check has not completed yet.\n", Updates(null));

    [Fact]
    public void The_newest_running_release_claims_no_update() =>
        Assert.Equal($"### Updates\nThis controller's image matches {Link(Current)}, the newest published baseline release at {Checked}.\n",
            Updates(new(At, Current, Current, 0, null)));

    [Fact]
    public void A_newer_release_is_offered_once_with_its_image_tools_and_how_an_operator_adopts_it()
    {
        Assert.Equal($"""
            ### Updates
            **Update available:** baseline release {Link(Newest)} is newer than this controller's {Link(Current)} (2 newer releases; checked at {Checked}).
            - Image `{Newest.Image}` ([package](https://github.com/repoagent/controller/pkgs/container/controller)) with gh `2.103.1` and Codex `0.164.0`.
            - Publication is not deployment: this controller keeps running its current image. It never replaces itself and has no Docker host or deployment access.
            - To update, an operator recreates the controller from that image, or rebuilds an image derived from the baseline on it, keeping the controller's external configuration, credentials and session data; see [baseline releases](https://github.com/repoagent/controller/blob/{Newest.Commit}/docs/ci-cd.md#baseline-releases).

            """.ReplaceLineEndings("\n"), Updates(new(At, Current, Newest, 2, null)));
        Assert.Contains("(1 newer release; ", Updates(new(At, Current, Newest, 1, null)));
    }

    [Fact]
    public void A_failed_check_claims_neither_release_and_publishes_its_failure()
    {
        // As the check keeps it: redacted.
        var text = Updates(new(At, null, null, 0, null, "gh failed: HTTP 503 for token <redacted>\n  retry later"));

        Assert.Equal($"### Updates\nThe baseline release check failed at {Checked}: `gh failed: HTTP 503 for token <redacted> retry later`. No update is claimed; the next check is in about an hour.\n",
            text);
    }

    [Fact]
    public void Without_a_published_baseline_release_none_is_claimed() =>
        Assert.Equal($"### Updates\nNo published baseline release was found at {Checked}.\n", Updates(new(At, null, null, 0, "its image inventory matches none")));

    [Fact]
    public void Uncertain_identity_names_its_reason_and_the_newest_release_without_suggesting_a_replacement()
    {
        var text = Updates(new(At, null, Newest, 0, "the image cannot be identified"));

        Assert.Equal($"""
            ### Updates
            **Release identity uncertain** (checked at {Checked}): the image cannot be identified. No replacement is suggested.
            - Newest published baseline release: {Link(Newest)}, image `{Newest.Image}` ([package](https://github.com/repoagent/controller/pkgs/container/controller)) with gh `2.103.1` and Codex `0.164.0`. Compare it with this controller's image before deciding whether to rebuild or recreate it.

            """.ReplaceLineEndings("\n"), text);
    }
}
