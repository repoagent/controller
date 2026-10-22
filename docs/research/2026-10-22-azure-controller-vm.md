# Azure VM controller host research

Researched 2026-10-22 for [#142](https://github.com/repoagent/controller/issues/142), against public first-party registry, source, and vendor documentation. This is implementation input only: no Azure resources, credentials, registry authentication, Docker state, controller enrollment, or local controller were touched.

## Confirmed infrastructure contract

- The public MCR tag list currently ends at `br/public:avm/res/compute/virtual-machine:0.22.3`; its OCI manifest was created 2026-08-11 and identifies the exact module documentation/source. The existing virtual-network module's latest public tag remains `0.10.2` (manifest created 2026-08-20). Use those exact versions, not a floating tag: [VM 0.22.3 manifest](https://mcr.microsoft.com/v2/bicep/avm/res/compute/virtual-machine/manifests/0.22.3), [VM 0.22.3 documentation](https://github.com/Azure/bicep-registry-modules/tree/avm/res/compute/virtual-machine/0.22.3/avm/res/compute/virtual-machine/README.md), [VNet 0.10.2 manifest](https://mcr.microsoft.com/v2/bicep/avm/res/network/virtual-network/manifests/0.10.2), [VNet 0.10.2 documentation](https://github.com/Azure/bicep-registry-modules/tree/avm/res/network/virtual-network/0.10.2/avm/res/network/virtual-network/README.md).
- The VM module's published source accepts `publicKeys` (each `keyData` and full authorized-keys `path`), `nicConfigurations`, `dataDisks`, and plaintext `customData`; it calls `base64(customData)` itself. It has `enableTelemetry` defaulting to `true`, so set it to `false`. Its NIC configuration requires an IP configuration with `subnetResourceId`; that IP configuration can create a PIP through `pipConfiguration` or use an existing PIP resource ID. Its data-disk type permits `lun`, `diskSizeGB`, managed-disk SKU, caching and `deleteOption: 'Detach'`. These are source facts from the [published 0.22.3 artifact](https://mcr.microsoft.com/v2/bicep/avm/res/compute/virtual-machine/manifests/0.22.3); the module's readable source is linked by its manifest documentation annotation above.
- Minimal Ubuntu 24.04/x64 shape (illustrative schema, not an approved VM size or a deployment):

```bicep
module vm 'br/public:avm/res/compute/virtual-machine:0.22.3' = {
  name: 'controller-vm'
  params: {
    name: vmName
    location: location
    vmSize: vmSize
    osType: 'Linux'
    diskControllerType: 'SCSI' // makes the SCSI LUN path below applicable
    adminUsername: adminUsername
    publicKeys: [{
      keyData: sshPublicKey
      path: '/home/${adminUsername}/.ssh/authorized_keys'
    }]
    imageReference: {
      publisher: 'Canonical'
      offer: 'ubuntu-24_04-lts'
      sku: 'server'
      version: 'latest'
    }
    osDisk: { managedDisk: { storageAccountType: 'StandardSSD_LRS' } }
    dataDisks: [{
      lun: 0
      diskSizeGB: dataDiskSizeGiB
      caching: 'None'
      deleteOption: 'Detach'
      managedDisk: { storageAccountType: 'StandardSSD_LRS' }
    }]
    nicConfigurations: [{
      networkSecurityGroupResourceId: nsg.id
      ipConfigurations: [{
        subnetResourceId: subnet.id
        pipConfiguration: { publicIpNameSuffix: '-pip' }
      }]
    }]
    customData: cloudConfig // do not pre-base64 this value
    enableTelemetry: false
  }
}
```

  This deliberately omits managed identity, Storage/Key Vault, monitoring/diagnostic settings, VM extensions, and agent configuration. Microsoft identifies Canonical's Ubuntu 24.04 image resource with offer `ubuntu-24_04-lts`, SKU `server`, and version `latest`; availability must still be checked in the selected region/subscription before applying. [Microsoft Ubuntu 24.04 image example](https://learn.microsoft.com/en-us/azure/devops/managed-devops-pools/configure-images?view=azure-devops). Do not put secrets in `customData`: the module documentation says this is custom VM data and base64 is encoding, not secrecy.
- If native networking resources are used rather than the VM module's generated PIP, the current Learn template reference renders `Microsoft.Network/networkSecurityGroups@2026-03-01` (including child `securityRules`) and `Microsoft.Network/publicIPAddresses@2026-03-01`; the PIP schema supports Standard SKU, static allocation, DNS settings, and `deleteOption: 'Detach'`. [NSG reference](https://learn.microsoft.com/en-us/azure/templates/microsoft.network/networksecuritygroups), [NSG rule reference](https://learn.microsoft.com/en-us/azure/templates/microsoft.network/networksecuritygroups/securityrules), [PIP reference](https://learn.microsoft.com/en-us/azure/templates/microsoft.network/publicipaddresses). Prefer one approach for PIP/NSG ownership; do not create duplicates.

## First boot and persistent Docker state

Use a cloud-init-capable Ubuntu Marketplace image. Azure passes VM `customData` to cloud-init; cloud-init errors and logs must be diagnosed on the VM rather than inferred from ARM success. [Azure cloud-init overview](https://learn.microsoft.com/en-us/azure/virtual-machines/linux/cloud-init-deep-dive), [Azure cloud-init troubleshooting](https://learn.microsoft.com/en-us/azure/virtual-machines/linux/cloud-init-troubleshooting).

For the explicit SCSI LUN 0 above, Azure documents `/dev/disk/azure/scsi1/lun0` (and `.../lun0-part1` after partitioning) as the Azure disk symlink. It also says device paths can change across reboot and recommends the filesystem UUID in `/etc/fstab`; `nofail` avoids boot failure if the disk/filesystem is unavailable. [Format and mount managed disks](https://learn.microsoft.com/en-us/azure/virtual-machines/linux/disks-format-mount-data-disks-linux), [identify attached disks and SCSI LUNs](https://learn.microsoft.com/en-us/azure/virtual-machines/linux/add-disk).

Therefore the bootstrap must: wait for the LUN-0 path; determine whether it already has a filesystem; only partition/format a demonstrably new empty disk; mount its known filesystem by UUID; verify the mount (for example with `findmnt`) before creating the Docker root and before starting Docker; and stop/leave Docker stopped on any absent, ambiguous, or failed mount. An existing filesystem must never be reformatted. This is a safety policy derived from the Azure persistence semantics, not a Microsoft-provided ready-made script.

Docker officially supports Ubuntu Noble 24.04/x86_64 and recommends installing `docker-ce`, `docker-ce-cli`, `containerd.io`, `docker-buildx-plugin`, and `docker-compose-plugin` from Docker's apt repository; the convenience script is documented as unsuitable for production. [Docker Engine on Ubuntu](https://docs.docker.com/engine/install/ubuntu/). Configure Docker's data root only after the data-disk mount has been verified, so it cannot silently fall back to the OS disk.

Docker SSH transport is supported directly: an ad-hoc `DOCKER_HOST=ssh://docker-user@host` selects the remote daemon without changing the default Docker context, and that remote user must be able to access the Docker socket. [Protect Docker daemon access / SSH](https://docs.docker.com/engine/security/protect-access/). A decisive installer constraint: bind mounts are evaluated on the **daemon host**, not the client. Remote invocations cannot mount the operator workstation's paths; all bind paths must already exist on the VM. [Bind mounts](https://docs.docker.com/engine/storage/bind-mounts/).

## HTTPS proxy and the Azure hostname question

The current upstream Caddy release is `v2.11.7` (published 2026-10-03). The matching official `caddy:2.11.7-alpine` image index is pinned by `sha256:eb4d35b7452d1ab6171fbdbe8d9275b2301dad6dad92115d91e19fb9b24d510f`; its Linux/amd64 manifest is `sha256:04913b1da43339a40ad1ea87827d70a576ac5aa6cf674c6e24fdca675887533a`. [Caddy release](https://github.com/caddyserver/caddy/releases/tag/v2.11.7), [official image source](https://github.com/caddyserver/caddy-docker), [Docker Hub official image tags](https://hub.docker.com/_/caddy).

Caddy's ordinary configuration can be only:

```caddyfile
${PUBLIC_FQDN} {
  reverse_proxy 127.0.0.1:8080
}
```

For a hostname, Caddy automatically manages and renews a publicly trusted certificate and redirects HTTP to HTTPS when the name qualifies, DNS points to the host, and public TCP ports 80/443 reach Caddy. Its certificate/key state is in its storage; the official image requires writable, persistent data and config directories, so persist both `/data` and `/config` on the mounted disk. [Automatic HTTPS](https://caddyserver.com/docs/automatic-https), [HTTPS quick start](https://caddyserver.com/docs/quick-starts/https), [reverse-proxy quick start](https://caddyserver.com/docs/quick-starts/reverse-proxy), [official-image requirements](https://hub.docker.com/_/caddy).

With Linux Docker host networking, Caddy can reach a controller bound only to `127.0.0.1:8080`; it shares the host namespace and binds 80/443 directly. Do not specify Compose `ports:` for that service: port mappings are unsupported with `network_mode: host`. [Docker host networking](https://docs.docker.com/engine/network/drivers/host/), [Compose networking](https://docs.docker.com/compose/how-tos/networking/).

Azure confirms that a public-IP DNS label creates `label.region.cloudapp.azure.com` in Azure-managed DNS. [Azure public-IP DNS label](https://learn.microsoft.com/en-us/azure/virtual-network/ip-services/public-ip-addresses/). **Unverified deployment assumption:** Caddy's published eligibility rules do not exclude this syntactically public FQDN, and Azure documents its DNS mapping, but neither vendor source specifically promises that a public ACME CA will issue for every Azure-managed `cloudapp.azure.com` name. Do not claim HTTPS readiness until a deployed Standard PIP resolves to the VM and the 80/443 ACME flow succeeds. This is the only research finding that could require selecting a custom domain or a different ingress plan before activation.

## Tradeoff against ACA + NFS

A VM removes the ACA environment/revision model and the NFS share/mount compatibility surface; it gives one host-local Docker engine and one ordinary managed disk. In return, it creates guest OS patching/reboot/SSH/firewall and Docker-engine maintenance, retains compute allocation while the VM runs, and still needs a VM plus disk, NIC, PIP, VNet/subnet, NSG and related resource objects. It is not literally “one Azure resource.” ACA can scale application replicas to zero, while Azure documents managed disks as persistent VM storage across reprovisioning; neither observation is a cost estimate or a promise of availability. [ACA scaling](https://learn.microsoft.com/en-us/azure/container-apps/scale-app), [managed disk persistence](https://learn.microsoft.com/en-us/azure/virtual-machines/linux/add-disk).

## Implementation blocker / handoff

No cloud action is authorized or implied. Before provisioning, root must decide whether an Azure public-IP FQDN is acceptable subject to the live ACME test, select the explicit admin source CIDR and VM size, and make the bootstrap fail closed when the retained data disk is not mounted. The remote-Docker bind-mount rule also needs to be honored by the installer interface; this note intentionally does not inspect that code.
