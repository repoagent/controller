using static RepoAgent.Auth.IssuancePolicy;

namespace RepoAgent.Auth;

// GitHub confirmed, with fresh well-formed evidence, that the approval or its binding changed. Callers close the
// enrollment for good; uncertain evidence denies instead (IssuanceDenied) and leaves the enrollment recoverable.
public sealed class ApprovalChanged : Exception;

// A controller enrollment's bound approval, which GitHub must still show unchanged before the broker issues.
public sealed record ControllerApproval(ApprovalContract Contract, RepositoryIdentity Repository, long InstallationId,
    string Profile, string Variable, string Marker, string? Environment = null, long? EnvironmentId = null);

// What a pending controller enrollment asked for and, as setup proceeds, bound. Values are null until bound.
public sealed record ApprovalRequest(ApprovalContract Contract, long RepositoryId, string RequestedRepository, string Profile,
    string Variable, string? Environment = null, long? InstallationId = null, long? EnvironmentId = null,
    string? Repository = null, string? Marker = null);

// GitHub's canonical repository name, and for setup v1 the approval environment, that setup binds.
public sealed record ApprovalBinding(string Repository, string? Environment, long? EnvironmentId);

// Whether GitHub showed the pending approval's marker, and when the oldest contributing read started.
public sealed record ApprovalObservation(bool Approved, DateTimeOffset Started);

// Decides what GitHub's answers prove about controller approvals. Every read is charged to the operation's
// work budget first. Only well-formed, fresh evidence can prove a change or a withdrawal; anything else denies.
public sealed class ApprovalObserver(IAppInstallations installations, IControllerApprovals approvals, TimeProvider clock)
{
    // One App signature for the reserved window, never at or after its expiry, even when signing is slow.
    public async Task<AppAuthorization> AuthorizeAsync(AppAuthorizationWindow window, CancellationToken ct)
    {
        if (window.ExpiresAt <= clock.GetUtcNow()) throw new IssuanceDenied();
        var app = await installations.AuthorizeAsync(window, ct);
        ct.ThrowIfCancellationRequested();
        if (window.ExpiresAt <= clock.GetUtcNow()) throw new IssuanceDenied();
        return app;
    }

    // Returns when the oldest read started. A binding change is final only while that read is fresh.
    public async Task<DateTimeOffset> ObserveAsync(ControllerApproval approval, AppAuthorization app, GitHubToken verifier, IssuanceWork work)
    {
        var started = clock.GetUtcNow();
        work.Read();
        var installation = Installed(await installations.FindAsync(app, approval.Repository.FullName, work.Cancellation), approval.Profile, approval.Contract);
        if (installation != approval.InstallationId)
        {
            work.Fresh(started);
            throw new ApprovalChanged();
        }
        work.Read();
        var repository = OnlyRepository(await installations.RepositoriesAsync(verifier, work.Cancellation));
        if (!Same(repository, approval.Repository.Id, approval.Repository.FullName))
        {
            work.Fresh(started);
            throw new ApprovalChanged();
        }
        var variable = new ApprovalVariable(approval.Contract, approval.Repository.FullName, approval.Environment, approval.Variable);
        if (approval.Contract.IsRepositoryVariable)
        {
            var approved = await ObserveRepositoryVariableAsync(variable, approval.Marker, verifier, work);
            // A negative observation is authority to close only while all contributing
            // evidence is fresh. Stale evidence must leave the grant recoverable.
            work.Fresh(started);
            if (!approved) throw new ApprovalChanged();
            return started;
        }
        var name = approval.Environment ?? throw new IssuanceDenied();
        work.Read();
        var environment = Value(await approvals.EnvironmentAsync(verifier, approval.Repository.FullName, name, work.Cancellation));
        if (environment.Id != approval.EnvironmentId || environment.Name != name) throw new ApprovalChanged();
        work.Read();
        var marker = Value(await approvals.VariableAsync(verifier, variable, work.Cancellation));
        // A variable without a readable value is no evidence, so it never proves a change.
        if (marker.Value is null) throw new IssuanceDenied();
        if (marker.Name != approval.Variable || marker.Value != approval.Marker) throw new ApprovalChanged();
        work.Fresh(started);
        return started;
    }

    // Candidate repository identity is never authority until both the installation and the verifier agree.
    public async Task<long> DiscoverAsync(ApprovalRequest request, AppAuthorization app, IssuanceWork work)
    {
        work.Read();
        var installation = Installed(await installations.FindAsync(app, request.RequestedRepository, work.Cancellation), request.Profile, request.Contract);
        if (request.InstallationId is { } bound && bound != installation) throw new ApprovalChanged();
        return installation;
    }

    public async Task<ApprovalBinding> BindAsync(ApprovalRequest request, GitHubToken verifier, IssuanceWork work)
    {
        work.Read();
        var observed = OnlyRepository(await installations.RepositoriesAsync(verifier, work.Cancellation));
        // Before a marker is published the request was only a claim; afterwards a mismatch is a change.
        if (!Same(observed, request.RepositoryId, request.RequestedRepository))
            throw request.Marker is null ? new IssuanceDenied() : new ApprovalChanged();
        if (request.Contract.IsRepositoryVariable) return new(observed.FullName, null, null);
        var name = request.Environment ?? throw new IssuanceDenied();
        work.Read();
        var environment = Value(await approvals.EnvironmentAsync(verifier, observed.FullName, name, work.Cancellation));
        if (environment.Id <= 0 || environment.Name != name) throw new IssuanceDenied();
        if (request.EnvironmentId is { } bound && bound != environment.Id) throw new ApprovalChanged();
        return new(observed.FullName, environment.Name, environment.Id);
    }

    public async Task<ApprovalObservation> ObserveAsync(ApprovalRequest request, AppAuthorization app, GitHubToken verifier, IssuanceWork work)
    {
        var started = clock.GetUtcNow();
        try
        {
            await DiscoverAsync(request, app, work);
            await BindAsync(request, verifier, work);
        }
        catch (ApprovalChanged)
        {
            // Early binding mismatches must satisfy the same oldest-read freshness
            // boundary as marker observations before the service may close a grant.
            work.Fresh(started);
            throw;
        }
        var repository = request.Repository ?? throw new IssuanceDenied();
        var marker = request.Marker ?? throw new IssuanceDenied();
        var variable = new ApprovalVariable(request.Contract, repository, request.Environment, request.Variable);
        if (request.Contract.IsRepositoryVariable)
        {
            var approved = await ObserveRepositoryVariableAsync(variable, marker, verifier, work);
            work.Fresh(started);
            return new(approved, started);
        }
        // A complete listing supplies positive absence evidence; a lone 404 never does.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var shown = false;
        int? total = null;
        for (var page = 1; page <= 5; page++)
        {
            work.Read();
            var listing = Value(await approvals.VariablesAsync(verifier, variable, 100, page, work.Cancellation));
            if (listing.TotalCount is < 0 or > 500 || total is not null && total != listing.TotalCount) throw new IssuanceDenied();
            total = listing.TotalCount;
            if (listing.Variables.Count > 100) throw new IssuanceDenied();
            foreach (var item in listing.Variables)
            {
                if (!seen.Add(item.Name)) throw new IssuanceDenied();
                // Only the approval variable's value is read; without a readable value it proves nothing.
                if (item.Name == variable.Name) shown = (item.Value ?? throw new IssuanceDenied()) == marker;
            }
            if (seen.Count == total && listing.Variables.Count < 100 || seen.Count == total && total == page * 100)
            {
                work.Fresh(started);
                return new(shown, started);
            }
            if (listing.Variables.Count < 100 || seen.Count > total) throw new IssuanceDenied();
        }
        throw new IssuanceDenied();
    }

    // Reads the approval variable by name from the one location its contract chooses. Work is at most two reads,
    // however many unrelated variables (CI, repository settings) share that location.
    private async Task<bool> ObserveRepositoryVariableAsync(ApprovalVariable variable, string marker, GitHubToken verifier, IssuanceWork work)
    {
        work.Read();
        switch (await approvals.VariableAsync(verifier, variable, work.Cancellation))
        {
            case GitHubRead<GitHubVariable>.Found { Value: var found }:
                // GitHub answers with the stored name. Another name, or no readable value, is malformed evidence, never a change.
                if (found.Name != variable.Name || found.Value is null) throw new IssuanceDenied();
                return found.Value == marker;
            case GitHubRead<GitHubVariable>.NotFound:
                break;
            default:
                throw new IssuanceDenied();
        }
        // GitHub also answers 404 when this token cannot read the location (lost repository access or
        // permission), so an isolated 404 never proves withdrawal. A well-formed listing page read
        // afterwards with the same token proves the location was readable, which confirms the 404 as
        // absence. Reading it after the 404 means access lost before or during the named read is caught.
        // The page is only an access proof: its count never matters, and a page showing the selected
        // name contradicts the 404 and proves nothing.
        work.Read();
        var page = Value(await approvals.VariablesAsync(verifier, variable, 1, null, work.Cancellation));
        if (page.TotalCount < 0 || page.Variables.Count > 1) throw new IssuanceDenied();
        foreach (var item in page.Variables)
            if (item.Name.Length == 0 || item.Name == variable.Name) throw new IssuanceDenied();
        return false;
    }

    // An installation is usable only when it is this App's, active, and grants both the enrollment's
    // runtime authority and its verifier's.
    private static long Installed(GitHubRead<AppInstallation> read, string profile, ApprovalContract contract)
    {
        var installation = Value(read);
        if (installation.Id <= 0 || installation.AppId != AppId || installation.AppSlug != AppSlug || installation.Suspended)
            throw new IssuanceDenied();
        var runtime = RuntimeAuthority.For(contract, profile);
        var verifier = RuntimeAuthority.Verifier(contract);
        if (!runtime.IsGrantedBy(installation.Permissions) || !verifier.IsGrantedBy(installation.Permissions)) throw new IssuanceDenied();
        return installation.Id;
    }

    // The verifier token reaches exactly one repository. Shape is checked apart from comparison:
    // malformed evidence never proves a change.
    private static RepositoryIdentity OnlyRepository(GitHubRead<InstallationRepositories> read)
    {
        var page = Value(read);
        if (page.TotalCount != 1 || page.Repositories is not [var repository] || repository.Id <= 0 || !IsRepositoryName(repository.FullName))
            throw new IssuanceDenied();
        return repository;
    }

    private static bool Same(RepositoryIdentity observed, long id, string name) =>
        observed.Id == id && string.Equals(observed.FullName, name, StringComparison.OrdinalIgnoreCase);

    // Only a well-formed 200 is evidence; any other answer denies without proving anything.
    private static T Value<T>(GitHubRead<T> read) => read is GitHubRead<T>.Found found ? found.Value : throw new IssuanceDenied();
}
