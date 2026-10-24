using Xunit;

namespace RepoAgent.Auth.Tests;

// The broker core's rules over what GitHub answers about App installations and controller approvals.
// Only well-formed, fresh evidence proves a change or a withdrawal; every other answer denies and proves nothing.
public sealed class ApprovalObserverTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly GitHubToken Verifier = new("verifier-token");
    private readonly IssuanceClock clock = new();
    private readonly InMemoryGitHub github = new();
    private ApprovalObserver Observer => new(github, github, clock);

    // App authorization

    [Fact]
    public async Task App_authorization_signs_once_for_exactly_the_reserved_window()
    {
        var window = Window();
        await Observer.AuthorizeAsync(window, Token);
        Assert.Equal<AppAuthorizationWindow>([window], github.Windows);
        Assert.Equal(1, github.Authorizations);
    }

    [Fact]
    public async Task Expired_window_is_never_signed()
    {
        var window = Window();
        clock.Advance(TimeSpan.FromSeconds(120));
        await Assert.ThrowsAsync<IssuanceDenied>(() => Observer.AuthorizeAsync(window, Token));
        Assert.Equal(0, github.Authorizations);
    }

    [Fact]
    public async Task Signing_that_outlasts_the_window_denies()
    {
        github.BeforeAuthorize = () => { clock.Advance(TimeSpan.FromSeconds(121)); return Task.CompletedTask; };
        await Assert.ThrowsAsync<IssuanceDenied>(() => Observer.AuthorizeAsync(Window(), Token));
        Assert.Equal(1, github.Authorizations);
    }

    [Theory]
    [InlineData(-60, 120, true)]
    [InlineData(-59, 120, false)]
    [InlineData(-60, 121, false)]
    [InlineData(0, 180, false)]
    public void Window_claims_are_fixed_by_the_reservation(int issued, int expires, bool valid)
    {
        var reserved = clock.GetUtcNow();
        AppAuthorizationWindow Reserved() => AppAuthorizationWindow.Reserved(reserved, reserved.AddSeconds(issued), reserved.AddSeconds(expires));
        if (valid) Assert.Equal(new AppAuthorizationWindow(reserved.AddSeconds(-60), reserved.AddSeconds(120)), Reserved());
        else Assert.Throws<IssuanceDenied>(Reserved);
    }

    // Controller enrollment (runtime issuance)

    [Theory]
    [InlineData("legacy", 4, "read variable environment approval/environment owner/project APPROVAL")]
    [InlineData("agent", 3, "read variable agents owner/project APPROVAL")]
    [InlineData("actions", 3, "read variable actions owner/project REPOAGENT_APPROVAL")]
    public async Task Unchanged_approval_is_read_from_its_contracts_own_variable(string kind, int reads, string read)
    {
        var approval = Approval(kind);
        Publish(approval, approval.Marker);
        Assert.Equal("approved", await ObserveAsync(approval));
        Assert.Equal(reads, github.Reads);
        Assert.Contains(read, github.Requests);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("actions")]
    public async Task Marker_in_another_contracts_variable_is_withdrawal(string kind)
    {
        var approval = Approval(kind);
        (kind == "agent" ? github.Actions : github.Agent)[approval.Variable] = approval.Marker;
        Assert.Equal("changed", await ObserveAsync(approval));
    }

    [Theory]
    [InlineData("nonpositive-id")]
    [InlineData("other-app")]
    [InlineData("other-slug")]
    [InlineData("suspended")]
    [InlineData("missing-runtime-permission")]
    [InlineData("missing-verifier-permission")]
    [InlineData("read-for-write")]
    [InlineData("unreadable-runtime-permission")]
    [InlineData("refused")]
    [InlineData("not-found")]
    [InlineData("malformed")]
    public async Task Installation_that_is_not_this_Apps_usable_installation_denies_without_proving_a_change(string fault)
    {
        var approval = Approval("agent", profile: "webhook");
        Publish(approval, approval.Marker);
        github.Installation = fault switch
        {
            "refused" => new GitHubRead<AppInstallation>.Refused(403),
            "not-found" => new GitHubRead<AppInstallation>.NotFound(),
            "malformed" => new GitHubRead<AppInstallation>.Malformed(GitHubDefect.Shape),
            _ => GitHubRead.Found(Installation(fault))
        };
        Assert.Equal("denied", await ObserveAsync(approval));
    }

    [Fact]
    public async Task Unreadable_level_of_a_permission_the_broker_does_not_need_is_ignored()
    {
        var approval = Approval("agent");
        Publish(approval, approval.Marker);
        github.Installation = GitHubRead.Found(Installation("", ("pages", null)));
        Assert.Equal("approved", await ObserveAsync(approval));
    }

    [Fact]
    public async Task Write_permission_satisfies_a_read_requirement()
    {
        var approval = Approval("agent");
        Publish(approval, approval.Marker);
        github.Installation = GitHubRead.Found(Installation("", ("agent_variables", "write")));
        Assert.Equal("approved", await ObserveAsync(approval));
    }

    [Theory]
    [InlineData(false, "changed")]
    [InlineData(true, "denied")]
    public async Task Another_installation_is_a_change_only_while_the_evidence_is_fresh(bool stale, string outcome)
    {
        var approval = Approval("agent");
        Publish(approval, approval.Marker);
        github.Installation = GitHubRead.Found(Installation("") with { Id = 790 });
        Stale(stale, "installation");
        Assert.Equal(outcome, await ObserveAsync(approval));
    }

    [Theory]
    [InlineData("total-two")]
    [InlineData("total-zero")]
    [InlineData("none-listed")]
    [InlineData("two-listed")]
    [InlineData("nonpositive-id")]
    [InlineData("name-without-owner")]
    [InlineData("dot-segment")]
    [InlineData("refused")]
    [InlineData("malformed")]
    public async Task Repository_evidence_that_is_not_exactly_one_valid_repository_denies(string fault)
    {
        var approval = Approval("agent");
        Publish(approval, approval.Marker);
        github.Repositories = fault switch
        {
            "total-two" => Repositories(2, InMemoryGitHub.Project),
            "total-zero" => Repositories(0, InMemoryGitHub.Project),
            "none-listed" => Repositories(1),
            "two-listed" => Repositories(1, InMemoryGitHub.Project, new RepositoryIdentity(654321, "owner/other")),
            "nonpositive-id" => Repositories(1, new RepositoryIdentity(0, "owner/project")),
            "name-without-owner" => Repositories(1, new RepositoryIdentity(123456, "project")),
            "dot-segment" => Repositories(1, new RepositoryIdentity(123456, "owner/..")),
            "refused" => new GitHubRead<InstallationRepositories>.Refused(403),
            _ => new GitHubRead<InstallationRepositories>.Malformed(GitHubDefect.DuplicateMember)
        };
        Assert.Equal("denied", await ObserveAsync(approval));
    }

    [Theory]
    [InlineData(999999, "owner/project", false, "changed")]
    [InlineData(123456, "other/project", false, "changed")]
    [InlineData(999999, "owner/project", true, "denied")]
    [InlineData(123456, "other/project", true, "denied")]
    [InlineData(123456, "Owner/Project", false, "approved")]
    public async Task Another_repository_is_a_change_only_while_the_evidence_is_fresh(long id, string name, bool stale, string outcome)
    {
        var approval = Approval("agent");
        Publish(approval, approval.Marker);
        github.Repositories = Repositories(1, new RepositoryIdentity(id, name));
        Stale(stale, "repositories");
        Assert.Equal(outcome, await ObserveAsync(approval));
    }

    [Theory]
    [InlineData("changed", "changed")]
    [InlineData("removed", "changed")]
    [InlineData("removed-among-unrelated", "changed")]
    [InlineData("removed-with-unreadable-listing-value", "changed")]
    [InlineData("changed-stale", "denied")]
    [InlineData("removed-stale", "denied")]
    [InlineData("other-name", "denied")]
    [InlineData("unreadable-value", "denied")]
    [InlineData("refused", "denied")]
    [InlineData("listing-not-found", "denied")]
    [InlineData("listing-refused", "denied")]
    [InlineData("listing-shows-the-variable", "denied")]
    [InlineData("listing-of-two", "denied")]
    [InlineData("listing-negative-total", "denied")]
    [InlineData("listing-unnamed-variable", "denied")]
    public async Task Repository_variable_is_withdrawn_only_by_a_fresh_change_or_a_404_with_a_readable_listing(string observation, string outcome)
    {
        var approval = Approval("actions");
        Publish(approval, observation.StartsWith("changed", StringComparison.Ordinal) ? "changed-marker" : approval.Marker);
        if (observation.StartsWith("removed", StringComparison.Ordinal) || observation.StartsWith("listing", StringComparison.Ordinal))
            github.Actions.Remove(approval.Variable);
        if (observation == "removed-among-unrelated")
            foreach (var i in Enumerable.Range(0, 57)) github.Actions[$"CI_SETTING_{i:00}"] = "public";
        // A changed marker answers the named read late; a removal's evidence is complete only with the listing.
        if (observation == "changed-stale") Stale(true, "variable ");
        if (observation == "removed-stale") Stale(true, "variables ");
        GitHubVariable Variable(string name) => new(name, "public");
        github.VariableAnswer = observation switch
        {
            "other-name" => _ => GitHubRead.Found(Variable("REPOAGENT_OTHER")),
            "unreadable-value" => _ => GitHubRead.Found(new GitHubVariable(approval.Variable, null)),
            "refused" => _ => new GitHubRead<GitHubVariable>.Refused(403),
            _ => null
        };
        github.PageAnswer = observation switch
        {
            "listing-not-found" => (_, _) => new GitHubRead<GitHubVariablePage>.NotFound(),
            "listing-refused" => (_, _) => new GitHubRead<GitHubVariablePage>.Refused(403),
            "listing-shows-the-variable" => (_, _) => GitHubRead.Found(new GitHubVariablePage(1, [new(approval.Variable, approval.Marker)])),
            "listing-of-two" => (_, _) => GitHubRead.Found(new GitHubVariablePage(2, [Variable("A"), Variable("B")])),
            "listing-negative-total" => (_, _) => GitHubRead.Found(new GitHubVariablePage(-1, [])),
            "listing-unnamed-variable" => (_, _) => GitHubRead.Found(new GitHubVariablePage(1, [Variable("")])),
            "removed-with-unreadable-listing-value" => (_, _) => GitHubRead.Found(new GitHubVariablePage(1, [new("CI_SETTING", null)])),
            _ => null
        };
        Assert.Equal(outcome, await ObserveAsync(approval));
    }

    // Neither a malformed named read nor a malformed access-proof listing proves the variable gone.
    [Fact]
    public async Task Malformed_read_never_proves_withdrawal()
    {
        var approval = Approval("agent");
        github.VariableAnswer = _ => new GitHubRead<GitHubVariable>.Malformed(GitHubDefect.Shape);
        Assert.Equal("denied", await ObserveAsync(approval));
        github.VariableAnswer = _ => new GitHubRead<GitHubVariable>.NotFound();
        github.PageAnswer = (_, _) => new GitHubRead<GitHubVariablePage>.Malformed(GitHubDefect.Shape);
        Assert.Equal("denied", await ObserveAsync(approval));
    }

    [Fact]
    public async Task Isolated_404_is_absence_only_with_a_listing_read_by_the_same_verifier_token()
    {
        var approval = Approval("agent");
        Assert.Equal("changed", await ObserveAsync(approval));
        Assert.Equal<string>(["read variable agents owner/project APPROVAL", "read variables agents owner/project per_page=1"], github.Requests[^2..]);
        Assert.Equal<GitHubToken>([Verifier, Verifier, Verifier], github.VerifierTokens);
    }

    [Theory]
    [InlineData("environment-id", "changed")]
    [InlineData("environment-name", "changed")]
    [InlineData("marker", "changed")]
    [InlineData("unreadable-marker", "denied")]
    [InlineData("environment-refused", "denied")]
    [InlineData("variable-not-found", "denied")]
    public async Task Setup_v1_approval_changes_with_its_environment_or_marker(string observation, string outcome)
    {
        var approval = Approval("legacy");
        Publish(approval, observation == "marker" ? "changed-marker" : approval.Marker);
        github.Environment = observation switch
        {
            "environment-id" => GitHubRead.Found(new ApprovalEnvironment(988, "approval/environment")),
            "environment-name" => GitHubRead.Found(new ApprovalEnvironment(987, "approval/renamed")),
            "environment-refused" => new GitHubRead<ApprovalEnvironment>.Refused(403),
            _ => github.Environment
        };
        if (observation == "variable-not-found") github.EnvironmentVariables.Clear();
        if (observation == "unreadable-marker") github.VariableAnswer = _ => GitHubRead.Found(new GitHubVariable(approval.Variable, null));
        Assert.Equal(outcome, await ObserveAsync(approval));
    }

    [Fact]
    public async Task One_operation_reads_at_most_eight_times()
    {
        var approval = Approval("legacy");
        Publish(approval, approval.Marker);
        using var work = new IssuanceWork(clock);
        var app = await Observer.AuthorizeAsync(Window(), Token);
        await Observer.ObserveAsync(approval, app, Verifier, work);
        await Observer.ObserveAsync(approval, app, Verifier, work);
        await Assert.ThrowsAsync<IssuanceDenied>(() => Observer.ObserveAsync(approval, app, Verifier, work));
        Assert.Equal(8, github.Reads);
    }

    // Pending controller enrollment (setup)

    [Theory]
    [InlineData(null, "789")]
    [InlineData(789L, "789")]
    [InlineData(790L, "changed")]
    public async Task Discovery_finds_the_installation_and_a_bound_one_must_not_change(long? bound, string outcome)
    {
        var request = Request("agent") with { InstallationId = bound };
        Assert.Equal(outcome, await Outcome(async () => (await Observer.DiscoverAsync(request, await AppAsync(), Work())).ToString()));
    }

    [Fact]
    public async Task Discovery_denies_an_installation_that_lacks_the_verifier_permission()
    {
        github.Installation = GitHubRead.Found(Installation("missing-verifier-permission"));
        Assert.Equal("denied", await Outcome(async () => (await Observer.DiscoverAsync(Request("agent"), await AppAsync(), Work())).ToString()));
    }

    [Theory]
    [InlineData("OWNER/project", null, "owner/project")]
    [InlineData("owner/other", null, "denied")]
    [InlineData("owner/other", "published-marker", "changed")]
    public async Task Binding_takes_GitHubs_repository_name_and_a_mismatch_is_a_change_once_the_marker_is_published(string requested, string? marker, string outcome)
    {
        var request = Request("agent") with { RequestedRepository = requested, Marker = marker };
        Assert.Equal(outcome, await Outcome(async () => (await Observer.BindAsync(request, Verifier, Work())).Repository));
    }

    [Theory]
    [InlineData(987, "approval/environment", null, "987")]
    [InlineData(0, "approval/environment", null, "denied")]
    [InlineData(987, "approval/other", null, "denied")]
    [InlineData(988, "approval/environment", 987L, "changed")]
    public async Task Setup_v1_binding_requires_the_named_environment_and_it_must_not_change(long id, string name, long? bound, string outcome)
    {
        github.Environment = GitHubRead.Found(new ApprovalEnvironment(id, name));
        var request = Request("legacy") with { EnvironmentId = bound };
        Assert.Equal(outcome, await Outcome(async () => (await Observer.BindAsync(request, Verifier, Work())).EnvironmentId.ToString()!));
    }

    [Theory]
    [InlineData(false, "changed")]
    [InlineData(true, "denied")]
    public async Task Pending_binding_change_is_final_only_while_the_evidence_is_fresh(bool stale, string outcome)
    {
        var request = Bound(Request("agent"));
        github.Repositories = Repositories(1, new RepositoryIdentity(123456, "other/project"));
        Stale(stale, "repositories");
        Assert.Equal(outcome, await ObservePendingAsync(request));
    }

    [Theory]
    [InlineData("shown", "approved")]
    [InlineData("absent", "pending")]
    [InlineData("changed", "pending")]
    [InlineData("uncertain", "denied")]
    public async Task Pending_repository_variable_approval_is_observed_from_its_variable(string observation, string outcome)
    {
        var request = Bound(Request("actions"));
        if (observation == "shown") github.Actions[request.Variable] = request.Marker!;
        if (observation == "changed") github.Actions[request.Variable] = "earlier-marker";
        if (observation == "uncertain") github.VariableAnswer = _ => new GitHubRead<GitHubVariable>.Refused(403);
        Assert.Equal(outcome, await ObservePendingAsync(request));
    }

    [Theory]
    [InlineData("shown", "approved")]
    [InlineData("absent", "pending")]
    [InlineData("changed", "pending")]
    [InlineData("unrelated-without-value", "approved")]
    [InlineData("approval-without-value", "denied")]
    [InlineData("partial", "denied")]
    [InlineData("listing-not-found", "denied")]
    [InlineData("duplicate-name", "denied")]
    [InlineData("over-500", "denied")]
    [InlineData("total-changes-between-pages", "denied")]
    [InlineData("over-100-on-a-page", "denied")]
    public async Task Setup_v1_approval_needs_a_complete_environment_listing(string observation, string outcome)
    {
        var request = Bound(Request("legacy"));
        if (observation == "shown") github.EnvironmentVariables["APPROVAL"] = request.Marker!;
        if (observation == "changed") github.EnvironmentVariables["APPROVAL"] = "earlier-marker";
        var unrelated = new GitHubVariable("UNRELATED", "public");
        github.PageAnswer = observation switch
        {
            "partial" => (_, _) => GitHubRead.Found(new GitHubVariablePage(2, [unrelated])),
            "unrelated-without-value" => (_, _) => GitHubRead.Found(new GitHubVariablePage(2, [new("APPROVAL", request.Marker), new("UNRELATED", null)])),
            "approval-without-value" => (_, _) => GitHubRead.Found(new GitHubVariablePage(2, [new("APPROVAL", null), unrelated])),
            "listing-not-found" => (_, _) => new GitHubRead<GitHubVariablePage>.NotFound(),
            "duplicate-name" => (_, _) => GitHubRead.Found(new GitHubVariablePage(2, [unrelated, unrelated])),
            "over-500" => (_, _) => GitHubRead.Found(new GitHubVariablePage(501, [unrelated])),
            "total-changes-between-pages" => (_, page) => GitHubRead.Found(new GitHubVariablePage(page == 1 ? 150 : 151,
                [.. Enumerable.Range(page == 1 ? 0 : 100, page == 1 ? 100 : 50).Select(i => new GitHubVariable($"V{i:000}", "public"))])),
            "over-100-on-a-page" => (_, _) => GitHubRead.Found(new GitHubVariablePage(101,
                [.. Enumerable.Range(0, 101).Select(i => new GitHubVariable($"V{i:000}", "public"))])),
            _ => null
        };
        Assert.Equal(outcome, await ObservePendingAsync(request));
    }

    [Theory]
    [InlineData(100, 1)]
    [InlineData(150, 2)]
    [InlineData(499, 5)]
    public async Task Setup_v1_listing_completes_across_pages(int count, int pages)
    {
        var request = Bound(Request("legacy"));
        foreach (var i in Enumerable.Range(1, count - 1)) github.EnvironmentVariables[$"V{i:000}"] = "public";
        github.EnvironmentVariables["APPROVAL"] = request.Marker!;
        Assert.Equal("approved", await ObservePendingAsync(request));
        Assert.Equal(pages, github.Requests.Count(r => r.StartsWith("read variables", StringComparison.Ordinal)));
    }

    private AppAuthorizationWindow Window() => new(clock.GetUtcNow().AddSeconds(-60), clock.GetUtcNow().AddSeconds(120));

    private IssuanceWork Work() => new(clock);

    private Task<AppAuthorization> AppAsync() => Observer.AuthorizeAsync(Window(), Token);

    private static ControllerApproval Approval(string kind, string profile = "polling") => kind switch
    {
        "legacy" => new(ApprovalContract.Legacy, InMemoryGitHub.Project, 789, profile, "APPROVAL", "bound-marker", "approval/environment", 987),
        "agent" => new(ApprovalContract.Agent, InMemoryGitHub.Project, 789, profile, "APPROVAL", "bound-marker"),
        _ => new(ApprovalContract.Actions, InMemoryGitHub.Project, 789, profile, "REPOAGENT_APPROVAL", "bound-marker")
    };

    private static ApprovalRequest Request(string kind) => kind switch
    {
        "legacy" => new(ApprovalContract.Legacy, 123456, "owner/project", "polling", "APPROVAL", "approval/environment"),
        "agent" => new(ApprovalContract.Agent, 123456, "owner/project", "polling", "APPROVAL"),
        _ => new(ApprovalContract.Actions, 123456, "owner/project", "polling", "REPOAGENT_APPROVAL")
    };

    // A pending enrollment after start: installation, repository (and environment) bound, marker published.
    private static ApprovalRequest Bound(ApprovalRequest request) => request with
    {
        InstallationId = 789, Repository = "owner/project", Marker = "pending-marker",
        EnvironmentId = request.Contract == ApprovalContract.Legacy ? 987 : null
    };

    private void Publish(ControllerApproval approval, string marker) =>
        (approval.Contract.IsActions ? github.Actions : approval.Contract.IsAgent ? github.Agent : github.EnvironmentVariables)[approval.Variable] = marker;

    // The answer arrives eleven seconds after the read started: past freshness, within the operation's lifetime.
    private void Stale(bool stale, string read)
    {
        if (stale) github.BeforeRead = request =>
        {
            if (request.StartsWith(read, StringComparison.Ordinal)) clock.Advance(TimeSpan.FromSeconds(11));
            return Task.CompletedTask;
        };
    }

    private static AppInstallation Installation(string fault, params (string Name, string? Level)[] grants)
    {
        var permissions = new Dictionary<string, string?> { ["discussions"] = "write", ["repository_hooks"] = "write", ["agent_variables"] = "read" };
        if (fault == "missing-runtime-permission") permissions.Remove("repository_hooks");
        if (fault == "missing-verifier-permission") permissions.Remove("agent_variables");
        if (fault == "read-for-write") permissions["repository_hooks"] = "read";
        if (fault == "unreadable-runtime-permission") permissions["repository_hooks"] = null;
        foreach (var (name, level) in grants) permissions[name] = level;
        return new(fault == "nonpositive-id" ? 0 : 789, fault == "other-app" ? 1 : IssuancePolicy.AppId,
            fault == "other-slug" ? "other-app" : IssuancePolicy.AppSlug, fault == "suspended", permissions);
    }

    private static GitHubRead<InstallationRepositories> Repositories(int total, params RepositoryIdentity[] repositories) =>
        GitHubRead.Found(new InstallationRepositories(total, repositories));

    private async Task<string> ObserveAsync(ControllerApproval approval)
    {
        using var work = Work();
        var app = await AppAsync();
        return await Outcome(async () => { await Observer.ObserveAsync(approval, app, Verifier, work); return "approved"; });
    }

    private async Task<string> ObservePendingAsync(ApprovalRequest request)
    {
        using var work = Work();
        var app = await AppAsync();
        return await Outcome(async () => (await Observer.ObserveAsync(request, app, Verifier, work)).Approved ? "approved" : "pending");
    }

    private static async Task<string> Outcome(Func<Task<string>> observe)
    {
        try { return await observe(); }
        catch (ApprovalChanged) { return "changed"; }
        catch (IssuanceDenied) { return "denied"; }
    }
}
