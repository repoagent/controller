# Read-only Azure controller planning

PowerShell 7 and an authenticated host Azure CLI with Container Apps support already installed are required. The planner never logs in, changes the active subscription, installs extensions, registers providers, provisions resources, publishes images, reads secret values or assigns roles. Every Azure invocation includes the selected subscription.

```powershell
./scripts/plan-azure.ps1 `
  -SubscriptionId '11111111-1111-1111-1111-111111111111' `
  -ResourceGroup 'controller-rg' -Location 'westeurope' `
  -EnvironmentName 'controller-env' -RegistryName 'myregistry' `
  -RepositoryId '123456' `
  -ControllerId '22222222-2222-2222-2222-222222222222' `
  -Image 'myregistry.azurecr.io/controller@sha256:REPLACE_WITH_64_LOWERCASE_HEX_DIGITS' `
  -ImageManifest './image/default.json'
```

Replace example identities and the image digest. `Image` identifies the **final published controller image**, independently of the optional immutable base in the existing [software manifest](image-composition.md). The planner consumes that delivered v1 manifest with native PowerShell validation and its existing reviewed software catalog. It records the manifest fingerprint; it does not build, publish, inspect layers or prove that a supplied final image matches those selections. Source-digest verification remains the image builder's responsibility. Only Azure public-cloud ACR in the selected resource group is supported initially.

Omit `AppName` for the suggested `repoagent-<numeric repository ID>`. Invalid names, mutable images, missing inputs and discovery failures exit nonzero without prompting. A CLI request has a configurable 1–60 second timeout, default 15 seconds, covering process exit and both output pipes. Combined stdout/stderr capture is limited to 1 MiB. Provider stderr and raw resource JSON are never printed. `Microsoft.App` must already be registered. CLI support/permission/network errors fail closed; only explicit `ResourceNotFound` or `ResourceGroupNotFound` provider errors establish absence. Successful responses must match the exact requested resource ID. A malformed, inaccessible or incompatible result is never treated as permission to create.

The JSON plan distinguishes `create`, `compatible-reuse` for a provisioned external environment in the selected region, `verified-update`, and `explicit-adoption-required`. Update eligibility requires exact `repoagent-controller-id` and `repoagent-repository-id` tag matches and the requested environment/region. These operator-controlled tags establish discovery metadata only; they do not authenticate enrollment or prove broker authorization. An unowned app always needs explicit adoption approval. A conflicting environment or app name stops planning without replacement. Apply must re-read all evidence because discovery is not an atomic snapshot.

Desired settings describe HTTPS external ingress on port 8080, the image's UID1000 runtime, single revision and one replica, TCP health probes, durable `/data`, managed identity registry pulls, and versioned Key Vault references. This is a plan vocabulary, not an apply template or proof that a hosting option implements each setting. A TCP probe proves listener availability only. One replica does not fence outstanding tokens. Secret references and runtime identity are deliberately unresolved rather than fabricated; no secret values are accepted. Registry `AcrPull` scope/role ID and future operator roles are reported. An ABAC-enabled registry may require a different scoped repository role and remains a gate. Key Vault access must be scoped to the eventual vault/identity; neither exists in these inputs.

`readyToApply` is always false. Gates include actual durable-storage UID/locking/atomic-write/restart/backup evidence, broker hosted acceptance and token barriers, identity and versioned secrets, image publication/provenance, ownership/adoption, preserving unrelated settings and data, and a captured previous revision/image/secret/settings/storage backup for rollback. A future apply must prepare a concrete write plan and must never delete/recreate resources to recover automatically. All listed compute, registry, logs, storage, secrets and networking can incur charges; the planner does not quote prices.

A stable existing origin is emitted only from a discovered external ingress FQDN under `azurecontainerapps.io`. No future app URL is invented. After provisioning, read the actual FQDN, verify HTTPS and webhook delivery, and configure GitHub with the versioned webhook secret. An existing FQDN alone is not hosted acceptance.

The planner is an installer script and has no automated tests; it is tested manually against the local AppHost ([#204](https://github.com/repoagent/controller/issues/204)) when it changes. Live Azure, actual storage, managed identity pulls, published image provenance, webhook delivery, broker operations and rollback require separate acceptance before an apply can be called usable. This command is reusable by a later `install.ps1` Azure setup slice; no installer entry point or deployment is added here.

Provider references: [Azure Container Apps CLI](https://learn.microsoft.com/en-us/cli/azure/containerapp?view=azure-cli-latest), [managed identity image pulls](https://learn.microsoft.com/en-us/azure/container-apps/managed-identity-image-pull), and [secret references](https://learn.microsoft.com/en-us/azure/container-apps/manage-secrets).
