namespace RepoAgent.Auth;

// A deliberately narrow diagnostic DTO. Fields are populated only by the fixed projections below,
// never by serializing a storage record, configuration, key, version or exception.
public sealed record AuthorityRowObservation(string Read, bool Complete = false, string? EpochRelation = null,
    IReadOnlyDictionary<string, object?>? Fields = null);

public sealed record RetainedAuthorityReport(DateTimeOffset ObservedAt, int ReadCount, string State, string GateObservation,
    AuthorityRowObservation Gate, AuthorityRowObservation Owner, AuthorityRowObservation Receipt,
    AuthorityRowObservation RuntimeAttempt, AuthorityRowObservation VerifierAttempt)
{
    public string Schema { get; } = "repoagent.authority-observation/v1";
    public string Scope { get; } = "fixed-repository";
    public string Coverage { get; } = "referenced-rows-only; gate reread is not a transaction snapshot or accounting reconciliation";
    public bool SettlementProven { get; }
    public bool AllOutstandingAttemptsExamined { get; }
}

public sealed class RetainedAuthorityObservation(IBrokerState state, string configuredEpoch, TimeProvider clock)
{
    private const string Repository = "1432194081";

    public async Task<RetainedAuthorityReport> ObserveAsync(CancellationToken ct)
    {
        if (!BrokerOptions.IsDeploymentEpoch(configuredEpoch)) throw new InvalidOperationException("Invalid configured epoch.");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20), clock);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        var token = stop.Token;
        var now = clock.GetUtcNow().ToUniversalTime();
        var reads = 0;
        var stopped = false;
        async Task<(string Read, Versioned<T>? Record)> Read<T>(StateKey<T> key) where T : BrokerRecord, new()
        {
            if (stopped || token.IsCancellationRequested || reads == 6) return ("unavailable", null);
            reads++;
            try
            {
                var value = await state.ReadAsync(key, token).WaitAsync(token);
                return (value is null ? "missing" : "present", value);
            }
            catch (BrokerStateUnavailable) { return ("unavailable", null); }
            catch (BrokerRecordUnreadable) { return ("unreadable", null); }
            catch (OperationCanceledException) { stopped = true; return ("unavailable", null); }
        }
        var gateRead = await Read(RepositoryGate.Key(Repository));
        var gate = new AuthorityRowObservation(gateRead.Read);
        AuthorityRowObservation owner = new("unavailable"), receipt = new("unavailable"), runtime = new("unavailable"), verifier = new("unavailable");
        var gateObservation = "not-checked";
        if (gateRead.Record is { Value: { RepositoryId: Repository } } first)
        {
            var value = first.Value;
            gate = Gate(value, configuredEpoch);
            var ownerReference = EnrollmentReference(value.OwnerEnrollmentId);
            string? runtimeReference = null;
            if (ownerReference == "absent")
                owner = receipt = runtime = new("not-referenced", true);
            else if (ownerReference == "invalid")
                owner = receipt = new("invalid-reference");
            else
            {
                var read = await Read(ControllerEnrollment.Key(Repository, value.OwnerEnrollmentId!));
                owner = new(read.Read);
                ControllerEnrollment? boundOwner = null;
                if (read.Record is { } found)
                {
                    if (found.Value.RepositoryId != Repository || found.Value.EnrollmentId != value.OwnerEnrollmentId)
                        owner = new("binding-mismatch");
                    else
                    {
                        boundOwner = found.Value;
                        owner = Owner(boundOwner, value.Epoch, now);
                    }
                }
                var receiptRead = await Read(RevocationReceipt.Key(Repository, value.OwnerEnrollmentId!));
                receipt = receiptRead.Record is not { } received ? new(receiptRead.Read)
                    : received.Value.RepositoryId != Repository || received.Value.EnrollmentId != value.OwnerEnrollmentId
                        ? new("binding-mismatch") : Receipt(received.Value, value.Epoch, now);
                if (boundOwner is not null)
                {
                    runtimeReference = boundOwner.RuntimeAttempt;
                    runtime = OperationReference(runtimeReference) switch
                    {
                        "absent" => new("not-referenced", true),
                        "invalid" => new("invalid-reference"),
                        _ => await ReadAttempt(runtimeReference!, "runtime", value.OwnerEnrollmentId, value.Epoch)
                    };
                }
            }
            var verifierReference = OperationReference(value.VerifierAttempt);
            if (verifierReference == "absent") verifier = new("not-referenced", true);
            else if (verifierReference == "invalid") verifier = new("invalid-reference");
            else if (value.VerifierAttempt == runtimeReference)
                runtime = verifier = new(runtime.Read is "missing" or "unavailable" or "unreadable" ? runtime.Read : "binding-mismatch",
                    Fields: new Dictionary<string, object?>(StringComparer.Ordinal)
                    { ["referenceBinding"] = "purpose-ambiguous" }); // One exact row cannot prove both purposes; never read it twice.
            else verifier = await ReadAttempt(value.VerifierAttempt!, "verification",
                ownerReference == "valid" ? value.OwnerEnrollmentId : null, value.Epoch);

            var again = await Read(RepositoryGate.Key(Repository));
            gateObservation = again.Read == "missing" ? "missing-on-reread"
                : again.Record is not { } last ? "unavailable"
                : first.Version == last.Version && SameGate(first.Value, last.Value) ? "unchanged" : "changed";

            async Task<AuthorityRowObservation> ReadAttempt(string operation, string purpose, string? expectedOwner, string? epoch)
            {
                var read = await Read(IssuanceAttempt.Key(Repository, operation));
                if (read.Record is not { } found) return new(read.Read);
                var attempt = found.Value;
                if (attempt.RepositoryId != Repository || attempt.OperationId != operation || attempt.Purpose != purpose
                    || EnrollmentReference(attempt.EnrollmentId) != "valid" || expectedOwner is not null && attempt.EnrollmentId != expectedOwner)
                    return new("binding-mismatch");
                return Attempt(attempt, epoch, expectedOwner is not null, now);
            }
        }
        else if (gateRead.Record is not null) gate = new("binding-mismatch");
        var status = gate.Read != "present" ? "unavailable"
            : new[] { gate, owner, receipt, runtime, verifier }.All(row => row.Complete) && gateObservation == "unchanged" ? "observed" : "partial";
        return new(now, reads, status, gateObservation, gate, owner, receipt, runtime, verifier);
    }

    private static bool SameGate(RepositoryGate first, RepositoryGate last) => first.RepositoryId == last.RepositoryId
        && first.Epoch == last.Epoch && first.State == last.State && first.OwnerEnrollmentId == last.OwnerEnrollmentId
        && first.VerifierAttempt == last.VerifierAttempt && first.Sequence == last.Sequence
        && first.OutstandingTokenCount == last.OutstandingTokenCount && first.PendingCount == last.PendingCount;

    private static string EnrollmentReference(string? value) => value is null ? "absent" : ControllerSecret.IsEnrollmentId(value) ? "valid" : "invalid";
    private static string OperationReference(string? value) => value is null ? "absent" : Guid.TryParseExact(value, "N", out _) ? "valid" : "invalid";
    private static string Relation(string? value, string? baseline) => value is null || baseline is null ? "missing"
        : !BrokerOptions.IsDeploymentEpoch(value) || !BrokerOptions.IsDeploymentEpoch(baseline) ? "invalid"
        : value == baseline ? "match" : "mismatch";
    private static string Clock(DateTimeOffset? value, DateTimeOffset now) => value is null ? "absent"
        : value <= now ? "at-or-before-observed-time" : "after-observed-time";
    private static string Known(string? value, ref bool complete, params string[] allowed)
    {
        if (value is not null && allowed.Contains(value, StringComparer.Ordinal)) return value;
        complete = false;
        return "unknown";
    }
    private static string Optional(string? value, ref bool complete, params string[] allowed) => value is null ? "absent" : Known(value, ref complete, allowed);
    private static long? Count(long? value, ref bool complete)
    {
        if (value is >= 0) return value;
        complete = false;
        return null;
    }

    private static AuthorityRowObservation Gate(RepositoryGate value, string epoch)
    {
        var relation = Relation(value.Epoch, epoch);
        var complete = relation == "match" && EnrollmentReference(value.OwnerEnrollmentId) != "invalid" && OperationReference(value.VerifierAttempt) != "invalid";
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["observedState"] = Known(value.State, ref complete, "vacant", "active", "unacknowledged", "closed"),
            ["ownerReference"] = EnrollmentReference(value.OwnerEnrollmentId),
            ["verifierReference"] = OperationReference(value.VerifierAttempt),
            ["sequence"] = Count(value.Sequence, ref complete),
            ["outstandingTokenCount"] = Count(value.OutstandingTokenCount, ref complete),
            ["pendingCount"] = Count(value.PendingCount, ref complete)
        };
        return new("present", complete, relation, fields);
    }

    private static AuthorityRowObservation Owner(ControllerEnrollment value, string? epoch, DateTimeOffset now)
    {
        var relation = Relation(value.Epoch, epoch);
        var complete = relation == "match" && value.Acknowledged.HasValue && value.PayloadRedacted.HasValue
            && OperationReference(value.RuntimeAttempt) != "invalid";
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["observedState"] = Known(value.State, ref complete, "unacknowledged", "active", "revoked", "redacted"),
            ["acknowledged"] = value.Acknowledged,
            ["payloadRedacted"] = value.PayloadRedacted,
            ["outstandingTokenCount"] = Count(value.OutstandingTokenCount, ref complete),
            ["runtimeReference"] = OperationReference(value.RuntimeAttempt),
            ["observationReferencePresent"] = value.RuntimeObservation is not null,
            ["expiresAt"] = Clock(value.ExpiresAt, now),
            ["lastActivityAt"] = Clock(value.LastActivityAt, now),
            ["firstTerminalAt"] = Clock(value.FirstTerminalAt, now),
            ["runtimeExpiresAt"] = Clock(value.RuntimeExpiresAt, now),
            ["runtimeObservationReservedAt"] = Clock(value.RuntimeObservationReservedAt, now),
            ["runtimeObservationJwtIssuedAt"] = Clock(value.RuntimeObservationJwtIssuedAt, now),
            ["runtimeObservationJwtExpiresAt"] = Clock(value.RuntimeObservationJwtExpiresAt, now)
        };
        return new("present", complete, relation, fields);
    }

    private static AuthorityRowObservation Receipt(RevocationReceipt value, string? epoch, DateTimeOffset now)
    {
        var relation = Relation(value.Epoch, epoch);
        var complete = relation == "match";
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["reason"] = Optional(value.Reason, ref complete, "revoked-acknowledged", "expired-unacknowledged"),
            ["revokedAt"] = Clock(value.RevokedAt, now),
            ["ownerReleasedAt"] = Clock(value.OwnerReleasedAt, now)
        };
        return new("present", complete, relation, fields);
    }

    private static AuthorityRowObservation Attempt(IssuanceAttempt value, string? epoch, bool ownerChecked, DateTimeOffset now)
    {
        var relation = Relation(value.Epoch, epoch);
        var complete = relation == "match" && ownerChecked && value.RepositoryCharged.HasValue && value.ScopeValid.HasValue
            && value.CapacityReleased.HasValue && value.PayloadRedacted.HasValue;
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["observedState"] = Known(value.State, ref complete, "reserved", "sending", "known", "released", "settled", "redacted"),
            ["purpose"] = Known(value.Purpose, ref complete, "runtime", "verification"),
            ["ownerBinding"] = ownerChecked ? "match" : "not-checkable",
            ["repositoryCharged"] = value.RepositoryCharged,
            ["scopeValid"] = value.ScopeValid,
            ["capacityReleased"] = value.CapacityReleased,
            ["payloadRedacted"] = value.PayloadRedacted,
            ["settlementReason"] = Optional(value.SettlementReason, ref complete, "expired", "revoked", "unsent", "unpublished"),
            ["reservedAt"] = Clock(value.ReservedAt, now),
            ["payloadPurgeAt"] = Clock(value.PayloadPurgeAt, now),
            ["jwtIssuedAt"] = Clock(value.JwtIssuedAt, now),
            ["jwtExpiresAt"] = Clock(value.JwtExpiresAt, now),
            ["sendClaimedAt"] = Clock(value.SendClaimedAt, now),
            ["expiresAt"] = Clock(value.ExpiresAt, now),
            ["releasedAt"] = Clock(value.ReleasedAt, now),
            ["settledAt"] = Clock(value.SettledAt, now),
            ["firstTerminalAt"] = Clock(value.FirstTerminalAt, now)
        };
        return new("present", complete, relation, fields);
    }
}
