using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class BrokerRoutesTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Missing_credentials_and_unexpected_bodies_perform_no_work(int version)
    {
        var f = new InMemoryBroker();
        var missing = InMemoryBroker.Request(null);
        var body = InMemoryBroker.Request(InMemoryBroker.SetupSecret, new { unexpected = true });
        const string id = "123456_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        Assert.Equal(401, (await BrokerRoutes.IssueAsync(missing, id, () => f.Issuer().IssueAsync, version)).Status);
        Assert.Equal(401, (await BrokerRoutes.RevokeAsync(missing, id, new(f.State, InMemoryBroker.Epoch, f.Clock), version)).Status);
        Assert.Equal(400, (await BrokerRoutes.IssueAsync(body, id, () => f.Issuer().IssueAsync, version)).Status);
        Assert.Equal(400, (await BrokerRoutes.RevokeAsync(body, id, new(f.State, InMemoryBroker.Epoch, f.Clock), version)).Status);
        Assert.Equal(0, f.State.Reads + f.State.Commits);
        Assert.Equal(0, f.GitHub.Mints);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Setup_rejects_unknown_or_duplicate_body_members_before_storage(int version)
    {
        var f = new InMemoryBroker();
        const string json = "{\"repositoryId\":123456,\"RepositoryId\":123456}";
        var request = new BrokerRequest([new(50, "Bearer", InMemoryBroker.SetupSecret)], true,
            InMemoryBroker.Body(System.Text.Encoding.UTF8.GetBytes(json)), Token);
        Assert.Equal(400, (await BrokerRoutes.StartAsync(request, f.Enrollment, version)).Status);
        Assert.Equal(0, f.State.Reads + f.State.Commits);
    }
    [Fact]
    public async Task Valid_requests_reach_the_composed_fresh_issuer()
    {
        var (f, id, secret) = await CurrentBrokerTests.Active();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var answer = await BrokerRoutes.IssueAsync(InMemoryBroker.Request(secret), id, () => f.Issuer().IssueAsync, 1);
        Assert.Equal(200, answer.Status);
        CurrentBrokerTests.AssertNoTokens(f);
    }
    [Theory]
    [InlineData(null, "1")]
    [InlineData(0.2, "1")]
    [InlineData(1.2, "2")]
    public void Retry_after_rounds_up(double? seconds, string expected) =>
        Assert.Equal(expected, BrokerRoutes.RetryAfter(seconds is null ? null : TimeSpan.FromSeconds(seconds.Value)));
}
