using Azure.Data.Tables;

namespace RepoAgent.Auth;

// Temporary API scaffold for the independent behavioral-red checkpoint.
public sealed class BrokerPayloadRedaction
{
    public BrokerPayloadRedaction(TableClient table, BrokerOptions options, TimeProvider clock)
    {
        _ = table;
        _ = options;
        _ = clock;
    }

    public Task RedactEnrollmentAsync(string idempotencyId, string generation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task RedactIssuanceAsync(string operationId, string generation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
