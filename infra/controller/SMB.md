# Low-idle-cost controller setup candidate

[`low-idle-setup.bicep`](low-idle-setup.bicep) prepares three top-level resources:
a Consumption Container Apps environment on default managed networking, a
Standard LRS storage account with a classic Hot SMB share, and a setup-only app.
The share and environment storage definition are child resources. There is no
custom VNet/NSG, separately billed managed network group, NFS, registry, vault,
user-assigned identity, Log Analytics workspace or compatibility Job in this
candidate. The public image is the existing immutable selection in `image.json`.

This is a candidate for [#37](https://github.com/repoagent/controller/issues/37),
prepared by [#145](https://github.com/repoagent/controller/issues/145). It has not
passed real Azure storage or scale-to-zero acceptance. The exact working local
Docker controller must keep running. The original NFS resources and their data
remain in place; creating this candidate would not remove their existing bill.
No controller enrollment, account credentials or broker/GitHub work occurs during
this setup deployment.

## Compile and inspect

The selected subscription is `46f7e5d6-05f3-435b-8089-6c63a203dbfa`, region
`swedencentral`, resource group `rg-storybeam-repoagent-controller`. The default
prefix `storybeam-lean` is distinct from the existing NFS host. Inspect those
candidate names before applying; do not adopt unrelated resources or change a
prefix to work around retained data. Source merge does not deploy this file.

```sh
az bicep install --version v0.48.1
"$HOME/.azure/bin/bicep" build infra/controller/low-idle-setup.bicep \
  --outfile /tmp/repoagent-low-idle-setup.json
```

After selecting this reviewed candidate for a real Azure check:

```sh
az deployment group create \
  --subscription 46f7e5d6-05f3-435b-8089-6c63a203dbfa \
  --resource-group rg-storybeam-repoagent-controller \
  --name controller-smb-candidate \
  --template-file /tmp/repoagent-low-idle-setup.json \
  --query properties.outputs

az containerapp show --resource-group rg-storybeam-repoagent-controller \
  --subscription 46f7e5d6-05f3-435b-8089-6c63a203dbfa \
  --name storybeam-lean-controller \
  --query '{state:properties.provisioningState,origin:properties.configuration.ingress.fqdn,scale:properties.template.scale,volumes:properties.template.volumes}'
az containerapp env show --resource-group rg-storybeam-repoagent-controller \
  --subscription 46f7e5d6-05f3-435b-8089-6c63a203dbfa \
  --name storybeam-lean-environment \
  --query '{profiles:properties.workloadProfiles,network:properties.vnetConfiguration,logs:properties.appLogsConfiguration}'
```

Read the deployment's `storageAccountName` and `shareName` outputs, then inspect
the actual account kind/SKU, secure transfer, public-blob denial, share protocol,
Hot tier and quota with `az storage account show` and `az storage share-rm show`.
Pass `--subscription 46f7e5d6-05f3-435b-8089-6c63a203dbfa` to both storage
inspection commands too; a deployment's subscription flag does not select the
Azure CLI default subscription.
Inspect only nonsecret fields: do not print account keys, environment storage
credentials, private files, tokens or the whole runtime environment.

Call the returned `publicOrigin` at `/health/setup`: it must return fixed setup
metadata with HTTP200. `/health/ready` must remain503; other routes return404.
Those responses establish only that the setup initializer and HTTP host ran.
Observe the actual replica count after the HTTP requests stop; a configured
minimum of zero does not prove that the app has reached zero.

For a bounded shell/compatibility window, explicitly recompile with a parameters
file or apply the same Bicep with `setupMinReplicas=1`. After the check, while
still unenrolled, reapply `setupMinReplicas=0` and confirm actual zero replicas.
Do not repeat cold-start health requests while measuring idle scaling.

## Storage hypothesis and acceptance

The setup module uses SMB3.1.1, UID/GID1000, private fallback file and directory
modes, `serverino` and `mfsymlinks`. The storage account requires secure transfer,
so Azure Files requires encrypted SMB. Real ACA preflight rejected `seal`,
`cifsacl`, `modefromsid`, `idsfromsid`, `actimeo` and `closetimeo`; the candidate
therefore uses the restricted options that ACA accepts. Fallback mount modes
do not establish per-file Unix permission changes or executable-file support.
The unchanged initializer and later native checks must establish compatibility
on the real mount. Byte-range locking stays enabled.
[Linux SMB mount and permission documentation](https://learn.microsoft.com/en-us/azure/storage/files/storage-how-to-use-files-linux),
[ACA mount configuration](https://learn.microsoft.com/en-us/azure/container-apps/storage-mounts)

The existing initializer is unchanged. It verifies owned0700 private directories,
0600 single-link regular files, binding retention and the native-history symlink,
and uses atomic replacement and a nonblocking filesystem lock. A mount that
cannot meet these requirements must fail setup. Do not weaken the checks, make
credentials executable/public, add `nobrl`, or delete data to obtain a green
result. A failed SMB candidate keeps cloud activation gated.

Before activation, #37 must exercise real private-file publication, executable
tools, native history, Git, exclusive native state leases across independent
clients, restart and interrupted-write recovery on the actual backing share.
Use synthetic files for storage checks; no broker owner or local credential
copy is needed. A successful mount, compiler, local filesystem test or setup
health response does not replace those observations.

The stable storage API uses `storage.listKeys()` directly inside the environment
storage definition. No key is a template input, literal, client argument or
output. Public networking permits authenticated SMB access from default ACA
networking; blob anonymous access is disabled and secure transfer is required.
The dedicated account key is broad authority over that account, so do not share
the account with other applications. Key rotation requires updating the storage
definition and testing remounts. The documented identity-based SMB alternative
is preview and absent from the stable `2026-07-01` storage schema; it must not be
silently substituted. [ACA storage authentication](https://learn.microsoft.com/en-us/azure/container-apps/storage-mounts), [stable storage schema](https://learn.microsoft.com/en-us/azure/templates/microsoft.app/managedenvironments/storages)

## Cost and active-controller boundary

Zero replicas means no ACA replica-consumption charge. Standard Hot storage
continues charging for used bytes and operations; its32GiB quota is a size limit,
not a provisioned32GiB bill. The public Sweden Central Hot LRS data-storage rate
observed on2026-10-22 was USD0.0271/GB-month, before operations, transfer, tax or
discounts. Subscription-wide ACA free grants are shared with the auth service.
See the [dated cost assessment](../../docs/research/2026-10-22-container-apps-idle-cost.md)
and [Azure Files billing](https://learn.microsoft.com/en-us/azure/storage/files/understanding-billing).

This setup-only host can sleep safely because it runs no agent work. The active
controller currently returns202 before a native turn finishes and disables its
webhook during shutdown. `runtime.bicep` and the existing update/rollback command
remain the NFS active-host path and must not be used to activate this SMB
candidate. Active-turn scale signalling, durable admission/recovery, preserving
the wake-up webhook during idle sleep, cold-start delivery and controlled
activation/update require their own source and real Azure acceptance. A saved
conversation is not an executing turn and must not require permanent compute.

No old resource retirement or local-to-cloud handover is part of this candidate.
Any later selected cutover must positively settle old broker authority and enroll
a fresh cloud device through the normal host flow, preserving private enrollment
before acknowledgement. Account sign-in remains a human step.
