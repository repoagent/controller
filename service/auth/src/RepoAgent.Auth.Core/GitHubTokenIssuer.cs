using static RepoAgent.Auth.IssuancePolicy;

namespace RepoAgent.Auth;

public sealed class GitHubTokenIssuer(IssuanceStore store, IGitHubTokens tokens, IAppInstallations installations,
    IControllerApprovals approvals, TimeProvider clock)
{
    private readonly ApprovalObserver observer = new(installations, approvals, clock);
    private readonly GitHubTokenMinter minter = new(tokens, clock);

    public async Task<IssuanceResult> IssueAsync(string enrollmentId, string? secret, CancellationToken caller, int runtimeVersion = 1)
    {
        using var work = new IssuanceWork(clock);
        GrantSnapshot? grant = null;
        StoredAttempt? runtime = null;
        KnownGitHubToken? known = null;
        var released = false;
        try
        {
            caller.ThrowIfCancellationRequested();
            grant = await store.AuthenticateAsync(enrollmentId, secret, work.Cancellation);
            if (grant.Approval.RuntimeVersion != runtimeVersion) throw new IssuanceDenied(401);
            if (await store.ReclaimForRenewalAsync(grant, work))
                grant = await store.AuthenticateAsync(enrollmentId, secret, work.Cancellation);
            var verifier = await store.VerifierAsync(grant, work.Cancellation);
            if (verifier is null)
            {
                // This internal capability never requires runtime approval, reaches the caller,
                // activates a controller enrollment or renews inactivity. Public enrollment consumes this in #52.
                verifier = await AcquireVerifierAsync(grant, work);
                grant = await store.SnapshotAsync(grant.PartitionRepositoryId, grant.EnrollmentId, work.Cancellation);
            }
            if (await store.PublishedRuntimeAsync(grant, work.Cancellation) is { } published)
                return await ReuseAsync(grant, published, work, caller);
            (grant, runtime) = await store.ReserveAsync(grant, "runtime", work);
            verifier = await store.VerifierAsync(grant, work.Cancellation) ?? throw new IssuanceDenied();
            var verifierToken = store.Recover(grant, verifier);
            var app = await observer.AuthorizeAsync(IssuanceStore.ReservedWindow(runtime.Value), work.Cancellation);
            work.Check();
            var admission = await ObserveAsync(grant, app, verifierToken, work);
            work.Fresh(admission);
            caller.ThrowIfCancellationRequested();
            runtime = await store.ClaimSendAsync(grant, runtime, work.Cancellation);
            // From the durable send claim onward only the bounded server-owned lifetime controls
            // completion. Client disconnect cannot cancel accounting or cause a replay.
            known = await minter.MintAsync(app, grant.InstallationId, grant.BoundRepository, RuntimeAuthority.For(grant.Approval, grant.Profile), work.Cancellation);
            runtime = await store.RecordKnownAsync(grant, runtime, known, work.Cancellation);
            if (!known.ScopeValid) throw new IssuanceDenied();
            grant = await store.SnapshotAsync(grant.PartitionRepositoryId, grant.EnrollmentId, work.Cancellation);
            grant.RequireActive(clock.GetUtcNow());
            var release = await ObserveAsync(grant, app, verifierToken, work);
            work.Fresh(admission);
            work.Fresh(release);
            await store.ReleaseAsync(grant, runtime, verifier, work.Cancellation);
            released = true;
            return Response(grant, known.Token);
        }
        catch (Exception ex) when (Answerable(ex))
        {
            // A lost release response is uncertain. Fence an unpublished runtime before revocation;
            // an already committed release can be shared and must remain valid.
            if (!released && known is not null && grant is not null && runtime is not null)
                await RevokeKnownAsync(grant, runtime, known.Token);
            return ex is IssuanceDenied denial ? new(denial.Status) { Rejection = denial.Reason } : new(503);
        }
    }

    private async Task<IssuanceResult> ReuseAsync(GrantSnapshot grant, StoredAttempt attempt, IssuanceWork work, CancellationToken caller)
    {
        var reservation = await store.ReservePublishedObservationAsync(grant, attempt, work);
        grant = reservation.Grant;
        attempt = reservation.Attempt;
        var verifier = await store.VerifierAsync(grant, work.Cancellation) ?? throw new IssuanceDenied();
        var token = store.Recover(grant, attempt);
        var observation = reservation.Observation;
        var app = await observer.AuthorizeAsync(AppAuthorizationWindow.Reserved(observation.ReservedAt, observation.JwtIssuedAt,
            observation.JwtExpiresAt), work.Cancellation);
        var verifierToken = store.Recover(grant, verifier);
        var admission = await ObserveAsync(grant, app, verifierToken, work);
        var release = await ObserveAsync(grant, app, verifierToken, work);
        work.Fresh(admission);
        work.Fresh(release);
        caller.ThrowIfCancellationRequested();
        await store.RepublishAsync(grant, attempt, verifier, observation.Id, work.Cancellation);
        return Response(grant, token);
    }

    private static IssuanceResult Response(GrantSnapshot grant, GitHubToken token)
    {
        var response = new Dictionary<string, object>
        {
            ["protocolVersion"] = grant.Approval.RuntimeVersion,
            ["appId"] = AppId,
            ["appSlug"] = AppSlug,
            ["installationId"] = grant.InstallationId,
            ["repositoryId"] = Id(grant.RepositoryId),
            ["repository"] = grant.Repository,
            ["permissions"] = RuntimeAuthority.For(grant.Approval, grant.Profile).Permissions,
            ["token"] = token.Value,
            ["expiresAt"] = token.ExpiresAt
        };
        if (grant.Approval.NamesProfile)
        {
            response["profile"] = grant.Profile;
            response["policyVersion"] = grant.Approval.Policy;
        }
        return new(200, response);
    }

    public async Task<StoredAttempt> AcquireVerifierAsync(GrantSnapshot grant, IssuanceWork work)
    {
        StoredAttempt? attempt = null;
        KnownGitHubToken? token = null;
        try
        {
            (grant, attempt) = await store.ReserveAsync(grant, "verification", work);
            var app = await observer.AuthorizeAsync(IssuanceStore.ReservedWindow(attempt.Value), work.Cancellation);
            work.Check();
            attempt = await store.ClaimSendAsync(grant, attempt, work.Cancellation);
            token = await minter.MintAsync(app, grant.InstallationId, grant.BoundRepository,
                RuntimeAuthority.Verifier(grant.Approval), work.Cancellation);
            attempt = await store.RecordKnownAsync(grant, attempt, token, work.Cancellation);
            if (!token.ScopeValid) throw new IssuanceDenied();
            return attempt;
        }
        catch (Exception ex) when (Answerable(ex))
        {
            if (token is not null && attempt is not null) await RevokeKnownAsync(grant, attempt, token.Token);
            throw;
        }
    }

    private async Task<DateTimeOffset> ObserveAsync(GrantSnapshot grant, AppAuthorization app, GitHubToken verifier, IssuanceWork work)
    {
        try { return await observer.ObserveAsync(grant.BoundApproval(), app, verifier, work); }
        catch (ApprovalChanged)
        {
            await store.CloseChangedApprovalAsync(grant, work.Cancellation);
            throw new IssuanceDenied(401);
        }
    }

    private async Task RevokeKnownAsync(GrantSnapshot grant, StoredAttempt attempt, GitHubToken token)
    {
        // Finite finalization allowance after the operation deadline; no fire-and-forget work.
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(6), clock);
        try
        {
            if (IssuanceValues.Text(attempt.Value.Purpose) == "runtime")
                attempt = await store.FenceUnpublishedRuntimeAsync(grant, attempt, completion.Token);
            await minter.RevokeAsync(token, completion.Token);
            await store.MarkRevokedAsync(grant, attempt, completion.Token);
        }
        catch (Exception ex) when (Answerable(ex)) { /* Preserve unknown/known evidence for explicit reconciliation. */ }
    }
}
