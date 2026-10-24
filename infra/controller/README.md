# StoryBeam controller on Azure

The current cost-first evaluation is the [three-resource SMB setup candidate](SMB.md).
Keep the exact working local Docker controller running during that evaluation.
The NFS foundation and activation instructions below describe the existing hosting
path; the SMB candidate is setup-only and has not passed live storage acceptance.

This foundation creates a dedicated VNet, network-restricted classic NFS 4.1 share, Consumption Container Apps environment and manual smoke job. The job uses the [accepted public controller image](https://github.com/repoagent/controller/actions/runs/39806177465) by immutable digest and runs its installed tools as UID 1000. There are no registry credentials, active controller, controller secrets or changes to the existing broker. Local StoryBeam keeps running.

Deploy to the selected subscription and dedicated resource group:

```sh
az account set --subscription 46f7e5d6-05f3-435b-8089-6c63a203dbfa
az bicep install --version v0.48.1
az bicep build --file infra/controller/main.bicep --outfile /tmp/repoagent-controller.json
az group create --name rg-storybeam-repoagent-controller --location swedencentral
az deployment group create --resource-group rg-storybeam-repoagent-controller \
  --name controller-foundation --template-file infra/controller/main.bicep
az resource list --resource-group rg-storybeam-repoagent-controller --output table
az containerapp job start --resource-group rg-storybeam-repoagent-controller \
  --name storybeam-repoagent-smoke
az containerapp job execution list --resource-group rg-storybeam-repoagent-controller \
  --name storybeam-repoagent-smoke --output table
```

Rerun the same incremental deployment to reuse the foundation. These commands target a dedicated resource group; do not use them to adopt unrelated resources. No deployment wrapper or simulated Azure validation is needed. `.github/workflows/ci-controller-infra.yml` compiles affected PRs and supports manual compilation.

## Storage, cost and lifecycle

The classic FileStorage account uses PremiumV2_LRS, a 32 GiB NFS share, 3,000 IOPS and 100 MiB/s. Microsoft documents the [SSD v2 included IOPS/throughput](https://learn.microsoft.com/en-us/azure/storage/files/file-estimate-cost) and [provisioning limits](https://learn.microsoft.com/en-us/azure/storage/files/understanding-billing). The Sweden Central USD retail storage meter on 2026-10-22 is $0.000196/GiB/hour: about $4.58/month at 730 hours. The share continues billing with no running jobs. There are no dedicated compute nodes, NAT gateway, private endpoint or Log Analytics workspace. Managed networking, egress and tax can add costs. A full 300-second smoke execution uses at most about $0.0023 compute before free grants. A future continuously active 0.5 CPU/1 GiB controller would add about $39.42/month before free grants; that runtime is not deployed here. Rates are estimates, not spending caps. See the [Azure retail prices API](https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices).

[Container Apps cannot mount encrypted NFS connections](https://learn.microsoft.com/en-us/azure/container-apps/storage-mounts). This dedicated account therefore disables the secure-transfer requirement and denies access outside its dedicated subnet. The subnet has a Storage service endpoint; NFS uses network access and Unix permissions, with root squash enabled. No account key or managed-identity SMB substitution is used. The NSG's normal VNet and outbound rules allow required storage ports; no controller app has public ingress in this slice.

Latest published AVM versions were checked in MCR on 2026-10-22: [virtual-network 0.10.2](https://github.com/Azure/bicep-registry-modules/tree/main/avm/res/network/virtual-network), [managed-environment 0.16.0](https://github.com/Azure/bicep-registry-modules/tree/main/avm/res/app/managed-environment). Native storage/job resources use the latest stable template APIs (2026-09-01 and 2026-07-01). Read those module documents and check current releases before changing versions.

Retain the foundation for the following storage and hosting work. Deleting the dedicated resource group deletes its share and data; stopping a job does not. Never delete persistent state as part of an update or rollback.

## Acceptance boundary

Inspect the actual Azure account firewall, share protocol/capacity, environment subnet and job image/resources, then run the real manual job. Its success establishes UID 1000 execution, installed tools and the mounted NFS filesystem only. [Issue #37](https://github.com/repoagent/controller/issues/37) still requires actual controller state/lock, private-file, Git/tool/session and crash recovery evidence. [Issue #34](https://github.com/repoagent/controller/issues/34) still requires positive settlement of every old capability before any cloud owner is enrolled; never copy the local enrollment. [Issue #38](https://github.com/repoagent/controller/issues/38) still owns active hosting and controlled updates/rollback. These gates remain open after foundation deployment.

## Prepare the hosted controller

After the foundation, deploy the **setup-only** app. This allocates no controller owner and does not contact GitHub or the broker:

```sh
az bicep build --file infra/controller/stage.bicep --outfile /tmp/repoagent-controller-stage.json
az deployment group create --resource-group rg-storybeam-repoagent-controller \
  --name controller-setup --template-file infra/controller/stage.bicep
az containerapp show --resource-group rg-storybeam-repoagent-controller \
  --name storybeam-repoagent-controller \
  --query '{state:properties.provisioningState,origin:properties.configuration.ingress.fqdn,image:properties.template.containers[0].image}'
```

The `publicOrigin` deployment output is the stable app HTTPS address. `/health/setup` returns fixed `setup-required` metadata; `/health/ready` returns 503. Other routes cannot read files or configure credentials. Platform revision readiness means the setup endpoint is available, not that a controller is running. HTTP activation allows 0..1 replicas, each .25 CPU/.5 GiB; idle can scale to zero. Full active allocation is about $19.71/month at the documented Sweden rates before grants, plus the existing storage/network costs. No continuous bot is enabled yet.

The non-root initializer creates private0700 `runtime/data` and `runtime/workspace` on the existing share, mounted at `/data` and `/workspace`. Only their parent `runtime` uses0711: the root-squashed platform mount helper needs traversal permission to mount subpaths. Other users cannot list that parent, read its0600 binding or traverse the private data/workspace directories. The binding, directories and native-session history link are retained on setup restart/redeployment. The binding records immutable repository/App/installation IDs and broker origin; reuse refuses a mismatch. Nonempty unbound storage, unsafe ownership/permissions/links and existing enrollment are refused rather than repaired or adopted. Interrupted initialization with unbound partial files requires inspection; do not delete retained data to retry. NFS access is restricted to the dedicated subnet; same-UID workloads on that share remain within the same trust boundary.

**Use this template only before enrollment.** It is not a controller update/rollback command. Reapplying setup to an enrolled app is refused by initialization and can make that revision unavailable. Active hosting must preflight configuration/sign-in and use supported owner settlement before enabling a fresh cloud enrollment. Never copy the working local enrollment or use an existing device concurrently. Real cloud inference, signed webhook delivery and controlled update/rollback remain #37/#34/#38 work.

The runtime script is filesystem/process/HTTP code embedded by Bicep, not an Azure deployment wrapper. All Azure settings stay declarative. All entrypoints share the accepted public image in `image.json`, with no private registry or credentials. Native Container Apps API `2026-07-01` follows the current [resource schema](https://learn.microsoft.com/en-us/azure/templates/microsoft.app/2026-07-01/containerapps), [init-container behavior](https://learn.microsoft.com/en-us/azure/container-apps/containers) and [storage subpaths](https://learn.microsoft.com/en-us/azure/container-apps/storage-mounts). The active origin combines the app name and environment DNS suffix; [CONTAINER_APP_HOSTNAME is revision-specific](https://learn.microsoft.com/en-us/azure/container-apps/environment-variables).

## First activation

Keep the local controller running during cloud preflight. Before opening the shell, keep one setup replica running for the device-authentication window and verify its setup endpoint:

```sh
setup_origin=$(az deployment group create --resource-group rg-storybeam-repoagent-controller \
  --name controller-setup --template-file infra/controller/stage.bicep \
  --parameters setupMinReplicas=1 --query properties.outputs.publicOrigin.value --output tsv)
curl --fail --silent --show-error --retry 6 --retry-all-errors --retry-delay 5 \
  --max-time 45 "$setup_origin/health/setup"
az containerapp exec --resource-group rg-storybeam-repoagent-controller \
  --name storybeam-repoagent-controller --command /bin/sh
# Inside that shell; complete the displayed account sign-in before continuing:
codex -c 'cli_auth_credentials_store="file"' login --device-auth
codex login status
exit
```

The setup endpoint returns 200; `/health/ready` intentionally returns 503. The explicit minimum keeps the shell available during authentication. After preflight, proceed to activation or, while still unenrolled, reapply setup with `setupMinReplicas=0` to allow idle scale-to-zero. This writes cloud credentials under `/data/credentials/codex`; never copy the local controller's enrollment. Check that the installed App grants `discussions:write`, `contents:read` and `repository_hooks:write`, and that the selected installation covers StoryBeam/StoryBeam. The webhook profile cannot fall back to polling.

Create a dedicated enrollment vault and identity. The explicitly selected signed-in operator gets Secrets Officer on this vault; the controller gets Secrets User there. Neither grant covers the auth-service vault, signing keys or Table. These use the latest stable [Key Vault API](https://learn.microsoft.com/en-us/azure/templates/microsoft.keyvault/vaults) (`2026-05-15`) and [managed identity API](https://learn.microsoft.com/en-us/azure/templates/microsoft.managedidentity/2024-11-30/userassignedidentities).

```sh
operator_id=$(az ad signed-in-user show --query id --output tsv)
az bicep build --file infra/controller/credentials.bicep --outfile /tmp/repoagent-controller-credentials.json
az deployment group create --resource-group rg-storybeam-repoagent-controller \
  --name controller-credentials --template-file infra/controller/credentials.bicep \
  --parameters operatorPrincipalId="$operator_id" --query properties.outputs
vault_name=$(az deployment group show --resource-group rg-storybeam-repoagent-controller \
  --name controller-credentials --query properties.outputs.vaultName.value --output tsv)
```

After preflight, stop the local controller with its existing instance selection (`repoagent.ps1 stop`). Use `scripts/revoke-approval.ps1` with its exact saved `-ApprovalStatePath`, `-EnrollmentFile` and `-Apply -NonInteractive` to withdraw the selected marker and close that device. Its result deliberately reports outstanding tokens as unsettled: closure alone does not release ownership. Observe positive broker cleanup/settlement and old-device denial before creating the cloud enrollment. Unknown capabilities remain blocked; never repair this with owner/counter/epoch resets.

Use a fresh host state directory for the cloud device. The normal host flow saves the private enrollment before acknowledging it; retry with the same directory if interrupted.

The example keeps the deployed Agent-variable approval explicit. Once the deployed broker serves `/v3` setup and the App installation has accepted **Variables: Read**, drop `-ApprovalLocation AgentVariable` and `-ApprovalVariable` to use the default Actions variable `REPOAGENT_APPROVAL` ([ADR 0007](../../docs/adr/0007-use-repository-actions-variables-for-approval.md)). Run this only after settlement:

```sh
cloud_state="$HOME/.repoagent/enrollment/storybeam-azure"
pwsh -NoProfile -File scripts/enroll-controller.ps1 \
  -Repository StoryBeam/StoryBeam \
  -BrokerOrigin https://repoagent-auth.livelydune-309701b4.swedencentral.azurecontainerapps.io \
  -ApprovalLocation AgentVariable -ApprovalVariable APPROVED_CONTROLLER -Profile webhook-clone \
  -StateDirectory "$cloud_state" -Apply -NonInteractive
secret_uri=$(az keyvault secret set --vault-name "$vault_name" \
  --name controller-enrollment --file "$cloud_state/enrollment.json" --encoding utf-8 \
  --query id --output tsv)
secret_version=${secret_uri##*/}
az bicep build --file infra/controller/runtime.bicep --outfile /tmp/repoagent-controller-runtime.json
az deployment group create --resource-group rg-storybeam-repoagent-controller \
  --name controller-active --template-file infra/controller/runtime.bicep \
  --parameters enrollmentVersion="$secret_version" --query properties.outputs
```

Secret bytes travel from a private file to Key Vault and from its pinned version through a mounted secret into the existing private import transport. They never appear in source, command arguments or deployment outputs. [Versioned Key Vault references](https://learn.microsoft.com/en-us/azure/container-apps/manage-secrets#key-vault-secret-uri-and-secret-rotation) avoid automatic selection of newer enrollment versions. Initial active allocation is one .25 CPU/.5 GiB replica, about $19.71/month before grants plus storage/vault/network costs. This is not a spending cap.

Startup validates the persistent binding and passes its numeric repository ID to the native enrollment consumer before any broker request. It configures the existing native runtime once, then starts clone mode at the stable HTTPS origin. Matching restarts retain saved settings, Codex sign-in/trust and native history. Conflicting settings fail before importing enrollment. Inspect the actual ready revision, native `ready` log, signed webhook receipt, canonical checkout, accepted agent turn and retained history after a controlled restart. The native webhook listener has no `/health/ready` route; TCP platform readiness alone does not prove those outcomes.

## Activation recovery

If upload or activation fails, retain the cloud host journal and every persistent file. Inspect the real deployment/revision and its logs, correct the cause and retry the same enrollment/version/settings. Never create a second device or restore the old approval to work around failure. A settings conflict requires inspection and the controlled configuration lifecycle; startup will not silently overwrite it. Do not reapply `stage.bicep` after enrollment, delete the share or copy local enrollment into cloud. `runtime.bicep` owns first activation.

## Subsequent controller image updates

Use PowerShell 7.4+, authenticated Azure CLI with the Container Apps commands installed, and a running Docker daemon. The host preflights the desired and rollback images with an empty registry-auth configuration. Select the same private host state directory for every update and recovery of this app:

```powershell
New-Item -ItemType Directory -Force "$HOME/.repoagent/azure" | Out-Null
$selection = @{
    SubscriptionId = '46f7e5d6-05f3-435b-8089-6c63a203dbfa'
    ResourceGroup = 'rg-storybeam-repoagent-controller'
    AppName = 'storybeam-lean-controller'
    Repository = 'StoryBeam/StoryBeam'
    RepositoryId = 1069791767
    StateDirectory = "$HOME/.repoagent/instances/storybeam-azure-lean-1/azure-update"
}
./scripts/update-azure-controller.ps1 @selection                    # preview
./scripts/update-azure-controller.ps1 @selection -Apply             # use image.json
./scripts/update-azure-controller.ps1 @selection -Action Status
./scripts/update-azure-controller.ps1 @selection -Action Rollback -Apply
```

Pass `-Image ghcr.io/repoagent/controller@sha256:...` to select another published controller digest. The command creates a revision from the validated retained app template and changes its main controller image, preserving explicit scaling intervals, the initializer image, startup settings, persistent mounts, managed identity, enrollment references and stable app origin. Initial selection requires the sole active revision to match both the latest and latest-ready app revision. Revision comparison ignores named scale-rule ordering and supplies omitted polling/cooldown values only from that anchored template or the journal-verified retained configuration; explicit differences still fail. The replacement uses a private JSON Merge Patch request file rather than copying Azure’s incomplete revision projection. It recognizes the original NFS/versioned-Key-Vault profile and the explicit [SMB conversation profile](SMB.md). Bicep remains the source for initial resources/settings; changing those settings needs a separately reviewed deployment rather than an image update.

Preview runs the same source, recovery and anonymous-image checks as Apply, without changing the journal or Azure resources. It can download image layers. `Status` reports inventory without those action checks. A subsequent update must start from the exact revision and image recorded by the completed update or rollback; unexpected revisions require inspection.

The SMB profile must retain its `nativePersistence=conversation-rollouts` tag,
private/executable views, locking options and fixed local SQLite setting. Only
its dedicated native `controller-enrollment` app secret is supported. Because a
native secret name does not pin its value, each protected configuration read
retrieves that value through the app's `listSecrets` action and fingerprints it
in memory. The operator needs that existing app-secret read authority, including
for `Status`; no broader role is assigned. No raw value enters command arguments,
console output or the update journal. `Status` exposes the configuration hash for
inspection. The NFS path still requires versioned managed-identity Key Vault
references and never reads their values.

These are sampled readbacks: a detected change invalidates journal continuity,
but they do not provide an atomic lock against another Azure administrator or
prove that a value never changed between reads. Serialize operator changes and
retain the private journal on uncertainty. Secret rotation, storage migration
and persistence-policy changes require their own lifecycle operation.
[Microsoft list-secrets API](https://learn.microsoft.com/en-us/rest/api/resource-manager/containerapps/container-apps/list-secrets?view=rest-resource-manager-containerapps-2026-07-01).

Updates deliberately interrupt service. The command saves a private atomic journal, takes a host process lock, selects multiple-revision control, deactivates the previous revision and checks actual replicas before starting the replacement. It routes traffic after platform readiness and records the transition. Verify an agent turn and signed webhook afterward. Keep other Azure resource writers fenced; the host lock does not serialize other machines or portal changes.

An interrupted or failed update retains its journal and requires explicit `Rollback`. Rollback checks the recorded configuration, stops any recorded candidate, confirms its replicas are gone and reactivates the previous exact revision. Unexpected drift requires inspection; use the exact journal revision names when inspecting Azure and do not delete private state to retry. Data, history and enrollment are preserved. The same logical device continues to own its issued tokens; image replacement is not token revocation or owner reassignment. A different device still requires the supported broker settlement and fresh-enrollment flow.

After a launch may have been requested, recovery requires the recorded candidate to be visible with a terminal provisioning outcome. A missing revision or unfinished Azure operation keeps recovery blocked; inspect the actual Azure operation before retrying. A failure recorded before launch does not require a candidate that was never requested. The selected source and recovery revisions must match the retained configuration before any change.

Real setup-only update/recovery evidence covers platform mechanics. Active native/webhook/history acceptance remains tracked in [#138](https://github.com/repoagent/controller/issues/138) and [#38](https://github.com/repoagent/controller/issues/38).
