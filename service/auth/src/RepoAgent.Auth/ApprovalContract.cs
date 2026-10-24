using Azure.Data.Tables;
using static RepoAgent.Auth.TableValues;

namespace RepoAgent.Auth;

// How an approval contract is stored in, and read back from, broker Table rows.
internal static class ApprovalContractRows
{
    extension(ApprovalContract)
    {
        // A present field must be a valid value of its type; the core decides which contract the fields name.
        public static ApprovalContract Read(TableEntity entity) => ApprovalContract.Stored(
            entity.ContainsKey("ApprovalKind") ? Text(entity, "ApprovalKind") : null,
            entity.ContainsKey("SetupVersion") ? Number(entity, "SetupVersion") : null,
            entity.ContainsKey("PolicyVersion") ? Text(entity, "PolicyVersion") : null,
            entity.ContainsKey("Environment") || entity.ContainsKey("EnvironmentId"));
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
    }
}
