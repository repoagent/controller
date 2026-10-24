using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using static RepoAgent.Auth.IssuancePolicy;
using static RepoAgent.Auth.TableValues;

namespace RepoAgent.Auth;

public sealed class InstallationTokenIssuer(IssuanceStore store, GitHubIssuance github, AppJwtSigner signer, TimeProvider clock)
{
    public async Task<IssuanceResult> IssueAsync(string deviceId, string? secret, CancellationToken caller, int runtimeVersion = 1)
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
            if (grant.Approval.RuntimeVersion != runtimeVersion) throw new IssuanceDenied(401);
            var verifier = await store.VerifierAsync(grant, work.Cancellation);
            if (verifier is null)
            {
                // This internal capability never requires runtime approval, reaches the caller,
                // activates a device or renews inactivity. Public enrollment consumes this in #52.
                verifier = await AcquireVerifierAsync(grant, work);
                grant = await store.SnapshotAsync(grant.Partition, grant.DeviceId, work.Cancellation);
            }
            if (await store.PublishedRuntimeAsync(grant, work.Cancellation) is { } published)
                return await ReuseAsync(grant, published, work, caller);
            (grant, runtime) = await store.ReserveAsync(grant, "runtime", work);
            verifier = await store.VerifierAsync(grant, work.Cancellation) ?? throw new IssuanceDenied();
            var verifierToken = store.Recover(grant, verifier);
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
            return Response(grant, known);
        }
        catch (Exception ex) when (Expected(ex))
        {
            // A lost release response is uncertain. Fence an unpublished runtime before revocation;
            // an already committed release can be shared and must remain valid.
            if (!released && known is not null && grant is not null && runtime is not null)
                await RevokeKnownAsync(grant, runtime, known.Token);
            return new(ex is IssuanceDenied denial ? denial.Status : 503);
        }
    }

    private async Task<IssuanceResult> ReuseAsync(GrantSnapshot grant, TableEntity attempt, IssuanceWork work, CancellationToken caller)
    {
        var reservation = await store.ReservePublishedObservationAsync(grant, attempt, work);
        grant = reservation.Grant;
        attempt = reservation.Attempt;
        var verifier = await store.VerifierAsync(grant, work.Cancellation) ?? throw new IssuanceDenied();
        var token = store.Recover(grant, attempt);
        var jwt = await signer.SignAsync(reservation.Jwt, work.Cancellation);
        var verifierToken = store.Recover(grant, verifier);
        var admission = await ObserveAsync(grant, jwt, verifierToken, work);
        var release = await ObserveAsync(grant, jwt, verifierToken, work);
        work.Fresh(admission);
        work.Fresh(release);
        caller.ThrowIfCancellationRequested();
        await store.RepublishAsync(grant, attempt, verifier, reservation.Jwt.RowKey, work.Cancellation);
        return Response(grant, new(token, Date(attempt, "ExpiresAt"), true));
    }

    private static IssuanceResult Response(GrantSnapshot grant, IssuedToken token)
    {
        var response = new Dictionary<string, object>
        {
            ["protocolVersion"] = grant.Approval.RuntimeVersion, ["appId"] = AppId, ["appSlug"] = AppSlug,
            ["installationId"] = grant.InstallationId, ["repositoryId"] = Id(grant.RepositoryId), ["repository"] = grant.Repository,
            ["permissions"] = Permissions(grant.Profile), ["token"] = token.Token, ["expiresAt"] = token.ExpiresAt
        };
        if (grant.Approval.NamesProfile)
        {
            response["profile"] = grant.Profile;
            response["policyVersion"] = grant.Approval.Policy;
        }
        return new(200, response);
    }

    public async Task<TableEntity> AcquireVerifierAsync(GrantSnapshot grant, IssuanceWork work)
    {
        TableEntity? attempt = null;
        IssuedToken? token = null;
        try
        {
            (grant, attempt) = await store.ReserveAsync(grant, "verification", work);
            var jwt = await signer.SignAsync(attempt, work.Cancellation);
            work.Check();
            attempt = await store.ClaimSendAsync(grant, attempt, work.Cancellation);
            token = await github.MintAsync(grant, grant.Approval.VerifierProfile, jwt, work.Cancellation);
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
            if (Text(attempt, "Purpose") == "runtime")
                attempt = await store.FenceUnpublishedRuntimeAsync(grant, attempt, completion.Token);
            await github.RevokeAsync(token, completion.Token);
            await store.MarkRevokedAsync(grant, attempt, completion.Token);
        }
        catch (Exception ex) when (Expected(ex)) { /* Preserve unknown/known evidence for explicit reconciliation. */ }
    }
    private static bool Expected(Exception ex) => ex is IssuanceDenied or RequestFailedException or AuthenticationFailedException
        or HttpRequestException or IOException or InvalidDataException or InvalidCastException or FormatException or JsonException
        or OverflowException or OperationCanceledException or CryptographicException or KeyNotFoundException or InvalidOperationException or ArgumentException
        or SecretProtectionFailed;
}
