# StoryBeam controller foundation

This foundation creates a dedicated VNet, network-restricted classic NFS 4.1 share, Consumption Container Apps environment and manual smoke job. The job uses the [accepted public controller image](https://github.com/repoagent/controller/actions/runs/39713172578) by immutable digest and runs its installed tools as UID 1000. There are no registry credentials, active controller, enrollment secrets or changes to the existing auth service. Local StoryBeam keeps running.

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
