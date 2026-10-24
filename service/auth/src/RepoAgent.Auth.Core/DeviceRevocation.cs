using System.Globalization;

namespace RepoAgent.Auth;

// A controller revokes its own controller enrollment with its controller secret. One transaction closes the repository
// gate, revokes the enrollment and adds a revocation receipt; a retry after a lost response finds the receipt and
// succeeds without writing again. Every other field is kept, including outstanding GitHub token counts, and issuance
// attempts are never touched. Returns the HTTP status: 204 revoked, 401 not this enrollment's authority, 503 unknown.
public sealed class DeviceRevocation(IBrokerState state, string epoch, TimeProvider clock)
{
    public async Task<int> RevokeAsync(string deviceId, string? secret, CancellationToken cancellationToken, int runtimeVersion = 1)
    {
        var hash = DeviceSecret.Hash(secret);
        if (!DeviceSecret.IsDeviceId(deviceId) || hash is null) return 401;
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A locator only selects the partition. The records there must independently bind this enrollment.
                var locator = await state.ReadAsync(EnrollmentLocator.Key(deviceId), cancellationToken);
                if (locator is null) return 401;
                var repository = Required(locator.Value.RepositoryId);
                if (Required(locator.Value.EnrollmentId) != deviceId) return 401;
                if (!ulong.TryParse(repository, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId)
                    || numericId == 0 || numericId.ToString(CultureInfo.InvariantCulture) != repository)
                    throw new InvalidDataException("Invalid repository identifier.");
                var gateKey = RepositoryGate.Key(repository);
                var enrollmentKey = ControllerEnrollment.Key(repository, deviceId);
                var gateRead = await state.ReadAsync(gateKey, cancellationToken);
                var enrollmentRead = await state.ReadAsync(enrollmentKey, cancellationToken);
                if (gateRead is null || enrollmentRead is null) return 401;
                var (gate, enrollment) = (gateRead.Value, enrollmentRead.Value);
                if (Required(gate.RepositoryId) != repository
                    || Required(enrollment.RepositoryId) != repository
                    || Required(enrollment.EnrollmentId) != deviceId
                    || Required(gate.OwnerEnrollmentId) != deviceId
                    || Required(gate.Epoch) != epoch
                    || Required(enrollment.Epoch) != epoch) return 401;
                if (!DeviceSecret.Matches(hash, Required(enrollment.ControllerSecretHash))) return 401;
                if (Required(enrollment.ExpiresAt) <= clock.GetUtcNow()) return 401;
                // Historical rows may predate profile and approval fields; only runtime v1 can use them.
                if (enrollment.ApprovalKind is not null || enrollment.SetupVersion is not null
                    || enrollment.PolicyVersion is not null || enrollment.Profile is not null)
                {
                    var approval = ApprovalContract.Stored(enrollment.ApprovalKind, enrollment.SetupVersion, enrollment.PolicyVersion,
                        enrollment.Environment is not null || enrollment.EnvironmentId is not null);
                    var profile = enrollment.Profile is { Length: > 0 and <= 8192 } stored ? stored : throw new IssuanceDenied();
                    if (approval.RuntimeVersion != runtimeVersion || !approval.Allows(profile)) return 401;
                }
                else if (runtimeVersion != 1) return 401;
                var sequence = gate.Sequence is >= 0 and var value ? value : throw new InvalidDataException("Missing or invalid gate sequence.");
                var gateState = Required(gate.State);
                var enrollmentState = Required(enrollment.State);
                var receiptKey = RevocationReceipt.Key(repository, deviceId);
                var receipt = await state.ReadAsync(receiptKey, cancellationToken);
                if (Required(enrollment.ExpiresAt) <= clock.GetUtcNow()) return 401;
                if (enrollmentState == "revoked" && gateState == "closed")
                {
                    if (receipt is null) continue;
                    if (Required(receipt.Value.RepositoryId) != repository
                        || Required(receipt.Value.EnrollmentId) != deviceId
                        || Required(receipt.Value.Epoch) != epoch)
                        return 503;
                    _ = Required(receipt.Value.RevokedAt);
                    return 204;
                }
                if (enrollmentState is not ("active" or "unacknowledged" or "revoked")
                    || gateState is not ("active" or "unacknowledged" or "closed")) return 503;
                // Point reads may straddle another successful revocation. Reconcile from a fresh snapshot.
                if (enrollmentState != gateState || enrollmentState is not ("active" or "unacknowledged") || receipt is not null) continue;

                var now = clock.GetUtcNow();
                gate.State = "closed";
                gate.Sequence = checked(sequence + 1);
                enrollment.State = "revoked";
                enrollment.FirstTerminalAt ??= now;
                var revoked = new RevocationReceipt { RepositoryId = repository, EnrollmentId = deviceId, Epoch = epoch, RevokedAt = now };
                try
                {
                    await state.CommitAsync(new StateTransaction()
                        .Replace(gateKey, gate, gateRead.Version)
                        .Replace(enrollmentKey, enrollment, enrollmentRead.Version)
                        .Add(receiptKey, revoked), cancellationToken);
                    return 204;
                }
                catch (StateConflict)
                {
                    // Reevaluate the entire authorization decision; never reuse records read before the conflict.
                }
            }
            return 503;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is BrokerStateUnavailable or BrokerRecordUnreadable or IssuanceDenied
            or InvalidDataException or OverflowException or OperationCanceledException)
        {
            // An unknown commit outcome is never compensated: a retry finds the receipt or reevaluates.
            return 503;
        }
    }

    private static string Required(string? value) =>
        value is { Length: > 0 } text ? text : throw new InvalidDataException("Missing or invalid durable field.");

    private static DateTimeOffset Required(DateTimeOffset? value) =>
        value ?? throw new InvalidDataException("Missing or invalid durable date.");
}
