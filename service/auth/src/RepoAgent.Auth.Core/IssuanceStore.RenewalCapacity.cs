using static RepoAgent.Auth.IssuancePolicy;
using static RepoAgent.Auth.IssuanceValues;

namespace RepoAgent.Auth;

public sealed partial class IssuanceStore
{
    // Pressure relief visits only fixed global slots, never the unbounded retained attempt history. At most eight
    // pages / 128 slots and eight same-repository reclamations share the issuance operation's original deadline.
    // Reclamation retains sole ownership of expiry margins, unknown outcomes, generation/CAS fencing and settlement.
    public async Task<bool> ReclaimForRenewalAsync(GrantSnapshot grant, IssuanceWork work)
    {
        var quota = await capacity.SchemaAsync(work.Cancellation);
        if (Number(grant.Gate.OutstandingTokenCount) < Math.Max(1, limits.RepositoryAttempts - 2)
            && Number(quota.Value.Slots) < limits.GlobalAttempts) return false;
        var range = new StateRange<IssuanceSlot>(IssuanceCapacity.Scope, "slot:", "slot;");
        var selected = new List<IssuanceSlot>();
        var after = "";
        string? continuation = null;
        var seen = 0;
        for (var pageNumber = 0; pageNumber < 8; pageNumber++)
        {
            work.Check();
            var page = await state.ListAsync(range, after, continuation, work.Cancellation);
            if (page.Records.Count > 32) throw new IssuanceDenied();
            foreach (var listed in page.Records)
            {
                if (++seen > GlobalAttempts || listed.Key.Scope != range.Scope
                    || string.CompareOrdinal(listed.Key.Name, after) <= 0) throw new IssuanceDenied();
                var slot = listed.Read().Value;
                if (listed.Key.Name != IssuanceSlot.Key(Number(slot.GlobalSlot)).Name) throw new IssuanceDenied();
                after = listed.Key.Name;
                if (slot.RepositoryId == grant.RepositoryId && slot.Epoch == epoch
                    && Guid.TryParseExact(slot.OperationId, "N", out _) && Guid.TryParseExact(slot.Generation, "N", out _))
                {
                    work.Check();
                    var attempt = await state.ReadAsync(IssuanceAttempt.Key(grant.RepositoryId, Text(slot.OperationId)), work.Cancellation);
                    // Only positively known expired tokens are selected. A prior repository settlement whose global
                    // release response was lost can finish through its exact release proof. Unknown outcomes stay charged.
                    if (attempt is { Value: { ScopeValid: true, ExpiresAt: { } expiry, ReservedAt: { } reserved } known }
                        && (known.State is "known" or "released" || known is { State: "settled", CapacityReleased: true, SettlementReason: "expired" })
                        && expiry > reserved && expiry <= DateTimeOffset.MaxValue - TimeSpan.FromMinutes(5)
                        && clock.GetUtcNow() >= expiry.AddMinutes(5)) selected.Add(slot);
                }
                if (selected.Count == RepositoryAttempts) break;
            }
            continuation = page.Continuation;
            if (selected.Count == RepositoryAttempts || continuation is null) break;
            if (pageNumber == 7) throw new IssuanceDenied();
        }
        var reclamation = new CapacityReclamation(state, epoch, clock);
        foreach (var slot in selected)
        {
            work.Check();
            await reclamation.ReclaimIssuanceAsync(Text(slot.OperationId), Text(slot.Generation), work.Cancellation);
        }
        return true;
    }
}
