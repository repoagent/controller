using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class StandardReadingTests
{
    public static TheoryData<string> Profiles
    {
        get
        {
            var profiles = new TheoryData<string>();
            foreach (var mode in new[] { "polling", "webhook" })
                foreach (var clone in new[] { "", "-clone" })
                    foreach (var settings in new[] { "", "-settings" })
                        foreach (var images in new[] { "", "-images" }) profiles.Add(mode + clone + settings + images);
            return profiles;
        }
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public async Task New_approvals_add_reading_without_changing_profiles_or_runtime_protocols(string profile)
    {
        var (broker, id, secret) = await CurrentBrokerTests.Active(5, profile, GrantWrite);
        var contract = ApprovalContract.ForSetup(5, profile);
        var frozen = contract.IsImages ? ApprovalContract.AgentImagesV5 : contract.IsSettings ? ApprovalContract.AgentSettingsV5
            : contract.IsClone ? ApprovalContract.AgentCloneV5 : ApprovalContract.AgentV5;
        Assert.Equal(frozen.RuntimeVersion, contract.RuntimeVersion);
        broker.Clock.Advance(TimeSpan.FromMinutes(1));
        var result = await broker.Issuer().IssueAsync(id, secret, TestContext.Current.CancellationToken, contract.RuntimeVersion);
        Assert.Equal(200, result.Status);
        var permissions = Body(result).GetProperty("permissions");
        Assert.Equal("read", permissions.GetProperty("issues").GetString());
        Assert.Equal("read", permissions.GetProperty("pull_requests").GetString());
        var original = RuntimeAuthority.For(frozen, profile).Permissions;
        Assert.All(original, p => Assert.Equal(p.Value, permissions.GetProperty(p.Key).GetString()));
        Assert.Equal(original.Count + 2, permissions.EnumerateObject().Count());
        Assert.False(RuntimeAuthority.Verifier(contract).Permissions.ContainsKey("issues"));
        Assert.False(RuntimeAuthority.Verifier(contract).Permissions.ContainsKey("pull_requests"));
        // Simulate the same installation's already-active historical setup-v5 record.
        var historical = broker.Row;
        historical.PolicyVersion = frozen.Policy;
        historical.Marker = historical.CreateMarker();
        broker.State.Seed(CurrentRepository.Key(123456), historical);
        broker.GitHub.Marker = historical.Marker;
        broker.Clock.Advance(TimeSpan.FromMinutes(1));
        var renewed = await broker.Issuer().IssueAsync(id, secret, TestContext.Current.CancellationToken, contract.RuntimeVersion);
        Assert.Equal(200, renewed.Status);
        Assert.False(Body(renewed).GetProperty("permissions").TryGetProperty("issues", out _));
        Assert.False(Body(renewed).GetProperty("permissions").TryGetProperty("pull_requests", out _));
        CurrentBrokerTests.AssertNoTokens(broker);
    }

    [Fact]
    public async Task Retrying_a_historical_pending_request_preserves_its_original_policy()
    {
        var broker = new InMemoryBroker();
        var request = CurrentBrokerTests.Request();
        Assert.Equal(200, (await broker.Enrollment().StartRepositoryVariableAsync(request, InMemoryBroker.SetupSecret, TestContext.Current.CancellationToken, 5)).Status);
        var historical = broker.Row;
        historical.PolicyVersion = ApprovalContract.AgentV5.Policy;
        historical.Marker = historical.CreateMarker();
        broker.State.Seed(CurrentRepository.Key(123456), historical);
        var retried = await broker.Enrollment().StartRepositoryVariableAsync(request, InMemoryBroker.SetupSecret, TestContext.Current.CancellationToken, 5);
        Assert.Equal(200, retried.Status);
        Assert.Equal(historical.Marker, Body(retried).GetProperty("marker").GetString());
        Assert.Equal(ApprovalContract.AgentV5, broker.Row.Approval);
        Assert.False(RuntimeAuthority.For(broker.Row.Approval, "polling").Permissions.ContainsKey("issues"));
    }

    [Fact]
    public async Task Closed_historical_enrollment_can_be_replaced_immediately_and_never_renewed_again()
    {
        var (broker, oldId, oldSecret) = await CurrentBrokerTests.Active(4, "polling-clone");
        broker.Clock.Advance(TimeSpan.FromMinutes(1));
        var old = await broker.Issuer().IssueAsync(oldId, oldSecret, TestContext.Current.CancellationToken, 2);
        Assert.Equal(200, old.Status);
        Assert.False(Body(old).GetProperty("permissions").TryGetProperty("issues", out _));
        var request = CurrentBrokerTests.Request() with { Profile = "polling-clone" };
        Assert.Equal(409, (await broker.Enrollment().StartRepositoryVariableAsync(request, InMemoryBroker.SetupSecret, TestContext.Current.CancellationToken, 5)).Status);
        // Reset the issuance minute budget while the old GitHub token is still valid.
        broker.Clock.Advance(TimeSpan.FromMinutes(1));
        var revoke = new ControllerSelfRevocation(broker.State, broker.DeploymentEpoch, broker.Clock);
        Assert.Equal(204, await revoke.RevokeAsync(oldId, oldSecret, TestContext.Current.CancellationToken, 2));
        var started = await broker.Enrollment().StartRepositoryVariableAsync(request, InMemoryBroker.SetupSecret, TestContext.Current.CancellationToken, 5);
        Assert.Equal(200, started.Status);
        broker.GitHub.Marker = Body(started).GetProperty("marker").GetString()!;
        var pending = Body(started).GetProperty("pendingId").GetString()!;
        broker.Clock.Advance(TimeSpan.FromMinutes(1));
        var completed = await broker.Enrollment().CompleteAsync(pending, InMemoryBroker.SetupSecret, TestContext.Current.CancellationToken, 5);
        Assert.Equal(200, completed.Status);
        var enrollment = Body(completed).GetProperty("enrollment");
        var id = enrollment.GetProperty("enrollmentId").GetString()!;
        var secret = enrollment.GetProperty("controllerSecret").GetString()!;
        Assert.NotEqual(oldId, id);
        Assert.Equal(200, (await broker.Enrollment().AcknowledgeAsync(id, Body(completed).GetProperty("completionId").GetString()!, secret, TestContext.Current.CancellationToken, 5)).Status);
        Assert.Equal(401, (await broker.Issuer().IssueAsync(oldId, oldSecret, TestContext.Current.CancellationToken, 2)).Status);
        broker.Clock.Advance(TimeSpan.FromMinutes(1));
        var current = await broker.Issuer().IssueAsync(id, secret, TestContext.Current.CancellationToken, 2);
        Assert.Equal(200, current.Status);
        Assert.Equal("read", Body(current).GetProperty("permissions").GetProperty("issues").GetString());
    }

    private static JsonElement Body(IssuanceResult result) => JsonSerializer.SerializeToElement(result.Body, JsonSerializerOptions.Web);
    private static void GrantWrite(InMemoryBroker broker) => broker.GitHub.Installation = GitHubRead.Found(InMemoryGitHub.Installed with
    {
        Permissions = new Dictionary<string, string?>(InMemoryGitHub.Installed.Permissions!)
        {
            ["contents"] = "write",
            ["issues"] = "write",
            ["pull_requests"] = "write"
        }
    });
}
