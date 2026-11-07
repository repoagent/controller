using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class ImagePublicationProfileTests
{
    public static TheoryData<string> Profiles => new()
    {
        "polling-images", "webhook-images", "polling-clone-images", "webhook-clone-images",
        "polling-settings-images", "webhook-settings-images", "polling-clone-settings-images", "webhook-clone-settings-images"
    };

    [Theory]
    [MemberData(nameof(Profiles))]
    public async Task Only_explicit_image_grants_issue_repository_write_tokens_on_runtime_four(string profile)
    {
        var (broker, id, secret) = await CurrentBrokerTests.Active(5, profile, GrantWrite);
        broker.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(401, (await broker.Issuer().IssueAsync(id, secret, TestContext.Current.CancellationToken, 3)).Status);
        var result = await broker.Issuer().IssueAsync(id, secret, TestContext.Current.CancellationToken, 4);
        Assert.Equal(200, result.Status);
        var body = JsonSerializer.SerializeToElement(result.Body, JsonSerializerOptions.Web);
        Assert.Equal("write", body.GetProperty("permissions").GetProperty("contents").GetString());
        Assert.Equal(profile, body.GetProperty("profile").GetString());
        Assert.Equal("repository-agent-images-read-approval-v5", body.GetProperty("policyVersion").GetString());
        Assert.Equal(profile.Contains("-settings", StringComparison.Ordinal), body.GetProperty("permissions").TryGetProperty("agent_variables", out _));
        Assert.Throws<IssuanceDenied>(() => ApprovalContract.ForSetup(4, profile));
        Assert.False(ApprovalContract.AgentSettingsV5.Allows(profile));
        CurrentBrokerTests.AssertNoTokens(broker);
    }

    [Fact]
    public async Task Accepting_App_write_permission_does_not_widen_existing_clone_enrollments()
    {
        var (broker, id, secret) = await CurrentBrokerTests.Active(5, "polling-clone", GrantWrite);
        broker.Clock.Advance(TimeSpan.FromMinutes(1));
        var result = await broker.Issuer().IssueAsync(id, secret, TestContext.Current.CancellationToken, 2);
        Assert.Equal(200, result.Status);
        var body = JsonSerializer.SerializeToElement(result.Body, JsonSerializerOptions.Web);
        Assert.Equal("read", body.GetProperty("permissions").GetProperty("contents").GetString());
        Assert.Equal(401, (await broker.Issuer().IssueAsync(id, secret, TestContext.Current.CancellationToken, 4)).Status);
    }

    static void GrantWrite(InMemoryBroker broker) => broker.GitHub.Installation = GitHubRead.Found(InMemoryGitHub.Installed with
    {
        Permissions = new Dictionary<string, string?>(InMemoryGitHub.Installed.Permissions!) { ["contents"] = "write" }
    });
}
