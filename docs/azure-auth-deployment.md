# Azure authentication-service deployment

The maintainer-operated [credential broker](../GLOSSARY.md) will run as an Azure Container App. Its deployment resources belong in one resource group, `rg-repoagent-controller-auth`, in Sweden Central (`swedencentral`). User-hosted controllers have separate deployment requirements; see the [controller deployment research](research/2026-10-20-azure-controller-deployment.md).

## Current state

Verified on 2026-10-20: the resource group contains one user-assigned managed identity, `id-controller-auth-github`. GitHub Actions can authenticate as this identity using OIDC and execute resource-group Bicep deployments. The subscription has `Microsoft.ManagedIdentity` and `Microsoft.App` registered.

The Container App, its environment, image registry, runtime identity, durable store and signing-key resources have not been provisioned. There is no deployed authentication endpoint. Automatic verification, image publication and revision deployment source already exists; live provisioning and acceptance remain unperformed. Storage, signing and hosted acceptance requirements remain in the [operations research](research/2026-10-20-azure-auth-service-operations.md) and [existing security ADR](adr/0001-keep-the-shared-app-key-out-of-controller-installations.md).

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

## Verification and delivery workflow

[`ci-auth-service.yml`](../.github/workflows/ci-auth-service.yml), **Auth service CI**, verifies service and infrastructure changes on pull requests and `main`. Its full delivery path verifies the service against pinned Azurite, compiles and tests Bicep, publishes an immutable auth image after the existing public-registry/readiness gates, applies guarded runtime infrastructure, and verifies the intended image and ready revision. Publication and deployment run only on `main`; deployment uses the existing branch OIDC identity with `contents: read` and `id-token: write`. No GitHub environment is introduced.

Manual `operation=access-check` retains the original outputs-only [`verify-access.bicep`](../infra/auth/verify-access.bicep) check. It verifies the resource-group location and records an incremental deployment named `github-oidc-<run-id>-<attempt>`. Its success proves OIDC login and deployment access, not service provisioning or broker acceptance. Manual `operation=deploy` selects full delivery. [`deploy-auth.yml`](../.github/workflows/deploy-auth.yml) updates an existing app to an approved immutable digest for deployment or rollback, preserving its acceptance gate.

For a separately authorized access check:

```sh
gh workflow run ci-auth-service.yml --repo repoagent/controller --ref main -f operation=access-check
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

[ADR 0006](adr/0006-use-repository-agent-variables-for-approval.md) selects direct repository Agent variables for controller approval. [Provider #68](https://github.com/repoagent/controller/issues/68) owns the implemented [v2 setup/marker source contract](../service/auth/README.md#repository-agent-variable-enrollment-v2), with frozen v1 controller files/runtime and separate legacy Environment bindings. Guided installation and host integration remain [#53](https://github.com/repoagent/controller/issues/53) work; Azure, live App-installation and native Windows acceptance remain release gates. The new flow requires no GitHub Environment creation, name, numeric identity or deployment settings. Azure Container Apps environments remain a separate infrastructure concept. Actions repository variables in the configuration table above are also separate from repository Agent variables.

The host needs GitHub-authorized Agent-variable write access; only the broker's internal verifier receives Agent variables read, subject to live installation-scope proof. Runtime controller profiles stay frozen without approval-variable access. No deployment resources, workflow, OIDC trust or live variable is changed by this decision; hosted acceptance remains required. See [the approval contract](auth-repository-approval.md).

## First activation through existing OIDC

These are source mechanics, not hosted evidence. Source merge authorizes none of the live commands below. Obtain the specific operator authorization for publication, resource apply, key import, grants and variable changes. Keep #35/#28 open until actual hosted gates pass. Preserve nonsecret source/digest/app/revision/replica/origin, workflow-run and acceptance-evidence references; never private requests/responses or tokens in logs/artifacts.

The existing workflow has one shared main-only activation route: `foundation-plan`, `foundation-apply`, `stage-plan`, `stage-apply`, `promote-staged`, `stage-stop`, `stage-resume`. These choices exclude normal build/publication/deploy jobs. Every choice requires `expected-source-sha`: the exact full SHA must match the dispatched main checkout, `GITHUB_SHA` and actual remote main, rechecked before writes. A moving-main mismatch fails; deliberately accept the new artifact instead of bypassing the check. Runtime choices additionally require anonymous fixed-GHCR image attestation, including the source revision label. Existing branch OIDC, Contributor scope, concurrency and no-environment trust remain unchanged.

1. Select and retain stable `AUTH_APP_NAME`, dedicated Table name, external restore epoch and distinct signing/wrapping key names. Inspect the selected destination/configuration with authorized operator access; establish cost, cleanup and recovery ownership. Local `infra/auth/deploy.sh foundation-check` discovers deterministic names without Azure calls.
2. Plan/apply foundation through the working OIDC route (the available local Azure session cannot select this subscription):

   ```sh
   gh workflow run ci-auth-service.yml --repo repoagent/controller --ref main \
     -f operation=foundation-plan -f expected-source-sha="$accepted_source"
   # Inspect the plan/run output before separately authorized apply.
   gh workflow run ci-auth-service.yml --repo repoagent/controller --ref main \
     -f operation=foundation-apply -f expected-source-sha="$accepted_source"
   ```

   Foundation guards remain unchanged; no image/hosted flags are needed and no app is created. Capture owned environment domain, runtime resource/client/principal IDs and exact Table/Blob/vault IDs. Apply starts billed resources and purge-protected vault retention.
3. With separate key-administration authority, securely import the real GitHub App signing key and create a distinct wrapping key. With separate role authority, review/apply `runtime-rbac.bicep` at the exact key, Blob-container and Table scopes in [the infrastructure contract](auth-infrastructure.md#operator-only-permissions). Record only pinned version URIs. Never place PEM/key material in Bicep, argv or logs. Deployment Contributor cannot perform this role setup; do not broaden it.
4. Stop/fence every broker writer. Initialize only a genuinely fresh dedicated Table with the two complete **Int64** quota entities in [retained-state.md](../service/auth/retained-state.md), verifying actual stored types/values. Existing unaccounted data requires an offline charged inventory; never seed it as zero.
5. With separately authorized registry credentials established using stdin/private credential storage, bootstrap the real reviewed commit:

   ```sh
   AUTH_IMAGE_REPOSITORY=ghcr.io/repoagent/controller-auth GITHUB_SHA="$accepted_source" \
     python3 scripts/delivery/auth-pipeline.py bootstrap-publish
   ```

   The helper archives the entire actual commit into temporary storage, runs source/service verification there and builds only its `service/auth` context. Dirty/untracked local source is excluded. It checks role/nonroot/Linux-amd64/revision, pushes only the fixed repository and records the actual source/digest. It never calls Azure. A private first push prints `publicationComplete=false` and fails anonymous attestation; retain that receipt. An operator must make that exact package public and grant repository Actions access. No helper changes registry visibility/permissions. Anonymous pull alone cannot establish Actions push access: bootstrap reports complete only when the operator has also supplied `AUTH_PACKAGE_ACTIONS_ACCESS_CONFIRMED=true` after verifying that access. This is an operator assertion, never set by the helper. Resume by anonymously attesting the recorded digest/source without rebuilding:

   ```sh
   AUTH_IMAGE_REPOSITORY=ghcr.io/repoagent/controller-auth AUTH_IMAGE="$accepted_image" \
     AUTH_EXPECTED_SOURCE_SHA="$accepted_source" python3 scripts/delivery/auth-pipeline.py attest
   ```

   After resumed attestation and separate Actions-access verification, append both nonsecret evidence references to the original receipt. Do not rerun bootstrap merely to change its receipt status.

6. Supply real nonsecret runtime configuration using the existing Actions variables (pinned distinct keys, epoch, stable app/Table configuration). Leave hosted flags unset/false. Review stage-plan, then separately authorize stage-apply:

   ```sh
   gh workflow run ci-auth-service.yml --repo repoagent/controller --ref main \
     -f operation=stage-plan -f expected-source-sha="$accepted_source" -f image="$accepted_image"
   gh workflow run ci-auth-service.yml --repo repoagent/controller --ref main \
     -f operation=stage-apply -f expected-source-sha="$accepted_source" -f image="$accepted_image"
   ```

   Confirm ingress absent (internal ingress is also forbidden), immutable selected revision, actual runtime identity, canonical origin and temporary min/max 1/1. Compute/log bounds remain unchanged. A public app is refused, never taken offline. Staging sets no READY assertion and performs no public rollout verification.
7. Use the selected-console procedure below for actual runtime/provider/private-repository, storage/recovery and denial evidence. Verify independent cleanup #72 with HTTP stopped/zero replicas; a local suite/readiness 200 does not prove that. Stop the isolated replica while paused.
8. Only after every corresponding evidence item exists, set `AUTH_PROVIDER_READY`, `AUTH_STORAGE_READY`, `AUTH_CLEANUP_READY`, `AUTH_RUNTIME_ACCESS_READY` and `AUTH_DEPLOYMENT_READY` truthfully, then promote the *same digest*:

   ```sh
   gh workflow run ci-auth-service.yml --repo repoagent/controller --ref main \
     -f operation=promote-staged -f expected-source-sha="$accepted_source" -f image="$accepted_image"
   ```

   Promotion requires the current owned no-ingress staging app with that artifact and all release flags. It uses guarded full apply and public rollout/origin checks, restoring HTTPS/min 0/max 1 without rebuild. A newly built CI digest is a subsequent release with its own acceptance. Prove public TLS/cold start, bounded enrollment/recovery/ack, issuance/renewal, withdrawal/revocation/token settlement, rollback and selected-private-repository binding. Native Windows/Docker Desktop remains separately owned acceptance.

## Selected console and private runtime evidence

Use the returned app/revision and inspect replicas to select the exact `auth-service` container. Use Azure's existing selected-container exec or ephemeral debug console. An authorized CLI session can open:

```sh
az containerapp exec --subscription "$AZURE_SUBSCRIPTION_ID" --resource-group "$AZURE_RESOURCE_GROUP" \
  --name "$AUTH_APP_NAME" --revision "$staging_revision" --replica "$staging_replica" \
  --container auth-service --command /bin/sh
```

The production image uses standard ASP.NET 10.0.13 Ubuntu Noble and installs curl and jq for selected-container checks, retaining UID/GID 1654. Verify the actual deployed digest and installed tool options; a debug container must not be assumed to inherit the application's identity context. Require successful localhost `/health/live` and `/health/ready` on port 8080. Readiness200 proves composition only. Exercise normal broker enrollment/issuance/recovery endpoints for real lifecycle proof.

Before private input, confirm console/session-recording policy supports it. Disable shell tracing and terminal echo; use `umask 077`, a private 0700 scratch directory and 0600 request/config/response files. Transfer secrets only through an approved private-input facility. Use installed tool-supported header configuration, body-file and response-file options; never expand bearer/identity headers into process argv, terminal output or logs. Use curl with a private `--config` file containing header, `data-binary = @<request-file>` and output-file settings; verify these options with nonsecret inputs first. Use jq to read tokens from private files and write extracted values to private files without terminal output. Missing tools/options/private-input support leaves acceptance failed; separately scope tooling rather than installing diagnostics or adding a production harness.

Direct data-plane proof requires the selected auth-service container's actual `IDENTITY_ENDPOINT` and `IDENTITY_HEADER`, plus explicit `client_id` equal to the recorded runtime user-assigned client ID. Supply the header from a private config file, request `api-version=2019-08-01` and the appropriate `resource` (`https://storage.azure.com/` or `https://vault.azure.net`). Keep responses/tokens private. Verify returned identity context against recorded client/principal IDs before accepting evidence. Never use operator `az` credentials, a default identity or assumed debug identity inheritance.

Prove Table access/ETags and denial outside the dedicated table; encrypted Blob key-ring persistence across selected-revision restart and denial outside its container; real App-key signing accepted by GitHub; distinct-key wrap/unwrap and denial outside each scope. Prove exact private-pilot installation-token repository/read scope and runtime approval-access denial. Prove epoch/restore recovery cannot revive revoked authority. Use normal broker requests for lifecycle behavior and private runtime-MI requests for direct scope evidence. Preserve only redacted outcomes/evidence references. Remove scratch files, clear sensitive variables, restore terminal state and close/clean up the ephemeral console session.

## Confirmed isolated stop and resume

Close acceptance/debug sessions. Both operations require exact source/digest and the recorded revision:

```sh
gh workflow run ci-auth-service.yml --repo repoagent/controller --ref main \
  -f operation=stage-stop -f expected-source-sha="$accepted_source" \
  -f image="$accepted_image" -f revision="$staging_revision"
# Resume only when continuing acceptance:
gh workflow run ci-auth-service.yml --repo repoagent/controller --ref main \
  -f operation=stage-resume -f expected-source-sha="$accepted_source" \
  -f image="$accepted_image" -f revision="$staging_revision"
```

Stop rechecks ownership/no ingress and the exact revision/digest, deactivates only that revision, then inspects every observed revision and each actual replica list. Confirmation requires no active revisions and zero actual replicas within a 60-second observation deadline. Empty/ambiguous/failed observations are not proof. Failure is unconfirmed shutdown, requiring operator inspection; a successful deactivate command alone does not establish zero compute. No deletion or permanent stop scheduler is used.

Resume activates only that verified revision, reselects its replica and checks no ingress/digest plus local live/ready health through selected-container exec. Existing curl or wget is required; unavailable tools/console behavior or failed health produces unconfirmed resume. Re-establish private runtime identity context before data-plane acceptance. If main advanced, both routes refuse stale source; obtain a separately reviewed maintenance procedure rather than weaken source binding.

Configured 1/1 is a per-revision target, not a global hard spend cap; maintenance/rollout can overlap replicas. Storage, logs and vault charges continue after confirmed zero compute replicas.

## Bootstrap history

An initial West Europe identity creation was rejected by Azure with `RequestDisallowedByAzure` because the region was not accepting new customers for this subscription. The final group and identity were created in Sweden Central. The superseded `rg-controller-auth` group and its temporary identity were deleted; the repository variables point to the final identity and group above. This records the observed failure, not a permanent statement about West Europe eligibility.

## Cleanup identity bootstrap and scheduling

These source routes preserve the exact main-only OIDC subject, Contributor scope, shared noncanceling delivery concurrency and all normal release gates. They exclude ordinary build/publication/deploy jobs and never set readiness flags. Source merge is not permission for live operations below. Use the accepted current-main source and the same anonymously attested public auth image; no rebuild or registry-secret handling is needed.

1. With the owned foundation already present, inspect `cleanup-identity-plan`, then separately authorize `cleanup-identity-apply` using the existing workflow:

   ```sh
   gh workflow run ci-auth-service.yml --repo repoagent/controller --ref main \
     -f operation=cleanup-identity-plan -f expected-source-sha="$accepted_source"
   # Only after reviewing this plan and authorizing the identity write:
   gh workflow run ci-auth-service.yml --repo repoagent/controller --ref main \
     -f operation=cleanup-identity-apply -f expected-source-sha="$accepted_source"
   ```

2. Retain the actual `cleanupIdentityId`, client/principal IDs and exact Table resource ID. A separate authorized operator reviews/applies `infra/auth/cleanup-rbac.bicep` with the reported app/storage/Table parameters, checks exact scope/principal and role propagation. The workflow cannot grant roles. Establish access before starting the schedule; missing evidence is an unresolved operator checkpoint, not a new readiness variable or permission fallback.
3. Keep the external epoch and existing app/Table names stable. Inspect `cleanup-plan`, then separately authorize `cleanup-apply`:

   ```sh
   gh workflow run ci-auth-service.yml --repo repoagent/controller --ref main \
     -f operation=cleanup-plan -f expected-source-sha="$accepted_source" -f image="$accepted_image"
   # Apply immediately activates minute scheduling; review that cost and access first.
   gh workflow run ci-auth-service.yml --repo repoagent/controller --ref main \
     -f operation=cleanup-apply -f expected-source-sha="$accepted_source" -f image="$accepted_image"
   ```

Successful ARM status alone is insufficient. Receipts require actual owned identity and exact Job readback, including Consumption, immutable desired image, four-variable allowlist, cleanup-only identity, command, fixed scheduling/capacity and absence of forbidden additions. Unconfirmed apply requires inspection before retry; a Job may already be running. An owned prior approved-repository digest can update to the new current-main-attested digest, but prior-image drift during planning fails. Changing epoch/identity or other nonconforming prior state is not an automatic update/repair. Restoring a Manual Job also resumes scheduling with the desired image and does not prove older executions drained.

## Cleanup schedule suspension and drain

This is a separately authorized operator procedure, not an automated lifecycle route or tested Azure drain proof. Verify installed CLI/extension support before use. Use the approved subscription/group and exact owned Job; preserve the recorded source/digest/epoch/identity and runtime shape from the deployment receipt. Record current execution IDs before switching the trigger, because history truncation is not proof of absence. No cursor deletion/reset or epoch change proves settlement.

Capture `before.json` with `az resource show` at API2025-01-01. Inspect its ID/type/location/ownership tags, cleanup-only identity, desired digest/environment/epoch, command/environment allowlist and fixed runtime against the receipt. Reject nonconforming state. The following local construction copies only writable fields and changes only trigger configuration; it does not authorize the write:

```sh
job_id="/subscriptions/$AZURE_SUBSCRIPTION_ID/resourceGroups/$AZURE_RESOURCE_GROUP/providers/Microsoft.App/jobs/$AUTH_APP_NAME-cleanup"
az resource show --ids "$job_id" --api-version 2025-01-01 \
  --subscription "$AZURE_SUBSCRIPTION_ID" --output json > before.json
az containerapp job execution list --name "$AUTH_APP_NAME-cleanup" \
  --resource-group "$AZURE_RESOURCE_GROUP" --subscription "$AZURE_SUBSCRIPTION_ID" --output json > executions-before.json
jq '{"$schema":"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
  contentVersion:"1.0.0.0", resources:[{
    type:"Microsoft.App/jobs", apiVersion:"2025-01-01", name:.name,
    location:.location, tags:.tags, identity:.identity,
    properties:{environmentId:.properties.environmentId,
      workloadProfileName:.properties.workloadProfileName, template:.properties.template,
      configuration:{triggerType:"Manual", replicaRetryLimit:0, replicaTimeout:90,
        manualTriggerConfig:{parallelism:1,replicaCompletionCount:1}}}}]}' before.json > manual.json
# Keep only the identity's writable type and resource-ID keys; strip read-only IDs.
jq '.resources[0].identity |= {type:.type,
  userAssignedIdentities:(.userAssignedIdentities | with_entries(.value={}))}' manual.json > manual-checked.json
az deployment group validate --resource-group "$AZURE_RESOURCE_GROUP" \
  --subscription "$AZURE_SUBSCRIPTION_ID" --mode Incremental --template-file manual-checked.json
az deployment group what-if --resource-group "$AZURE_RESOURCE_GROUP" \
  --subscription "$AZURE_SUBSCRIPTION_ID" --mode Incremental --template-file manual-checked.json \
  --result-format FullResourcePayloads --no-pretty-print
```

Check the constructed resource's runtime template/environment/profile/identity/tags against `before.json`: only trigger configuration may differ, with scheduleTriggerConfig removed. Check what-if contains only the exact Job and only the reviewed trigger delta. Unresolved/extra changes require inspection; do not ignore them. Immediately re-read the full Job and compare with `before.json` before the separately authorized write. Any intervening change requires a new review. Keep other group writers fenced; a check/write sequence is not atomic.

```sh
az resource show --ids "$job_id" --api-version 2025-01-01 \
  --subscription "$AZURE_SUBSCRIPTION_ID" --output json > prewrite.json
jq -S . before.json > before-sorted.json
jq -S . prewrite.json > prewrite-sorted.json
cmp before-sorted.json prewrite-sorted.json
# Proceed only after the comparisons and exact what-if review succeed.
az deployment group create --resource-group "$AZURE_RESOURCE_GROUP" \
  --subscription "$AZURE_SUBSCRIPTION_ID" --mode Incremental --template-file manual-checked.json
az resource show --ids "$job_id" --api-version 2025-01-01 \
  --subscription "$AZURE_SUBSCRIPTION_ID" --output json > after.json
```

Require actual Manual1/1/retry0/90s readback and unchanged template, environment, Consumption, identity and ownership. Failed/missing/mismatched readback leaves suspension UNCONFIRMED. Do not infer it from ARM success or invent a `job update --trigger-type` flag. The ordinary fixed Schedule apply reactivates runs.

Now inspect every pre-switch and subsequently observed outstanding execution by its actual name. The exact inspection/stop/replica commands are:

```sh
az containerapp job execution show --name "$AUTH_APP_NAME-cleanup" \
  --resource-group "$AZURE_RESOURCE_GROUP" --job-execution-name "$execution_name" \
  --subscription "$AZURE_SUBSCRIPTION_ID"
# Stop only a confirmed outstanding execution, repeat for each exact observed ID.
az containerapp job stop --name "$AUTH_APP_NAME-cleanup" \
  --resource-group "$AZURE_RESOURCE_GROUP" --job-execution-name "$execution_name" \
  --subscription "$AZURE_SUBSCRIPTION_ID"
az containerapp job replica list --name "$AUTH_APP_NAME-cleanup" \
  --resource-group "$AZURE_RESOURCE_GROUP" --execution "$execution_name" \
  --subscription "$AZURE_SUBSCRIPTION_ID"
```

Always specify `--execution`; omission selects latest, not all executions. The [replica command](https://learn.microsoft.com/en-us/cli/azure/containerapp/job/replica?view=azure-cli-latest) is a preview extension capability requiring CLI2.79.0 or later; verify the installed CLI/extension and actual response semantics. Within a bounded120-second observation, repeat execution listing, include delayed launches, require terminal execution states and zero actual replicas for each tracked execution. Unavailable/malformed/ambiguous observations, unresolved executions or timeout leave drain UNCONFIRMED and require inspection. An empty list alone is not an established overall fence. Stopping one execution never suspends future scheduling. No additional manual-start role or template override is authorized.

Before restoring storage, fence cleanup AND HTTP at actual zero replicas, all old writers/signing, and require positive settlement/publication proofs, a fresh external epoch and repository approval. Do not reset cursors/epochs to infer those proofs. Existing backups/key retention remain separately owned. This source procedure needs real suspension/drain/restore acceptance before relying on it.

## Cleanup acceptance remains open

Bind evidence to each actual execution's image/configuration, not merely the Job's current template. Prohibit manual execution overrides during acceptance. Prove actual cleanup identity Table read/write/delete and denial on other tables/Blob/keys/signing; reviewed runtime/transport scope and auth-reissue accounting; real cadence,90s timeout/failures/overlap and durable per-stream throughput at supported retained populations/churn. With HTTP confirmed at zero actual replicas for24h, use authentic due/near-due/behind-cursor entries and physical reads to prove purge within the explicitly selected deadline while preserving unknown authority/audit clocks. Successful exit or sampled oldest age alone is insufficient. The [capacity counterexample and pending retention choice](auth-infrastructure.md#independent-cleanup-job) prevent claiming full-capacity24h cleanup from this schedule.

Latest100 execution history covers only about100minutes at nominal minute cadence. Use the existing bounded logs for24h evidence and monitor their cap; missing/capped logs cannot mean success. Before production acceptance configure and test real alert delivery to an explicit operator destination: no summary5min, any failed/invariant execution or unknown deadline, backlog without cursor progress15min, cycle age warn6h/critical12h while validating the selected objective. Overdue age>0 requires inspection; null/incomplete samples are not healthy coverage. These are proposed live gates, not installed alert resources. Budget currency/amount/dates/recipients and log-cap monitoring remain required. Keep `AUTH_CLEANUP_READY=false` and #35/#28 open until the actual evidence exists.
