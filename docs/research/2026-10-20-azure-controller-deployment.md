# Azure controller deployment research

Researched 2026-10-20 against Microsoft documentation. This is an installer design proposal, not a verified deployment. This research did not create cloud resources, read credentials or execute deployment commands. Existing controller requirements below were supplied by the coordinating task; this note does not independently review runtime implementation.

This research concerns user-hosted controllers. The separately maintained authentication service has its own [Azure deployment runbook](../azure-auth-deployment.md), including the provisioned deployment identity and verified GitHub OIDC/Bicep access. That bootstrap does not verify the controller deployment proposed here.

## Decision and release gates

Azure Container Apps (ACA) is plausible for the composed controller image, but **ordinary single-revision rollout is unsafe for the stated singleton runtime**. Azure starts the replacement and waits for readiness before deactivating the old revision. A file-lock-dependent new controller can either fail readiness indefinitely or run concurrently if locking is ineffective. Traffic weights do not fence background processing. Use deliberate downtime with controlled revision deactivation, and retain an independently validated exclusive-owner mechanism. [Revision lifecycle](https://learn.microsoft.com/en-us/azure/container-apps/revisions).

Do not declare the Azure installer production-ready until an actual ACA storage test proves UID 1000 access, credential permissions, exclusive FileStream behavior across replicas, JSON replacement/recovery, and Codex/workspace/tool compatibility. Azure Files support alone does not establish those properties. These are proposed acceptance gates, not Microsoft guarantees.

## Image and ingress contract

Build a Linux AMD64 image containing the NativeAOT controller executable, the selected agent CLI (currently the Codex protocol adapter), `gh`, and selected development tools. ACA supports Linux x86-64 images, public/private registries, and no privileged host access. Consumption limits total images per replica to 8 GB. Therefore reject a local-only image for Azure deployment until it has been published, and reject tool selections that require privileged containers or a host Docker socket. [Container requirements](https://learn.microsoft.com/en-us/azure/container-apps/containers).

Proposal: run as UID/GID 1000, listen on `0.0.0.0:8080`, explicitly configure external HTTP ingress with target port 8080 and `allowInsecure: false`. TLS terminates at Azure ingress; the public HTTPS endpoint uses 443 and TLS 1.2/1.3. Default HTTP behavior redirects to HTTPS. A public ACA environment and suitable ingress policy are required for GitHub delivery. Keep webhook signature authentication in the application. [Ingress behavior](https://learn.microsoft.com/en-us/azure/container-apps/ingress-overview), [ingress configuration](https://learn.microsoft.com/en-us/azure/container-apps/ingress-how-to).

Discover the actual app FQDN rather than fabricate it:

```bash
az containerapp show --subscription "$subscription" -g "$group" -n "$app" \
  --query properties.configuration.ingress.fqdn -o tsv
```

Build the controller's public webhook URL from `https://` plus this FQDN and its configured webhook path. [Documented discovery command](https://learn.microsoft.com/en-us/azure/container-apps/troubleshoot-deployment-errors).

Proposal: avoid a bootstrapping revision solely to set the URL by teaching the runtime to derive its default app hostname from `CONTAINER_APP_NAME.CONTAINER_APP_ENV_DNS_SUFFIX`, with an explicit public URL override taking precedence. The installer still retrieves and verifies the published FQDN. Do **not** use `CONTAINER_APP_HOSTNAME`, which is revision-specific. If this runtime behavior is not added, first launch in a setup-only mode that cannot acquire work or register a webhook, discover FQDN, then perform the controlled update. [Built-in environment variables](https://learn.microsoft.com/en-us/azure/container-apps/environment-variables).

## Guided and argument-driven Azure flow

Use the same Azure CLI calls from Bash and Windows PowerShell; do not require Az PowerShell modules as an additional dependency. Discover installed `az`; offer Azure only after prerequisites are satisfied. Read subscriptions with `az account list`; require an unambiguous selected ID, and pass `--subscription` on subsequent commands rather than silently changing global defaults. [Account commands](https://learn.microsoft.com/en-us/cli/azure/account?view=azure-cli-latest).

Proposal: arguments and prompts produce one saved, nonsecret deployment plan: subscription, location, resource group, environment resource ID, app name, repository/App identity, registry/digest, volume mappings, secret-reference URIs, and tool versions. Read existing resources before creating anything; distinguish a real not-found response from permission/network failures. Reuse only compatible resources. Check ownership tags and repository/App binding before updating an existing app; fail on ambiguous ownership. Serialize concurrent installers. Preserve unrelated settings and existing data; no destructive recreate fallback.

Example creation primitive, only when environment absence is established:

```bash
az containerapp env create --subscription "$subscription" \
  --name "$environment" --resource-group "$group" --location "$location"
```

For reuse, inspect the selected environment's region, network exposure, workload profiles and storage definitions. NFS may require a different network plan. [Environment CLI](https://learn.microsoft.com/en-us/cli/azure/containerapp/env?view=azure-cli-latest).

Render reviewed YAML for app creation/update, including mounts, references, probes and explicit scale. Set `minReplicas: 1` and `maxReplicas: 1`; scaling is revision-scoped and these settings do not establish ownership across revisions or other apps. Never present max=1 as a distributed lock. [Scaling](https://learn.microsoft.com/en-us/azure/container-apps/scale-app).

## Stop-before-start updates and rollback

Proposal, intentionally allowing downtime:

1. Preflight new image digest, pull authorization, secrets, mount compatibility and rollback snapshot. Validate the repository/App ownership binding. Quiesce the controller and wait for its work/checkpoint policy before shutdown.
2. Set revision mode to `multiple` without modifying the template. Enumerate every active revision; deactivate all of them. Microsoft documents that deactivation stops all replicas, and changing revision mode does not create a revision. [Revision management](https://learn.microsoft.com/en-us/azure/container-apps/revisions-manage).
3. Poll each old revision and its replica list until inactive with no replicas; timeout is a failure, never permission to proceed. Control-plane observations are necessary evidence, not a substitute for storage fencing. [Replica-list API](https://learn.microsoft.com/en-us/rest/api/resource-manager/containerapps/container-apps-revision-replicas/list-replicas?view=rest-resource-manager-containerapps-2025-01-01).
4. Apply the desired new template, using a unique revision suffix and immutable image digest. Wait for the new controller to acquire exclusive ownership, load state, and become ready; route 100% ingress to that revision. Assert exactly one active revision and the expected digest, volumes, FQDN and repository binding.
5. On failure, deactivate the failed revision and prove it stopped before reactivating the prior revision. Never roll back image alone across an incompatible state-schema migration. Retain data and diagnostic evidence.

Illustrative primitives (the installer must implement the polling/error handling):

```bash
az containerapp revision set-mode -g "$group" -n "$app" --mode multiple
az containerapp revision list -g "$group" -n "$app" -o json
az containerapp revision deactivate -g "$group" -n "$app" --revision "$oldRevision"
az containerapp replica list -g "$group" -n "$app" --revision "$oldRevision" -o json
az containerapp update -g "$group" -n "$app" --yaml desired-app.yaml
az containerapp ingress traffic set -g "$group" -n "$app" --revision-weight "$newRevision=100"
```

Each real command also receives the selected subscription. These are documented command shapes, not an executable safe-deployment script. [Revision CLI](https://learn.microsoft.com/en-us/cli/azure/containerapp/revision?view=azure-cli-latest), [replica commands](https://learn.microsoft.com/en-us/azure/container-apps/container-console), [traffic command](https://learn.microsoft.com/en-us/azure/container-apps/troubleshoot-deployment-errors).

Residual risk: platform replacement/restart, a second app, or a second local installation can bypass this installer sequence. A proven shared-storage lock only covers participants using that same storage; repository/App-wide singleton enforcement may require a broker lease/fencing design. Explicitly validate this before promising global singleton behavior.

## Persistent storage and Linux semantics

Persist configuration, mutable credentials, Codex history, workspace, JSON state and mutable tool storage; ephemeral storage is suitable only for disposable caches. ACA supports classic Azure Files shares (`Microsoft.Storage/storageAccounts/fileServices/shares`), not the new top-level `Microsoft.FileShares` resource. SMB uses `AzureFile`; NFS uses `NfsAzureFile`. Multiple revisions/apps can mount the same share. Mount declarations belong in revision YAML, while the environment owns the storage definition. The documented SMB setup uses a storage account key: do not assume that app managed identity removes that credential. NFS requires a custom VNet and storage-network access; current ACA docs say encrypted NFS transit is unsupported. [ACA storage contract](https://learn.microsoft.com/en-us/azure/container-apps/storage-mounts).

Candidate SMB mount options for a dedicated private configuration/state volume: `uid=1000,gid=1000,dir_mode=0700,file_mode=0600`. This is a hypothesis to test, not a verified recipe. Tool/workspace volumes need different executable permissions. Do not copy permissive 0777 examples. Microsoft's mount-options guide documents UID/GID settings and warns that `nobrl` disables server byte-range lock requests. **Do not add `nobrl` to make a lock error disappear.** [Mount options](https://learn.microsoft.com/en-us/troubleshoot/azure/azure-kubernetes/storage/mountoptions-settings-azure-files).

Microsoft's Linux mount guide even recommends `nobrl` for some .NET writes; that is a compatibility workaround, not evidence that this controller's FileStream exclusivity survives it. Verify the actual runtime lock across independent replicas and reconnects, including lock release after crash. Mount-wide mode presentation is not proof that per-file chmod or atomic replacement has local POSIX behavior. Test credential mode checks, symlinks, Git operations, executable tools, rename/flush/recovery, and Codex state on the actual backing share. [Linux SMB mounting](https://learn.microsoft.com/en-us/azure/storage/files/storage-how-to-use-files-linux).

NFS should be evaluated separately rather than used as an automatic fallback: client UID permissions and root squash affect ownership initialization; root squash can prevent chown. [NFS ownership troubleshooting](https://learn.microsoft.com/en-us/troubleshoot/azure/azure-storage/files/security/files-troubleshoot-linux-nfs).

## Deployment identity, pull identity and secrets

The interactive installer uses the user's Azure CLI identity for resource creation and image publication. `az acr login --name <registry>` uses that Azure login for Docker authentication; it does not grant the running container permission to pull. Do not copy the user's Azure or Docker credential directories into the image. [ACR authentication](https://learn.microsoft.com/en-us/azure/container-registry/container-registry-authentication).

Prefer a separate user-assigned managed identity for ACR pulls. Attach it to the app and use its resource ID as registry identity. Verify ARM-audience token support with `az acr config authentication-as-arm show -r <registry>`; enabling it is a registry security-setting change, so describe it explicitly in the deployment plan rather than silently changing an existing registry. ACA checks images at container start; use immutable digests. [Managed identity image pulls](https://learn.microsoft.com/en-us/azure/container-apps/managed-identity-image-pull).

Inspect `az acr show --name <registry> --query roleAssignmentMode`. RBAC-only registries use `AcrPull`; ABAC-enabled registries ignore that role and need `Container Registry Repository Reader`, optionally repository-scoped. Catalog listing is a separate permission; do not grant it merely to pull a known image. Publication needs separate writer permission. Never change an existing registry's authorization mode to accommodate an installer. Do not rely on ACA's older generic AcrPull examples for ABAC registries. [ACR ABAC permissions](https://learn.microsoft.com/en-us/azure/container-registry/container-registry-rbac-abac-repository-permissions).

Public Docker Hub images can be pulled without a private credential; authenticated pulls can reduce shared-IP throttling exposure but do not erase all plan limits. Private pulls use `server: docker.io`, a username and `passwordSecretRef` referencing an app secret. Prefer a scoped Docker Hub access token. [Registry configuration](https://learn.microsoft.com/en-us/azure/container-apps/containers), [Microsoft's Docker Hub guidance](https://learn.microsoft.com/en-us/azure/container-registry/buffer-gate-public-content).

Keep the shared GitHub App PEM solely in the separately hosted token broker. The deployed controller receives only its repository-bound enrollment credential and required runtime secrets. No secret enters image layers, build arguments, generated public manifests or logs. Proposed default: Key Vault references with a dedicated app identity granted narrowly scoped `Key Vault Secrets User`. CLI reference syntax is `name=keyvaultref:<secret-uri>,identityref:<identity-resource-id>`; runtime env vars use `secretref:name`. Versionless references refresh within 30 minutes and can restart active revisions, so use versioned references when controlled restart timing is required. Use Azure CLI from PowerShell because the docs flag gaps in Az PowerShell Key Vault reference support. [ACA secrets](https://learn.microsoft.com/en-us/azure/container-apps/manage-secrets).

## Verification before release

Required evidence: fresh deploy and resource reuse; image repull after restart; private registry denied access; ABAC registry; denied Key Vault access; persistence after revision replacement; concurrent-owner rejection; abrupt termination and storage reconnect; UID/mode/symlink/tool execution; failed update and failed rollback; cancellation after old revision stopped; two concurrent installers; FQDN discovery and signed webhook delivery; no secret material in image history, diagnostics or deployment artifacts. Treat cloud-backed tests as a separate authorized validation activity; this research provides no claim that they passed.
