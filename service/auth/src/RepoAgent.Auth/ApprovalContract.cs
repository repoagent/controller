using Azure.Data.Tables;
using Microsoft.AspNetCore.DataProtection;
using static RepoAgent.Auth.IssuancePolicy;

namespace RepoAgent.Auth;

// The durable discriminator selects authority, never a request hint or a successful lookup.
internal sealed record ApprovalContract(string Kind, long SetupVersion, string Policy, string VerifierProfile)
{
    public static readonly ApprovalContract Legacy = new("environment-variable", 1, IssuancePolicy.Version, "verification");
    public static readonly ApprovalContract Agent = new("repository-agent-variable", 2, "repository-agent-approval-v2", "agent-variable-verification");

    public static readonly ApprovalContract AgentClone = new("repository-agent-variable", 2, "repository-agent-clone-approval-v2", "agent-variable-verification");
    // Setup v3 moves approval to direct repository Actions variables. It is a distinct
    // kind and policy: v2 Agent grants are never reinterpreted as, or migrated to, v3.
    public static readonly ApprovalContract Actions = new("repository-actions-variable", 3, "repository-actions-approval-v3", "actions-variable-verification");
    public static readonly ApprovalContract ActionsClone = new("repository-actions-variable", 3, "repository-actions-clone-approval-v3", "actions-variable-verification");
    public bool IsAgent => this == Agent || this == AgentClone;
    public bool IsActions => this == Actions || this == ActionsClone;
    // Agent and Actions approvals are direct repository variables without Environment identity.
    public bool IsRepositoryVariable => IsAgent || IsActions;
    public bool IsClone => this == AgentClone || this == ActionsClone;
    public int RuntimeVersion => IsClone ? 2 : 1;
    public bool Allows(string profile) => IsClone ? profile is "polling-clone" or "webhook-clone" : profile is "polling" or "webhook";
    // Only the bound kind's direct listing is authority; locations never fall back to each other.
    public string VariablesPath => IsActions ? "actions/variables" : IsAgent ? "agents/variables" : throw new IssuanceDenied();

    public static ApprovalContract ForSetup(int setupVersion, string profile)
    {
        var clone = profile.EndsWith("-clone", StringComparison.Ordinal);
        return setupVersion switch
        {
            2 => clone ? AgentClone : Agent,
            3 => clone ? ActionsClone : Actions,
            _ => throw new IssuanceDenied(400)
        };
    }

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
            "repository-agent-variable" => Text(entity, "PolicyVersion") == AgentClone.Policy ? AgentClone : Agent,
            "repository-actions-variable" => Text(entity, "PolicyVersion") == ActionsClone.Policy ? ActionsClone : Actions,
            _ => throw new IssuanceDenied()
        };
        if (Number(entity, "SetupVersion") != contract.SetupVersion || Text(entity, "PolicyVersion") != contract.Policy)
            throw new IssuanceDenied();
        if (contract.IsRepositoryVariable &&(entity.ContainsKey("Environment") || entity.ContainsKey("EnvironmentId"))) throw new IssuanceDenied();
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
