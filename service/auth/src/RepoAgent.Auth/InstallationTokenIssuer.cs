using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using static RepoAgent.Auth.IssuancePolicy;

namespace RepoAgent.Auth;

public sealed class InstallationTokenIssuer(IssuanceStore store, GitHubIssuance github, AppJwtSigner signer, TimeProvider clock)
{
    public async Task<IssuanceResult> IssueAsync(string deviceId, string? secret, CancellationToken caller)
    {
        using var work = new IssuanceWork(clock);
        GrantSnapshot? grant = null;
        TableEntity? runtime = null;
        IssuedToken? known = null;
        var released = false;
        try
        {
            caller.ThrowIfCancellationRequested();
            grant = await store.AuthenticateAsync(deviceId, secret, work.Cancellation);
            var verifier = await store.VerifierAsync(grant, work.Cancellation);
            if (verifier is null)
            {
                // This internal capability never requires runtime approval, reaches the caller,
                // activates a device or renews inactivity. Public enrollment consumes this in #52.
                verifier = await AcquireVerifierAsync(grant, work);
                grant = await store.SnapshotAsync(grant.Partition, grant.DeviceId, work.Cancellation);
            }
            var verifierToken = store.Recover(grant, verifier);
            (grant, runtime) = await store.ReserveAsync(grant, "runtime", work.Cancellation);
            var jwt = await signer.SignAsync(runtime, work.Cancellation);
            work.Check();
            var admission = await ObserveAsync(grant, jwt, verifierToken, work);
            work.Fresh(admission);
            caller.ThrowIfCancellationRequested();
            runtime = await store.ClaimSendAsync(grant, runtime, work.Cancellation);
            // From the durable send claim onward only the bounded server-owned lifetime controls
            // completion. Client disconnect cannot cancel accounting or cause a replay.
            known = await github.MintAsync(grant, grant.Profile, jwt, work.Cancellation);
            runtime = await store.RecordKnownAsync(grant, runtime, known, work.Cancellation);
            if (!known.ScopeValid) throw new IssuanceDenied();
            grant = await store.SnapshotAsync(grant.Partition, grant.DeviceId, work.Cancellation);
            grant.RequireActive(clock.GetUtcNow());
            var release = await ObserveAsync(grant, jwt, verifierToken, work);
            work.Fresh(admission);
            work.Fresh(release);
            await store.ReleaseAsync(grant, runtime, verifier, work.Cancellation);
            released = true;
            return new(200, new
            {
                protocolVersion = 1, appId = AppId, appSlug = AppSlug,
                installationId = grant.InstallationId, repositoryId = Id(grant.RepositoryId), repository = grant.Repository,
                permissions = Permissions(grant.Profile), token = known.Token, expiresAt = known.ExpiresAt
            });
        }
        catch (Exception ex) when (Expected(ex))
        {
            // A lost release response is deliberately treated as uncertain: revoke when possible,
            // retaining either the committed release or send/known record if reconciliation fails.
            if (!released && known is not null && grant is not null && runtime is not null)
                await RevokeKnownAsync(grant, runtime, known.Token);
            return new(ex is IssuanceDenied denial ? denial.Status : 503);
        }
    }

    public async Task<TableEntity> AcquireVerifierAsync(GrantSnapshot grant, IssuanceWork work)
    {
        TableEntity? attempt = null;
        IssuedToken? token = null;
        try
        {
            (grant, attempt) = await store.ReserveAsync(grant, "verification", work.Cancellation);
            var jwt = await signer.SignAsync(attempt, work.Cancellation);
            work.Check();
            attempt = await store.ClaimSendAsync(grant, attempt, work.Cancellation);
            token = await github.MintAsync(grant, "verification", jwt, work.Cancellation);
            attempt = await store.RecordKnownAsync(grant, attempt, token, work.Cancellation);
            if (!token.ScopeValid) throw new IssuanceDenied();
            return attempt;
        }
        catch (Exception ex) when (Expected(ex))
        {
            if (token is not null && attempt is not null) await RevokeKnownAsync(grant, attempt, token.Token);
            throw;
        }
    }

    private async Task<DateTimeOffset> ObserveAsync(GrantSnapshot grant, string jwt, string verifier, IssuanceWork work)
    {
        try { return await github.ObserveAsync(grant, jwt, verifier, work); }
        catch (ApprovalChanged)
        {
            await store.CloseChangedApprovalAsync(grant, work.Cancellation);
            throw new IssuanceDenied(401);
        }
    }
    private async Task RevokeKnownAsync(GrantSnapshot grant, TableEntity attempt, string token)
    {
        // Finite finalization allowance after the operation deadline; no fire-and-forget work.
        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        try
        {
            await github.RevokeAsync(token, completion.Token);
            await store.MarkRevokedAsync(grant, attempt, completion.Token);
        }
        catch (Exception ex) when (Expected(ex)) { /* Preserve unknown/known evidence for explicit reconciliation. */ }
    }
    private static bool Expected(Exception ex) => ex is IssuanceDenied or RequestFailedException or AuthenticationFailedException
        or HttpRequestException or IOException or InvalidDataException or InvalidCastException or FormatException or JsonException
        or OverflowException or OperationCanceledException or CryptographicException or KeyNotFoundException or InvalidOperationException or ArgumentException;
}
