using System.Text.Json;
using static RepoAgent.Auth.IssuancePolicy;
using static RepoAgent.Auth.TableValues;

namespace RepoAgent.Auth;

// Canonical, bounded public encoding shared by enrollment/setup. The challenge is public;
// the independent device secret is never included. Property order and compact JSON are v1.
public static class ApprovalMarker
{
    public static string Create(GrantSnapshot grant)
    {
        var challenge = Text(grant.Device, "Challenge");
        if (challenge.Length != 43 || !challenge.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new IssuanceDenied();
        // Agent (v2) and Actions (v3) markers share one field order; version and kind differ.
        if (grant.Approval.IsRepositoryVariable)
            return JsonSerializer.Serialize(new
            {
                version = grant.Approval.SetupVersion, approvalKind = grant.Approval.Kind, githubHost = "github.com", appId = AppId, appSlug = AppSlug,
                repositoryId = Id(grant.RepositoryId), repository = grant.Repository,
                installationId = grant.InstallationId, deviceId = grant.DeviceId, challenge,
                profile = grant.Profile, policyVersion = grant.Approval.Policy, epoch = grant.Epoch, variable = grant.Variable
            });
        return JsonSerializer.Serialize(new
        {
            version = 1, githubHost = "github.com", appId = AppId, appSlug = AppSlug,
            repositoryId = Id(grant.RepositoryId), repository = grant.Repository,
            installationId = grant.InstallationId, deviceId = grant.DeviceId, challenge,
            profile = grant.Profile, policyVersion = IssuancePolicy.Version, epoch = grant.Epoch,
            environment = grant.Environment, environmentId = grant.EnvironmentId, variable = grant.Variable
        });
    }
}
