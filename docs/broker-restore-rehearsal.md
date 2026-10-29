# Isolated broker restore rehearsal

The tools supply an offline foundation plan and explicit operator Table commands. Each live run requires verified operator authority, capability settlement and worker/signing fences; there is no automatic restore or workflow activation. The [recorded isolated run](#verified-isolated-run-2026-10-29) demonstrates the restore, alert and owned-retirement boundary for [#230](https://github.com/repoagent/controller/issues/230).

## Plan the foundation

From an accepted clean source checkout, choose a fresh run identifier containing a UTC calendar date and six lowercase hexadecimal characters:

```sh
python3 scripts/delivery/auth-restore-plan.py --run-id 20261029-a1b2c3 > restore-plan.json
```

The command uses only the Python standard library and writes JSON to stdout. It never signs in, contacts Azure, reads credentials, initializes Table rows or deploys resources. It fixes the intended subscription, resource group and region, derives run-owned names, and lists the required operator checks. Do not reuse the example identifier without checking availability. A plan does not prove resource freshness or permissions.

The app name is `r230-<runId>` (20 characters), leaving room for its derived `-cleanup` Job name (28 characters) within Azure's 32-character limit. Earlier plans used `repoagent-r230-<runId>` and could create a foundation and API before cleanup Job creation failed. A corrected plan requires a fresh run identifier and a new resource inventory; do not apply it over an earlier partial run or reuse that run's storage, wrapping key or Table state. Record and fence the earlier attempt separately before retiring its resources. This naming correction does not migrate or delete anything, and compiled checks do not establish corrected Azure deployment acceptance.

The plan targets `infra/auth/restore-foundation.bicep`. It creates an isolated environment, workspace, storage account, `brokerstate` Table, Data Protection Blob container, runtime and cleanup identities, Insights and one new wrapping key. It references the existing `repoagent-auth-kv` vault without redeploying it. No API, cleanup Job, role assignment, action group or alert is enabled. The Table remains uninitialized and cannot admit enrollment.

Compile with the repository's pinned Bicep CLI, and inspect the compiled template before any cloud action:

```sh
"$HOME/.azure/bin/bicep" build infra/auth/restore-foundation.bicep --outfile /tmp/restore-foundation.json
```

An operator with separately established authority can then request an Incremental what-if against the fixed subscription and group recorded in the plan, passing exactly the same `runId`. Require only new run-owned resources; reject changes to the shared vault, existing keys, production storage, app, Job, identities or their roles. ARM key creation returns an existing key without changing it, so a successful response alone does not prove a fresh wrapping key. Inspect absence before apply and reject an unexpected existing name. The existing deployment identity's GitHub trust and role are unchanged. This tool has no apply mode.

## Signing, wrapping and storage isolation

The rehearsal runtime uses the existing App signing key through **its own exact-key read/sign assignment**. It uses the new wrapping key through an exact-key read/wrap/unwrap assignment and its own Blob container and Table. The cleanup identity receives only its own Table access. The reviewed operator-only `runtime-rbac.bicep`, `cleanup-rbac.bicep` and `monitoring-rbac.bicep` can express these assignments after the foundation exists. Shared App signing authority is still App-wide authority; only the accepted immutable broker image may use it. Fence rehearsal signing by removing the rehearsal identity's assignment, never by disabling the shared signing key or changing production grants.

The wrapping key is created through the [ARM key resource](https://learn.microsoft.com/en-us/azure/key-vault/keys/quick-create-template#creating-key-using-arm-template-is-different-from-creating-key-via-data-plane). This uses control-plane `Microsoft.KeyVault/vaults/keys/write`, which the verified operator Owner role can supply; a vault-wide Crypto Officer data-plane grant is unnecessary. Any later temporary wrapping-key administrative access must be scoped only to the new key.

`main.bicep` keeps its existing default of provisioning the vault. The rehearsal wrapper explicitly passes `provisionVault=false`. The ordinary `deploy.sh` path does not select this mode; do not point it at the shared production vault with rehearsal names. Deploy the runtime only after reviewing its exact source, image, bindings and authority.

## Operator run

Use the operator's normal authorized GitHub CLI profile for real setup v4 enrollment. Keep its native credentials outside Git; no hosted E2E secret retrieval or new hosted handoff is needed. A fresh run-owned repository is preferable only if the normal App installation can select it. Otherwise use run-owned approvals in the existing private test repository while serializing with other test journeys. Repository admin access alone does not prove App installation scope. The App-only repository installation endpoint and OAuth-App installation listing cannot establish that scope using an ordinary personal access token; their401/403 responses are authentication-surface limits. Verify through the actual normal enrollment/provider path before claiming acceptance. Do not touch production Table rows or existing controller enrollments.

The operator Table helper below preserves EDM types and transfers state only between its two fresh rehearsal tables. Raw snapshots, protected payloads and credentials stay private. Preserve the isolated key ring, wrapping key and application discriminator separately. Cloud account keys, counter/clock edits and production Table operations are excluded.

The rehearsal API and cleanup Job must use the same exact accepted released image digest as production. Run-owned enrollment must first authorize successfully under the original external epoch. Settle every actual capability using positive durable evidence and original provider expiry plus the documented allowance, then stop/fence all old HTTP and cleanup replicas, signing and publishers. Restore the isolated Table under a fresh external epoch and show old issuance401 plus preserved pre-backup revocation. A new cleanup failure rule must target only the isolated Job and reuse the existing operator action group. Bind deliberate Failed/Succeeded execution evidence, Fired/Resolved native alerts and actual notifications to that isolated provenance. Existing production email evidence cannot replace it. Follow the full [restore and cleanup suspension procedure](azure-auth-deployment.md#cleanup-identity-bootstrap-and-scheduling).

After copying state, grant the runtime its target-Table access at that exact Table scope. Apply an API-only template that preserves the accepted image, Data Protection application/blob/wrapping key, origin, identity, ingress and scale, changing only the Table name and external epoch. Keep cleanup Manual on its unchanged binding until the old-grant rejection and no-write check finishes. The ordinary paired deployment would also reactivate cleanup scheduling; do not use it for this transition. Inspect the new ready revision, actual bindings and old inactive revisions/replicas before testing the unchanged old grant.

## Verified isolated run (2026-10-29)

Run `20261029-b7d4c2` used infrastructure source `77f3f48ea5935bfe23f6a1ecc60a11fa31cec7ea` and the same released Broker 1.2.0 image on API and cleanup as the production readback: `ghcr.io/repoagent/controller-auth@sha256:db0bb1cacfc6bdbfb582b022e65fe8359b942e5b3f41752601ca6052cae5d3be`. It used separate storage, key ring, wrapping key and identities, with normal operator setup-v4 enrollments and run-owned approvals in the existing private test repository. See [enrollment and initial alert evidence](https://github.com/repoagent/controller/issues/230#issuecomment-6491803911) and subsequent receipts on [#230](https://github.com/repoagent/controller/issues/230).

- A received a bound runtime GitHub token with HTTP 200 under the original `before` epoch. B was separately revoked before backup; its acknowledged revoked enrollment and typed revocation/owner-release receipt were recorded.
- All three actual capabilities settled after their original provider expiry plus five minutes. Exact ledger/attempt/release-proof relationships, publication closures, zero slots and zero outstanding counts were verified without editing clocks or accounting rows. HTTP/signing/publishers and cleanup were fenced before copying.
- Manual suspension preserved the Job's runtime/identity. Full history reconciliation accounted for 104 consecutive scheduled minute IDs plus one deliberate manual execution. Three already successful sole-worker executions had positive completion/end records and their original 1/1/retry-0 configuration captured before the first run; they were not outstanding at suspension. Their later history404 was not treated as zero replicas. Both sweeps observed the other 102 terminal executions with actual replica arrays empty. The first 499-second sweep was not a 120-second proof; the second full sweep took 64 seconds and found no delayed execution.
- The real typed CLI took a 30-row snapshot in two complete passes, restored into fresh `brokerrestore`, and verified exact application properties and EDM types. An API-only change selected that Table and a fresh external `after` epoch while preserving image and Data Protection bindings; cleanup remained Manual. The single unchanged old A grant then returned 401/no-store/empty body. Complete real SDK reads before/after found all 30 rows, every EDM property, ETag and Timestamp unchanged. B's exact typed revocation was preserved through snapshot/restore and that no-write comparison.
- The isolated deliberate execution-only Table-setting override produced a Failed execution; later ordinary scheduled runs succeeded. Its first request was rejected with HTTP400 for unsupported `volumes:null` and created no execution. The corrected request omitted null optional fields while preserving the image, command, identity and persistent Job configuration. The isolated rule Fired at 08:18:12 UTC and Resolved at 08:35:52 UTC; both actual operator emails were independently verified, sent at 08:18:17 and 08:35:56 UTC. The resolution time comes from the condition's resolved field, not its last-modified timestamp. A separate ordinary scheduled failure at 08:46 UTC had an unknown cause and is not counted as the deliberate failure. Aggregated alert values do not establish distinct execution counts or missing end times.

The old-epoch 401 is grant denial, not proof that A was revoked or that its former GitHub token was settled. The same request resolves the issuer before core authentication, but epoch rejection precedes admission, signing and minting in the implemented control flow; no exhaustive provider wire capture is claimed. B's pre-backup revocation and the positive capability settlements are separate proofs. No production state, shared signing key, existing enrollment or unrelated grant was used as rehearsal state. This bounded run does not establish the separate 24-hour zero-HTTP cleanup/retention acceptance.

### Retirement and preserved observations

The original A approval was normally withdrawn; conservative broker-closure output remained unconfirmed under the new epoch and was not relabeled A revocation or owner release. Before disposal, fresh post-fence SDK observations again matched the complete target content/system metadata and all 30 original source authority records, including positive settlement and publication closures. Final readback at 11:58:46 UTC confirmed all nine owned resources, ten original assignments and three custom definitions absent. The original client timed out after 600 seconds during environment deletion; its intent and journal were preserved. After the provider confirmed that deletion complete, a reviewed continuation deleted only the four remaining resources once. The environment delete was not repeated, and removed Table permissions were not recreated to force more reads.

The own wrapping key's single normal DELETE returned a successful [DeletedKeyBundle acknowledgment](https://learn.microsoft.com/en-us/rest/api/keyvault/keys/delete-key/delete-key) for the exact original key version, with deletion at 12:06:59 UTC and scheduled retention purge at 2027-01-27T12:06:59Z. An additional verification GET returned KeyNotFound, so the wrapper's original exit 1 remains recorded; a later 403 after temporary authority removal is not absence proof. The successful primary DELETE acknowledgment establishes normal soft deletion. Neither the delete nor a role grant was repeated to obtain a green wrapper result. The temporary exact-key administrative assignment was removed and its absence confirmed. No manual purge was performed, and future scheduled purge is not claimed as already completed.

Final preservation observations at 12:31:02 UTC retained the exact production Broker 1.2 API/Job images and readiness, enabled shared App signing key/version, native operator grant and existing enabled action group. Production state, epoch and enrollments were untouched; private evidence remains retained. This acceptance is for the recorded isolated run, not automatic recovery, production soak or the separate 24-hour cleanup objective. Future runs must independently verify ownership, post-fence authority and deletion results before claiming retirement; preserve partial/timeout/error observations rather than silently rerunning mutations.

## Typed offline Table commands

The standalone operator project requires the pinned .NET SDK and a native Linux/WSL filesystem. Build once:

```sh
dotnet build scripts/operations/RestoreTable/RestoreTable.csproj --configuration Release
operator_dll="$PWD/scripts/operations/RestoreTable/bin/Release/net11.0/RestoreTable.dll"
run_id=20261029-a1b2c3
private_dir="$HOME/.local/state/repoagent/restore230-$run_id"
umask 077
mkdir -p -- "$private_dir"
```

The existing directory must allow only its owner, have no symlink ancestors and be outside Git. Files are created with mode0600 and never overwritten. Keep this directory and its snapshot out of logs and uploaded artifacts. Windows ACLs are not inferred; use the native Linux filesystem in WSL.

The run ID fixes the only cloud endpoint to `https://ra23020261029a1b2c3.table.core.windows.net/` for this example. The CLI fixes source `brokerrehearsal` and target `brokerrestore`; it cannot select the foundation's **unused `brokerstate`**, a production account, another table, SAS or a connection string. Requests are also checked at the transport for the exact origin/table and only create/read/Add methods. Authentication uses only the human's native Azure CLI sign-in in the accepted tenant, with no credential copy, default-credential fallback or key export. Verify the account's ARM identity, subscription and run ownership before granting data access.

Fresh Table creation may need a temporary human [Storage Table Data Contributor](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/storage#storage-table-data-contributor) grant at the isolated storage account. [Account scope applies to all its tables](https://learn.microsoft.com/en-us/azure/storage/tables/authorize-access-azure-active-directory#resource-scope), including unused brokerstate, which this CLI cannot select. Revoke/split the grant to exact Table scopes after the command completes. Never grant a production account role or enable shared-key authentication for this procedure. Actual authorization and propagation require Azure evidence.

With traffic disabled, initialize only a source table created successfully by this command:

```sh
dotnet "$operator_dll" initialize --run-id "$run_id" --directory "$private_dir"
```

It calls CreateTable, records a private creation receipt, Adds the exact two activation quota rows with Int64 values, and verifies the persisted typed rows. It never accepts an existing empty table as fresh. A conflict, partial write or unknown response is unconfirmed; no reset, overwrite, compensation or automatic retry is provided. Configure the later rehearsal runtime with `Broker__TableName=brokerrehearsal`, leaving brokerstate unused.

After real enrollment, positive settlement and all-worker/signing/publication fences, write private `fence.json` with the following exact shape and your actual evidence references and observation time:

```json
{"schema":1,"runId":"20261029-a1b2c3","endpoint":"https://ra23020261029a1b2c3.table.core.windows.net/","writersStopped":true,"signingFenced":true,"capabilitiesSettled":true,"observedAt":"2026-10-29T00:00:00Z","evidenceReferences":["actual operator fence and settlement receipt"]}
```

This is an operator attestation, not an automated proof. The helper always reports `settlementProven=false` and `workerFenceProven=false`. It does not inventory authority or authorize restoring state. Then run:

```sh
dotnet "$operator_dll" snapshot --run-id "$run_id" --directory "$private_dir"
dotnet "$operator_dll" restore --run-id "$run_id" --directory "$private_dir"
dotnet "$operator_dll" verify --run-id "$run_id" --directory "$private_dir"
```

Snapshot follows every continuation page, including empty pages, in two complete passes; differing observed content refuses publication. This is drift detection, not a replacement for the actual fence. It stores explicit String, Boolean, Int32, Int64, finite Double, Guid, Binary and UTC DateTime EDM values, original application properties and a private canonical integrity digest. Provider Timestamp/ETag are excluded and regenerated. The schema, run/account/source binding, duplicates, ordering, types, values and digest are checked before restore can contact storage. Restore requires successful creation of the fresh brokerrestore table, records its creation receipt before the first Add, then verifies all typed content. It never merges/upserts, changes epoch, or repairs partial results. A second restore refuses an existing target.

Limits are 18,948 rows, 1,024 logical pages per pass at 32 rows per page, 64 MiB per snapshot, 128 total entity properties including system fields (124 application properties) and 64 KiB decoded application JSON per entity. Snapshot performs two passes and reports the second pass separately as `validationPages`. A command has a ten-minute cooperative deadline, zero SDK retries and three-second Table network timeout; underlying HTTP replays and identity requests are not a measured wire bound. Any cap, malformed data, incomplete read, conflict or timeout remains unconfirmed. stdout contains only operation/run/table/counts and outcome flags; private material and provider errors are not printed. Preserve failed private artifacts for operator inspection rather than rerunning against an ambiguous target.

For the explicit synthetic local operator journey against an available native loopback Azurite 3.35.0 Table endpoint:

```sh
dotnet run --project scripts/operations/RestoreTable.LocalCheck -- http://127.0.0.1:10002/devstoreaccount1
```

It exercises the actual closed CLI, fresh Int64 initialization, refusal of existing source/target, multiple real query pages, all supported EDM kinds and preserved revocation-like fields. It removes only the local tables it created. The endpoint must be literal IPv4 loopback, with no query/credentials; the public development key is accepted only there. The fixture is not real controller authority or Azure RBAC/empty-nonterminal-page acceptance, and is not a separate fake-provider adapter suite. Ordinary broker verification builds it and runs deterministic guard/codec tests without starting an emulator or contacting Azure.

## Verification contract

- **Protected behavior:** an offline rehearsal plan can only select run-owned data resources and a new wrapping key; the compiled foundation omits a shared-vault write and does not enable traffic or permissions.
- **Risk and seam:** the public plan CLI and compiled ARM deployment document are the operator's review boundary. Real Python processes and Bicep compilation cover project-owned validation and infrastructure composition without imitating Azure.
- **Boundary fidelity:** filesystem, argument parsing, JSON and Bicep are real; no provider is replaced. No test claims RBAC propagation, ARM freshness, managed identity behavior or hosted lifecycle acceptance.
- **CI ownership:** general Controller Python discovery exercises the pure plan CLI without requiring Bicep. Broker verification installs the pinned compiler and explicitly runs `python3 scripts/delivery/check_auth_restore_foundation.py -v` as a mandatory command. Both operator and check helpers trigger Broker CI and are attributed to the Broker release.
- **Meaningful red:** reversing the explicit existing-vault selection or conditional vault declaration makes the compiled resource safety check fail. CLI validation rejects traversal, shell-like suffixes, uppercase and malformed dates before producing a plan.
- **Residual risk:** the recorded run supplies bounded real-provider evidence, not automatic restore, future resource eligibility or the separate 24-hour cleanup objective. Every new run still requires its own complete inventory, positive settlement, fences and retirement evidence.
