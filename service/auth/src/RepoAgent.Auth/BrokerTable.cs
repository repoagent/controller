using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace RepoAgent.Auth;

// Shared bounds apply before every broker write, including revocation and locator publication.
internal static class BrokerTable
{
    internal static int Validate(ITableEntity entity)
    {
        if (entity is not TableEntity row || row.Count > 128
            || row.Any(p => p.Value is double number && !double.IsFinite(number))) throw new InvalidDataException("Invalid durable entity.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(row).Length;
        if (bytes > 64 * 1024) throw new InvalidDataException("Durable entity exceeds its size budget.");
        return bytes;
    }

    internal static async Task<TableEntity?> ReadAsync(TableClient table, string partition, string key, CancellationToken ct)
    {
        try
        {
            var row = (await table.GetEntityAsync<TableEntity>(partition, key, cancellationToken: ct)).Value;
            Validate(row);
            if (row.ETag == ETag.All || string.IsNullOrEmpty(row.ETag.ToString())) throw new InvalidDataException("Missing durable version.");
            return row;
        }
        catch (RequestFailedException ex) when (ex.Status == 404 && ex.ErrorCode == "ResourceNotFound") { return null; }
    }

    internal static Task<Response> AddAsync(TableClient table, TableEntity row, CancellationToken ct)
    {
        Validate(row);
        return table.AddEntityAsync(row, ct);
    }

    internal static Task<Response<IReadOnlyList<Response>>> SubmitAsync(TableClient table,
        IReadOnlyList<TableTransactionAction> actions, CancellationToken ct)
    {
        if (actions.Count is < 1 or > 32) throw new InvalidDataException("Invalid transaction size.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var partition = actions[0].Entity.PartitionKey;
        var bytes = 0;
        foreach (var action in actions)
        {
            // OData may repeat property names in type annotations; allow a second copy
            // of the JSON plus per-entity protocol and multipart framing.
            bytes = checked(bytes + 2 * Validate(action.Entity) + 8192);
            if (action.Entity.PartitionKey != partition || !keys.Add(action.Entity.RowKey)
                || action.ActionType is not (TableTransactionActionType.Add or TableTransactionActionType.UpdateReplace or TableTransactionActionType.Delete)
                || action.ActionType != TableTransactionActionType.Add
                    && (action.ETag == ETag.All || string.IsNullOrEmpty(action.ETag.ToString())))
                throw new InvalidDataException("Invalid conditional transaction.");
        }
        if (bytes > 1024 * 1024) throw new InvalidDataException("Transaction exceeds its size budget.");
        return table.SubmitTransactionAsync(actions, ct);
    }
}
