# Authentication service infrastructure

[Issue #55](https://github.com/repoagent/controller/issues/55) connects the dedicated Azure Table and frozen `Broker__*` configuration to the guarded foundation from [#47](https://github.com/repoagent/controller/issues/47). The approved [repository-held approval contract](auth-repository-approval.md) has no human OAuth client secret. Signing and Data Protection keys remain separate. These are source artifacts; templates and local tests prove neither a provisioned endpoint nor live managed-identity acceptance. The [deployment runbook](azure-auth-deployment.md) defines the approved destination and OIDC boundary.

## Resources and boundaries

`infra/auth/main.bicep` targets the existing resource group. `deploy.sh` permits only subscription `46f7e5d6-05f3-435b-8089-6c63a203dbfa`, group `rg-repoagent-controller-auth`, location `swedencentral`. It never creates a group, registers providers, grants roles, imports keys or recovers a deleted vault. The deployment identity `id-controller-auth-github` is outside its write set.

| Resource | Configuration |
| --- | --- |
| `${AUTH_APP_NAME}-runtime` | Separate user-assigned identity, no deployment authority |
| `${AUTH_APP_NAME}-logs` | 30-day retention and 1 GB/day Log Analytics ingestion quota |
| `${AUTH_APP_NAME}-environment` | Container Apps Consumption profile |
| `${AUTH_STORAGE_ACCOUNT_NAME}` StorageV2 account | Standard_LRS, HTTPS/TLS 1.2, no shared-key access or anonymous Blob access; Azure identity authentication |
| `data-protection` Blob container | Separate durable key ring at `keyring.xml`; no lifecycle expiration |
| `${AUTH_TABLE_NAME}` under default Table service | Dedicated broker state; default `brokerstate`; application does not create tables |
| `${AUTH_KEY_VAULT_NAME}` Key Vault | Empty, RBAC-enabled, 90-day soft delete and purge protection |
| `${AUTH_APP_NAME}` in runtime modes | Immutable image, HTTPS ingress on 8080, managed identity, startup/liveness/readiness probes, Single revision mode, minimum 0/maximum 1 replica, 0.25 CPU/0.5Gi memory |

Storage and vault names are required explicit configuration in every deployment mode, including cleanup. `foundation-check` validates the selected names and reports their references locally. These names are globally unique; collisions fail without choosing another name or adopting foreign resources. Existing deployments must supply their current names explicitly before using this source. Changing names requires a separately planned operator cutover: it does not migrate or delete data, or prove old storage empty. Keep the app, managed environment, storage, vault and Table names stable.

The endpoints use public Azure networking and authenticated data access. Anonymous Blob denial does not mean private networking. Public ingress can incur request, storage and network charges. Process-local request bounds, configured compute/log capacity and delayed spending alerts are distinct controls; none is a hard spending cap. Log ingestion quota may suppress diagnostics. Logs must exclude credentials, request bodies and sensitive enrollment data. Maximum one replica does not serialize authorization or establish a global quota; rollout overlap can exist.

## Guarded entrypoints

Use Bash, Python 3.10+ and standalone Bicep on PATH, `BICEP_BIN`, or `$HOME/.azure/bin/bicep`. Scripts do not install tools or authenticate.

```sh
infra/auth/validate.sh
python3 -m unittest discover -s infra/auth/tests -v
export AZURE_SUBSCRIPTION_ID=46f7e5d6-05f3-435b-8089-6c63a203dbfa
export AZURE_RESOURCE_GROUP=rg-repoagent-controller-auth
export AZURE_LOCATION=swedencentral
export AUTH_APP_NAME=repoagent-auth
export AUTH_STORAGE_ACCOUNT_NAME=repoagentauth
export AUTH_KEY_VAULT_NAME=repoagent-auth-kv
infra/auth/deploy.sh foundation-check
infra/auth/deploy.sh cost-plan
```

`AUTH_STORAGE_ACCOUNT_NAME` must be 3–24 lowercase ASCII letters/digits. `AUTH_KEY_VAULT_NAME` must be 3–24 ASCII letters/digits/hyphens, start with a letter, end with a letter/digit and contain no consecutive hyphens. Both are required without defaults; missing, empty or invalid values fail before the helper calls GitHub, image attestation or Azure. Examples select names; they do not reserve them or authorize a cutover.

`AUTH_APP_NAME` must be 3–22 lowercase letters/digits/hyphens, start with a letter, end with a letter/digit and contain no consecutive hyphens. `AUTH_TABLE_NAME` must be 3–63 ASCII alphanumerics starting with a letter. Omitted Table name defaults to `brokerstate`; explicit empty input is invalid.

| Mode | Behavior |
| --- | --- |
| `foundation-check` | Local target/name validation and reference discovery |
| `foundation-plan` | Compile, inspect ownership, ARM validate and what-if; no apply |
| `foundation-apply` | Foundation plan, repeat ownership checks, Incremental apply |
| `check` | Local target/runtime prerequisite validation; no image or Azure calls |
| `plan` | Runtime validation, compile, foundation checks, ARM validate and what-if |
| `apply` | Runtime plan, repeat ownership checks, Incremental apply |
| `cost-plan` | Local capacity/Table/alert plan with actionable missing inputs; no Azure calls |
| `stage-plan` / `stage-apply` | Attested source/digest and owned runtime configuration; no ingress, fixed 1/1; these isolated HTTP modes omit hosted flags |
| `promote-staged` | Exact owned staged artifact, every normal release gate, guarded public apply and same-digest rollout |
| `stage-stop` / `stage-resume` | Exact isolated revision only; bounded actual-zero observation or selected-replica local health verification |

Only `foundation-apply`/`apply`, the explicit activation apply/promotion operations and the cleanup identity/Job apply operations described below can create or modify resources; stage-stop/resume only deactivate/activate the exact staging revision. Neither can assign roles, read storage account keys or deploy budgets. All Azure calls select the subscription explicitly. Compiler inspection rejects changes to the approved pilot compute/log envelope and shared-key denial before Azure calls.

Ownership preflight checks exact ARM ID/type, location and tags `repositoryId=1426154477`, `ownerId=345029828`, `managedBy=repoagent-auth` plus resource role. Blob container ownership uses equivalent metadata. Azure Tables have no ARM tags/metadata: the already-verified owned account and exact reserved Table name form that child ownership boundary. Reserve the selected Table name for the broker; inspect an existing same-account Table before first deployment. Other tables and default services are outside the write set. Runtime mode requires all foundation resources present. Failed access, transport, malformed JSON or unexpected identity is fatal; only explicit resource absence permits foundation creation.

What-if must succeed, allow only owned Create/Modify/NoChange, and forbid deletions or unsupported changes. Ignore entries leave unrelated resources alone under Incremental mode. Repeat ownership inspection immediately before apply narrows the race but does not make inspection/write atomic: serialize deployments and restrict group mutation. Tags are operational guards, not protection against another Contributor.

Stdout JSON contains `appName`, `runtimeIdentityId`, `runtimeIdentityPrincipalId`, `runtimeIdentityClientId`, `keyVaultName`, `dataProtectionBlobUri`, `tableEndpoint`, `tableName`, `tableResourceId`, `brokerOrigin`, `runtimeConfiguration`, `capacityPlan`, and `applied`. Identity client/principal IDs are platform-issued values read from the owned identity during Azure plans/applies; local checks or a plan creating a missing identity report null rather than fabricating IDs. Bicep outputs the same references and binds its own resource expressions directly into the container. Progress goes to stderr without Azure response bodies or secrets. Output is configuration, not endpoint readiness.

## Canonical origin and readiness

Bicep derives `https://<appName>.<owned environment.properties.defaultDomain>`, injects `Broker__BrokerOrigin` on every runtime deployment and outputs the same `brokerOrigin`. It uses the owned foundation environment, without an app self-reference or custom-origin override. Foundation exists before runtime, so the real domain is available before the first app. The helper verifies exact environment ownership and validates its domain as a DNS suffix before runtime writes, then reports the origin. Offline checks return null for the unresolved origin; they never invent an Azure domain.

Runtime apply checks that the deployed app uses the owned environment and its public ingress FQDN matches that origin. Normal image deployment/rollback also checks the owned environment, configured origin and public FQDN before updating and before reporting a ready revision. Keep app/environment names stable after enrollment; changing the origin requires a controlled migration. Do not use an ad hoc environment-variable update that a later Bicep apply overwrites.

`/health/ready` reports local composition only: valid startup settings plus enrollment and issuance registrations return 200; incomplete/revoke-only composition returns 503 while `/health/live` remains 200. The response is empty and noncacheable. Probes inspect registrations without resolving provider-backed services and do no external I/O. Data Protection may attempt normal key-ring preload at startup. Neither startup nor health 200 establishes runtime permissions, initialized quotas, upstream availability, independent cleanup or recovery acceptance.

## Runtime prerequisites

`check`, `plan` and `apply` require the four explicit readiness assertions `AUTH_PROVIDER_READY=true`, `AUTH_STORAGE_READY=true`, `AUTH_CLEANUP_READY=true`, `AUTH_RUNTIME_ACCESS_READY=true`. Leave them unset until provider/security, durable transaction/recovery, independent cleanup and runtime managed-identity evidence exists. They are assertions, not automatic proof.

| Input | Container binding |
| --- | --- |
| `AUTH_SIGNING_KEY_URI` | `Auth__SigningKeyUri`: versioned `https://<owned-vault>.vault.azure.net/keys/<name>/<32-hex-version>` |
| `AUTH_DATA_PROTECTION_KEY_URI` | `Auth__DataProtectionKeyUri`: different key name in the same vault |
| Owned Blob reference/stable app name | `Auth__DataProtectionBlobUri` and `Auth__DataProtectionApplicationName` |
| Owned environment defaultDomain/stable app name | `Broker__BrokerOrigin`: canonical HTTPS origin, no caller override |
| Owned storage endpoint/selected Table | `Broker__TableEndpoint`, `Broker__TableName` |
| `AUTH_DEPLOYMENT_EPOCH` | Required `Broker__DeploymentEpoch`: 1–128 ASCII letters/digits/hyphen/underscore |
| Owned runtime identity client ID | `Broker__ManagedIdentityClientId` and `AZURE_CLIENT_ID`; never principal/resource ID or caller-supplied credentials |
| `AUTH_REQUEST_LIMIT_PER_MINUTE` | `Broker__RequestLimitPerMinute`, default 120, range 1–10000 |
| `AUTH_CONCURRENT_REQUEST_LIMIT` | `Broker__ConcurrentRequestLimit`, default 8, range 1–128 |
| `AUTH_REQUEST_TIMEOUT_SECONDS` | `Broker__RequestTimeoutSeconds`, default 15, range 1–60 |

Defaults apply only when an input is absent; explicit empty strings fail. HTTP limits are process-local. Keep the external restore epoch outside restored Table backups; fence old workers/access and supply a fresh epoch during recovery. Do not use the generic `AUTH_STORE_RESOURCE_ID` or obsolete `AUTH_OAUTH_CLIENT_SECRET_URI`: neither is part of this runtime interface. Endpoints and client ID derive from owned resources; callers cannot spoof them.

`plan`/`apply` additionally require registry-qualified `AUTH_IMAGE` ending in `@sha256:<64 lowercase hex>`, `AUTH_IMAGE_ROLE=auth-service`, `AUTH_IMAGE_NON_ROOT=true`, `AUTH_IMAGE_PUBLIC=true`. The delivery owner must inspect that digest's OCI role/nonzero numeric user and prove anonymous pullability before setting attestations. The template contains no registry credentials. The provider listens on 8080 and exposes `/health/live` and `/health/ready`. Its signing/Data Protection SDK adapters, remaining GitHub App settings and hosted acceptance retain their own gates. Retain old wrapping-key versions while protected payloads require them.

## Operator-only permissions

`runtime-rbac.bicep` is optional and never called by ordinary deployment. It defines three custom data roles assignable inside the group, plus the built-in [Storage Table Data Contributor](https://learn.microsoft.com/en-us/azure/storage/tables/assign-azure-role-data-access) assignment. Each assignment uses the runtime principal at the exact resource scope:

| Role | Data actions/scope |
| --- | --- |
| `sign` | Key read/sign on imported GitHub App key |
| `wrap` | Key read/wrap/unwrap on separate Data Protection key |
| `keyring` | Blob read/write on `data-protection` container |
| Storage Table Data Contributor | Built-in `0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3` on `/storageAccounts/<owned>/tableServices/default/tables/<selected>` only |

No OAuth secret role remains. No storage account key is read. The Table role permits entity operations; exact Table scope prevents authority over neighbors. Key actions omit create/delete/export and Blob actions omit deletion. Key scopes span versions while runtime URIs pin versions. Verify minimal SDK permissions and denial outside the selected resources with real managed identity before readiness.

Operator parameters are `appName`, `storageName`, `vaultName`, `signingKeyName`, `dataProtectionKeyName`, `tableName`. The operator needs role-definition read/write at the group and assignment read/write at each target, plus runtime identity read. Contributor cannot grant these permissions; do not elevate the workflow. Import the GitHub key and provision wrapping keys separately with data-plane administration, without secret values in Bicep/parameters/outputs/arguments/images.

Before any separately authorized role apply, inspect foundation ownership, exact existing role definitions/assignment GUIDs and principal/scope/permissions; stop on conflicts. Review explicit-subscription Incremental what-if. This template is not an automatic repair tool. Table scope is supported by ARM/CLI; propagation and actual data-plane behavior remain live gates.

## Capacity and spending-alert plan

`cost-plan` always emits the configured pilot envelope, exact Table resource ID and a parameterized alert plan. Optionally set `AUTH_BUDGET_PLAN_FILE` to a local JSON object containing operator-selected `amount` (positive integer in billing currency), `timeGrain` (`Monthly`, `Quarterly`, `Annually`), `startDate`, `endDate` (`YYYY-MM-DD`), `thresholds` (1–5 distinct integer percentages 1–1000), and `contactEmails` and/or `contactGroups` (existing approved action-group ARM IDs in the selected group). Optional `scope` must equal the approved group. It never supplies amount, dates, threshold or destination defaults. Missing fields appear together in `budgetPlan.missingInputs`; malformed supplied values fail locally. This compact plan supports Actual/GreaterThanOrEqualTo alerts. It is not an ARM budget payload.

The [Consumption budgets 2024-08-01 schema](https://learn.microsoft.com/en-us/azure/templates/microsoft.consumption/2024-08-01/budgets) supports resource-group scope, the selected calendar periods, up to five notification thresholds, explicit recipients or action groups, and first-of-month start dates before end dates. Compilation of infrastructure is not proof of billing eligibility. The plan deliberately retains `billingEligibility=unknown` and `readyToApply=false`, even with every field supplied. Before any later separately authorized budget write, verify the selected subscription offer, currency, current-period/future-start constraints and action-group ownership, then review the budget deployment and notification plan. No budget API or notification deployment is supplied here. Missing operator choices do not block Table source/configuration work.

Alerts notify after measured spending; they do not stop charges. Storage, networking, public ingress, control-plane activity, rollout overlap and diagnostic billing remain residual exposure. No estimated monthly price is fabricated.

## Remaining hosted work

Prove real managed-identity read/write and denial outside the broker Table; production GitHub/signing/Data Protection behavior; external epoch recovery; independent zero-replica cleanup; and resource/billing eligibility. Run authorized ARM validation/what-if/deployments, cold starts, HTTPS, rollback and broker journey acceptance before advertising availability. Local tests replace only Azure CLI transport and never prove provisioning, permission propagation or notification delivery.

## First-activation configuration

The [operator runbook](azure-auth-deployment.md#first-activation-through-existing-oidc) provides the existing main-only OIDC route for foundation and runtime operations. Foundation retains its existing guards and never requires an image/hosted flags or creates an app. Activation runtime operations require `AUTH_EXPECTED_SOURCE_SHA` (full accepted current main commit), `GITHUB_REF=refs/heads/main`, matching `GITHUB_SHA`/checkout/remote main, fixed `AUTH_IMAGE_REPOSITORY=ghcr.io/repoagent/controller-auth` and anonymously source-attested `AUTH_IMAGE`. These are process inputs bound from workflow dispatch, not new Actions secrets. Stop/resume also require `AUTH_STAGE_REVISION`. Ordinary entrypoints retain their existing interfaces/gates; supplying expected source additionally enforces main binding.

Within HTTP activation, only isolated staging/lifecycle modes omit the four hosted flags; distinct pinned keys, epoch, exact owned foundation/runtime identity and canonical origin are still required. Promotion additionally requires `AUTH_DEPLOYMENT_READY=true`. Attestation flags alone cannot bypass new runtime routes: they run the anonymous image/source check themselves. Staged app eligibility is absent app (plan/apply only) or the exact owned no-ingress, single-container 1/1 configuration using the accepted image and runtime bindings. Public/internal ingress, different identity/environment/image/key/epoch, extra commands/containers or changed capacity are refused, including immediately before write.

Bicep exposes one boolean `staged`, set only by these fixed routes. Compiled-template validation permits exactly two configuration/scale combinations: staging omits the ingress property, min/max 1/1 and no scale rules; release retains external HTTPS 8080 and min 0/max 1 with its existing HTTP rule. Both retain 0.25 CPU/0.5Gi, 30-day/1GB-day logs and the same resource manifest. No arbitrary ingress/scale overrides, new role/resource kind or registry credentials are admitted.

Activation receipts add source/image/isolated/revision/replica coordinates; `releaseAcceptance=false` means source orchestration is never hosted acceptance. Stage receipts are not release assertions. Stop confirmation requires all revisions inactive and actual replica lists empty; resume requires selected-replica local health and fails when existing tools/context are unavailable. The service image installs curl and jq at build time while retaining runtime UID/GID 1654; verify the actual deployed image and private-file options before acceptance. Disabled Dapr and empty scaling rules accept absent/null readbacks while enabled/malformed configurations remain refused. See the runbook for private runtime-MI evidence and persistent storage/log/vault charges after compute stops.

## Independent cleanup Job

`cleanup.bicep` adds `${AUTH_APP_NAME}-cleanup` as a dedicated user-assigned identity and a separately owned Container Apps Job. They share a name across different resource types. The four existing branch-OIDC operations are `cleanup-identity-plan`, `cleanup-identity-apply`, `cleanup-plan`, and `cleanup-apply`. Identity bootstrap writes only the UAMI; Job apply writes only the Job. Both inspect the existing owned environment, log workspace, storage account and exact Table. The HTTP app, signer identity, vault and Blob are outside their write set. Normal foundation/runtime manifests and release gates remain unchanged.

All four require the accepted full current-main source SHA. Job operations independently anonymously attest the desired public `ghcr.io/repoagent/controller-auth@sha256:…` image/source, require the external `AUTH_DEPLOYMENT_EPOCH`, and recheck immediately before writing. They do not build, publish or recompose an image. Identity-only bootstrap requires no image, signing configuration or hosted flags. The operator grants access before any scheduled Job exists; no helper attempts a grant or treats ARM access as data-plane access.

An existing owned Job must retain exact cleanup identity, environment, epoch, command, environment allowlist and capacity. Its prior immutable digest in the approved repository may differ from the desired accepted digest. That observed prior digest must remain stable through planning; observing it does not attest its historical source. Apply may replace it with the desired image, including when restoring Manual to Schedule. Actual post-apply readback must match the desired digest. Fresh observations of every dependency and UAMI/Job are required; a missing/mismatched readback produces an unconfirmed result requiring inspection, even after ARM success. The Job might already have started: failure is not proof no execution ran.

The Consumption Job attaches only the cleanup identity, with exactly four environment bindings: `Broker__TableEndpoint`, `Broker__TableName`, `Broker__DeploymentEpoch`, `Broker__ManagedIdentityClientId`. It runs `dotnet RepoAgent.Auth.dll cleanup --once`, one container, 0.25 vCPU/0.5Gi, UTC cron `* * * * *`, parallelism/completion 1/1, retries 0, timeout 90 seconds. No HTTP ingress/probes, signing/wrapping/App/Blob configuration, secrets, registry credentials, extra/init containers or mounts are supplied. Reusing the signer identity is forbidden even without signing variables. Conditional identity property references are lazy; the identity bootstrap never needs the absent Job.

`cleanup-rbac.bicep` is operator-only. Its parameters are `appName`, `storageName`, `tableName`; it assigns built-in Storage Table Data Contributor `0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3` to the actual cleanup principal at the exact broker Table. Review ownership, principal, assignment GUID, role and scope with separate role authority before applying it. The Contributor workflow never invokes it or retrieves storage keys. This role permits table-wide operations including table management, not row-only redaction; the cleanup code remains trusted to preserve authority/accounting. Actual cleanup-MI access and denial on other tables, Blob and keys/signing remain live acceptance.

Cleanup receipts report operation/source, desired image/epoch where applicable, exact Job/UAMI/environment/Table IDs, platform-issued client/principal IDs, cleanup configuration and capacity plan, `applied` and `releaseAcceptance=false`. An identity plan for an absent UAMI has null identity values; no IDs are fabricated. The existing local cost-plan also includes `cleanupCapacityPlan`; it does not deploy a budget or alerts.

The process remains bounded to 60 seconds, 128 SDK Table dispatches, 32 SDK batch dispatches, 16 logical query-page calls (32 entities/page) and 40-dispatch/8-batch whole-turn admission. SDK counts include authentication reissues and failures, not measured wire sends. The reviewed Azure.Core1.62.0/.NET10.0.13/direct HTTP1.x/no handler authentication reference envelope permits a conservative 4 sends per SDK dispatch: 512 total Table/128 batch sends per start. Batches are already included in total. This calculation is not executing-runtime attestation or a bound on identity/DNS/TCP/TLS/intermediary work; establish the actual image/runtime scope before using it. Direct cleanup Table transport disables proxies, redirects, cookies and handler authentication; proxy-required connectivity is unsupported.

At 1,440 nominal starts/day, the envelope is 184,320 SDK Table/46,080 SDK batch dispatches, with scoped calculated send allowances 737,280 total/184,320 batch. At 90 seconds/start, compute arithmetic is 129,600 replica-seconds, 32,400 vCPU-seconds and 64,800 GiB-seconds/day. Existing logs remain 30 days/1GiB daily quota. These are per-start arithmetic, not observed traffic, prices, a strict daily spend cap or global serialization: minute starts can overlap 90-second executions or delayed launches. Budget currency/amount/dates/recipients and real alert delivery are still explicit operator inputs. Log capping can conceal failures.

**Capacity remains unresolved.** One turn handles one allocation, not 32 entities. Four initialization reads and ordinary retained issuance turns costing 17–18 SDK dispatches allow at most five such turns with the current 128/40 admission budget. Four competing streams yield roughly 1,800 issuance-payload visits/day: about 54.6 hours for 4,096 entries before latency/fault/terminal overhead. This supported representative counterexample defeats a universal 24-hour sweep claim; it is not an SLA. Retained capacities remain 256 enrollment/4,096 issuance. Do not silently lower issuance to 64 (30-day audit would admit about two new issuances/day), assume parallelism scales shared cursors, or select the separate UNSELECTED 650/130 budget candidate here.

`PayloadPurgeAt` is earliest eligibility, usually original clock +24 hours. The user must still choose strict physical deletion by that instant versus eligibility plus an explicit accepted lag/capacity envelope. No clock or retention choice is made by this infrastructure. `AUTH_CLEANUP_READY` remains false; #35/#28 retain actual scheduler/throughput/zero-HTTP24h evidence and the [suspension/drain procedure](azure-auth-deployment.md#cleanup-schedule-suspension-and-drain).
