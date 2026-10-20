using System.Globalization;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Identity;

namespace RepoAgent.Auth;

// This module owns one Table-specific authorization transaction, not a generic persistence interface.
public sealed class DeviceRevocation(TableClient table, BrokerOptions options, TimeProvider clock)
{
    public async Task<int> RevokeAsync(string deviceId, string? secret, CancellationToken cancellationToken)
    {
        var hash = DeviceSecret.Hash(secret);
        if (!DeviceSecret.IsDeviceId(deviceId) || hash is null) return 401;
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A locator only selects the partition. The records there must independently bind this device.
                var locator = await ReadAsync("locator:" + deviceId, "locator", cancellationToken);
                if (locator is null) return 401;
                var repository = RequiredString(locator, "RepositoryId");
                if (RequiredString(locator, "DeviceId") != deviceId) return 401;
                if (!ulong.TryParse(repository, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId)
                    || numericId == 0 || numericId.ToString(CultureInfo.InvariantCulture) != repository)
                    throw new InvalidDataException("Invalid repository identifier.");
                var partition = "repository:" + repository;
                var gate = await ReadAsync(partition, "gate", cancellationToken);
                var device = await ReadAsync(partition, "device:" + deviceId, cancellationToken);
                if (gate is null || device is null) return 401;
                if (RequiredString(gate, "RepositoryId") != repository
                    || RequiredString(device, "RepositoryId") != repository
                    || RequiredString(device, "DeviceId") != deviceId
                    || RequiredString(gate, "OwnerDeviceId") != deviceId
                    || RequiredString(gate, "Epoch") != options.DeploymentEpoch
                    || RequiredString(device, "Epoch") != options.DeploymentEpoch) return 401;
                if (!DeviceSecret.Matches(hash, RequiredString(device, "SecretHash"))) return 401;
                if (RequiredDate(device, "ExpiresAt") <= clock.GetUtcNow()) return 401;
                var sequence = RequiredSequence(gate);
                var gateState = RequiredString(gate, "State");
                var deviceState = RequiredString(device, "State");
                var receiptKey = "revocation:" + deviceId;
                var receipt = await ReadAsync(partition, receiptKey, cancellationToken);
                if (RequiredDate(device, "ExpiresAt") <= clock.GetUtcNow()) return 401;
                if (deviceState == "revoked" && gateState == "closed")
                {
                    if (receipt is null || RequiredString(receipt, "RepositoryId") != repository
                        || RequiredString(receipt, "DeviceId") != deviceId
                        || RequiredString(receipt, "Epoch") != options.DeploymentEpoch)
                        return 503;
                    _ = RequiredDate(receipt, "RevokedAt");
                    return 204;
                }
                if (deviceState != "active" || gateState != "active" || receipt is not null) return 503;

                // Retain all other properties, including outstanding-token bounds. Never delete attempts here.
                gate["State"] = "closed";
                gate["Sequence"] = checked(sequence + 1);
                device["State"] = "revoked";
                var newReceipt = new TableEntity(partition, receiptKey)
                {
                    ["RepositoryId"] = repository, ["DeviceId"] = deviceId,
                    ["Epoch"] = options.DeploymentEpoch, ["RevokedAt"] = clock.GetUtcNow()
                };
                try
                {
                    await table.SubmitTransactionAsync([
                        new(TableTransactionActionType.UpdateReplace, gate, gate.ETag),
                        new(TableTransactionActionType.UpdateReplace, device, device.ETag),
                        new(TableTransactionActionType.Add, newReceipt)
                    ], cancellationToken);
                    return 204;
                }
                catch (TableTransactionFailedException exception) when (exception.Status is 409 or 412)
                {
                    // Reevaluate the entire authorization decision; do not refresh ETags on stale decisions.
                }
            }
            return 503;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is RequestFailedException or AuthenticationFailedException
            or HttpRequestException or IOException or InvalidCastException or FormatException
            or JsonException or OverflowException or OperationCanceledException)
        {
            // SDK errors may contain entity values. Neither log nor return their bodies or messages.
            return 503;
        }
    }

    private async Task<TableEntity?> ReadAsync(string partition, string row, CancellationToken cancellationToken)
    {
        try
        {
            var entity = (await table.GetEntityAsync<TableEntity>(partition, row, cancellationToken: cancellationToken)).Value;
            // Each of the three batch entities stays far below Table's entity and transaction payload limits.
            if (entity.Count > 128 || JsonSerializer.SerializeToUtf8Bytes(entity).Length > 64 * 1024
                || string.IsNullOrEmpty(entity.ETag.ToString()) || entity.ETag == ETag.All)
                throw new InvalidDataException("Invalid durable entity.");
            return entity;
        }
        catch (RequestFailedException exception) when (exception.Status == 404 && exception.ErrorCode == "ResourceNotFound")
        {
            return null;
        }
    }

    private static string RequiredString(TableEntity entity, string property) =>
        entity.TryGetValue(property, out var value) && value is string text && text.Length > 0
            ? text : throw new InvalidDataException("Missing or invalid durable field.");

    private static DateTimeOffset RequiredDate(TableEntity entity, string property) =>
        entity.TryGetValue(property, out var value) && value is DateTimeOffset date
            ? date : throw new InvalidDataException("Missing or invalid durable date.");

    private static long RequiredSequence(TableEntity entity) =>
        entity.TryGetValue("Sequence", out var value) && value is long sequence && sequence >= 0
            ? sequence : throw new InvalidDataException("Missing or invalid gate sequence.");
}
