namespace RepoAgent.Auth.Tests;

internal sealed class IssuanceClock : TimeProvider
{
    private DateTimeOffset current = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
    public TimeSpan ReadStep { get; set; }
    public override DateTimeOffset GetUtcNow()
    {
        var now = current;
        current += ReadStep;
        return now;
    }
    public void Advance(TimeSpan amount) => current += amount;
}
