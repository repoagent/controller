using RepoAgentController;

// Drives the real manager through its public lifecycle; external HTTPS replies
// are supplied by the process fixture. Never publishes or prints credentials.
using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
var clock = new Clock();
using var credentials = GitHubAppCredentials.FromBroker(http, "https://broker.fixture", args[0],
    "123", "owner/project", args[1], new RuntimeProfile(ManagedClone: true), clock);
await credentials.Initialize(null, CancellationToken.None);
Console.WriteLine("initialized");
while (Console.ReadLine() is { } command)
{
    if (command == "stop") break;
    if (command is not ("renew" or "deny")) throw new InvalidOperationException("Unknown test lifecycle command");
    clock.Advance();
    try
    {
        await credentials.GetToken(CancellationToken.None);
        Console.WriteLine("renewed");
    }
    catch (InvalidOperationException) { Console.WriteLine("denied"); }
}
sealed class Clock : TimeProvider
{
    DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance() => now += TimeSpan.FromMinutes(56);
}
