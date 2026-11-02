namespace RepoAgent.Auth.Tests;

internal sealed class IssuanceClock : TimeProvider
{
    private DateTimeOffset current = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => current;
    public void Advance(TimeSpan amount) => current += amount;
}
