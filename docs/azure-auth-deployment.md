# Azure authentication-service deployment

The maintainer-operated [credential broker](../GLOSSARY.md) will run as an Azure Container App. Its deployment resources belong in one resource group, `rg-repoagent-controller-auth`, in Sweden Central (`swedencentral`). User-hosted controllers have separate deployment requirements; see the [controller deployment research](research/2026-10-20-azure-controller-deployment.md).

## Current state

Verified on 2026-10-20: the resource group contains one user-assigned managed identity, `id-controller-auth-github`. GitHub Actions can authenticate as this identity using OIDC and execute resource-group Bicep deployments. The subscription has `Microsoft.ManagedIdentity` and `Microsoft.App` registered.

The Container App, its environment, image registry, runtime identity, durable store and signing-key resources have not been provisioned. There is no deployed authentication endpoint or automatic image/revision deployment yet. Storage, signing and hosted acceptance requirements remain in the [operations research](research/2026-10-20-azure-auth-service-operations.md) and [existing security ADR](adr/0001-keep-the-shared-app-key-out-of-controller-installations.md).

## Azure and GitHub configuration

The subscription is **Visual Studio Enterprise Subscription**. These non-secret [repository variables](https://github.com/repoagent/controller/settings/variables/actions) are configured in `repoagent/controller`:

| Variable | Value |
| --- | --- |
| `AZURE_SUBSCRIPTION_ID` | `46f7e5d6-05f3-435b-8089-6c63a203dbfa` |
| `AZURE_TENANT_ID` | `3c356090-bfe9-459b-a2ac-064231707881` |
| `AZURE_CLIENT_ID` | `f79a35bd-ed2f-4c9f-be3e-f0c336009562` |
| `AZURE_RESOURCE_GROUP` | `rg-repoagent-controller-auth` |
| `AZURE_LOCATION` | `swedencentral` |

The deployment identity's principal ID is `7bfe4ce7-46ff-4682-9f2f-091ee46febf0`. It has **Contributor** at this scope only:

```text
/subscriptions/46f7e5d6-05f3-435b-8089-6c63a203dbfa/resourceGroups/rg-repoagent-controller-auth
```

This permits resource-group deployments and resource management. It does not permit creating RBAC role assignments, creating other resource groups or registering subscription resource providers. Any future Bicep that assigns runtime or image-pull roles needs an operator to create those assignments, or a separately scoped authorization decision. Deployment access does not establish runtime data access.

The identity's federated credential is named `github-main`:

| Setting | Value |
| --- | --- |
| Issuer | `https://token.actions.githubusercontent.com` |
| Audience | `api://AzureADTokenExchange` |
| Subject | `repo:repoagent@345029828/controller@1426154477:ref:refs/heads/main` |

The subject includes the permanent GitHub owner and repository IDs returned by the repository's OIDC settings. Use this exact subject rather than the older name-only format. The credential trusts jobs on `main` without a GitHub environment. An environment changes the subject; inspect GitHub's actual OIDC configuration and update Azure trust before adding one. Branch protection is a separate GitHub setting and is not established by this trust. See the [GitHub OIDC reference](https://docs.github.com/en/actions/reference/security/oidc).

Azure Login exchanges the GitHub token for Azure access; this setup stores no Azure client secret in GitHub. Keep this deployment identity separate from the future broker runtime identity and its signing/data permissions.

## Verification workflow

[`ci-auth-service.yml`](../.github/workflows/ci-auth-service.yml), displayed as **Auth service CI**, is currently a manually dispatched access check. Its job runs only on `main`, grants `contents: read` and `id-token: write`, and uses Azure Login with the repository variables above. It checks the resource group's location, then executes an incremental deployment of [`verify-access.bicep`](../infra/auth/verify-access.bicep).

The Bicep template declares no resources. It outputs the resource-group ID and location, leaving a deployment record named `github-oidc-<run-id>-<attempt>`. A successful run proves GitHub OIDC login and resource-group deployment access; it does not prove Container App provisioning, image publication, revision rollout or broker behavior.

Run it again from an authenticated GitHub CLI:

```sh
gh workflow run ci-auth-service.yml --repo repoagent/controller --ref main
gh run list --repo repoagent/controller --workflow ci-auth-service.yml --limit 5
gh run watch <run-id> --repo repoagent/controller --exit-status
```

The [first successful run](https://github.com/repoagent/controller/actions/runs/39416909065) used commit `7019ab9fffcd832c8006d5327c0875cdda00252a`. Its Azure deployment, `github-oidc-39416909065-1`, reached `Succeeded` and returned the expected resource-group ID and `swedencentral`.

Inspect the external settings before changing deployment configuration:

```sh
gh variable list --repo repoagent/controller
gh api repos/repoagent/controller/actions/oidc/customization/sub
az resource list --subscription <subscription-id> --resource-group <resource-group>
az identity federated-credential list --subscription <subscription-id> --resource-group <resource-group> --identity-name id-controller-auth-github
az role assignment list --subscription <subscription-id> --assignee-object-id <principal-id> --all
```

Use the IDs and resource-group name in the tables above. Updating a repository variable alone does not move Azure resources or change the identity's trust or RBAC scope.

## Repository approval boundary

[ADR 0006](adr/0006-use-repository-agent-variables-for-approval.md) selects direct repository Agent variables for controller approval. [Provider #68](https://github.com/repoagent/controller/issues/68) and [host #53](https://github.com/repoagent/controller/issues/53) remain pending; merged #52 supports legacy GitHub Environment approval. The new flow requires no GitHub Environment creation, name, numeric identity or deployment settings. Azure Container Apps environments remain a separate infrastructure concept. Actions repository variables in the configuration table above are also separate from repository Agent variables.

The host needs GitHub-authorized Agent-variable write access; only the broker's internal verifier receives Agent variables read, subject to live installation-scope proof. Runtime controller profiles stay frozen without approval-variable access. No deployment resources, workflow, OIDC trust or live variable is changed by this decision; hosted acceptance remains required. See [the approval contract](auth-repository-approval.md).

## Completing service deployment

The guarded Bicep foundation, runtime gates, operator-only permission template and local verification commands are documented in [authentication infrastructure](auth-infrastructure.md). They are source artifacts; no foundation or runtime apply has been performed.

The intended delivery flow is to test and build the authentication service when its code or deployment configuration changes on `main`, publish an immutable image, deploy the required resources with Bicep into the existing resource group, and deploy and verify a Container App revision. Reuse this OIDC login and resource-group boundary. The manual bootstrap workflow is the current access check, not that delivery pipeline.

Implement the service resources, runtime permissions, image publication, rollout/rollback and hosted acceptance before reporting the service as available. The existing [delivery slices](research/2026-10-20-auth-delivery-slices.md#delivery-sequence-and-exit-criteria) and operations research retain those requirements; this bootstrap does not settle their open storage or authorization decisions.

## Bootstrap history

An initial West Europe identity creation was rejected by Azure with `RequestDisallowedByAzure` because the region was not accepting new customers for this subscription. The final group and identity were created in Sweden Central. The superseded `rg-controller-auth` group and its temporary identity were deleted; the repository variables point to the final identity and group above. This records the observed failure, not a permanent statement about West Europe eligibility.
