using Azure.Data.Tables;
using static RepoAgent.Auth.IssuancePolicy;
using static RepoAgent.Auth.TableValues;

namespace RepoAgent.Auth;

// Fixed retained allocations and exact outstanding reservations. Quotas are initialized offline.
internal sealed class RetainedState(TableClient table)
{
    internal const string Enrollment = "global:enrollment-v1", Issuance = "global:issuance-v1";

    internal async Task<(TableEntity Enrollment, TableEntity Issuance)> SchemaAsync(CancellationToken ct)
    {
        var enrollment = await BrokerTable.ReadAsync(table, Enrollment, "quota", ct) ?? throw new IssuanceDenied();
        var issuance = await BrokerTable.ReadAsync(table, Issuance, "quota", ct) ?? throw new IssuanceDenied();
        if (Number(enrollment, "RetentionSchemaVersion") != 1 || Number(issuance, "RetentionSchemaVersion") != 1
            || Number(enrollment, "CandidateCount") > 128 || Number(enrollment, "RetainedEnrollmentCount") > 256
            || Number(enrollment, "Count") > GlobalPending || Number(issuance, "RetainedIssuanceCount") > 4096
            || Number(issuance, "Slots") > GlobalAttempts
            || Number(enrollment, "Count") > Number(enrollment, "RetainedEnrollmentCount")
            || Number(issuance, "Slots") > Number(issuance, "RetainedIssuanceCount")) throw new IssuanceDenied();
        foreach (var field in new[] { "Window", "Admissions", "Reads", "Signs", "Mints" }) _ = Number(issuance, field);
        return (enrollment, issuance);
    }

    internal async Task<TableEntity?> DirectoryAsync(string repository, CancellationToken ct)
    {
        _ = Id(repository);
        var row = await BrokerTable.ReadAsync(table, Enrollment, "repository:" + repository, ct);
        if (row is not null && (Text(row, "RepositoryId") != repository || Number(row, "PendingCount") > RepositoryPending))
            throw new IssuanceDenied();
        return row;
    }

    internal static string SlotKey(long slot)
    {
        if (slot is < 0 or >= GlobalAttempts) throw new IssuanceDenied();
        return "slot:" + slot.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
    }

    internal async Task<TableEntity> RequireIssuanceAsync(string operation, string generation, string repository,
        string device, string purpose, string epoch, long index, CancellationToken ct)
    {
        var ledger = await BrokerTable.ReadAsync(table, Issuance, "operation:" + operation, ct) ?? throw new IssuanceDenied();
        var slot = await BrokerTable.ReadAsync(table, Issuance, SlotKey(index), ct) ?? throw new IssuanceDenied();
        if (ledger.ContainsKey("RetirementState") || ledger.ContainsKey("CapacityState") && ledger.GetString("CapacityState") != "charged"
            || Text(ledger, "DeviceId") != device || Number(ledger, "RowCredits") != 4
            || Text(ledger, "AttemptRowKey") != "attempt:" + operation) throw new IssuanceDenied();
        foreach (var row in new[] { ledger, slot })
        {
            if (Text(row, "OperationId") != operation || Text(row, "Generation") != generation
                || Text(row, "RepositoryId") != repository || Text(row, "Purpose") != purpose
                || Text(row, "Epoch") != epoch || Number(row, "GlobalSlot") != index) throw new IssuanceDenied();
            ApprovalContract.Read(ledger).Require(row);
        }
        return slot;
    }
}
