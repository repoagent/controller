using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using static RepoAgent.Auth.IssuancePolicy;

namespace RepoAgent.Auth;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnrollmentRequest(string IdempotencyId, long RepositoryId, string Repository, string Environment, string Variable, string Profile)
{
    public void Validate()
    {
        new RepositoryVariableEnrollmentRequest(IdempotencyId, RepositoryId, Repository, Variable, Profile).Validate();
        if (Profile is not ("polling" or "webhook")) throw new IssuanceDenied(400);
        if (Environment is null || Environment.Length is < 1 or > 255 || Environment.Any(char.IsControl)) throw new IssuanceDenied(400);
    }
    public string Digest() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));
}

// The setup v2 and v4 (repository Agent variable) and v3 (repository Actions variable) start body. Wire names
// and digests come from its properties and domain strings, never from this type name.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RepositoryVariableEnrollmentRequest(string IdempotencyId, long RepositoryId, string Repository, string Variable, string Profile)
{
    public void Validate()
    {
        if (!Guid.TryParseExact(IdempotencyId, "N", out _) || RepositoryId <= 0
            || !IsRepositoryName(Repository)
            || Variable is null || Variable.Length is < 1 or > 100 || char.IsAsciiDigit(Variable[0])
            || Variable.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase)
            || !Variable.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || Profile is not ("polling" or "webhook" or "polling-clone" or "webhook-clone"
                or "polling-settings" or "webhook-settings" or "polling-clone-settings" or "webhook-clone-settings")) throw new IssuanceDenied(400);
    }
    // The setups share this body grammar; domain separation keeps one idempotency ID from replaying a request
    // across approval locations or setup versions.
    internal string Digest(string domain) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { domain, request = this })));
}

internal sealed record EnrollmentInput(string IdempotencyId, long RepositoryId, string Repository, string? Environment,
    string Variable, string Profile, string BodyHash, ApprovalContract Approval)
{
    public static EnrollmentInput From(EnrollmentRequest request)
    {
        request.Validate();
        return new(request.IdempotencyId, request.RepositoryId, request.Repository, request.Environment,
            request.Variable, request.Profile, request.Digest(), ApprovalContract.Legacy);
    }
    public static EnrollmentInput From(RepositoryVariableEnrollmentRequest request, int setupVersion = 2)
    {
        request.Validate();
        var approval = ApprovalContract.ForSetup(setupVersion, request.Profile);
        if (!approval.AcceptsVariable(request.Variable)) throw new IssuanceDenied(400);
        // "repository-agent-variable/setup-v2", "repository-actions-variable/setup-v3" or "repository-agent-variable/setup-v4".
        var digest = request.Digest($"{approval.Kind}/setup-v{approval.SetupVersion}");
        return new(request.IdempotencyId, request.RepositoryId, request.Repository, null,
            request.Variable, request.Profile, digest, approval);
    }
}

internal static class EnrollmentSecrets
{
    public static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
