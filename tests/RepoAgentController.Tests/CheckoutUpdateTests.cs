using Xunit;

namespace RepoAgentController.Tests;

/// <summary>Idle checkout refresh decisions, over the same typed facts used to open the checkout.</summary>
public sealed class CheckoutUpdateTests
{
    const string Target = "2222222222222222222222222222222222222222";
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static CheckoutUpdate Update(InMemoryCheckout checkout, RecordingLog? log = null) =>
        new(checkout, checkout, InMemoryCheckout.RootPath, new(checkout.Repository, checkout.Saved!), log ?? new());

    [Fact]
    public async Task An_authoritative_commit_is_recorded_before_the_checkout_is_replaced()
    {
        var checkout = new InMemoryCheckout { Target = Target };
        var log = new RecordingLog();
        var update = Update(checkout, log);
        var applying = false;

        Assert.True(await update.Refresh((previous, target) =>
        {
            Assert.Equal(InMemoryCheckout.Head, previous);
            Assert.Equal(Target, target);
            Assert.Null(checkout.Forced);
            Assert.Null(checkout.RewrittenCommit);
            applying = true;
        }, Token));

        Assert.True(applying);
        Assert.Equal(Target, checkout.Forced!.Value.Target);
        Assert.Equal("trunk", checkout.Forced.Value.Branch);
        Assert.Equal(Target, checkout.Commits["HEAD^{commit}"]);
        Assert.Equal(Target, checkout.RewrittenCommit);
        Assert.Contains(log.Entries, entry => entry.Kind == "checkout-refreshed" && entry.Message == $"root={InMemoryCheckout.RootPath} commit={Target}");
    }

    [Fact]
    public async Task A_settled_checkout_does_not_fetch_apply_rewrite_or_report_a_refresh()
    {
        var checkout = new InMemoryCheckout();
        var log = new RecordingLog();

        Assert.False(await Update(checkout, log).Refresh((_, _) => Assert.Fail("An unchanged checkout cannot begin applying"), Token));

        Assert.Empty(checkout.Writes);
        Assert.Empty(log.Entries);
    }

    public static TheoryData<string> LocalChanges() =>
        ["tracked", "untracked", "commit", "branch", "detached", "skip-worktree", "assume-unchanged", "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD"];

    [Theory]
    [MemberData(nameof(LocalChanges))]
    public async Task An_unmoved_origin_restores_local_state_to_the_recorded_commit(string local)
    {
        var checkout = new InMemoryCheckout();
        switch (local)
        {
            case "tracked": checkout.TrackedChanges = true; break;
            case "untracked": checkout.UntrackedFiles = true; break;
            case "commit": checkout.Commits["HEAD^{commit}"] = Target; break;
            case "branch": checkout.Branch = "refs/heads/agent-branch"; break;
            case "detached": checkout.Branch = null; break;
            case "skip-worktree": checkout.Entries[1] = new("tracked-sentinel", true, false); break;
            case "assume-unchanged": checkout.Entries[1] = new("tracked-sentinel", false, true); break;
            default: checkout.Held.Add(local); break;
        }
        var log = new RecordingLog();

        Assert.True(await Update(checkout, log).Refresh((previous, target) => Assert.Equal(previous, target), Token));

        var forced = checkout.Forced!.Value;
        Assert.Equal(("trunk", InMemoryCheckout.Head), (forced.Branch, forced.Target));
        Assert.Equal(local is "skip-worktree" or "assume-unchanged" ? ["tracked-sentinel"] : Array.Empty<string>(), forced.Hidden);
        Assert.Equal(InMemoryCheckout.Head, checkout.RewrittenCommit);
        Assert.Equal($"root={InMemoryCheckout.RootPath} commit={InMemoryCheckout.Head} restored=local-changes", Assert.Single(log.Entries).Message);
    }

    [Theory]
    [InlineData("id", false)]
    [InlineData("name", false)]
    [InlineData("remote", false)]
    [InlineData("id", true)]
    [InlineData("name", true)]
    [InlineData("remote", true)]
    public async Task Changed_authoritative_identity_blocks_before_any_apply_even_if_the_ref_cannot_be_read(string field, bool afterFetch)
    {
        var checkout = new InMemoryCheckout { Target = Target };
        var update = Update(checkout);
        var changed = field switch
        {
            "id" => checkout.Repository with { Id = 346 },
            "name" => checkout.Repository with { Name = "owner/other" },
            _ => checkout.Repository with { Remote = "https://github.com/owner/other.git" }
        };
        if (afterFetch) checkout.Fetching = () => checkout.Repository = changed;
        else
        {
            checkout.Repository = changed;
            checkout.Reading = read => { if (read.StartsWith("authoritative-commit", StringComparison.Ordinal)) throw new IOException("ref unavailable"); };
        }

        var error = await Assert.ThrowsAsync<IOException>(() => update.Refresh((_, _) => Assert.Fail("Changed authority cannot begin applying"), Token));

        Assert.Contains("authoritative repository identity changed" + (afterFetch ? " during fetch" : ""), error.Message, StringComparison.Ordinal);
        Assert.Null(checkout.Forced);
        Assert.Null(checkout.RewrittenCommit);
        if (!afterFetch) Assert.DoesNotContain(checkout.Reads, read => read.StartsWith("authoritative-commit", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, "default branch changed; inspect the recorded binding")]
    [InlineData(true, "default branch changed during fetch")]
    public async Task A_changed_default_branch_defers_without_rebinding_the_owned_checkout(bool afterFetch, string reason)
    {
        var checkout = new InMemoryCheckout { Target = Target };
        var update = Update(checkout);
        if (afterFetch) checkout.Fetching = () => checkout.Repository = checkout.Repository with { Branch = "main" };
        else checkout.Repository = checkout.Repository with { Branch = "main" };

        await Defers(update, checkout, reason);
    }

    [Theory]
    [InlineData("repository", "authoritative repository lookup unavailable; retry pending")]
    [InlineData("ref", "authoritative default-branch lookup unavailable; retry pending")]
    [InlineData("final repository", "authoritative repository lookup unavailable after fetch; retry pending")]
    public async Task An_unavailable_authoritative_read_defers_and_preserves_the_record(string failure, string reason)
    {
        var checkout = new InMemoryCheckout { Target = Target };
        var repositories = 0;
        checkout.Reading = read =>
        {
            if (read == "repository") repositories++;
            if (failure == "repository" && read == "repository" || failure == "ref" && read == "authoritative-commit trunk" ||
                failure == "final repository" && read == "repository" && repositories == 2) throw new IOException("provider unavailable");
        };

        await Defers(Update(checkout), checkout, reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_authoritative_metadata_is_never_reinterpreted_as_a_transient_read_failure(bool afterFetch)
    {
        var checkout = new InMemoryCheckout { Target = Target };
        var reads = 0;
        checkout.Reading = read =>
        {
            if (read == "repository" && ++reads == (afterFetch ? 2 : 1)) throw new CheckoutIdentityException("invalid authoritative metadata");
        };

        var error = await Assert.ThrowsAsync<CheckoutIdentityException>(() => Update(checkout).Refresh((_, _) => Assert.Fail("Invalid authority cannot apply"), Token));

        Assert.Equal("invalid authoritative metadata", error.Message);
        Assert.Null(checkout.Forced);
    }

    [Theory]
    [InlineData("oversized", "fetch refused: ambiguous fetched default branch")]
    [InlineData("several entries", "fetch refused: default branch changed during fetch; retry pending")]
    [InlineData("fetched commit", "fetch refused: default branch changed during fetch; retry pending")]
    [InlineData("resolved commit", "fetch refused: default branch changed during fetch; retry pending")]
    [InlineData("authoritative commit", "fetch refused: default branch changed during fetch; retry pending")]
    [InlineData("ref unavailable", "fetch refused: provider unavailable")]
    [InlineData("fetch error", "fetch refused: transport unavailable")]
    public async Task An_unconfirmed_fetched_target_never_reaches_apply(string failure, string reason)
    {
        var checkout = new InMemoryCheckout { Target = Target };
        if (failure == "oversized") checkout.Fetched = new(null, true);
        if (failure == "several entries") checkout.Fetched = new(null);
        if (failure == "fetched commit") checkout.Fetched = new(InMemoryCheckout.Head);
        if (failure == "resolved commit") checkout.Fetching = () => checkout.Commits["FETCH_HEAD^{commit}"] = InMemoryCheckout.Head;
        if (failure == "authoritative commit") checkout.Fetching = () => checkout.Target = InMemoryCheckout.Head;
        if (failure == "ref unavailable") checkout.Fetching = () => checkout.Reading = read =>
        { if (read == "authoritative-commit trunk") throw new IOException("provider unavailable"); };
        if (failure == "fetch error") checkout.FetchFailure = new IOException("transport unavailable");

        await Defers(Update(checkout), checkout, reason);
    }

    [Fact]
    public async Task A_late_fetch_ref_change_does_not_replace_the_captured_validated_target()
    {
        var checkout = new InMemoryCheckout { Target = Target };
        var lookups = 0;
        checkout.Reading = read =>
        {
            if (read == "authoritative-commit trunk" && ++lookups == 2) checkout.Commits["FETCH_HEAD^{commit}"] = InMemoryCheckout.Head;
        };

        Assert.True(await Update(checkout).Refresh((_, target) => Assert.Equal(Target, target), Token));

        Assert.Equal(InMemoryCheckout.Head, checkout.Commits["FETCH_HEAD^{commit}"]);
        Assert.Equal(Target, checkout.Forced!.Value.Target);
        Assert.Equal(Target, checkout.RewrittenCommit);
    }

    [Fact]
    public async Task An_unsafe_fetch_destination_blocks_instead_of_becoming_a_retry()
    {
        var checkout = new InMemoryCheckout { Target = Target, PrepareFailure = new IOException("unsafe fetch destination") };

        var error = await Assert.ThrowsAsync<IOException>(() => Update(checkout).Refresh((_, _) => Assert.Fail("Unsafe fetch destination cannot apply"), Token));

        Assert.Equal("unsafe fetch destination", error.Message);
        Assert.Empty(checkout.Writes);
    }

    [Theory]
    [InlineData("record", false)]
    [InlineData("owner", false)]
    [InlineData("missing owner", false)]
    [InlineData("record", true)]
    [InlineData("owner", true)]
    [InlineData("missing owner", true)]
    public async Task Ownership_is_rechecked_before_fetch_and_before_apply(string failure, bool afterFetch)
    {
        var checkout = new InMemoryCheckout { Target = Target };
        void Change()
        {
            if (failure == "record") checkout.RecordUnchanged = false;
            else checkout.Marker = failure == "owner" ? "another-owner" : null;
        }
        if (afterFetch) checkout.Fetching = Change;
        else Change();

        var error = await Assert.ThrowsAsync<IOException>(() => Update(checkout).Refresh((_, _) => Assert.Fail("Lost ownership cannot apply"), Token));

        Assert.Contains(failure == "missing owner" ? "private state is missing or unsafe" : "ownership record changed", error.Message, StringComparison.Ordinal);
        Assert.Null(checkout.Forced);
        Assert.Null(checkout.RewrittenCommit);
        if (!afterFetch) Assert.Empty(checkout.Writes);
    }

    [Theory]
    [InlineData("rebase-merge")]
    [InlineData("index.lock")]
    public async Task An_interrupted_git_operation_defers_before_fetch_and_leaves_local_state_alone(string operation)
    {
        var checkout = new InMemoryCheckout { Target = Target, TrackedChanges = true };
        checkout.Held.Add(operation);

        await Defers(Update(checkout), checkout, "in-progress Git operation");

        Assert.Empty(checkout.Writes);
        Assert.True(checkout.TrackedChanges);
    }

    [Fact]
    public async Task A_failed_applying_record_prevents_destructive_checkout_changes()
    {
        var checkout = new InMemoryCheckout { Target = Target };

        var error = await Assert.ThrowsAsync<IOException>(() => Update(checkout).Refresh((_, _) => throw new IOException("journal write failed"), Token));

        Assert.Equal("journal write failed", error.Message);
        Assert.Null(checkout.Forced);
        Assert.Null(checkout.RewrittenCommit);
    }

    [Fact]
    public async Task A_checkout_that_did_not_apply_the_target_does_not_publish_a_new_record_or_success()
    {
        var checkout = new InMemoryCheckout { Target = Target, ForceSettles = false };
        var log = new RecordingLog();

        var error = await Assert.ThrowsAsync<IOException>(() => Update(checkout, log).Refresh((_, _) => { }, Token));

        Assert.Contains("applied commit differs", error.Message, StringComparison.Ordinal);
        Assert.Equal(InMemoryCheckout.Head, checkout.Saved!.Commit);
        Assert.Null(checkout.RewrittenCommit);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task A_transient_final_lookup_keeps_assistance_admissible_and_retries_at_the_next_requested_idle_boundary()
    {
        var checkout = new InMemoryCheckout { Target = Target };
        var lookups = 0;
        checkout.Reading = read => { if (read == "repository" && ++lookups == 2) throw new IOException("provider unavailable"); };
        var journal = new InMemoryRefreshJournal();
        var log = new RecordingLog();
        var runtime = new FakeAgentRuntime();
        var clock = new SteppedClock(new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
        await using var refresh = new CheckoutRefresh(Update(checkout, log),
            new(InMemoryCheckout.RootPath, InMemoryCheckout.Owner, 345, "refs/heads/trunk"), journal, false, false, clock, log);

        await refresh.Start(new AgentSessions(runtime, log, refresh), TimeSpan.FromMinutes(1), Token);

        Assert.False(journal.Saved!.Blocked);
        Assert.Null(journal.Saved.Applying);
        Assert.True(journal.Saved.Requested > journal.Saved.Completed);
        Assert.Equal("authoritative repository lookup unavailable after fetch; retry pending", journal.Saved.Reason);
        Assert.Null(checkout.Forced);
        using (var admitted = await refresh.Admit(Token)) admitted.Abandon();
        checkout.Reading = null;
        checkout.Forcing = () => Assert.Equal(new(InMemoryCheckout.Head, Target, journal.Saved!.Requested), journal.Saved.Applying);
        refresh.Request();

        Assert.True(await refresh.CommitAtIdle(_ => Task.CompletedTask, Token));
        Assert.Equal(Target, checkout.RewrittenCommit);
        Assert.False(journal.Saved!.Blocked);
        Assert.Null(journal.Saved.Applying);
        Assert.Equal(journal.Saved.Requested, journal.Saved.Completed);
        Assert.Null(journal.Saved.Reason);
    }

    [Fact]
    public async Task Observed_identity_change_blocks_admission_without_applying_even_when_the_ref_is_unavailable()
    {
        var checkout = new InMemoryCheckout { Target = Target };
        var update = Update(checkout);
        checkout.Repository = checkout.Repository with { Id = 346 };
        checkout.Reading = read => { if (read == "authoritative-commit trunk") throw new IOException("ref unavailable"); };
        var journal = new InMemoryRefreshJournal();
        var log = new RecordingLog();
        var clock = new SteppedClock(new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
        await using var refresh = new CheckoutRefresh(update,
            new(InMemoryCheckout.RootPath, InMemoryCheckout.Owner, 345, "refs/heads/trunk"), journal, false, false, clock, log);

        await Assert.ThrowsAsync<IOException>(() => refresh.Start(new AgentSessions(new FakeAgentRuntime(), log, refresh), TimeSpan.FromMinutes(1), Token));
        await Assert.ThrowsAsync<IOException>(() => refresh.Admit(Token));

        Assert.True(journal.Saved!.Blocked);
        Assert.Null(journal.Saved.Applying);
        Assert.Null(checkout.Forced);
        Assert.Null(checkout.RewrittenCommit);
        Assert.DoesNotContain(checkout.Reads, read => read.StartsWith("authoritative-commit", StringComparison.Ordinal));
    }

    static async Task Defers(CheckoutUpdate update, InMemoryCheckout checkout, string reason)
    {
        var error = await Assert.ThrowsAsync<CheckoutDeferred>(() => update.Refresh((_, _) => Assert.Fail("A deferred checkout cannot begin applying"), Token));
        Assert.Equal(reason, error.Message);
        Assert.Null(checkout.Forced);
        Assert.Null(checkout.RewrittenCommit);
        Assert.Equal(InMemoryCheckout.Head, checkout.Saved!.Commit);
    }
}
