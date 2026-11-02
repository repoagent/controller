using System.Text.Json;
using static RepoAgent.Auth.IssuancePolicy;
using static RepoAgent.Auth.IssuanceValues;

namespace RepoAgent.Auth;

// Canonical, bounded public encoding shared by enrollment/setup. The challenge is public;
// the independent controller secret is never included. Property order and compact JSON are v1.
public static class ApprovalMarker
{
    // A setup v1 (Legacy) marker names its approval environment; repository-variable markers never do.
    public static string Create(ApprovalContract approval, string repositoryId, string repository, long installationId,
        string enrollmentId, string? challenge, string profile, string epoch, string variable, string? environment, long? environmentId)
    {
        var text = Text(challenge);
        if (text.Length != 43 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new IssuanceDenied();
        // Agent (v2, v4) and Actions (v3) markers share one field order; version and kind differ.
        if (approval.IsRepositoryVariable)
            return JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["version"] = approval.SetupVersion,
                ["approvalKind"] = approval.Kind,
                ["githubHost"] = "github.com",
                ["appId"] = AppId,
                ["appSlug"] = AppSlug,
                ["repositoryId"] = Id(repositoryId),
                ["repository"] = repository,
                ["installationId"] = installationId,
                [approval.CanonicalNames ? "enrollmentId" : "deviceId"] = enrollmentId,
                ["challenge"] = text,
                ["profile"] = profile,
                ["policyVersion"] = approval.Policy,
                ["epoch"] = epoch,
                ["variable"] = variable
            });
        return JsonSerializer.Serialize(new
        {
            version = 1,
            githubHost = "github.com",
            appId = AppId,
            appSlug = AppSlug,
            repositoryId = Id(repositoryId),
            repository,
            installationId,
            deviceId = enrollmentId,
            challenge = text,
            profile,
            policyVersion = IssuancePolicy.Version,
            epoch,
            environment = Text(environment),
            environmentId = Number(environmentId),
            variable
        });
    }
}
