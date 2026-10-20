using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Data.Tables;
using static RepoAgent.Auth.IssuancePolicy;

namespace RepoAgent.Auth;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EnrollmentRequest(string IdempotencyId, long RepositoryId, string Repository, string Environment, string Variable, string Profile)
{
    public void Validate()
    {
        new AgentEnrollmentRequest(IdempotencyId, RepositoryId, Repository, Variable, Profile).Validate();
        if (Environment is null || Environment.Length is < 1 or > 255 || Environment.Any(char.IsControl)) throw new IssuanceDenied(400);
    }
    public string Digest() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AgentEnrollmentRequest(string IdempotencyId, long RepositoryId, string Repository, string Variable, string Profile)
{
    public void Validate()
    {
        if (!Guid.TryParseExact(IdempotencyId, "N", out _) || RepositoryId <= 0
            || Repository is null || Repository.Split('/') is not [var owner, var name] || !Segment(owner) || !Segment(name)
            || Variable is null || Variable.Length is < 1 or > 100 || char.IsAsciiDigit(Variable[0])
            || Variable.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase)
            || !Variable.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') || Profile is not ("polling" or "webhook")) throw new IssuanceDenied(400);
    }
    private static bool Segment(string s) => s.Length is > 0 and <= 100 && s is not ("." or "..")
        && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    public string Digest() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { domain = "repository-agent-variable/setup-v2", request = this })));
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
    public static EnrollmentInput From(AgentEnrollmentRequest request)
    {
        request.Validate();
        return new(request.IdempotencyId, request.RepositoryId, request.Repository, null,
            request.Variable, request.Profile, request.Digest(), ApprovalContract.Agent);
    }
}

internal sealed record EnrollmentSnapshot(TableEntity Gate, TableEntity Pending)
{
    public string Partition => Gate.PartitionKey;
    public string RepositoryId => Text(Pending, "RepositoryId");
    public string Id => Text(Pending, "PendingId");
    public string DeviceId => Text(Pending, "DeviceId");
    public ApprovalContract Approval => ApprovalContract.Read(Pending);
}

internal static class EnrollmentSecrets
{
    public static string Generate() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
