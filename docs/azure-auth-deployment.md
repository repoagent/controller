# Azure authentication-service deployment

The maintainer-operated [credential broker](../GLOSSARY.md) will run as an Azure Container App. Its deployment resources belong in one resource group, `rg-repoagent-controller-auth`, in Sweden Central (`swedencentral`). User-hosted controllers have separate deployment requirements; see the [controller deployment research](research/2026-10-20-azure-controller-deployment.md).

## Current state

Verified on 2026-10-21: the foundation is provisioned through the reviewed `main.bicep`: environment `repoagent-auth-environment`, log workspace `repoagent-auth-logs`, runtime identity `repoagent-auth-runtime`, storage account `repoagentauth` and vault `repoagent-auth-kv`. Signing and wrapping keys and narrowly scoped runtime grants are provisioned. The deployment identity `id-controller-auth-github` and its GitHub OIDC trust remain unchanged.

The public auth image, HTTPS Container App and minute-scheduled cleanup Job are deployed. On 2026-10-21, WSL requests to both public `/health/live` and `/health/ready` returned HTTP 200 at `https://repoagent-auth.livelydune-309701b4.swedencentral.azurecontainerapps.io`. Monitoring, the budget and verified operator email are configured; a native notification test succeeded. Full enrollment, controller handover and hosted lifecycle acceptance remain tracked in #35/#28. Prior resource retirement is tracked separately. Storage, signing and hosted acceptance requirements remain in the [operations research](research/2026-10-20-azure-auth-service-operations.md) and [existing security ADR](adr/0001-keep-the-shared-app-key-out-of-controller-installations.md).

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

[`broker.yml`](../.github/workflows/broker.yml), **Broker**, verifies service and infrastructure changes on pull requests and `main`. [Broker releases](broker-releases.md) use independent semantic versions proposed by Release Please. Ordinary source pushes do not rebuild or redeploy an already released version. After an approved release exists, the workflow verifies its exact source, publishes and anonymously verifies one immutable public image, confirms its version-to-digest release mapping, and applies Bicep to deploy **both the HTTP API and scheduled cleanup Job from that digest**. It checks both deployed images, the ready API revision, and public live/ready HTTP responses. Pull requests verify without publishing or deploying. Hosted lifecycle acceptance stays tracked in issues; manual `AUTH_*_READY` assertions do not block normal deployment. Publication and deployment run only from the `main` workflow; deployment keeps the existing branch OIDC identity with `contents: read` and `id-token: write`. The separate release-metadata writer has no Azure identity or registry-write permission. No GitHub environment is introduced.

For infrastructure changes, read the current resource/module documentation, use the latest released registry module versions, and compile locally before pushing. These templates use direct resource declarations. GitHub jobs select the installed Bicep v0.48.1 executable explicitly. Verify infrastructure by deploying to Azure and inspecting the actual resources. Before an environment becomes mission critical, establish a separate staging or test environment in a real Azure region.

Resource settings and references live in Bicep. `main.bicep` includes the local cleanup module for public deployment; staged and foundation deployments retain their existing scope. `deploy.sh` compiles and applies the selected template, then inspects Azure and checks HTTP health for a public deployment. The image publication helper handles Docker/OCI operations only; there is no separate imperative Python Azure deployer.

Manual `operation=access-check` retains the original outputs-only [`verify-access.bicep`](../infra/auth/verify-access.bicep) check. It verifies the resource-group location and records an incremental deployment named `github-oidc-<run-id>-<attempt>`. Its success proves OIDC login and deployment access, not service provisioning or broker acceptance. Manual `operation=deploy` retries the current approved source release; `operation=release -f release-tag=broker-v1.0.0` selects an exact approved release for verified publication and deployment. `operation=deploy-release` resolves an existing version mapping without rebuilding. Historical digest-only `operation=deploy-image` retains the same paired Bicep path. All broker delivery operations share noncanceling concurrency; superseded PR verification runs are cancelled. See the [versioned rollback procedure](broker-releases.md#deploy-or-roll-back-deliberately).

### Read-only operational diagnostics

Maintainers can inspect the existing deployment through the main workflow's existing resource-group identity, even when their local Azure sign-in has no access to that subscription:

```sh
gh workflow run broker.yml --repo repoagent/controller --ref main \
  -f operation=diagnostics -f expected-source-sha=EXACT_ACCEPTED_MAIN_SHA
```

The operation refuses a different repository, source, subscription, tenant, resource group or application. It makes only fixed read requests: the API and cleanup Job configuration, newest 20 executions from one returned page and replicas for the newest three, up to ten matching cleanup-failure alerts from the last day, and a fixed 24-hour aggregate of allowlisted cleanup/rejection metrics. It never follows provider pagination links. Each command has a 30-second deadline and a 2 MiB response limit. Only validated image identities, configuration bounds, statuses, timestamps, counts and fixed metric/reason labels reach the seven-day `broker-diagnostics-RUN-ATTEMPT` artifact. Provider environment, command arguments, logs, recipients, secrets and raw error bodies are discarded. The report records unavailable or malformed reads explicitly; missing telemetry never means zero failures or healthy cleanup. A partial observation exits unsuccessfully while preserving the report.

This does not read Table rows or use account keys, change resources/RBAC/signing access, start or stop executions, revoke credentials or prove capability settlement. The existing OIDC trust and role remain unchanged. Metrics can remain unavailable when that identity lacks query permission; the operation does not grant itself access or use another credential. A bounded execution list and selected replica snapshots are not a complete history, worker drain fence, notification-delivery proof or restore rehearsal. Use the evidence to investigate #283/#269, then perform the separately scoped settlement or restore procedure with its required proofs. Do not infer replacement readiness from these observations.

API references: [stable job execution history schema](https://github.com/Azure/azure-rest-api-specs/blob/7aa1ea88a08c6b12cabd943ad441fcebc26988a4/specification/app/resource-manager/Microsoft.App/ContainerApps/stable/2025-01-01/Jobs.json), [the CLI's separate replica preview request](https://github.com/Azure/azure-cli-extensions/blob/c33b530c0157710035927513abf8675624e72e56/src/containerapp/azext_containerapp/_clients.py#L103), [alert filtering](https://learn.microsoft.com/en-us/rest/api/alerts-management/alerts/alerts/get-all), [AppMetrics columns](https://learn.microsoft.com/en-us/azure/azure-monitor/reference/tables/appmetrics), and [Logs query format](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/api/request-format). Replica collection explicitly uses `2023-11-02-preview`, matching that CLI source, and projects only count/readiness/restarts/running state. It does not invent an execution exit code from a different API shape.

For a separately authorized access check:

```sh
gh workflow run broker.yml --repo repoagent/controller --ref main -f operation=access-check
gh run list --repo repoagent/controller --workflow broker.yml --limit 5
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

[ADR 0007](adr/0007-use-repository-actions-variables-for-approval.md) selects direct repository Actions variables in the controlled repository for controller approval. It supersedes the Agent-variable location of [ADR 0006](adr/0006-use-repository-agent-variables-for-approval.md). [#184](https://github.com/repoagent/controller/issues/184) implements the [v3 setup/marker source contract](../service/auth/README.md#repository-actions-variable-enrollment-v3). Existing [v2 Agent-variable](../service/auth/README.md#repository-agent-variable-enrollment-v2) and legacy Environment bindings keep their kinds. Frozen profiles keep their controller files and runtime routes (v1, or v2 for clone profiles); only the repository-settings profiles described below add runtime v3. Azure, live App-installation and native Windows acceptance remain release gates. The flow requires no GitHub Environment creation, name, numeric identity or deployment settings. Azure Container Apps environments remain a separate infrastructure concept. The deployment variables in the configuration table above are Actions variables of this repository. They are unrelated to a controlled repository's approval variable (default `REPOAGENT_APPROVAL`), and deployment tooling must never write that name.

The host needs GitHub-authorized write access to the selected Actions variable. The broker's internal verifier receives `actions_variables: read`, subject to live installation-scope proof. Runtime controller profiles never get variables write access. Only the explicit repository-settings profiles ([ADR 0008](adr/0008-add-an-explicit-repository-settings-runtime-profile.md)) add `actions_variables: read` to their runtime tokens. Operator actions before the installer default reaches operators:

1. Add Repository permission **Variables: Read** to the shared App, and have each installation accept it.
2. Deploy a broker build that serves the `/v3` setup routes. An older broker answers `/v3` with 404, and installers then fail before writing any approval.
3. Keep `-ApprovalLocation AgentVariable` available as the explicit rollback.

Repository-settings profiles reuse the same App permission. Their order of operations is:

1. Confirm that **Variables: Read** is on the shared App and accepted by the target installation. Settings runtime mints request `actions_variables: read`, and GitHub refuses them otherwise.
2. Deploy a broker build that serves runtime v3 (`POST /v3/devices/{id}/installation-token` and `DELETE /v3/devices/{id}`) before operators use `-RepositorySettings` or a `*-settings` profile. An older broker refuses settings profiles at setup with `400` and answers the v3 runtime routes with `404`, so nothing is enrolled with a scope it cannot serve.
3. Deploy the matching controller image. An older controller rejects a v3 enrollment file before contacting the broker.
4. Re-enroll each controller that should read settings: revoke, wait for settlement, and enroll a new instance with the settings profile. Existing grants are never widened.

To roll back, re-enroll with a frozen profile. Rolling the broker back below runtime v3 makes controllers with a repository-settings profile fail closed until they are re-enrolled. See [migration and rollback](repository-settings.md#broker-enrollments).

This change modifies no deployment resources, workflow, OIDC trust or live variable; hosted acceptance remains required. See [the approval contract](auth-repository-approval.md).

## First activation through existing OIDC

These are source mechanics, not hosted evidence. Source merge authorizes none of the live commands below. Obtain the specific operator authorization for publication, resource apply, key import, grants and variable changes. Keep #35/#28 open until actual hosted gates pass. Preserve nonsecret source/digest/app/revision/replica/origin, workflow-run and acceptance-evidence references; never private requests/responses or tokens in logs/artifacts.

The workflow has one main-only activation route for foundation, no-ingress runtime, cleanup and staging revisions. Each operation checks `expected-source-sha` against checkout, `GITHUB_SHA` and current remote main at operation start. Runtime and Job deployments additionally attest the immutable GHCR image/source anonymously. These choices exclude normal publication/deploy jobs. Branch OIDC, Contributor scope and shared concurrency remain unchanged.

The activation job has a 40-minute timeout. Azure CLI runs the actual Incremental deployment and reports its state, Bicep outputs and resource inventory. ARM can continue after a client disconnect or timeout; inspect the deployment in Azure before retrying.

1. Retain the stable app, storage, vault and Table names, external restore epoch and distinct signing/wrapping keys. Supply them through repository variables or local environment variables. `foundation-check` checks required target inputs locally. Inspect existing resources before deployment; changing names does not migrate data.
2. Plan/apply foundation through the working OIDC route:

   ```sh
   gh workflow run broker.yml --repo repoagent/controller --ref main \
     -f operation=foundation-plan -f expected-source-sha="$accepted_source"
   # Inspect the plan/run output before separately authorized apply.
   gh workflow run broker.yml --repo repoagent/controller --ref main \
     -f operation=foundation-apply -f expected-source-sha="$accepted_source"
   ```

   Foundation uses `main.bicep` with `deployApp=false`, requiring no image or hosted flags. Inspect the actual environment, workspace, identity, storage and vault with Azure CLI or the portal. Capture Bicep output values and resource IDs.
3. With separate key-administration authority, securely import the real GitHub App signing key and create a distinct wrapping key. With separate role authority, review/apply `runtime-rbac.bicep` at the exact key, Blob-container and Table scopes in [the infrastructure contract](auth-infrastructure.md#operator-only-permissions). Record only pinned version URIs. Never place PEM/key material in Bicep, argv or logs. Deployment Contributor cannot perform this role setup; do not broaden it.
4. Stop/fence every broker writer. Initialize only a genuinely fresh dedicated Table with the two complete **Int64** quota entities in [retained-state.md](../service/auth/retained-state.md), verifying actual stored types/values. Existing unaccounted data requires an offline charged inventory; never seed it as zero.
5. For an authorized first publication, dispatch the accepted current-main commit using the isolated Actions job token:

   ```sh
   gh workflow run broker.yml --repo repoagent/controller --ref main \
     -f operation=bootstrap-publish -f expected-source-sha="$accepted_source"
   ```

   The job has only contents-read/packages-write permissions, no Azure login or deployment dependency, and does not require hosted acceptance assertions. It binds the full source SHA to checkout/workflow/current upstream main before verification and immediately before push. A final main check and registry push are not atomic. Registry credentials use stdin and private temporary storage removed on exit. No personal token is needed.

   The helper archives the entire actual commit into temporary storage, runs source/service verification there and builds only its `service/auth` context. Dirty/untracked local source is excluded. It checks role/nonroot/Linux-amd64/revision, pushes only the fixed repository and records the actual source/digest. It never calls Azure. Retain the Actions run URL/attempt and flushed `publicationComplete=false` receipt even when the job fails: a private first push is already a registry mutation, and an uncertain push/digest-resolution failure does not establish absence of a mutation.

   Inspect the exact package's repository linkage, visibility and repository Actions access; configure public visibility/access with operator authority as needed. Do not infer public access from the source repository. No helper changes these settings. This first-publication job deliberately leaves `AUTH_PACKAGE_ACTIONS_ACCESS_CONFIRMED` empty and remains incomplete even after anonymous attestation succeeds. The local `bootstrap-publish` helper remains available with separately authorized registry credentials; it reports complete only with the operator's explicit access assertion. Resume by anonymously attesting the recorded digest/source without login, rebuilding or repushing:

   ```sh
   AUTH_IMAGE_REPOSITORY=ghcr.io/repoagent/controller-auth AUTH_IMAGE="$accepted_image" \
     AUTH_EXPECTED_SOURCE_SHA="$accepted_source" python3 scripts/delivery/auth-pipeline.py attest
   ```

   After resumed attestation and separate Actions-access verification, append both nonsecret evidence references to the original receipt. Do not rerun bootstrap merely to change its receipt status.

6. Supply real nonsecret runtime configuration using the existing Actions variables (pinned distinct keys, epoch, stable app/Table configuration). For optional isolated acceptance, review stage-plan and apply stage-apply:

   ```sh
   gh workflow run broker.yml --repo repoagent/controller --ref main \
     -f operation=stage-plan -f expected-source-sha="$accepted_source" -f image="$accepted_image"
   gh workflow run broker.yml --repo repoagent/controller --ref main \
     -f operation=stage-apply -f expected-source-sha="$accepted_source" -f image="$accepted_image"
   ```

   Inspect the target before applying staging configuration. After deployment confirm absent ingress, the selected immutable image, runtime identity, canonical origin and min/max 1/1. Perform application and data-plane acceptance against that deployed service. Staging does not establish hosted acceptance.
7. Use the selected-console procedure below for actual runtime/provider/private-repository, storage/recovery and denial evidence. Verify independent cleanup #72 with HTTP stopped/zero replicas; a local suite/readiness 200 does not prove that. Stop the isolated replica while paused.
8. To expose the optional staged deployment, promote the *same digest*. This deploys the API and cleanup together; deployment health does not establish the remaining lifecycle evidence:

   ```sh
   gh workflow run broker.yml --repo repoagent/controller --ref main \
     -f operation=promote-staged -f expected-source-sha="$accepted_source" -f image="$accepted_image"
   ```

   Promotion requires the current owned no-ingress staging app with that artifact. It uses guarded full apply and public rollout/origin checks, restoring HTTPS/min 0/max 1 without rebuild. A newly built CI digest is a subsequent release with its own acceptance. Prove public TLS/cold start, bounded enrollment/recovery/ack, issuance/renewal, withdrawal/revocation/token settlement, rollback and selected-private-repository binding. Native Windows/Docker Desktop remains separately owned acceptance.

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

Close acceptance/debug sessions. Both operations require the accepted source and recorded revision. Inspect that revision's digest before operating:

```sh
gh workflow run broker.yml --repo repoagent/controller --ref main \
  -f operation=stage-stop -f expected-source-sha="$accepted_source" \
  -f image="$accepted_image" -f revision="$staging_revision"
# Resume only when continuing acceptance:
gh workflow run broker.yml --repo repoagent/controller --ref main \
  -f operation=stage-resume -f expected-source-sha="$accepted_source" \
  -f image="$accepted_image" -f revision="$staging_revision"
```

The script requires no ingress and a revision belonging to the selected app, deactivates that revision and lists its replicas. Inspect every app revision and actual replicas in Azure to confirm zero compute. Command success alone does not prove all replicas stopped.

Resume activates the selected revision and lists replicas. Inspect readiness and use its container's local live/ready endpoints before continuing acceptance. Re-establish runtime identity context before data-plane checks. If main advanced, select the new accepted source explicitly.

Configured 1/1 is a per-revision target, not a global hard spend cap; maintenance/rollout can overlap replicas. Storage, logs and vault charges continue after confirmed zero compute replicas.

## Bootstrap history

An initial West Europe identity creation was rejected by Azure with `RequestDisallowedByAzure` because the region was not accepting new customers for this subscription. The final group and identity were created in Sweden Central. The superseded `rg-controller-auth` group and its temporary identity were deleted; the repository variables point to the final identity and group above. This records the observed failure, not a permanent statement about West Europe eligibility.

## Cleanup identity bootstrap and scheduling

These source routes preserve the exact main-only OIDC subject, Contributor scope, shared noncanceling delivery concurrency and the normal service verification and anonymous image checks. They exclude ordinary build/publication/deploy jobs. Source merge is not permission for live operations below. Use the accepted current-main source and the same anonymously attested public auth image; no rebuild or registry-secret handling is needed.

1. With the owned foundation already present, inspect `cleanup-identity-plan`, then separately authorize `cleanup-identity-apply` using the existing workflow:

   ```sh
   gh workflow run broker.yml --repo repoagent/controller --ref main \
     -f operation=cleanup-identity-plan -f expected-source-sha="$accepted_source"
   # Only after reviewing this plan and authorizing the identity write:
   gh workflow run broker.yml --repo repoagent/controller --ref main \
     -f operation=cleanup-identity-apply -f expected-source-sha="$accepted_source"
   ```

2. Retain the actual `cleanupIdentityId`, client/principal IDs and exact Table resource ID. A separate authorized operator reviews/applies `infra/auth/cleanup-rbac.bicep` with the reported app/storage/Table parameters, checks exact scope/principal and role propagation. The workflow cannot grant roles. Establish access before starting the schedule; missing evidence is an unresolved operator checkpoint, not a new readiness variable or permission fallback.
3. Keep the external epoch and existing app/Table names stable. Inspect `cleanup-plan`, then separately authorize `cleanup-apply`:

   ```sh
   gh workflow run broker.yml --repo repoagent/controller --ref main \
     -f operation=cleanup-plan -f expected-source-sha="$accepted_source" -f image="$accepted_image"
   # Apply immediately activates minute scheduling; review that cost and access first.
   gh workflow run broker.yml --repo repoagent/controller --ref main \
     -f operation=cleanup-apply -f expected-source-sha="$accepted_source" -f image="$accepted_image"
   ```

A cleanup-only plan/apply must select the current API image; it refuses a different release before a deployment write. To upgrade or roll back a release, use paired `operation=deploy` or `operation=deploy-image`, which deploy both resources. Cleanup apply also reads both images back before succeeding. Azure portal or direct CLI edits bypass these workflow checks and can still introduce drift.

After deployment inspect the actual Job: Consumption environment, immutable image, six environment bindings, cleanup-only identity, command, schedule and capacity must match Bicep. A Job can already be running after apply or a client disconnect. Restoring a Manual Job to its Bicep schedule does not prove older executions drained.

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

In an isolated rehearsal, prove a consistent current-epoch enrollment and credential authorize through the broker before backup. After restore under a fresh external epoch, the same well-formed old grant must answer401 to runtime issuance, without provider work or storage writes. Corrupt or incomplete stored bindings still answer503. Preserve and inspect a pre-backup revocation separately so epoch denial alone is not mistaken for revocation evidence. An issuance401 does not prove old GitHub capabilities were settled, permit owner reassignment or replace the worker/signing/publication fences above. These are source expectations; the isolated live restore rehearsal and deliberate cleanup-failure execution proof remain open under #230.

## Observed cleanup alert delivery

On 2026-10-28, the operator mailbox received both Fired and Resolved notifications for `repoagent-auth-cleanup-execution-failed`, alert ID `acdb87f2-5f5e-41f7-b568-848ebab9f000`. The condition fired at 13:40:05 UTC with `Executions`, `state=Failed`, value 1, and resolved at 16:06:54 UTC with value 0. Both emails passed the mailbox-recorded Microsoft SPF, DKIM and DMARC checks. See the [evidence recorded on #230](https://github.com/repoagent/controller/issues/230#issuecomment-6474823348).

This proves delivery and resolution of an observed production alert. The failed execution, its cause and a corresponding successful execution have not been identified; it is not evidence of a deliberate isolated rehearsal. No production cleanup failure or restore was initiated for this observation. The isolated restore, retained-revocation proof and execution-level alert rehearsal remain open.

## Cleanup acceptance remains open

Bind evidence to each actual execution's image/configuration, not merely the Job's current template. Prohibit manual execution overrides during acceptance. Prove actual cleanup identity Table read/write/delete and denial on other tables/Blob/keys/signing; reviewed runtime/transport scope and auth-reissue accounting; real cadence,90s timeout/failures/overlap and durable per-stream throughput at supported retained populations/churn. With HTTP confirmed at zero actual replicas for24h, use authentic due/near-due/behind-cursor entries and physical reads to prove 24-hour eligibility followed by successful bounded cleanup visits, preserving original clocks, immediate expiry/authorization denial and unknown authority/audit evidence. Successful exit or sampled oldest age alone is insufficient. The [selected eligibility policy and capacity counterexample](auth-infrastructure.md#independent-cleanup-job) promise no maximum physical-deletion lag; monitor failures/backlog and require operator follow-up.

Latest100 execution history covers only about100minutes at nominal minute cadence. Use the bounded aggregate monitoring below and separate operator evidence for24h acceptance; persisted console summaries are disabled and missing/capped telemetry cannot mean success. Before production acceptance configure and test real alert delivery to an explicit operator destination: no summary5min, any failed/invariant execution or unknown deadline, backlog without cursor progress15min, cycle age warn6h/critical12h while validating the selected objective. Overdue age>0 requires inspection; null/incomplete samples are not healthy coverage. Inspect the installed five monitoring rules and retain separate evidence for any lifecycle behavior they cannot establish. Budget currency/amount/dates/recipients and log-cap monitoring remain required. Keep #35/#28 open until the actual lifecycle evidence exists; ordinary code deployment continues independently.


## Low-volume monitoring deployment

The CLI entrypoint supports `monitoring-plan` and `monitoring-apply` using the accepted current-main checkout and existing target variables. No OIDC, role or readiness setting is added. Infrastructure deployment does not establish hosted application acceptance.

Deploy foundation with 30-day retention, cap0.1GB/day and disabled console persistence (null destination and Log Analytics configuration). Then deploy Insights/action group with alerts disabled. Apply `monitoring-rbac.bicep` separately with role authority for Metrics Publisher on Insights, and inspect the actual assignments. Cleanup identity bootstrap remains independent of monitoring.

Supply `AUTH_MONITORING_PARAMETERS_FILE` as a private0600 ARM parameters JSON file. Each input uses `parameters.<name>.value`: `recipient`, `budgetAmount` (0 omits budget), `budgetStartDate` (first-of-month YYYY-MM-DD), and optionally `enableAlerts` and `jobFailureState`. Pass the file by reference; keep the actual recipient out of source, argv and logs.

Use `infra/auth/deploy.sh monitoring-plan` for Azure's change summary or `monitoring-apply` for the actual deployment. Bicep declares Insights, a private action group, optional monthly budget and five optional alert rules. Inspect the deployed configuration in Azure afterward.

Runtime and cleanup Bicep reference the existing Insights connection directly. Both receive `APPLICATIONINSIGHTS_CONNECTION_STRING` and `APPLICATIONINSIGHTS_STATSBEAT_DISABLED=true`; cleanup has six variables and no signing-secret references. Inspect AAD-only ingestion, the workspace binding and both publisher assignments in Azure.

After the app/Job exist, set the private parameters `enableAlerts=true` and `jobFailureState` to the observed native failure-state value, then deploy again. The five rules cover Job failures, HTTP5xx, memory pressure, cleanup observations and telemetry volume. Verify actual metrics/KQL, dimensions, notification delivery/resolution and scale-to-zero in Azure. Six-hour cleanup cycle observations trigger investigation; twelve hours warrants urgent follow-up.

The user's nominal budget is20USD/month; verified subscription billing currency is EUR and root selected18EUR/month (about20.19USD at the dated2026-10-20 reference). Source accepts a generic amount and no currency/FX parameter. Monthly Actual50/80/100% notifications use Azure's ten-year default when endDate is omitted. The 18EUR/month budget was applied on 2026-10-21. Budget alerts, workspace caps and replica limits notify or constrain particular resources; they do not guarantee total spending or stop all charges. Console persistence is disabled; sensitive original cleanup JSON is not a cloud log-retention mechanism.
