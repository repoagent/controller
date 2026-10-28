using Xunit;

namespace RepoAgent.Auth.Tests;

// The approval marker is a persisted, public format: setup writes it to the approval variable, the enrollment stores it,
// and every renewal compares GitHub's copy with it exactly. The literals are markers enrollments stored before the
// encoding moved to the core (the enrollmentId rows BrokerStateGoldenTests and the #205 PR 4 recordings hold).
public sealed class ApprovalMarkerTests
{
    [Fact]
    public void Setup_v1_marker_keeps_its_environment_and_field_order() => Assert.Equal(
        """{"version":1,"githubHost":"github.com","appId":5387625,"appSlug":"repo-agent-connector","repositoryId":123456,"repository":"owner/project","installationId":789,"deviceId":"baa0968867944f7bb5390b9ba1f9d0f1","challenge":"A-E_jX5JlQiRWuUNd-PToEfu4AG9UfAaKUQB2j7JBiA","profile":"polling","policyVersion":"repository-approval-v1","epoch":"test-epoch","environment":"approval/environment","environmentId":987,"variable":"APPROVAL"}""",
        ApprovalMarker.Create(ApprovalContract.Legacy, "123456", "owner/project", 789, "baa0968867944f7bb5390b9ba1f9d0f1",
            "A-E_jX5JlQiRWuUNd-PToEfu4AG9UfAaKUQB2j7JBiA", "polling", "test-epoch", "APPROVAL", "approval/environment", 987));

    [Fact]
    public void Repository_variable_marker_keeps_its_kind_policy_and_field_order() => Assert.Equal(
        """{"version":2,"approvalKind":"repository-agent-variable","githubHost":"github.com","appId":5387625,"appSlug":"repo-agent-connector","repositoryId":123456,"repository":"owner/project","installationId":789,"deviceId":"702cd26275d2447f9150342e6ada61a4","challenge":"x-ux5CXgQqQ2JWtbZuRYclXufobG8ocVPI4tgxL4k_o","profile":"polling","policyVersion":"repository-agent-approval-v2","epoch":"test-epoch","variable":"APPROVAL"}""",
        ApprovalMarker.Create(ApprovalContract.Agent, "123456", "owner/project", 789, "702cd26275d2447f9150342e6ada61a4",
            "x-ux5CXgQqQ2JWtbZuRYclXufobG8ocVPI4tgxL4k_o", "polling", "test-epoch", "APPROVAL", null, null));

    // Setup v4 (ADR 0012) has no recorded marker yet; this literal is the format the installer publishes.
    [Fact]
    public void Setup_v4_marker_keeps_the_v2_field_order_with_its_version_and_policy() => Assert.Equal(
        """{"version":4,"approvalKind":"repository-agent-variable","githubHost":"github.com","appId":5387625,"appSlug":"repo-agent-connector","repositoryId":123456,"repository":"owner/project","installationId":789,"deviceId":"702cd26275d2447f9150342e6ada61a4","challenge":"x-ux5CXgQqQ2JWtbZuRYclXufobG8ocVPI4tgxL4k_o","profile":"webhook-clone-settings","policyVersion":"repository-agent-settings-approval-v4","epoch":"test-epoch","variable":"REPOAGENT_APPROVAL"}""",
        ApprovalMarker.Create(ApprovalContract.AgentSettingsV4, "123456", "owner/project", 789, "702cd26275d2447f9150342e6ada61a4",
            "x-ux5CXgQqQ2JWtbZuRYclXufobG8ocVPI4tgxL4k_o", "webhook-clone-settings", "test-epoch", "REPOAGENT_APPROVAL", null, null));

    [Theory]
    [InlineData("A-E_jX5JlQiRWuUNd-PToEfu4AG9UfAaKUQB2j7JBi")]
    [InlineData("A-E_jX5JlQiRWuUNd-PToEfu4AG9UfAaKUQB2j7JBi+")]
    [InlineData(null)]
    public void Challenge_must_be_43_base64url_characters(string? challenge) => Assert.Equal(503, Assert.Throws<IssuanceDenied>(() =>
        ApprovalMarker.Create(ApprovalContract.Agent, "123456", "owner/project", 789, "702cd26275d2447f9150342e6ada61a4",
            challenge, "polling", "test-epoch", "APPROVAL", null, null)).Status);

    [Fact]
    public void Setup_v1_marker_requires_its_environment() => Assert.Throws<IssuanceDenied>(() =>
        ApprovalMarker.Create(ApprovalContract.Legacy, "123456", "owner/project", 789, "baa0968867944f7bb5390b9ba1f9d0f1",
            "A-E_jX5JlQiRWuUNd-PToEfu4AG9UfAaKUQB2j7JBiA", "polling", "test-epoch", "APPROVAL", null, null));
}
