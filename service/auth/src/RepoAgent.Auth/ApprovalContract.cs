using Azure.Data.Tables;
using static RepoAgent.Auth.TableValues;

namespace RepoAgent.Auth;

// How an approval contract is stored in, and read back from, broker Table rows.
internal static class ApprovalContractRows
{
    extension(ApprovalContract)
    {
        public static ApprovalContract Read(TableEntity entity)
        {
            if (!entity.ContainsKey("ApprovalKind") && !entity.ContainsKey("SetupVersion"))
            {
                // Historical v1 rows predate these fields. Their complete legacy binding is
                // still required by the pending/device/attempt reader; this never selects v2.
                if (entity.ContainsKey("PolicyVersion") && Text(entity, "PolicyVersion") != IssuancePolicy.Version) throw new IssuanceDenied();
                return ApprovalContract.Legacy;
            }
            var contract = Text(entity, "ApprovalKind") switch
            {
                "environment-variable" => ApprovalContract.Legacy,
                "repository-agent-variable" => Text(entity, "PolicyVersion") == ApprovalContract.AgentClone.Policy ? ApprovalContract.AgentClone : ApprovalContract.Agent,
                "repository-actions-variable" => Text(entity, "PolicyVersion") switch
                {
                    var policy when policy == ApprovalContract.ActionsClone.Policy => ApprovalContract.ActionsClone,
                    var policy when policy == ApprovalContract.ActionsSettings.Policy => ApprovalContract.ActionsSettings,
                    _ => ApprovalContract.Actions
                },
                _ => throw new IssuanceDenied()
            };
            if (Number(entity, "SetupVersion") != contract.SetupVersion || Text(entity, "PolicyVersion") != contract.Policy)
                throw new IssuanceDenied();
            if (contract.IsRepositoryVariable && (entity.ContainsKey("Environment") || entity.ContainsKey("EnvironmentId"))) throw new IssuanceDenied();
            return contract;
        }
    }

    extension(ApprovalContract contract)
    {
        public void Write(TableEntity entity)
        {
            entity["ApprovalKind"] = contract.Kind;
            entity["SetupVersion"] = contract.SetupVersion;
            entity["PolicyVersion"] = contract.Policy;
        }

        public void Require(TableEntity entity)
        {
            if (ApprovalContract.Read(entity) != contract) throw new IssuanceDenied();
        }

        // Only the bound kind's direct location is authority; locations never fall back to each other.
        public string VariablesPath => contract.IsActions ? "actions/variables" : contract.IsAgent ? "agents/variables" : throw new IssuanceDenied();
    }
}
