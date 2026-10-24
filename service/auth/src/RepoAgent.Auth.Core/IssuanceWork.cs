namespace RepoAgent.Auth;

// Shared work/deadline is owned by one operation; it is never refreshed by retries or caller disconnect.
public sealed class IssuanceWork(TimeProvider clock) : IDisposable
{
    private readonly DateTimeOffset started = clock.GetUtcNow();
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(15), clock);
    private int reads;
    private int admissionEvaluations;
    public CancellationToken Cancellation => lifetime.Token;
    public void Check()
    {
        Cancellation.ThrowIfCancellationRequested();
        if (clock.GetUtcNow() - started >= TimeSpan.FromSeconds(15)) throw new IssuanceDenied();
    }
    public void Read() { Check(); if (++reads > 8) throw new IssuanceDenied(); }
    public void EvaluateAdmission() { Check(); if (++admissionEvaluations > 4) throw new IssuanceDenied(); }
    public void Fresh(DateTimeOffset readStart)
    {
        Check();
        if (clock.GetUtcNow() - readStart > TimeSpan.FromSeconds(10) || readStart > clock.GetUtcNow()) throw new IssuanceDenied();
    }
    public void Dispose() => lifetime.Dispose();
}
