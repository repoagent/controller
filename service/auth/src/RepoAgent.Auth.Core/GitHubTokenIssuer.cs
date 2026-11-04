using static RepoAgent.Auth.IssuancePolicy;
using static RepoAgent.Auth.IssuanceValues;

namespace RepoAgent.Auth;

// A fresh token per request. Only authorization/activity and minute counters are persisted.
public sealed class GitHubTokenIssuer(CurrentRepositoryStore store, IGitHubTokens tokens, IAppInstallations installations,
    IControllerApprovals approvals, TimeProvider clock)
{
    public async Task<IssuanceResult> IssueAsync(string enrollmentId, string? secret, CancellationToken caller, int runtimeVersion = 1)
    {
        using var work = new IssuanceWork(clock);
        await using var credentials = new RequestTokens(tokens, installations, clock);
        Versioned<CurrentRepository>? read = null;
        try
        {
            caller.ThrowIfCancellationRequested();
            read = await store.AuthenticateAsync(enrollmentId, secret, runtimeVersion, work.Cancellation);
            read = await store.ChargeAsync(read, 2, work.Cancellation);
            var row = read.Value;
            var observer = new ApprovalObserver(installations, approvals, clock);
            var app = await credentials.AuthorizeAsync(work.Cancellation);
            var verifier = await credentials.MintAsync(app, Number(row.InstallationId), row.Identity,
                RuntimeAuthority.Verifier(row.Approval), work.Cancellation);
            var admission = await observer.ObserveAsync(row.BoundApproval(), app, verifier, work);
            caller.ThrowIfCancellationRequested();
            var runtime = await credentials.MintAsync(app, Number(row.InstallationId), row.Identity,
                RuntimeAuthority.For(row.Approval, Text(row.Profile)), work.Cancellation);
            var release = await observer.ObserveAsync(row.BoundApproval(), app, verifier, work);
            await credentials.RevokeAsync(verifier);
            work.Fresh(admission);
            work.Fresh(release);
            caller.ThrowIfCancellationRequested();
            if (Date(row.ExpiresAt) <= clock.GetUtcNow()) throw new IssuanceDenied(401);
            row.ExpiresAt = row.NextCleanupAt = clock.GetUtcNow().AddDays(90);
            await store.SaveAsync(read, work.Cancellation);
            work.Fresh(admission);
            work.Fresh(release);
            caller.ThrowIfCancellationRequested();
            var response = Response(row, runtime);
            credentials.Published(runtime);
            return response;
        }
        catch (ApprovalChanged)
        {
            if (read is null) return new(401);
            try { read.Value.Close(); await store.SaveAsync(read, work.Cancellation); return new(401); }
            catch (Exception ex) when (Answerable(ex)) { return new(503); }
        }
        catch (Exception ex) when (Answerable(ex))
        { return ex is IssuanceDenied denied ? new(denied.Status) { Rejection = denied.Reason } : new(503); }
    }
    private static IssuanceResult Response(CurrentRepository row, GitHubToken token)
    {
        var response = new Dictionary<string, object>
        {
            ["protocolVersion"] = row.Approval.RuntimeVersion,
            ["appId"] = AppId,
            ["appSlug"] = AppSlug,
            ["installationId"] = Number(row.InstallationId),
            ["repositoryId"] = Number(row.RepositoryId),
            ["repository"] = Text(row.Repository),
            ["permissions"] = RuntimeAuthority.For(row.Approval, Text(row.Profile)).Permissions,
            ["token"] = token.Value,
            ["expiresAt"] = token.ExpiresAt
        };
        if (row.Approval.NamesProfile) { response["profile"] = Text(row.Profile); response["policyVersion"] = row.Approval.Policy; }
        return new(200, response);
    }
}
