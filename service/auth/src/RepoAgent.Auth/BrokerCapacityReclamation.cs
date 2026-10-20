using Azure.Data.Tables;

namespace RepoAgent.Auth;

// Temporary local scaffold for meaningful capacity-regression tests; reclamation is not implemented.
public sealed class BrokerCapacityReclamation
{
    public BrokerCapacityReclamation(TableClient table, BrokerOptions options, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
    }

    public Task ReclaimIssuanceAsync(string operationId, string generation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task ReclaimEnrollmentAsync(string idempotencyId, string generation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
