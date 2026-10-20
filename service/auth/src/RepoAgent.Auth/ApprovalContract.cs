using Azure.Data.Tables;
using Microsoft.AspNetCore.DataProtection;
using static RepoAgent.Auth.IssuancePolicy;

namespace RepoAgent.Auth;

// The durable discriminator selects authority, never a request hint or a successful lookup.
internal sealed record ApprovalContract(string Kind, long SetupVersion, string Policy, string VerifierProfile)
{
    public static readonly ApprovalContract Legacy = new("environment-variable", 1, IssuancePolicy.Version, "verification");
    public static readonly ApprovalContract Agent = new("repository-agent-variable", 2, "repository-agent-approval-v2", "agent-variable-verification");

    public static ApprovalContract Read(TableEntity entity)
    {
        if (!entity.ContainsKey("ApprovalKind") && !entity.ContainsKey("SetupVersion"))
        {
            // Historical v1 rows predate these fields. Their complete legacy binding is
            // still required by the pending/device/attempt reader; this never selects v2.
            if (entity.ContainsKey("PolicyVersion") && Text(entity, "PolicyVersion") != IssuancePolicy.Version) throw new IssuanceDenied();
            return Legacy;
        }
        var contract = Text(entity, "ApprovalKind") switch
        {
            "environment-variable" => Legacy,
            "repository-agent-variable" => Agent,
            _ => throw new IssuanceDenied()
        };
        if (Number(entity, "SetupVersion") != contract.SetupVersion || Text(entity, "PolicyVersion") != contract.Policy)
            throw new IssuanceDenied();
        if (contract == Agent && (entity.ContainsKey("Environment") || entity.ContainsKey("EnvironmentId"))) throw new IssuanceDenied();
        return contract;
    }

    public void Write(TableEntity entity)
    {
        entity["ApprovalKind"] = Kind;
        entity["SetupVersion"] = SetupVersion;
        entity["PolicyVersion"] = Policy;
    }

    public void Require(TableEntity entity)
    {
        if (Read(entity) != this) throw new IssuanceDenied();
    }

    public IDataProtector TokenProtector(IDataProtectionProvider protection, string repository, string epoch, string operation, string purpose) =>
        this == Legacy
            ? protection.CreateProtector("repoagent.issuer.v1", repository, epoch, operation, purpose)
            : protection.CreateProtector("repoagent.issuer.v2", Kind, Policy, repository, epoch, operation, purpose);

    public IDataProtector CompletionProtector(IDataProtectionProvider protection, string repository, string epoch, string pending) =>
        this == Legacy
            ? protection.CreateProtector("repoagent.enrollment.v1", repository, epoch, pending)
            : protection.CreateProtector("repoagent.enrollment.v2", Kind, Policy, repository, epoch, pending);
}
