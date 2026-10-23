# Low-idle-cost controller setup candidate

[`low-idle-setup.bicep`](low-idle-setup.bicep) prepares three top-level resources:
a Consumption Container Apps environment on default managed networking, a
Standard LRS storage account with a classic Hot SMB share, and a setup-only app.
The SMB share, dedicated work queue, environment storage definition and queue-scoped role assignment are child resources. The app uses its embedded system-assigned identity; it adds no separate identity resource. There is no
custom VNet/NSG, separately billed managed network group, NFS, registry, vault,
user-assigned identity, Log Analytics workspace or compatibility Job in this
candidate. Setup defaults to the immutable public selection in `image.json`.
Setup is the default; first activation uses the conversation persistence profile below.

Real Azure setup checks passed on 2026-10-22: the three-resource profile reached
zero replicas, woke through HTTP and an invisible queue message, held the replica
while that message existed, and returned to zero after its exact deletion. The
system identity exercised the queue successfully.

Direct native SQLite initialization on the SMB mount failed with `SQLITE_BUSY`.
The app therefore sets `CODEX_SQLITE_HOME=/tmp/repoagent-codex-sqlite`, keeping the
existing private native home and conversation rollouts on SMB. The documented
separate location supports native SQLite locking on the container filesystem.
Native SQLite metadata remains per replica; it is not wholly reconstructible.

The exact working local Docker controller must keep running. Original NFS data
and the Azure-owned account cache remain preserved; this candidate does not
remove their existing bill. Setup deployment does not enroll a device or start
broker, GitHub or native work.

## Conversation persistence boundary

The current [controller](../../src/RepoAgentController/Codex.cs) retains a native
thread ID for each Discussion and uses `thread/resume` at its canonical checkout.
It does not call native thread-name, metadata or goal APIs. The pinned native
version in `image/default.json` passed an actual Azure conversation check: a
completed turn remembered a generated value, the app reached zero, and a new
replica with an empty SQLite directory resumed the same thread and recalled that
value in a second completed turn. No tool or file lookup supplied the answer.
The rollout remained0600. [Observed result](https://github.com/repoagent/controller/issues/37#issuecomment-6353961822),
[OpenAI state locations](https://learn.chatgpt.com/docs/config-file/environment-variables),
[native thread protocol](https://learn.chatgpt.com/docs/app-server).

Account/configuration, controller checkpoints, checkout and conversation rollouts
remain on the existing SMB views. SQLite stays in private local storage with the
entry point's restrictive umask. The app tag `nativePersistence=conversation-rollouts`
identifies this policy during inspection and lifecycle preflight. A `sqlite_home` setting in native configuration
takes precedence over the environment variable; retain the generated native
configuration without such an override for this profile.

Native display names were lost with empty SQLite. SQLite-backed pin/Git metadata
and broader native UI/plugin/goal persistence are outside this profile's verified
boundary. Use NFS or another persistent POSIX host when those features require
retention. Do not infer that all native state can be rebuilt from rollouts.
Keep the native version pinned and repeat the real conversation/restart check
when changing it.

Full shipped-controller signed delivery, native restart/history, active-turn
survival and positive ownership handover remain acceptance gates under #37/#38.
These component checks leave the existing local controller running. Complete
the supported ownership handover as part of the selected full deployment;
source merge does not deploy this profile.

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

The real uniform0600 mount passed private setup but could not execute a workspace
script: `chmod +x` left the file0600 and execution failed with EACCES. The setup
profile now uses two volume views of the same share. Private `/data` retains0600
files; `/workspace`, `/data/tools`, and the generated Git/GitHub wrapper `bin` and
`clone-bin` directories use0700 files. All directories stay0700 and owned by
UID1000. These views add no storage account, identity or always-on service.

Checkout ownership records contain no token or account credentials. The native
controller accepts owner execute on those workspace records while preserving all
binding, content, link and exclusive-lease checks. Every group/other permission
remains rejected. Enrollment, token caches, native trust configuration and refresh
journals keep their existing strict private-file policy. Generated executable
wrappers contain token-file paths rather than token values. The initializer
creates their directories before ACA mounts the nested paths.

This profile still does not provide general POSIX chmod semantics: Git can track
executable bits in its index while the executable view reports0700 for all files.
Actual mixed-view mount behavior, Git/native execution and cross-client locks
must pass real Azure checks before activation. Public image selection must
include the native checkout-metadata change from
[#147](https://github.com/repoagent/controller/issues/147); an older image can
serve setup health but cannot establish active-controller acceptance.

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

## Queue scaling and gated activation design

The profile always provisions `controller-work` in the same Standard account and
gives the app's system identity **Storage Queue Data Contributor** at that queue
scope. The nested `queue-access.bicep` deployment receives the created principal;
its role name also includes that principal so recreating an app cannot update an
immutable assignment to a different identity. No queue connection string, SAS,
user-assigned identity or host Azure credential discovery is used at runtime.
[Queue role](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/storage#storage-queue-data-contributor),
[managed-identity scaling](https://learn.microsoft.com/en-us/azure/container-apps/scale-app#using-managed-identity)

Both setup and active selections use HTTP wake plus `azure-queue`, minimum zero
and maximum one, target one and activation zero. `queueLengthStrategy: all` counts
invisible leases as well as visible messages. The queue profile uses a15-second
poll interval and60-second cooldown; the original NFS setup defaults are retained.
A normal idle queue-mode shutdown preserves the wake hook. Unknown native work
or queue uncertainty stays recoverable and can keep compute allocated; it is not
idle. See [native work lifetime](../../docs/azure-durable-work.md) and
[KEDA queue counting](https://keda.sh/docs/2.21/scalers/azure-storage-queue/).

The default `activate=false` still initializes private layout and serves only
setup metadata. It does not read enrollment, import an account, contact GitHub or
start native work. Use that selection for actual identity, queue authorization,
invisible-message counting and zero/wake/zero checks before any owner handover.
Inspect the real app principal, queue-scoped assignment, both scale rules and
replica counts; compiled JSON and declared minimums are not acceptance evidence.
The operator applying this Bicep must be able to assign the narrowly scoped role;
the auth service's separate Contributor-only OIDC identity is not used or changed.

For the first selected activation/acceptance run, select a published immutable
`controllerImage` containing
issues #147/#151/#153/#155. Positively settle the old controller's authority through
the supported host/broker lifecycle before enrolling a fresh cloud device. Persist
the private enrollment file before ACK. Preserve or explicitly sign into the cloud
Codex account; never discover/copy host credentials. Keep the old local controller
running during setup/provider checks; final selected cutover is a separate step.

The activation input is a private0600 ARM parameter file with `activate=true`,
`enrollment` containing the private enrollment JSON string, and `controllerImage`
set to the selected digest. `enrollment` is a secure Bicep parameter and a native
app secret, mounted read-only at `/run/enrollment`; it is never an output. Pass only
the parameter-file path on the command line:

```sh
az deployment group create \
  --subscription 46f7e5d6-05f3-435b-8089-6c63a203dbfa \
  --resource-group rg-storybeam-repoagent-controller \
  --name controller-smb-first-activation \
  --template-file /tmp/repoagent-low-idle-setup.json \
  --parameters @/private/path/activation.parameters.json \
  --query properties.provisioningState
```

The bootstrap imports into the existing protected0600 enrollment file and checks
the saved immutable binding/settings before native validation. The queue endpoint
is retained in configuration and passed to native `--azure-work-queue`. Conflicting
saved settings refuse before credential replacement. Native secrets avoid a vault
resource here; operators with app-secret management authority can access that
credential. Restrict that authority and rotate through the supported enrollment
lifecycle. Do not log template parameters or dump app secrets/runtime environment.

This is a first-activation path. Do not apply it over a running active controller,
reapply the original setup template, or use the NFS-only image updater for this
profile. Image replacement still requires deliberate stop/drain, old-replica and
outstanding-authority settlement, preserved state and readiness before routing.
Real signed webhook/native/history/restart and active-turn survival beyond the
cooldown remain hosted acceptance gates under #37/#38.

No old resource retirement or local-to-cloud handover is part of this candidate.
Any later selected cutover must positively settle old broker authority and enroll
a fresh cloud device through the normal host flow, preserving private enrollment
before acknowledgement. Account sign-in remains a human step.
