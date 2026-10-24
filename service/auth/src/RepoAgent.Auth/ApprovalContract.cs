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
    // Each repository-settings profile (runtime v3) adds exactly Actions variables read to a frozen
    // profile. They exist only under setup v3 Actions approval; frozen profiles never gain it.
    public static readonly ApprovalContract ActionsSettings = new("repository-actions-variable", 3, "repository-actions-settings-approval-v3", "actions-variable-verification");
    public bool IsAgent => this == Agent || this == AgentClone;
    public bool IsActions => this == Actions || this == ActionsClone || this == ActionsSettings;
    // Agent and Actions approvals are direct repository variables without Environment identity.
    public bool IsRepositoryVariable => IsAgent || IsActions;
    public bool IsClone => this == AgentClone || this == ActionsClone;
    public bool IsSettings => this == ActionsSettings;
    public int RuntimeVersion => IsSettings ? 3 : IsClone ? 2 : 1;
    // Runtime v2 and v3 files and leases name their profile and approval policy explicitly.
    public bool NamesProfile => RuntimeVersion > 1;
    public bool Allows(string profile) => IsSettings ? profile is "polling-settings" or "webhook-settings" or "polling-clone-settings" or "webhook-clone-settings"
        : IsClone ? profile is "polling-clone" or "webhook-clone" : profile is "polling" or "webhook";
    // Only the bound kind's direct location is authority; locations never fall back to each other.
    public string VariablesPath => IsActions ? "actions/variables" : IsAgent ? "agents/variables" : throw new IssuanceDenied();
    // Repository-settings tooling writes REPOAGENT_ Actions variables. Actions approval may use that prefix
    // only under the name the settings catalogue (src/RepoAgentController/repository-settings.json) reserves
    // for approval, so settings changes never overwrite a marker. Agent variables are a separate store.
    public bool AcceptsVariable(string variable) => !IsActions || !variable.StartsWith("REPOAGENT_", StringComparison.OrdinalIgnoreCase)
        || string.Equals(variable, "REPOAGENT_APPROVAL", StringComparison.OrdinalIgnoreCase);

    public static ApprovalContract ForSetup(int setupVersion, string profile)
    {
        var settings = profile.EndsWith("-settings", StringComparison.Ordinal);
        var clone = profile.EndsWith("-clone", StringComparison.Ordinal);
        return setupVersion switch
        {
            2 when !settings => clone ? AgentClone : Agent,
            3 => settings ? ActionsSettings : clone ? ActionsClone : Actions,
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
            "repository-actions-variable" => Text(entity, "PolicyVersion") switch
            {
                var policy when policy == ActionsClone.Policy => ActionsClone,
                var policy when policy == ActionsSettings.Policy => ActionsSettings,
                _ => Actions
            },
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
