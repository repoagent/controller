using Xunit;

namespace RepoAgentController.Tests;

/// <summary>What opening the managed checkout accepts and refuses, over an in-memory workspace and checkout.</summary>
public sealed class CheckoutInspectionTests
{
    const string Other = "2222222222222222222222222222222222222222";
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static Task<OpenedCheckout> Open(InMemoryCheckout checkout, string state = InMemoryCheckout.State, long? credential = 345) =>
        CheckoutInspection.Open(checkout, checkout, state, InMemoryCheckout.RootPath, credential, Token);

    static string Blocked(string reason) => "Managed checkout blocked: " + reason + "; preserve this workspace and inspect it before retrying";

    static async Task Refuses(InMemoryCheckout checkout, string reason, string state = InMemoryCheckout.State, long? credential = 345)
    {
        var refused = await Assert.ThrowsAnyAsync<IOException>(() => Open(checkout, state, credential));
        Assert.IsNotType<CheckoutDeferred>(refused);
        Assert.Equal(Blocked(reason), refused.Message);
        Assert.Empty(checkout.Writes);
    }

    // dirty also stands for staged, which Git reports as the same fact; untracked also stands for ignored, since a restart reads
    // no working-tree state.
    [Theory]
    [InlineData("reuse")]
    [InlineData("dirty")]
    [InlineData("untracked")]
    [InlineData("branch")]
    [InlineData("detached")]
    [InlineData("commit")]
    [InlineData("assume-unchanged")]
    [InlineData("skip-worktree")]
    [InlineData("operation/MERGE_HEAD")]
    public async Task A_restart_accepts_local_state_the_idle_refresh_replaces_without_reading_it(string scenario)
    {
        var checkout = new InMemoryCheckout();
        switch (scenario)
        {
            case "reuse": checkout.Commits["refs/remotes/origin/trunk^{commit}"] = Other; break;
            case "dirty": checkout.TrackedChanges = true; break;
            case "untracked": checkout.UntrackedFiles = true; break;
            case "branch": checkout.Branch = "refs/heads/local-branch"; break;
            case "detached": checkout.Branch = null; break;
            case "commit": checkout.Commits["HEAD^{commit}"] = Other; break;
            case "assume-unchanged": checkout.Entries[1] = new("tracked-sentinel", false, true); break;
            case "skip-worktree": checkout.Entries[1] = new("tracked-sentinel", true, false); break;
            case "operation/MERGE_HEAD": checkout.Held.Add("MERGE_HEAD"); break;
        }

        var opened = await Open(checkout);

        Assert.Equal(InMemoryCheckout.Head, opened.Record.Commit);
        Assert.Same(checkout.Saved, opened.Record);
        Assert.Equal(checkout.Repository, opened.Repository);
        Assert.Empty(checkout.Writes);
        Assert.DoesNotContain(checkout.Reads, read => read is "head-branch" or "head-name" or "tracked-changes" or "changes" or "index" || read.StartsWith("commit ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_restart_reads_the_binding_then_the_checkout_then_its_owner()
    {
        var checkout = new InMemoryCheckout();

        await Open(checkout);

        Assert.Equal(["repository", "record", "root", "git-directory", "holds commondir", "holds gitdir", "holds objects/info/alternates",
            "holds info/grafts", "holds REBASE_HEAD", "holds BISECT_LOG", "holds rebase-apply", "holds rebase-merge", "holds sequencer",
            "holds index.lock", "holds HEAD.lock", "configuration", "top-level", "bare", "origin-urls", "owner"], checkout.Reads);
    }

    [Fact]
    public async Task A_restart_accepts_a_record_whose_commit_was_absent_and_returns_it_empty()
    {
        var checkout = new InMemoryCheckout();
        checkout.Saved!.Commit = "";

        Assert.Equal("", (await Open(checkout)).Record.Commit);
    }

    [Theory]
    [InlineData("metadata-id")]
    [InlineData("metadata-id-zero")]
    [InlineData("credential-unbound")]
    public async Task A_repository_other_than_the_credentials_one_is_refused_before_the_record_is_read(string scenario)
    {
        var checkout = new InMemoryCheckout();
        long? credential = 345;
        if (scenario == "metadata-id") checkout.Repository = checkout.Repository with { Id = 346 };
        if (scenario == "metadata-id-zero") { checkout.Repository = checkout.Repository with { Id = 0 }; credential = 0; }
        if (scenario == "credential-unbound") credential = null;

        await Refuses(checkout, "repository identity differs from credential binding", credential: credential);

        Assert.Equal(["repository"], checkout.Reads);
    }

    public static TheoryData<string> Bindings() =>
    [
        "state", "version 2", "version unreadable", "state unreadable", "root other", "repositoryId other", "repositoryId unreadable",
        "repository upper case", "remote other", "branch other", "owner not a GUID", "owner with dashes", "owner unreadable"
    ];

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task A_record_bound_to_another_state_file_root_or_repository_is_refused_before_the_checkout_is_read(string scenario)
    {
        var checkout = new InMemoryCheckout();
        var recorded = InMemoryCheckout.Recorded();
        var state = InMemoryCheckout.State;
        checkout.Saved = scenario switch
        {
            "state" => recorded,
            "version 2" => With(recorded, version: 2),
            "version unreadable" => With(recorded, version: null),
            "state unreadable" => With(recorded, state: null),
            "root other" => With(recorded, root: "/srv/repoagent/workspace/other"),
            "repositoryId other" => With(recorded, repositoryId: 346),
            "repositoryId unreadable" => With(recorded, repositoryId: null),
            "repository upper case" => With(recorded, repository: "Owner/Repository"),
            "remote other" => With(recorded, remote: "https://github.com/owner/other.git"),
            "branch other" => With(recorded, branch: "main"),
            "owner not a GUID" => With(recorded, owner: "not-a-guid"),
            "owner with dashes" => With(recorded, owner: "01234567-89ab-cdef-0123-456789abcdef"),
            _ => With(recorded, owner: null),
        };
        if (scenario == "state") state = "/srv/repoagent/other-state.json";

        await Refuses(checkout, "ownership or repository binding differs", state);

        Assert.Equal(["repository", "record"], checkout.Reads);
    }

    static CheckoutRecord With(CheckoutRecord record, int? version = 1, string? state = InMemoryCheckout.State, string? root = InMemoryCheckout.RootPath,
        string? owner = InMemoryCheckout.Owner, long? repositoryId = 345, string? repository = "owner/repository", string? remote = InMemoryCheckout.Remote,
        string? branch = "trunk") => new()
    {
        Version = version, State = state, Root = root, Owner = owner, RepositoryId = repositoryId, Repository = repository, Remote = remote,
        Branch = branch, Commit = record.Commit, Unknown = record.Unknown
    };

    [Fact]
    public async Task A_record_the_adapter_could_not_read_is_refused_as_invalid()
    {
        var checkout = new InMemoryCheckout { Saved = new() { Unreadable = true } };

        await Refuses(checkout, "invalid ownership record");
    }

    [Fact]
    public async Task A_record_whose_commit_is_not_text_is_refused_as_invalid_after_its_owner_is_confirmed()
    {
        var checkout = new InMemoryCheckout();
        checkout.Saved!.Commit = null;

        await Refuses(checkout, "invalid ownership record");

        Assert.Equal("owner", checkout.Reads[^1]);
    }

    [Theory]
    [InlineData(CheckoutRoot.Missing)]
    [InlineData(CheckoutRoot.Other)]
    public async Task A_recorded_checkout_whose_root_is_not_a_directory_is_refused(CheckoutRoot root)
    {
        // root-missing: the recorded checkout was moved away; a file in its place is no checkout either.
        var checkout = new InMemoryCheckout { RootEntry = root };

        await Refuses(checkout, "owned checkout is missing");

        Assert.Equal(0, checkout.Clones);
    }

    // "owner" also stands for executable-owner-mismatch: an executable marker is the adapter's to allow; its owner still decides.
    [Theory]
    [InlineData("owner")]
    [InlineData("owner upper case")]
    public async Task A_checkout_whose_marker_names_another_owner_was_replaced(string scenario)
    {
        var checkout = new InMemoryCheckout { Marker = scenario == "owner upper case" ? InMemoryCheckout.Owner.ToUpperInvariant() : "fedcba9876543210fedcba9876543210" };

        await Refuses(checkout, "checkout was replaced");
    }

    // marker-missing, and root-replaced: another clone in the root has a Git directory of its own but no marker.
    [Fact]
    public async Task A_checkout_without_its_owner_marker_is_refused()
    {
        var checkout = new InMemoryCheckout { Marker = null };

        await Refuses(checkout, "private state is missing or unsafe");
    }

    // A directory is partial, and record-missing: a clone whose record was removed.
    [Theory]
    [InlineData(CheckoutRoot.Directory)]
    [InlineData(CheckoutRoot.Other)]
    public async Task A_root_without_an_ownership_record_is_refused_and_not_cloned_over(CheckoutRoot root)
    {
        var checkout = new InMemoryCheckout { Saved = null, RootEntry = root };

        await Refuses(checkout, "checkout has no ownership record");

        Assert.Equal(0, checkout.Clones);
    }

    [Theory]
    [InlineData("user-work")]
    [InlineData(".hidden")]
    [InlineData(".repoagent-checkout.json.tmp")]
    [InlineData("Repository")]
    public void A_workspace_holding_anything_but_the_checkout_its_record_and_its_lock_is_refused(string entry)
    {
        CheckoutInspection.Workspace(["repository", ".repoagent-checkout.json", ".repoagent-checkout.lock"]);
        CheckoutInspection.Workspace([]);

        Assert.Equal(Blocked("workspace contains unmanaged files"),
            Assert.Throws<IOException>(() => CheckoutInspection.Workspace([".repoagent-checkout.lock", entry])).Message);
    }

    [Fact]
    public async Task A_checkout_without_its_own_Git_directory_is_refused()
    {
        // linked-git: .git is a file naming storage elsewhere.
        var checkout = new InMemoryCheckout { GitDirectory = false };

        await Refuses(checkout, "checkout must have its own Git directory");
    }

    [Theory]
    [InlineData("commondir")]
    [InlineData("gitdir")]
    [InlineData("objects/info/alternates")]
    [InlineData("info/grafts")]
    public async Task A_checkout_on_linked_Git_storage_is_refused(string path)
    {
        var checkout = new InMemoryCheckout();
        checkout.Held.Add(path);

        await Refuses(checkout, "linked Git storage");
    }

    [Theory]
    [InlineData("REBASE_HEAD")]
    [InlineData("BISECT_LOG")]
    [InlineData("rebase-apply")]
    [InlineData("rebase-merge")]
    [InlineData("sequencer")]
    [InlineData("index.lock")]
    [InlineData("HEAD.lock")]
    public async Task A_checkout_with_a_Git_operation_in_progress_is_deferred(string path)
    {
        // operation/rebase-merge and operation/sequencer, and every other marker a forced checkout would not discard.
        var checkout = new InMemoryCheckout();
        checkout.Held.Add(path);

        Assert.Equal("in-progress Git operation", (await Assert.ThrowsAsync<CheckoutDeferred>(() => Open(checkout))).Message);
        Assert.Empty(checkout.Writes);
    }

    [Theory]
    [InlineData("core.fsmonitor")]
    [InlineData("core.hookspath")]
    [InlineData("core.sshcommand")]
    [InlineData("core.worktree")]
    [InlineData("include.path")]
    [InlineData("filter.x.clean")]
    [InlineData("remote.origin.pushurl")]
    [InlineData("remote.other.url")]
    [InlineData("branch.trunk.rebase")]
    [InlineData("branch.x")]
    [InlineData("user.name")]
    [InlineData("core.bare.x")]
    public async Task A_checkout_with_local_Git_configuration_beyond_the_allowed_keys_is_refused(string key)
    {
        // unsafe-config
        var checkout = new InMemoryCheckout();
        checkout.Configuration.Add(key);

        await Refuses(checkout, "unsupported local Git configuration");

        Assert.Equal("configuration", checkout.Reads[^1]);
    }

    [Theory]
    [InlineData("core.ignorecase")]
    [InlineData("core.precomposeunicode")]
    [InlineData("Core.FileMode")]
    [InlineData("remote.origin.gh-resolved")]
    [InlineData("branch.other.merge")]
    [InlineData("branch..remote")]
    public async Task A_checkout_with_only_allowed_local_Git_configuration_is_accepted(string key)
    {
        var checkout = new InMemoryCheckout();
        checkout.Configuration.Add(key);

        await Open(checkout);
    }

    [Theory]
    [InlineData("top level")]
    [InlineData("bare")]
    [InlineData("second origin url")]
    [InlineData("origin url other")]
    public async Task A_checkout_whose_top_level_bareness_or_origin_differs_is_refused(string scenario)
    {
        var checkout = new InMemoryCheckout();
        if (scenario == "top level") checkout.TopLevelPath = "/srv/repoagent/workspace";
        if (scenario == "bare") checkout.Bare = true;
        if (scenario == "second origin url") checkout.Urls.Add(InMemoryCheckout.Remote);
        if (scenario == "origin url other") checkout.Urls[0] = "https://github.com/owner/other.git";

        await Refuses(checkout, "Git root or remote differs");
    }

    [Fact]
    public async Task A_differing_top_level_is_refused_before_Git_is_asked_about_bareness_or_origin()
    {
        var checkout = new InMemoryCheckout { TopLevelPath = "/srv/repoagent/workspace" };

        await Refuses(checkout, "Git root or remote differs");

        Assert.Equal("top-level", checkout.Reads[^1]);
    }

    [Fact]
    public async Task A_clone_is_inspected_then_owned_by_a_new_marker_and_record()
    {
        var checkout = new InMemoryCheckout { Saved = null, RootEntry = CheckoutRoot.Missing, Marker = null };

        var opened = await Open(checkout);

        var owner = checkout.Marker!;
        Assert.Matches("\\A[0-9a-f]{32}\\z", owner);
        Assert.Equal(["clone " + InMemoryCheckout.Remote, "owner " + owner, "record " + owner], checkout.Writes);
        Assert.Same(checkout.Saved, opened.Record);
        Assert.Equivalent(new CheckoutRecord
        {
            Version = 1, State = InMemoryCheckout.State, Root = InMemoryCheckout.RootPath, Owner = owner, RepositoryId = 345,
            Repository = "owner/repository", Remote = InMemoryCheckout.Remote, Branch = "trunk", Commit = InMemoryCheckout.Head
        }, opened.Record, strict: true);
        Assert.Equal(["head-branch", "tracked-changes", "index", "commit HEAD^{commit}", "commit refs/remotes/origin/trunk^{commit}"],
            checkout.Reads.SkipWhile(read => read != "origin-urls").Skip(1));
    }

    [Fact]
    public async Task Each_clone_is_owned_by_a_new_owner()
    {
        var first = new InMemoryCheckout { Saved = null, RootEntry = CheckoutRoot.Missing, Marker = null };
        var second = new InMemoryCheckout { Saved = null, RootEntry = CheckoutRoot.Missing, Marker = null };

        await Open(first);
        await Open(second);

        Assert.NotEqual(first.Marker, second.Marker);
    }

    [Theory]
    [InlineData("branch", "checkout does not match its default-branch commit")]
    [InlineData("dirty", "checkout does not match its default-branch commit")]
    [InlineData("assume-unchanged", "checkout does not match its default-branch commit")]
    [InlineData("skip-worktree", "checkout does not match its default-branch commit")]
    [InlineData("origin moved", "cloned default branch changed during bootstrap")]
    [InlineData("unsafe-config", "unsupported local Git configuration")]
    public async Task A_clone_that_does_not_settle_on_the_default_branch_commit_is_not_owned(string scenario, string reason)
    {
        var checkout = new InMemoryCheckout { Saved = null, RootEntry = CheckoutRoot.Missing, Marker = null };
        switch (scenario)
        {
            case "branch": checkout.Branch = "refs/heads/trunk-other"; break;
            case "dirty": checkout.TrackedChanges = true; break;
            case "assume-unchanged": checkout.Entries[1] = new("tracked-sentinel", false, true); break;
            case "skip-worktree": checkout.Entries[1] = new("tracked-sentinel", true, true); break;
            case "origin moved": checkout.Commits["refs/remotes/origin/trunk^{commit}"] = Other; break;
            case "unsafe-config": checkout.Configuration.Add("core.fsmonitor"); break;
        }

        Assert.Equal(Blocked(reason), (await Assert.ThrowsAsync<IOException>(() => Open(checkout))).Message);

        Assert.Equal(["clone " + InMemoryCheckout.Remote], checkout.Writes);
        Assert.Null(checkout.Marker);
    }

    [Fact]
    public void Hidden_index_entries_are_those_flagged_skip_worktree_or_assume_unchanged()
    {
        Assert.Equal(["assumed", "skipped", "both"], CheckoutInspection.Hidden(
            [new("tracked", false, false), new("assumed", false, true), new("skipped", true, false), new("both", true, true)]));
        Assert.Empty(CheckoutInspection.Hidden([]));
    }

    [Theory]
    [InlineData("metadata-name", "other/repository", "https://github.com/other/repository.git", "trunk")]
    [InlineData("metadata-url", "owner/repository", "https://github.com/owner/repository.git?unsafe", "trunk")]
    [InlineData("metadata-default", "owner/repository", "https://github.com/owner/repository.git", "../trunk")]
    [InlineData("url http", "owner/repository", "http://github.com/owner/repository.git", "trunk")]
    [InlineData("name dot segment", "owner/..", "https://github.com/owner/...git", "trunk")]
    [InlineData("name unsafe", "owner/repo sitory", "https://github.com/owner/repo sitory.git", "trunk")]
    [InlineData("name without owner", "repository", "https://github.com/repository.git", "trunk")]
    [InlineData("branch empty", "owner/repository", "https://github.com/owner/repository.git", "")]
    [InlineData("branch space", "owner/repository", "https://github.com/owner/repository.git", " ")]
    [InlineData("branch option", "owner/repository", "https://github.com/owner/repository.git", "-x")]
    [InlineData("branch whitespace", "owner/repository", "https://github.com/owner/repository.git", "a b")]
    [InlineData("branch control", "owner/repository", "https://github.com/owner/repository.git", "a\u0001b")]
    [InlineData("branch dots", "owner/repository", "https://github.com/owner/repository.git", "a..b")]
    [InlineData("branch tilde", "owner/repository", "https://github.com/owner/repository.git", "a~b")]
    [InlineData("branch caret", "owner/repository", "https://github.com/owner/repository.git", "a^b")]
    [InlineData("branch colon", "owner/repository", "https://github.com/owner/repository.git", "a:b")]
    [InlineData("branch question", "owner/repository", "https://github.com/owner/repository.git", "a?b")]
    [InlineData("branch star", "owner/repository", "https://github.com/owner/repository.git", "a*b")]
    [InlineData("branch bracket", "owner/repository", "https://github.com/owner/repository.git", "a[b")]
    [InlineData("branch backslash", "owner/repository", "https://github.com/owner/repository.git", "a\\b")]
    [InlineData("branch reflog", "owner/repository", "https://github.com/owner/repository.git", "a@{b")]
    [InlineData("branch empty part", "owner/repository", "https://github.com/owner/repository.git", "a//b")]
    [InlineData("branch dot part", "owner/repository", "https://github.com/owner/repository.git", "a/.b")]
    [InlineData("branch dot end", "owner/repository", "https://github.com/owner/repository.git", "a.")]
    [InlineData("branch lock", "owner/repository", "https://github.com/owner/repository.git", "a.lock")]
    public void Repository_metadata_other_than_the_approved_repository_is_refused(string scenario, string name, string remote, string branch)
    {
        _ = scenario;
        Assert.Equal("Repository metadata does not match the approved checkout",
            Assert.Throws<CheckoutIdentityException>(() => RepositoryCheckout.Approved("owner/repository", 345, name, remote, branch)).Message);
    }

    [Theory]
    [InlineData("owner/..")]
    [InlineData("./repository")]
    [InlineData("owner/repo sitory")]
    [InlineData("owner/repository\n")]
    [InlineData("owner/repository/extra")]
    [InlineData("owner/")]
    public void Repository_metadata_with_an_unsafe_name_is_refused_even_as_the_approved_name(string name) =>
        Assert.Throws<CheckoutIdentityException>(() => RepositoryCheckout.Approved(name, 345, name, "https://github.com/" + name + ".git", "trunk"));

    [Theory]
    [InlineData(0L, "owner/repository")]
    [InlineData(-1L, "owner/repository")]
    public void Repository_metadata_without_a_positive_ID_is_refused(long id, string name) =>
        Assert.Throws<CheckoutIdentityException>(() => RepositoryCheckout.Approved("owner/repository", id, name, "https://github.com/" + name + ".git", "trunk"));

    [Theory]
    [InlineData("Owner/Repository", "trunk")]
    [InlineData("owner/repository", "feature/x.y")]
    [InlineData("owner/my_repo.js-2", "release-1.0")]
    public void Repository_metadata_for_the_approved_repository_is_accepted_as_reported(string name, string branch)
    {
        var approved = name.Equals("owner/my_repo.js-2", StringComparison.Ordinal) ? name : "owner/repository";
        var remote = "https://github.com/" + name + ".git";

        Assert.Equal(new RepositoryCheckout(345, name, remote, branch), RepositoryCheckout.Approved(approved, 345, name, remote, branch));
    }
}
