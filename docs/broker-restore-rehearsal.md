# Isolated broker restore rehearsal

Issue [#230](https://github.com/repoagent/controller/issues/230) requires a real isolated restore and intentional cleanup-failure alert evidence. This first slice supplies an **offline foundation plan**, not a completed rehearsal or automatic restore. It introduces no workflow, HTTP route, runtime image or production state change.

## Plan the foundation

From an accepted clean source checkout, choose a fresh run identifier containing a UTC calendar date and six lowercase hexadecimal characters:

```sh
python3 scripts/delivery/auth-restore-plan.py --run-id 20261029-a1b2c3 > restore-plan.json
```

The command uses only the Python standard library and writes JSON to stdout. It never signs in, contacts Azure, reads credentials, initializes Table rows or deploys resources. It fixes the intended subscription, resource group and region, derives run-owned names, and lists the required operator checks. Do not reuse the example identifier without checking availability. A plan does not prove resource freshness or permissions.

The plan targets `infra/auth/restore-foundation.bicep`. It creates an isolated environment, workspace, storage account, `brokerstate` Table, Data Protection Blob container, runtime and cleanup identities, Insights and one new wrapping key. It references the existing `repoagent-auth-kv` vault without redeploying it. No API, cleanup Job, role assignment, action group or alert is enabled. The Table remains uninitialized and cannot admit enrollment.

Compile with the repository's pinned Bicep CLI, and inspect the compiled template before any cloud action:

```sh
"$HOME/.azure/bin/bicep" build infra/auth/restore-foundation.bicep --outfile /tmp/restore-foundation.json
```

An operator with separately established authority can then request an Incremental what-if against the fixed subscription and group recorded in the plan, passing exactly the same `runId`. Require only new run-owned resources; reject changes to the shared vault, existing keys, production storage, app, Job, identities or their roles. ARM key creation returns an existing key without changing it, so a successful response alone does not prove a fresh wrapping key. Inspect absence before apply and reject an unexpected existing name. The existing deployment identity's GitHub trust and role are unchanged. This tool has no apply mode.

## Signing, wrapping and storage isolation

The future rehearsal runtime uses the existing App signing key through **its own exact-key read/sign assignment**. It uses the new wrapping key through an exact-key read/wrap/unwrap assignment and its own Blob container and Table. The cleanup identity receives only its own Table access. The reviewed operator-only `runtime-rbac.bicep`, `cleanup-rbac.bicep` and `monitoring-rbac.bicep` can express these assignments after the foundation exists. Shared App signing authority is still App-wide authority; only the accepted immutable broker image may use it. Fence rehearsal signing by removing the rehearsal identity's assignment, never by disabling the shared signing key or changing production grants.

The wrapping key is created through the [ARM key resource](https://learn.microsoft.com/en-us/azure/key-vault/keys/quick-create-template#creating-key-using-arm-template-is-different-from-creating-key-via-data-plane). This uses control-plane `Microsoft.KeyVault/vaults/keys/write`, which the verified operator Owner role can supply; a vault-wide Crypto Officer data-plane grant is unnecessary. Any later temporary wrapping-key administrative access must be scoped only to the new key.

`main.bicep` keeps its existing default of provisioning the vault. The rehearsal wrapper explicitly passes `provisionVault=false`. The ordinary `deploy.sh` path does not select this mode; do not point it at the shared production vault with rehearsal names. Runtime deployment is a later reviewed slice.

## Remaining execution work

Use the operator's normal authorized GitHub CLI profile for real setup v4 enrollment. Keep its native credentials outside Git; no hosted E2E secret retrieval or new hosted handoff is needed. A fresh run-owned repository is preferable only if the normal App installation can select it. Otherwise use run-owned approvals in the existing private test repository while serializing with other test journeys. Repository admin access alone does not prove App installation scope. The App-only repository installation endpoint and OAuth-App installation listing cannot establish that scope using an ordinary personal access token; their401/403 responses are authentication-surface limits. Verify through the actual normal enrollment/provider path before claiming acceptance. Do not touch production Table rows or existing controller enrollments.

The later offline Table helper must preserve EDM types, paginate completely with all writers fenced, initialize only newly created Tables with Int64 Add operations, and restore by Add into a fresh Table. Raw snapshots, protected payloads and credentials stay private. Preserve the isolated key ring, wrapping key and application discriminator. Account keys, counter/clock edits and production Table operations are excluded.

The rehearsal API and cleanup Job must use the same exact accepted released image digest as production. Run-owned enrollment must first authorize successfully under the original external epoch. Settle every actual capability using positive durable evidence and original provider expiry plus the documented allowance, then stop/fence all old HTTP and cleanup replicas, signing and publishers. Restore the isolated Table under a fresh external epoch and show old issuance401 plus preserved pre-backup revocation. A new cleanup failure rule must target only the isolated Job and reuse the existing operator action group. Bind deliberate Failed/Succeeded execution evidence, Fired/Resolved native alerts and actual notifications to that isolated provenance. Existing production email evidence cannot replace it. Follow the full [restore and cleanup suspension procedure](azure-auth-deployment.md#cleanup-identity-bootstrap-and-scheduling).

## Verification contract

- **Protected behavior:** an offline rehearsal plan can only select run-owned data resources and a new wrapping key; the compiled foundation omits a shared-vault write and does not enable traffic or permissions.
- **Risk and seam:** the public plan CLI and compiled ARM deployment document are the operator's review boundary. Real Python processes and Bicep compilation cover project-owned validation and infrastructure composition without imitating Azure.
- **Boundary fidelity:** filesystem, argument parsing, JSON and Bicep are real; no provider is replaced. No test claims RBAC propagation, ARM freshness, managed identity behavior or hosted lifecycle acceptance.
- **CI ownership:** general Controller Python discovery exercises the pure plan CLI without requiring Bicep. Broker verification installs the pinned compiler and explicitly runs `python3 scripts/delivery/check_auth_restore_foundation.py -v` as a mandatory command. Both operator and check helpers trigger Broker CI and are attributed to the Broker release.
- **Meaningful red:** reversing the explicit existing-vault selection or conditional vault declaration makes the compiled resource safety check fail. CLI validation rejects traversal, shell-like suffixes, uppercase and malformed dates before producing a plan.
- **Residual risk:** actual resource eligibility, permissions, complete Table inventory, token settlement, worker drain, restore and notification delivery require the separately reviewed live run. This slice does not close #230.
