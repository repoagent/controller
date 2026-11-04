using static RepoAgent.Auth.IssuancePolicy;
using static RepoAgent.Auth.IssuanceValues;

namespace RepoAgent.Auth;

// Closure stops new publication. Previously issued tokens expire at GitHub.
public sealed class ControllerSelfRevocation(IBrokerState state, string epoch, TimeProvider clock)
{
    public async Task<int> RevokeAsync(string enrollmentId, string? secret, CancellationToken cancellationToken, int runtimeVersion = 1)
    {
        try
        {
            var hash = ControllerSecret.Hash(secret) ?? throw new IssuanceDenied(401);
            var store = new CurrentRepositoryStore(state, epoch, clock);
            var read = await store.FindAsync(enrollmentId, cancellationToken);
            // Missing UUID routing proves that this broker cannot authorize that frozen ID.
            // Keep DELETE retries successful after closure removes its locator, without history.
            if (read is null) return LegacyEnrollmentLocator.IsLegacyId(enrollmentId) ? 204 : 401;
            var row = read.Value;
            if (row.EnrollmentId != enrollmentId) return 401;
            if (row.State == "closed") return 204;
            if (row.Approval.RuntimeVersion != runtimeVersion || Date(row.ExpiresAt) <= clock.GetUtcNow()
                || !ControllerSecret.Matches(hash, Text(row.ControllerSecretHash))) return 401;
            row.Close();
            await store.SaveAsync(read, cancellationToken);
            return 204;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (Answerable(ex)) { return ex is IssuanceDenied denied ? denied.Status : 503; }
    }
}
