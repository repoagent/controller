# Credential broker

Optional [Discussion image publishing](../../docs/discussion-images.md) adds setup-v5 image profiles with runtime protocol v4 and policy `repository-agent-images-approval-v5`. They grant repository-wide Contents write only through fresh explicit enrollment; all earlier profiles retain their exact authority. Deploy this broker support before enrolling an image-capable controller.

The broker authenticates controller enrollments, verifies repository approval at GitHub and returns fresh repository-scoped GitHub tokens. The App signing key stays in Key Vault. **GitHub tokens and their issuance outcomes are never persisted by the broker.** See [the state and cutover contract](retained-state.md) and [ADR 0017](../../docs/adr/0017-keep-broker-tokens-in-request-memory.md).

## Authorization and revocation

Each runtime request authenticates the controller secret and immutable enrollment binding, verifies live approval, mints a fresh verifier/runtime token pair, validates actual scope/expiry and conditionally confirms the current enrollment before returning the runtime token. The verifier is request-local and receives bounded best-effort revocation. Failed/uncertain requests leave no issuance history or quarantine; the next request starts afresh under the same valid enrollment.

Closing an enrollment permanently denies that enrollment ID. Already-issued bearer tokens may remain valid until GitHub expiry, normally one hour from creation. The broker cannot individually revoke runtime tokens it no longer stores. This intentionally replaces the old positive capability-settlement/replacement barrier; removing or restoring an old approval never reactivates a closed grant.

## Enrollment and setup recovery

Setup admits one current pending enrollment per repository. Identical retries require the same original request and setup secret and retain the original 15-minute deadline. Another active/pending grant conflicts. A fresh attempt after closure/expiry creates a new random enrollment ID and challenge, requiring fresh repository approval.

The completion response contains the generated controller secret. Only that short recovery envelope is encrypted with the existing Data Protection Blob/Key Vault key ring. Store the private enrollment file atomically before acknowledgment. Acknowledgment clears the setup-secret hash and encrypted completion immediately; duplicate acknowledgments perform no write or lifetime extension. Unacknowledged/abandoned setup expires at its original deadline, with no additional 24-hour grace.

### Environment enrollment v1

The historical setup v1 request binds its exact Environment, numeric identity and variable. Its runtime is v1 with frozen polling/webhook permissions. Fresh installer selection is intentionally limited to the supported Agent-variable setup; legacy protocol support does not migrate old schema-1 storage.

### Repository Agent variable enrollment v2

Setup v2 binds a direct repository Agent variable with its original policy and frozen permissions. Clone profiles use runtime v2; ordinary profiles use runtime v1.

### Repository Actions variable enrollment v3

The historical v3 contract binds direct repository Actions variables and its own policies. Current controllers refuse retired Actions-v3 clone/settings enrollment files. Use fresh Agent-variable enrollment rather than rewriting policy fields.

### Repository Agent variable enrollment v4

V4 uses direct repository Agent variables, including the reserved `REPOAGENT_APPROVAL` name. Frozen profiles retain their original authority; explicit settings profiles add Agent variables read and use runtime v3. See [the approval contract](../../docs/auth-repository-approval.md).

### Canonical controller enrollment v5

V5 uses `enrollmentId` and `controllerSecret` in the private envelope and `enrollmentId` in the approval marker. It has its own Agent-variable policies and retains runtime versions 1–3 by profile. The installer must be upgraded before consuming repository-prefixed schema-2 IDs. Existing wire versions allow these opaque IDs; raw markers/credentials are never edited to migrate an enrollment.

## Public routes

Setup versions 1–5 expose:

- `POST /v{setup}/enrollments`: bounded request and setup-secret Bearer credential.
- `GET /v{setup}/enrollments/{pendingId}`: status with the original setup secret.
- `POST /v{setup}/enrollments/{pendingId}/completion`: recoverable completion after approval.
- `POST /v{setup}/devices/{enrollmentId}/acknowledgment`, or `/controller-enrollments/...` for setup v5: controller-secret credential and `{ "completionId": "..." }`.

Runtime versions 1–3 expose `POST /v{runtime}/devices/{enrollmentId}/installation-token` and `DELETE /v{runtime}/devices/{enrollmentId}`. Canonical aliases use `/controller-enrollments/{enrollmentId}/github-token` and `/controller-enrollments/{enrollmentId}`. The token request has no body and uses the controller secret as its Bearer credential. [Frozen runtime fields and origin/credential validation](../../docs/broker-protocol.md) remain in force.

Responses are uncached. 401 means missing/invalid/closed/expired enrollment; 409 means conflicting setup/binding; 429 means a work/admission limit; 503 means unavailable/malformed provider or storage/protection state. A transient failure does not require a new enrollment. A missing old schema-1 enrollment after the schema-2 cutover does require one.

## Storage and cleanup

[Schema 2](retained-state.md) requires a freshly created, explicitly initialized Table and a deliberate operator cutover. It stores one bounded current repository record plus fixed global counters. Successful token renewal never adds a row. The independent Table-only cleanup Job runs every five minutes and closes due setup/inactive enrollments in one paginated pass. No token cleanup, audit retention, durable cursors or secondary indexes exist in the current request path.

`Broker:CandidateRepositoryLimit` may lower the maximum 128 admitted repository records. Per-minute global and repository admission/read/sign/mint settings remain lowerable under `Broker:Issuance`. Old retained-issuance/enrollment and outstanding-attempt settings are retired. The fixed closed-generation record continues counting toward the repository bound; cleanup never resets global accounting or a restore epoch.

## Optional aggregate monitoring

Application Insights retains server error/exception observations and approximately 1% of routine request traces and Information/Warning logs. Redaction and aggregate metrics remain in force. No token values, secrets or response bodies belong in telemetry. Cleanup emits `completed`, `failures`, `examined`, `changed`, `conflicts` and `dispatches`; its rule emails on reported failures, incomplete coverage or missing completion/failure metrics over 15 minutes. Native Azure job failures remain enabled as diagnostic alert incidents without an email action: Azure can report `DeadlineExceeded` after the cleanup container exits successfully. Missing telemetry still alerts; a platform failure alone does not establish whether cleanup completed. Old cycle-age metrics/rules belong to schema 1 and must be replaced during cutover.

## Internal retained-authority observation

`--inspect-retained-authority` remains a historical, read-only schema-1 operational projection fixed to its accepted production identity/Table. It does not inspect schema 2, mutate either layout, prove current enrollment readiness or recall bearer tokens. Preserve historical restore/authority evidence as historical evidence.

## Version, local development and deployment

`dotnet RepoAgent.Auth.dll --version` or `--version --json` reads compiled version/source without credentials. Service versions are separate from enrollment and storage schema versions. [Broker releases](../../docs/broker-releases.md) and the [Azure runbook](../../docs/azure-auth-deployment.md) govern delivery and adoption.

The [local Aspire AppHost](local-development.md) composes local storage, wrapping and optional shared-vault signing. Its initializer creates schema-2 metadata only in a newly created loopback Table. Reset old emulator state or choose a fresh local Table for the storage cutover; it never resets an existing legacy Table.

A fenced schema-1 cutover may preserve valid active enrollments with their exact existing IDs, hashes, approval bindings and original expiry/counters. Frozen UUID-only IDs use one current routing row, deleted on closure/expiry/replacement. Existing controllers need no host access, approval rewrite or re-enrollment. See [current-state cutover](retained-state.md#fresh-table-initialization).
