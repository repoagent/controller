using static RepoAgent.Auth.IssuanceValues;

namespace RepoAgent.Auth;

public interface ICleanupBudget
{
    int Dispatches { get; }
    int BatchDispatches { get; }
}

// One paginated pass over due repository records. No token ledger, separate lanes, cursor writes
// or per-record verification reads. ETag conflicts are left to the next scheduled invocation.
public sealed class BrokerCleanup(IBrokerState state, string deploymentEpoch, ICleanupBudget budget, TimeProvider clock,
    Action<Exception>? observeFailure = null)
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);
    private int examined, changed, failures, conflicts, pages;
    private bool incomplete;
    private readonly long started = clock.GetTimestamp();

    public async Task<int> RunAsync(CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(Deadline, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var token = linked.Token;
        try
        {
            _ = await new CurrentRepositoryStore(state, deploymentEpoch, clock).QuotaAsync(token);
            var due = clock.GetUtcNow();
            string? continuation = null;
            do
            {
                token.ThrowIfCancellationRequested();
                var page = await state.ListDueAsync(due, continuation, token);
                pages++;
                foreach (var listed in page.Records)
                {
                    token.ThrowIfCancellationRequested();
                    examined++;
                    try
                    {
                        var read = listed.Read();
                        var row = read.Value;
                        var key = CurrentRepository.Key(Number(row.RepositoryId));
                        if (listed.Key.Scope != key.Scope || listed.Key.Name != key.Name) throw new IssuanceDenied();
                        row.Validate(Number(row.RepositoryId), IssuanceValues.Text(row.Epoch));
                        if (row.NextCleanupAt is null || row.NextCleanupAt > due || row.State == "closed") continue;
                        row.Close();
                        await state.CommitAsync(new StateTransaction().Replace(key, row, read.Version), token);
                        changed++;
                    }
                    catch (StateConflict) { conflicts++; }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) when (IssuancePolicy.Answerable(ex)) { Failure(ex); }
                }
                continuation = page.Continuation;
            } while (continuation is not null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { incomplete = true; }
        catch (Exception ex) when (IssuancePolicy.Answerable(ex)) { incomplete = true; Failure(ex); }
        return failures == 0 && !incomplete ? 0 : 1;
    }
    private void Failure(Exception exception)
    {
        failures++;
        try { observeFailure?.Invoke(exception); } catch (Exception) { }
    }
    public object Summary() => new
    {
        examinedRecords = examined,
        changedRecords = changed,
        failures,
        conflicts,
        queryPages = pages,
        incompleteCoverage = incomplete,
        sdkTableDispatches = budget.Dispatches,
        sdkBatchDispatches = budget.BatchDispatches,
        elapsedMilliseconds = (long)clock.GetElapsedTime(started).TotalMilliseconds
    };
}
