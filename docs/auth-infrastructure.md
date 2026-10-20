# Authentication service infrastructure

[Issue #47](https://github.com/repoagent/controller/issues/47) supplies the Azure foundation and a gated Container App template. It does not supply the credential broker provider, its transactional database or independent cleanup worker. No live resources or endpoint have been created by this change. The existing [deployment runbook](azure-auth-deployment.md) remains authoritative for the subscription, resource group, OIDC identity and immutable repository subject.

## Resources and boundaries

`infra/auth/main.bicep` targets the existing resource group. `deploy.sh` accepts only subscription `46f7e5d6-05f3-435b-8089-6c63a203dbfa`, group `rg-repoagent-controller-auth`, location `swedencentral`. It never creates a group, registers a provider, recovers a deleted vault, grants roles or imports a key. The existing `id-controller-auth-github` identity is not in its write set.

| Resource | Foundation behavior |
| --- | --- |
| `${AUTH_APP_NAME}-runtime` | Separate user-assigned runtime identity; no deployment authority |
| `${AUTH_APP_NAME}-logs` | Log Analytics, 30-day retention and 1 GB/day ingestion cap |
| `${AUTH_APP_NAME}-environment` | Container Apps Consumption workload profile |
| Deterministic `ra…dp` storage account and `data-protection` Blob container | HTTPS, OAuth authentication, no shared-key or anonymous Blob access; durable key ring at `keyring.xml` |
| Deterministic `ra-…-kv` Key Vault | Empty, RBAC-enabled, 90-day soft delete and purge protection; no keys/secrets generated |
| `${AUTH_APP_NAME}` (runtime mode only) | Dedicated immutable image, managed runtime identity, HTTPS ingress on port 8080, Single revision mode, minimum zero and maximum one replica, startup/liveness/readiness probes |

Global storage/vault names use the first 16 hex characters of SHA-256 over the selected group resource ID plus `/` and the app name. `foundation-check` prints their references without contacting Azure. Name conflicts outside this group fail ARM validation; the tooling does not adopt, rename or recover resources to bypass them.

The storage and vault endpoints use public Azure networking with authenticated data access. “Private Blob container” means anonymous access is disabled, not a private endpoint or VNet. Network isolation would require a separately reviewed network design. Application logs must exclude credentials, HTTP bodies and sensitive enrollment data. The log ingestion cap can suppress diagnostics and is not a strict spending limit; operator cost alerts remain a hosted operations task. Protection keys have no automatic deletion/lifecycle policy: removal must account for cookie/recovery lifetimes and backups.

The template uses Microsoft's published schemas for [Container Apps](https://learn.microsoft.com/en-us/azure/templates/microsoft.app/2025-01-01/containerapps), [managed environments](https://learn.microsoft.com/en-us/azure/templates/microsoft.app/2025-01-01/managedenvironments), [Storage](https://learn.microsoft.com/en-us/azure/templates/microsoft.storage/2023-05-01/storageaccounts) and [Key Vault](https://learn.microsoft.com/en-us/azure/templates/microsoft.keyvault/2023-07-01/vaults). Compilation verifies schema compatibility locally; Azure policy, region availability, quota and runtime access remain live acceptance requirements.

## Entrypoints

Run from the repository root with Bash, Python 3.10+ and a standalone Bicep CLI on PATH (or `BICEP_BIN`). The existing Azure CLI installation path `$HOME/.azure/bin/bicep` is also recognized. Scripts never install tools or authenticate automatically.

```sh
infra/auth/validate.sh
python3 -m unittest discover -s infra/auth/tests -v
```

`validate.sh` compiles both templates with the real Bicep compiler, checks the emitted main ARM resource write set against the ownership preflight manifest, and checks shell syntax. It requires no credentials. Set the four shared inputs explicitly for all deployment modes:

```sh
export AZURE_SUBSCRIPTION_ID=46f7e5d6-05f3-435b-8089-6c63a203dbfa
export AZURE_RESOURCE_GROUP=rg-repoagent-controller-auth
export AZURE_LOCATION=swedencentral
export AUTH_APP_NAME=controller-auth
infra/auth/deploy.sh foundation-check
```

The app name must be 3–22 lowercase letters/digits/hyphens, start with a letter, end with a letter/digit and contain no consecutive hyphens. Keep it stable: changing it selects different resources rather than migrating state.

| Command | Action |
| --- | --- |
| `foundation-check` | Local target/name validation and reference discovery |
| `foundation-plan` | Compile, discover ownership, ARM validate and what-if for foundation resources; no apply |
| `foundation-apply` | Same checks, then repeat ownership discovery and Incremental apply of foundation only |
| `check` | Local target and provider prerequisite validation, no image required and no Azure call |
| `plan` | Runtime checks, compile, foundation ownership checks, ARM validate and what-if |
| `apply` | Runtime plan, repeat ownership checks, then Incremental apply |

Only `foundation-apply` and `apply` write Azure resources. They are implemented for subsequent authorized operation; this slice stops before live apply. All Azure calls pass the subscription explicitly. Stdout is JSON containing `appName`, `runtimeIdentityId`, `keyVaultName`, `dataProtectionBlobUri`, and `applied`. Progress and planned resource IDs/types go to stderr; no provider response bodies or secret values are printed. The output names describe configuration, not endpoint readiness.

Ownership is checked individually by exact ARM ID, type, location and tags `repositoryId=1426154477`, `ownerId=345029828`, `managedBy=repoagent-auth`, plus the resource's role. The Blob container uses equivalent metadata because that resource has no tags. The default Blob service is an existing resource and is not written. Missing, unlabelled or conflicting ownership never becomes implicit adoption. Only explicit `ResourceNotFound`/`ParentResourceNotFound` responses permit foundation creation; permission, network, malformed and other errors are fatal. Runtime mode requires all foundation resources already present, because the operator must first grant the existing identity access.

What-if must succeed and contain only known resource IDs with Create/Modify/NoChange (Ignore entries are not applied). Deletions and unsupported changes fail. Unrelated group resources remain untouched under Incremental mode. Azure does not provide an atomic ownership-check-plus-template-write transaction: serialize deployments, restrict who can mutate the group, and inspect unexpected concurrent changes. The second preflight narrows that interval but cannot eliminate it. Existing same-owned tags/properties are declaratively reconciled; tags are an operational adoption guard, not a security boundary against someone with Contributor access.

## Runtime prerequisites and image contract

`check`, `plan` and `apply` require all of these explicit values:

| Variable | Meaning |
| --- | --- |
| `AUTH_PROVIDER_READY=true` | Real provider implementation understands this configuration and passes its security/consumer contracts |
| `AUTH_STORAGE_READY=true` | Reviewed durable store, concurrency, revocation, expiry and recovery behavior verified |
| `AUTH_CLEANUP_READY=true` | Independent cleanup works with zero HTTP replicas; admission limits and cleanup-lag evidence exist |
| `AUTH_RUNTIME_ACCESS_READY=true` | Operator grants exist and runtime managed-identity integration is verified |
| `AUTH_SIGNING_KEY_URI` | `https://<foundation-vault>.vault.azure.net/keys/<name>/<32-hex-version>` |
| `AUTH_DATA_PROTECTION_KEY_URI` | Versioned reference to a **different key name** in the same vault |
| `AUTH_OAUTH_CLIENT_SECRET_URI` | Versioned `https://<foundation-vault>.vault.azure.net/secrets/<name>/<32-hex-version>` |
| `AUTH_STORE_RESOURCE_ID` | Nonsecret ARM ID for the separately provisioned store in the selected subscription |

These readiness values are explicit release assertions, not automatic proof. Leave them unset until the evidence exists. References contain no secret values. The OAuth secret is resolved by Container Apps through the runtime identity; signing and wrapping URIs are passed to provider code for SDK operations. Pinning versions makes switching explicit; keep old wrapping-key versions while protected payloads need them. The shared GitHub App signing key must be imported through a separate protected operator procedure; automatic vault key generation cannot rotate GitHub's registered key.

`plan`/`apply` additionally require a registry-qualified `AUTH_IMAGE` ending in `@sha256:<64 lowercase hex>`, plus `AUTH_IMAGE_ROLE=auth-service`, `AUTH_IMAGE_NON_ROOT=true`, and `AUTH_IMAGE_PUBLIC=true`. The caller must inspect the exact digest's OCI role and configured nonzero numeric user, and prove anonymous pullability **before** setting these attestations or provisioning resources. Do not set them from user input alone. The template has no registry credentials: private registry pulling requires a separate identity/role design. The workflow slice #43 owns these checks and image publication. Local rollback of an existing app belongs to that delivery workflow.

The provider must listen on 8080 as a non-root user and implement `/health/live` and `/health/ready`; readiness must check required runtime dependencies without returning secrets. `Auth__SigningKeyUri`, `Auth__DataProtectionKeyUri`, `Auth__DataProtectionBlobUri`, stable `Auth__DataProtectionApplicationName`, `Auth__StoreResourceId`, and secret-backed `Auth__OAuthClientSecret` are the proposed provider configuration contract. `AZURE_CLIENT_ID` selects the runtime identity. The provider must add its reviewed GitHub App/client public settings and canonical origin/proxy configuration when its actual contract is implemented; do not enable runtime readiness based solely on these templates. Replica limits do not serialize authorization operations and do not replace durable mint/revoke concurrency controls.

## Operator-only runtime permissions

`runtime-rbac.bicep` is a separate, optional operator template. Neither `deploy.sh` nor the Contributor workflow calls it. First plan/apply the foundation under a separate approved operation; then securely import the GitHub signing key, create the independent wrapping key and populate the OAuth client secret. Never place private key material or secret values in Bicep, parameter files, outputs, command-line arguments or image layers.

The operator needs `Microsoft.Authorization/roleDefinitions/read,write` at the resource group and `Microsoft.Authorization/roleAssignments/read,write` at the specific resources (typically an already authorized Owner/custom administrative role), plus read access to the runtime identity. Existing Contributor cannot perform these operations; do not grant the workflow Owner or User Access Administrator to make them pass. Key/secret population requires separate data-plane administrative access.

The template defines four custom roles with no management actions. Each definition is assignable only inside the existing resource group. Each assignment goes to the runtime principal at the following **exact** resource scope, not the whole vault, account or group:

| Role suffix | Data actions | Assignment scope |
| --- | --- | --- |
| `sign` | Key read + sign | Imported GitHub App key |
| `wrap` | Key read + wrap + unwrap | Separate Data Protection wrapping key |
| `oauth` | Secret metadata read + getSecret | OAuth client secret |
| `keyring` | Blob read + write | `data-protection` container |

Key and secret RBAC scopes cover versions of that key/secret, while runtime configuration pins particular versions. These actions deliberately omit key create/delete/export and Blob deletion. They follow the [Key Vault data action definitions](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/security); actual first-party SDK behavior under these minimal permissions remains a required managed-identity integration check.

Supply `appName`, `storageName`, `vaultName`, `signingKeyName`, `dataProtectionKeyName`, and `oauthClientSecretName` as nonsecret parameters. This operator template has no automated adoption entrypoint: before using it, inspect foundation ownership with `foundation-plan`, then inspect the emitted role-definition/assignment IDs and any existing definitions/assignments. Existing GUIDs must match these exact permissions, assignable scopes, runtime principal and resource scopes; stop on any conflict. Review an explicit-subscription, resource-group Incremental what-if and apply only after that separate authorization. Do not use this file as an automatic repair tool for unknown assignments. Custom role changes and assignment propagation need operator verification; this repository has not executed them.

## Remaining hosted work

1. Approve and implement the provider database/transaction/partition/recovery contract. Cosmos DB is still a proposal; this template creates no database and proves no store semantics.
2. Implement and provision independent cleanup (for example a reviewed scheduled job) with bounded physical deletion and cleanup-lag monitoring. No cleanup worker exists here.
3. Implement the real provider configuration and health contract, durable Data Protection SDK wiring, signing, enrollment approval and current-administrator checks.
4. Import protected credentials, grant minimal runtime/store roles and verify managed-identity access. Verify old wrapping-key access during version rotation.
5. Run real ARM validation/what-if, initial and repeated hosted deployments, restart/cold-start, HTTPS, rollback, enrollment, token scope, revocation and recovery acceptance. Check budgets and diagnostics before advertising an endpoint.

The process tests replace only the external Azure CLI boundary. They prove the project's command orchestration and refusal behavior; fixtures are not proof of Azure acceptance, RBAC propagation, provider behavior or service availability.
