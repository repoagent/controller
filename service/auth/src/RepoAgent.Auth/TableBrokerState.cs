using Azure;
using Azure.Data.Tables;

namespace RepoAgent.Auth;

// IBrokerState over the broker table. Records keep their partition and row keys, stored property names and EDM types;
// ETags are versions. Every write goes through BrokerTable's bounds. Any failure of the Table SDK or of those bounds
// becomes BrokerStateUnavailable, except that 409 and 412 on a transaction are a StateConflict (Table applied none of
// it) and the caller's own cancellation stays a cancellation. A 404 other than ResourceNotFound, such as a missing
// table, is unavailable, never proof that a record is absent.
internal sealed class TableBrokerState(TableClient table) : IBrokerState
{
    public async Task<Versioned<T>?> ReadAsync<T>(StateKey<T> key, CancellationToken ct) where T : BrokerRecord, new()
    {
        TableEntity? row;
        try { row = await BrokerTable.ReadAsync(table, key.Scope, key.Name, ct); }
        catch (Exception ex) when (FromTable(ex, ct)) { throw new BrokerStateUnavailable("The broker table did not answer a read.", ex); }
        return row is null ? null : new(BrokerRecord.Read<T>(Properties(row)), new(row.ETag.ToString()));
    }

    public async Task<IReadOnlyList<StateVersion?>> CommitAsync(StateTransaction transaction, CancellationToken ct)
    {
        try
        {
            var response = await BrokerTable.SubmitAsync(table, [.. transaction.Writes.Select(Action)], ct);
            return [.. transaction.Writes.Select((write, i) => write.Kind == StateWriteKind.Delete ? null
                : response.Value[i].Headers.ETag is { } etag ? new StateVersion(etag.ToString())
                : throw new InvalidDataException("Missing durable version."))];
        }
        catch (TableTransactionFailedException ex) when (ex.Status is 409 or 412) { throw new StateConflict(); }
        catch (Exception ex) when (FromTable(ex, ct)) { throw new BrokerStateUnavailable("The broker table did not confirm a transaction.", ex); }
    }

    // The Table query cleanup has always made: the range and `after` as a RowKey filter in one partition, at most 32 rows
    // a page, resuming from the stored continuation. A page that breaks those bounds proves nothing and is unavailable.
    public async Task<StatePage<T>> ListAsync<T>(StateRange<T> range, string after, string? continuation, CancellationToken ct)
        where T : BrokerRecord, new()
    {
        Page<TableEntity>? page;
        try
        {
            var filter = TableClient.CreateQueryFilter(
                $"PartitionKey eq {range.Scope} and RowKey ge {range.From} and RowKey lt {range.To} and RowKey gt {after}");
            await using var pages = table.QueryAsync<TableEntity>(filter, maxPerPage: 32, cancellationToken: ct)
                .AsPages(continuation, pageSizeHint: 32).GetAsyncEnumerator(ct);
            page = await pages.MoveNextAsync() ? pages.Current : null;
            if (page is not null && (page.Values.Count > 32 || page.ContinuationToken is { Length: > 8192 }))
                throw new InvalidDataException("Invalid durable page.");
            foreach (var row in page?.Values ?? [])
            {
                BrokerTable.Validate(row);
                if (row.ETag == ETag.All || string.IsNullOrEmpty(row.ETag.ToString()) || row.PartitionKey != range.Scope
                    || string.CompareOrdinal(row.RowKey, range.From) < 0 || string.CompareOrdinal(row.RowKey, range.To) >= 0
                    || string.CompareOrdinal(row.RowKey, after) <= 0) throw new InvalidDataException("Invalid durable page.");
            }
        }
        catch (Exception ex) when (FromTable(ex, ct)) { throw new BrokerStateUnavailable("The broker table did not answer a listing.", ex); }
        return page is null ? new([], null) : new([.. page.Values.Select(row =>
            new ListedRecord<T>(new(row.PartitionKey, row.RowKey), Properties(row), new(row.ETag.ToString())))], page.ContinuationToken);
    }

    private static TableTransactionAction Action(StateWrite write)
    {
        var entity = new TableEntity(write.Key.Scope, write.Key.Name);
        foreach (var (name, value) in write.Row ?? new Dictionary<string, object>()) entity[name] = value;
        return write.Kind == StateWriteKind.Add ? new(TableTransactionActionType.Add, entity)
            : new(write.Kind == StateWriteKind.Replace ? TableTransactionActionType.UpdateReplace : TableTransactionActionType.Delete,
                entity, new ETag(write.Version!.Token));
    }

    // A row the adapter's Table code read, as the record it stores and the version it was read at. For the cleanup paths
    // (reclamation) that still read TableEntity rows until their slice moves them to the core.
    internal static Versioned<T> Versioned<T>(TableEntity row) where T : BrokerRecord, new() =>
        new(BrokerRecord.Read<T>(Properties(row)), new(row.ETag.ToString()));

    // The stored properties, without Table's keys and system properties.
    private static Dictionary<string, object> Properties(TableEntity row) => row
        .Where(property => property.Key is not ("PartitionKey" or "RowKey" or "Timestamp" or "odata.etag"))
        .ToDictionary(property => property.Key, property => property.Value, StringComparer.Ordinal);

    private static bool FromTable(Exception ex, CancellationToken ct) => ex is not OperationCanceledException || !ct.IsCancellationRequested;
}
