namespace RepoAgent.Auth;

// The durable discriminator selects authority, never a request hint or a successful lookup.
public sealed record ApprovalContract(string Kind, long SetupVersion, string Policy, string VerifierProfile)
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
    // Setup v4 (ADR 0012) returns approval to Agent variables, beside the repository settings, for every profile.
    // It keeps the v2 kind, which names the location; its setup version and policies are its own.
    public static readonly ApprovalContract AgentV4 = new("repository-agent-variable", 4, "repository-agent-approval-v4", "agent-variable-verification");
    public static readonly ApprovalContract AgentCloneV4 = new("repository-agent-variable", 4, "repository-agent-clone-approval-v4", "agent-variable-verification");
    public static readonly ApprovalContract AgentSettingsV4 = new("repository-agent-variable", 4, "repository-agent-settings-approval-v4", "agent-variable-verification");
    private static readonly ApprovalContract[] All = [Legacy, Agent, AgentClone, Actions, ActionsClone, ActionsSettings, AgentV4, AgentCloneV4, AgentSettingsV4];
    public bool IsAgent => Kind == Agent.Kind;
    public bool IsActions => Kind == Actions.Kind;
    // Agent and Actions approvals are direct repository variables without Environment identity.
    public bool IsRepositoryVariable => IsAgent || IsActions;
    public bool IsClone => this == AgentClone || this == ActionsClone || this == AgentCloneV4;
    public bool IsSettings => this == ActionsSettings || this == AgentSettingsV4;
    public int RuntimeVersion => IsSettings ? 3 : IsClone ? 2 : 1;
    // Runtime v2 and v3 files and leases name their profile and approval policy explicitly.
    public bool NamesProfile => RuntimeVersion > 1;
    public bool Allows(string profile) => IsSettings ? profile is "polling-settings" or "webhook-settings" or "polling-clone-settings" or "webhook-clone-settings"
        : IsClone ? profile is "polling-clone" or "webhook-clone" : profile is "polling" or "webhook";
    // Repository-settings tooling writes REPOAGENT_ repository variables. A repository-variable approval may use that
    // prefix only under the name the settings catalogue (src/RepoAgentController/repository-settings.json) reserves
    // for approval, so settings changes never overwrite a marker.
    public bool AcceptsVariable(string variable) => !IsRepositoryVariable || !variable.StartsWith("REPOAGENT_", StringComparison.OrdinalIgnoreCase)
        || string.Equals(variable, "REPOAGENT_APPROVAL", StringComparison.OrdinalIgnoreCase);

    // The contract a stored record names. Historical v1 rows predate the kind and setup version; their complete legacy
    // binding is still required by each reader, and they never select another contract.
    public static ApprovalContract Stored(string? kind, long? setupVersion, string? policy, bool hasEnvironment)
    {
        if (kind is null && setupVersion is null)
            return policy is null || policy == IssuancePolicy.Version ? Legacy : throw new IssuanceDenied();
        var contract = Array.Find(All, c => c.Kind == kind && c.SetupVersion == setupVersion && c.Policy == policy) ?? throw new IssuanceDenied();
        if (contract.IsRepositoryVariable && hasEnvironment) throw new IssuanceDenied();
        return contract;
    }

    public static ApprovalContract ForSetup(int setupVersion, string profile)
    {
        var settings = profile.EndsWith("-settings", StringComparison.Ordinal);
        var clone = profile.EndsWith("-clone", StringComparison.Ordinal);
        return setupVersion switch
        {
            2 when !settings => clone ? AgentClone : Agent,
            3 => settings ? ActionsSettings : clone ? ActionsClone : Actions,
            4 => settings ? AgentSettingsV4 : clone ? AgentCloneV4 : AgentV4,
            _ => throw new IssuanceDenied(400)
        };
    }

    // Persisted format: stored tokens and completions are readable only under these exact chains.
    public SecretPurpose TokenPurpose(string repository, string epoch, string operation, string purpose) =>
        this == Legacy
            ? new("repoagent.issuer.v1", repository, epoch, operation, purpose)
            : new("repoagent.issuer.v2", Kind, Policy, repository, epoch, operation, purpose);

    public SecretPurpose CompletionPurpose(string repository, string epoch, string pending) =>
        this == Legacy
            ? new("repoagent.enrollment.v1", repository, epoch, pending)
            : new("repoagent.enrollment.v2", Kind, Policy, repository, epoch, pending);
}
