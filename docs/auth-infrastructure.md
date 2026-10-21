# Authentication service infrastructure

Azure resource configuration lives in `infra/auth/*.bicep`. `deploy.sh` selects a template, passes deployment inputs to Azure CLI and displays the deployment result. The [deployment runbook](azure-auth-deployment.md) records the resource group, OIDC identity and actual deployment procedure.

## Resources and boundaries

| Template | Resources |
| --- | --- |
| `main.bicep` | Runtime identity, Log Analytics workspace, Container Apps environment, StorageV2 account, durable Blob container, broker Table, Key Vault and optional auth Container App |
| `cleanup.bicep` | Cleanup identity bootstrap or scheduled Container Apps Job |
| `monitoring.bicep` | AAD-only Application Insights, private action group, optional budget and operational alerts |
| `runtime-rbac.bicep` | Operator-managed signing, wrapping-key, Blob-container and Table grants |
| `cleanup-rbac.bicep` | Operator-managed cleanup Table grant |
| `monitoring-rbac.bicep` | Operator-managed Metrics Publisher grants on the Insights component |
| `verify-access.bicep` | Outputs-only real ARM deployment for the GitHub OIDC access check |

Templates target the existing resource group. Deployment Contributor cannot grant roles, import keys or register providers; those operations use separate operator authority. Keep the selected app, storage, vault and Table names stable. A name change does not migrate data.

The pilot uses Consumption, 0.25 vCPU/0.5 GiB, HTTP min/max 0/1, a 30-day workspace retention period and 0.1 GB daily ingestion cap. Console log persistence is disabled. Isolated first-activation mode has no ingress and min/max 1/1. Storage requires HTTPS/TLS 1.2 and Azure identity authentication, with shared keys and anonymous Blob access disabled. Key Vault uses RBAC, 90-day soft delete and purge protection. These settings are declared in Bicep.

## Guarded entrypoints

Install the current Bicep CLI and read the current resource/module documentation. Use the latest released versions of any registry modules; these templates currently use direct resource declarations. Compile locally before pushing:

```sh
export BICEP_BIN="$HOME/.azure/bin/bicep"
for template in infra/auth/*.bicep; do
  "$BICEP_BIN" build "$template" --stdout >/dev/null
done
```

Verify infrastructure by deploying against Azure and inspecting the resources with `az` or the portal. Before an environment becomes mission critical, establish a separate staging or test environment in a real Azure region for testing Bicep changes.

`deploy.sh` requires the existing `AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP`, `AZURE_LOCATION`, `AUTH_APP_NAME`, `AUTH_STORAGE_ACCOUNT_NAME` and `AUTH_KEY_VAULT_NAME` variables. The current destination is subscription `46f7e5d6-05f3-435b-8089-6c63a203dbfa`, group `rg-repoagent-controller-auth`, region `swedencentral`. Workflow jobs install Bicep v0.48.1 and set `BICEP_BIN` to that exact executable.

| Mode | Behavior |
| --- | --- |
| `foundation-check` | Check required target inputs locally |
| `foundation-plan` / `foundation-apply` | Compile and run Azure what-if or Incremental foundation deployment; no app |
| `check` | Check target inputs and hosted readiness assertions locally |
| `plan` / `apply` | Compile and run Azure what-if or Incremental public runtime deployment |
| `stage-plan` / `stage-apply` | Deploy the accepted image without ingress, with min/max 1/1 |
| `promote-staged` | Apply public runtime configuration after all hosted readiness assertions |
| `stage-stop` / `stage-resume` | Deactivate/activate the selected no-ingress staging revision and list its replicas |
| `cleanup-identity-plan` / `cleanup-identity-apply` | Plan/deploy the cleanup identity |
| `cleanup-plan` / `cleanup-apply` | Plan/deploy the scheduled cleanup Job |
| `monitoring-plan` / `monitoring-apply` | Plan/deploy monitoring from a private ARM parameters file |

The script performs an Azure what-if only for explicit plan operations. Apply operations compile the selected template, run `az deployment group create`, and display the actual deployment state, Bicep outputs and resource inventory. Inspect failures in Azure before retrying: ARM can continue after a client disconnect. The script does not claim endpoint or data-plane readiness from a successful infrastructure deployment.

## Canonical origin and readiness

Bicep derives `Broker__BrokerOrigin` from the app name and environment's actual default domain. Runtime identity IDs, storage endpoints and the Application Insights connection are references to Azure resources, rather than settings reconstructed in Python. Normal image rollout/rollback separately checks the app's public origin, intended digest and ready revision.

`/health/ready` verifies local application composition. Actual GitHub access, managed-identity permissions, retained-state recovery and cleanup behavior require application acceptance against the deployed service.

## Runtime prerequisites

Public runtime operations require the five hosted assertions: `AUTH_PROVIDER_READY`, `AUTH_STORAGE_READY`, `AUTH_CLEANUP_READY`, `AUTH_RUNTIME_ACCESS_READY` and `AUTH_DEPLOYMENT_READY`. Leave them false until the corresponding real evidence exists. Initial no-ingress staging omits them so acceptance can be performed.

Runtime inputs are the immutable `AUTH_IMAGE`, distinct pinned `AUTH_SIGNING_KEY_URI` and `AUTH_DATA_PROTECTION_KEY_URI`, and external `AUTH_DEPLOYMENT_EPOCH`. Optional `AUTH_TABLE_NAME` and request-limit variables override Bicep parameter defaults. Defaults and allowed ranges live in Bicep. Preserve the restore epoch outside Table backups.

Activation uses an accepted current-main `AUTH_EXPECTED_SOURCE_SHA`. Source/image checks belong to the existing image delivery helper; image building and publication remain separate from infrastructure. No GitHub App private key, bearer token or registry credential is passed to Bicep.

## Operator-only permissions

`runtime-rbac.bicep` grants the actual runtime principal key read/sign at the GitHub App key, read/wrap/unwrap at the separate Data Protection key, Blob read/write at the key-ring container, and Storage Table Data Contributor at the exact broker Table. The Table role is built-in `0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3`. Key scopes span versions while runtime key URIs pin versions.

`cleanup-rbac.bicep` grants the cleanup principal Table access only. `monitoring-rbac.bicep` grants runtime and cleanup principals Metrics Publisher only at the Insights component. Apply these templates with separate role authority and inspect the resulting assignments. Verify actual identity access and denial outside the selected resources before readiness.

## Capacity and spending-alert plan

The capacity settings and budget resource are declared in Bicep. Monitoring accepts a budget amount in the subscription's billing currency, a first-of-month start date and a private recipient. Actual-spend notifications use 50%, 80% and 100% thresholds. Verify the real Azure budget, currency and delivery. Alerts notify; they do not stop charges or impose a total spending cap.

## Remaining hosted work

After real resource deployment, verify application behavior, managed identities, private-repository scope, HTTPS/cold start, retained-state recovery, independent cleanup, rollback and alert delivery. Follow the [operator runbook](azure-auth-deployment.md) and keep #35/#28 open until hosted acceptance is complete.

## First-activation configuration

Use the existing main-only GitHub OIDC route. Bootstrap foundation, provision keys and exact grants, initialize only a genuinely fresh dedicated Table, then deploy the accepted image without ingress. Record actual resource IDs, image digest, workflow run, revision and observed outcomes. Inspect the resources after each real deployment. Promotion applies the public Bicep configuration only after hosted readiness assertions are established.

## Independent cleanup Job

`cleanup.bicep` bootstraps a separate `${appName}-cleanup` identity before scheduling its Job. The Job uses that identity, the existing Consumption environment and six environment bindings: Table endpoint/name, external epoch, identity client ID and two Application Insights values. It runs `dotnet RepoAgent.Auth.dll cleanup --once`, with UTC cron `* * * * *`, one 0.25 vCPU/0.5 GiB container, parallelism/completion 1/1, zero retries and a 90-second timeout. Its image comes from the accepted immutable auth digest.

Identity bootstrap needs no image or Insights reference. Job deployment requires those resources and starts scheduling immediately. Inspect actual Job identity, configuration and execution history in Azure. Confirm HTTP has zero actual replicas when demonstrating cleanup independence. A command exit, configured scale or sampled metric alone does not establish bounded physical deletion or a global concurrency/spending limit; actual cleanup and recovery acceptance remain in the runbook.
