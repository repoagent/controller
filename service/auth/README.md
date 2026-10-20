# Authentication service: enrollment, accounted issuance and self-revocation

[ADR 0006](../../docs/adr/0006-use-repository-agent-variables-for-approval.md) selects direct repository Agent variables for the new MVP. The source and API/marker examples below still implement the **legacy GitHub Environment provider** merged in #52. Agent-variable [provider #68](https://github.com/repoagent/controller/issues/68) and [host #53](https://github.com/repoagent/controller/issues/53) remain pending; draft PR #67 is an unmerged Environment prototype. No new setup/marker wire version has been selected. Never reinterpret existing Environment markers as Agent-variable approval. The frozen v1 controller file and runtime profiles remain unchanged.

The pending provider will read only direct `/repos/{owner}/{repo}/agents/variables/{name}` coordinates with broker-only `agent_variables:read`; organization/inherited fallback and Environment creation/identity are excluded. The host needs GitHub-authorized Agent-variable write access; controller tokens get no approval access. Published App permissions and a successful host GET do not prove installation-token scope or private-plan availability. Preserve all existing quotas, possession, private-file-before-ack, durable closure, known/unknown accounting and restore/replacement gates during adaptation; see [the current design contract](../../docs/auth-repository-approval.md).

This partial credential broker implements `DELETE /v1/devices/{deviceId}`. A valid enrolled device secret closes its grant and records a durable receipt without deleting outstanding-token evidence. `204` means future broker issuance is closed; it does not invalidate already issued GitHub tokens. The empty-body `POST /v1/devices/{deviceId}/installation-token` implements the frozen v1 token contract for acknowledged active grants. The enrollment routes below create those grants after independently verifying repository-held approval. `/health/live` reports process liveness; `/health/ready` always returns `503` until the full provider exists.

## Configuration

Production requires these environment variables:

| Variable | Value |
| --- | --- |
| `Broker__TableEndpoint` | HTTPS Table service origin, such as `https://ACCOUNT.table.core.windows.net` |
| `Broker__TableName` | Existing dedicated broker table |
| `Broker__DeploymentEpoch` | External epoch identifier, 1–128 ASCII letters/digits, `-` or `_` |
| `Broker__ManagedIdentityClientId` | Optional user-assigned identity client ID; otherwise system-assigned identity |

The service uses `ManagedIdentityCredential` only. It never creates the table or loads account keys, developer credentials, or emulator connection strings. Provision the table in the storage account planned for auth infrastructure and grant the runtime identity **Storage Table Data Contributor** at that table's scope. Deployment Contributor and Blob key-ring access do not grant Table data access. Actual Azure managed-identity acceptance remains required before hosted release. Production ingress must require TLS; the container listens on internal HTTP port 8080 behind that ingress.

Keep the deployment epoch outside the restored table backup. Recovery must fence old workers/access and establish a new epoch; restoring both data and its old epoch must never resurrect grants. Explicit cleanup and backup/restore are separate delivery work. See [the storage decision](../../docs/adr/0005-use-table-storage-for-broker-state.md).

## HTTP behavior and limits

Send `Authorization: Bearer <deviceSecret>` without a request body or client-selected scope. The frozen v1 grammar permits 43–128 base64url secret characters generated from at least 256 random bits, and 1–128 ASCII base64url device-ID characters. Only SHA-256 hashes of the UTF-8 secret text are persisted; comparison is constant-time.

- `204`: authenticated committed revocation, or a valid authenticated retry with matching closed gate/device and receipt.
- `401`: missing/invalid secret, missing grant, wrong binding/epoch, or expired device (including an expired retry).
- `503`: unavailable, malformed or unresolved durable state, exhausted conflicts, or request timeout. Retry safely with the same device secret; an ambiguous response may follow a committed revoke.
- `429` with `Retry-After`: request or concurrency budget exhausted before storage access.
- `400`: a DELETE with a body.

Built-in global limits use one fixed partition, without attacker-controlled per-IP/device state or a request queue. They apply to all non-health routes, including future routes. These per-process limits reduce resource/spending risk; they are not a distributed DDoS defense or guaranteed spend cap. Replica, CPU/memory, log-budget and Azure spending controls remain infrastructure acceptance work. Arbitrary forwarded headers are not trusted.

| Variable | Default | Allowed range |
| --- | --- | --- |
| `Broker__RequestLimitPerMinute` | 120 | 1–10,000 |
| `Broker__ConcurrentRequestLimit` | 8 | 1–128 |
| `Broker__RequestTimeoutSeconds` | 15 | 1–60 |

Kestrel caps request bodies at 64 KiB, headers at 16 KiB/64 fields, connections at 128, header arrival at 10 seconds and idle keep-alive at 30 seconds; default minimum data-rate protections remain enabled. Health paths perform no remote work and bypass application budgets. SDK automatic retries are disabled; conflicts get at most four complete authorization evaluations within the request deadline. Cancellation propagates to every SDK call. Request info logs and Azure info logs are suppressed; the service does not log secrets, entities or SDK response/error bodies, nor write audit records for invalid/throttled traffic.

## Durable shape

One dedicated table contains routing hints in `locator:<deviceId>` partitions (row `locator`) and authority under `repository:<immutableNumericGitHubRepositoryId>` partitions. A locator supplies `RepositoryId` and `DeviceId` but never authorizes a request.

| Row | Required properties |
| --- | --- |
| `gate` | `RepositoryId`, `Epoch`, `OwnerDeviceId`, `State` (`active`/`closed`), `Sequence` (nonnegative Int64) |
| `device:<deviceId>` | `RepositoryId`, `DeviceId`, `Epoch`, `SecretHash` (64 hex digits), `ExpiresAt` (DateTimeOffset), `State` (`active`/`revoked`) |
| `revocation:<deviceId>` | `RepositoryId`, `DeviceId`, `Epoch`, `RevokedAt` (DateTimeOffset) |

Revocation conditionally replaces gate and device using both observed ETags, creates one receipt in the same entity-group transaction, and preserves all other properties and attempt rows. A conflict rereads and reevaluates the whole decision. No wildcard updates, upserts, grants recreated on missing data, partition scans or production seed endpoint exist. Read entities are capped at 128 properties and 64 KiB of JSON; the three-operation batch stays below Azure's 100-operation/4 MiB limits. Future admission and cleanup must preserve these accounting and partition rules.

## Verification

Run `bash verify.sh` from this directory with .NET SDK 10.0.402, Docker and Python 3 installed. It starts a disposable pinned Azurite Table container on an ephemeral localhost port, runs actual HTTP-to-Table tests and rejects zero executed tests. Shared development credentials and direct enrollment fixtures exist only in the test project. For test development, `AUTH_TEST_TABLE_ENDPOINT` selects an existing local Azurite Table endpoint; the test default is localhost:10002. The production app never reads that variable.

Azurite 3.37.0 image `sha256:830430c1da1a2d537e08f3e6764dd1f5ae00cf0346bcaf625b968ec3f0971fd5` was separately qualified with Azure.Data.Tables 12.13.0: stale gate/device ETags rolled back all changes, valid batches retained accounting, concurrent same-version clients had one winner, and data survived storage restart. Application tests protect the owned revocation path. Azurite Table is a preview emulator; it cannot prove real Azure identity, production consistency, recovery or hosted readiness.

Build the Linux amd64, numeric nonroot image with `docker build --platform linux/amd64 -t repoagent-auth-local service/auth` from the repository root. No cloud deployment or image publication is performed by this directory.


## Issuance configuration and boundaries

Issuance is registered when `Auth__SigningKeyUri` is configured. It additionally requires `Auth__DataProtectionKeyUri` (both version-pinned HTTPS Key Vault key IDs), `Auth__DataProtectionBlobUri` (HTTPS key-ring blob, no SAS), and `Auth__DataProtectionApplicationName` (the shared key-ring application discriminator). These are the existing infrastructure outputs. Without signing configuration, token requests return 503 and self-revocation remains available. The server owns App ID 5387625, slug `repo-agent-connector`, and fixed `github.com`/`api.github.com` origins. Neither an App private key nor a human GitHub credential is loaded into this service.

`ManagedIdentityCredential`, `CryptographyClient`, Azure Blob Data Protection storage and Key Vault key wrapping provide the production boundaries. Signing, Table, Blob and wrapping SDK clients disable retries and use three-second network timeouts. GitHub redirects, cookies and automatic POST retries are disabled. GitHub bodies are bounded at 64 KiB and calls at three seconds. Signing uses a persisted reservation's fixed `iat=reserved−60s` and `exp=reserved+120s`; a delayed worker never refreshes either claim.

The request has a 15-second issuance deadline and eight GitHub reads. Each of the two fresh observations uses the repository's App installation lookup and the verifier's exact accessible-repository listing. Legacy grants then verify Environment identity and its variable; v2 grants use the direct Agent-variable listing described below. Release checks both observations from their oldest read start against ten seconds. No positive observation is cached. Before any GitHub observation, a definite global or repository admission ETag conflict permits a full fresh authority/quota evaluation, with at most four admission evaluations shared by verifier and runtime acquisition. The original operation ID and JWT timestamps remain fixed; a committed global precharge is reused, while a definitively rolled-back global charge is freshly evaluated. Changed binding/state, exhausted budgets and unknown outcomes deny. Post-observation conflicts deny rather than reusing stale observations; no mint is replayed. These checks do not make GitHub and Table atomic or establish continuous approval history. An isolated 404 denies without declaring withdrawal. A successfully read changed identity/value closes the grant monotonically.

The legacy verifier profile is exactly `environments:read` and `actions:read`; the v2 Agent-variable verifier is exactly `agent_variables:read`. Both allow only implicit `metadata:read`. It is never returned, never renews device activity and can be acquired internally without an active runtime grant. Its encrypted durable credential is reused until five minutes before actual expiry. A reserved/sending/unverifiable acquisition blocks subsequent acquisition indefinitely. Runtime profiles remain exactly polling `discussions:write` or webhook plus `repository_hooks:write`; actual returned permissions and repository scope must match.

A single `global:issuance-v1` partition precharges fixed UTC minute counters and one of 128 fixed slot keys before the repository transaction. Per-minute admission/read/sign/mint limits are 120/600/20/20 globally and 12/60/2/2 per repository. Optional `Broker__Issuance__{Repository|Global}{Admissions|Reads|Signs|Mints|Attempts}` settings may only lower the corresponding reviewed ceiling; no new setting is required. A runtime reservation conservatively precharges all eight reads; a verifier reservation charges its own admission/sign/mint. Repository capacity is eight combined attempts, including existing gate accounting. The global precharge and repository commit are separate transactions; failed or unknown second commits burn the global slot and work charges. No timeout compensation exists. Enrollment applies the same work budgets plus 4 pending/repository, 64/global, and an absolute 15-minute deadline.

Every repository authority change conditions the common gate, device and relevant attempt ETags. A persisted send claim precedes exactly one POST; restart never replays it. Known tokens are protected under repository, epoch, operation and purpose before release. Revoke-first prevents release; release-first remains accounted even if its response is delayed. Client disconnect after the send claim does not cancel server-owned completion. Failed known-token operations attempt one DELETE and durable settlement under an additional bounded six-second finalization allowance; failed finalization preserves evidence. Only a committed authorized runtime release advances 90-day inactivity.

This source slice deliberately does not reclaim capacity, including settled/expired rows. The independent cleanup/reconciliation slice #54 must prove durable settlement and cessation of the matching worker before reclaiming slots; fixed-window charges are never refunded. An unresolved mint, lost commit response or orphan global slot does not age out. At saturation the service denies; explicit operator reconciliation may be required. Neither JWT expiry nor changing the restore epoch settles a previously unknown token. Public enrollment remains a source-level integration surface; sustained hosted availability requires cleanup and live provider acceptance.

## Legacy Environment approval marker encoding

`ApprovalMarker.Create` defines the compact canonical JSON v1 marker, in this property order:

```json
{"version":1,"githubHost":"github.com","appId":5387625,"appSlug":"repo-agent-connector","repositoryId":123456,"repository":"owner/project","installationId":789,"deviceId":"DEVICE_ID","challenge":"43_CHARACTER_BASE64URL_CHALLENGE_FROM_32_BYTES","profile":"polling","policyVersion":"repository-approval-v1","epoch":"DEPLOYMENT_EPOCH","environment":"approval/environment","environmentId":987,"variable":"APPROVAL"}
```

The example identifiers/challenge are explanatory placeholders. Enrollment must generate a fresh 32-byte challenge, use unpadded base64url (43 characters), and generate the marker with this canonical serializer. The pending identity becomes the device identity; both the environment name and numeric ID are bound. The device secret is separate and never included. The operator preview may pretty-print these fields, but the written variable value is the exact compact string. This encoding does not change the frozen consumer enrollment or token-response schemas.

Before any issuance, the service recomputes the marker from authoritative durable fields and rejects a stored marker that disagrees. It then compares the independently fetched GitHub value exactly. Copying either the public marker or device ID cannot authenticate a caller. The host revoke tool continues to fingerprint the exact decoded value; it need not parse the marker.

## Verification and remaining acceptance

Tests use real pinned Azurite HTTP/Table transactions and real Data Protection envelopes with temporary disk key rings. Only GitHub and Key Vault signing transports are substituted for deterministic failures. These prove project orchestration and durable accounting, not real GitHub permission compatibility, managed-identity roles, Key Vault JWT acceptance, production Blob/key-ring recovery, Azure Table consistency, scale-to-zero cleanup or safe owner replacement. Those remain mandatory live release gates (#50/#35/#34); `/health/ready` stays 503.

The test project pins ASP.NET hosting, `System.Security.Cryptography.Pkcs` and `System.Security.Cryptography.Xml` to 10.0.13 with NuGet auditing enabled. Azure Data Protection introduced transitive 10.0.10 cryptography packages flagged by [PKCS advisory](https://github.com/advisories/GHSA-846f-f764-76mx) and [XML advisory](https://github.com/advisories/GHSA-r955-8q5j-3h83); 10.0.13 is their documented patched .NET 10 version. Production shared-framework servicing remains an image/release responsibility.


## Legacy Environment repository-held enrollment API

Enrollment is registered only with issuance configured and an explicit `Broker__BrokerOrigin`, a canonical HTTPS DNS origin without path, credentials, query or fragment. This origin is written into the frozen v1 enrollment file; request Host and forwarded headers never select a secret destination. Missing origin leaves enrollment unavailable. Readiness remains 503. The installer integration and live provider acceptance remain separately owned.

The host first creates/reuses the selected GitHub Environment through its own authority, then privately persists a random 32-byte recovery credential and a GUID idempotency ID (32 hexadecimal characters, no separators). Host GitHub credentials never reach this service. All four operations use bearer authorization and no-store responses:

| Operation | Body and success response |
| --- | --- |
| `POST /v1/enrollments` | `idempotencyId`, positive numeric `repositoryId` hint, `repository`, `environment`, `variable`, and `profile` (`polling` or `webhook`); returns `pendingId`, `state`, exact `marker`, `deadline` |
| `GET /v1/enrollments/{pendingId}` | Empty body; recovery bearer; own status only, without provider work |
| `POST /v1/enrollments/{pendingId}/completion` | Empty body; recovery bearer; returns `completionId` and `enrollment` containing precisely the frozen v1 fields |
| `POST /v1/devices/{deviceId}/acknowledgment` | `{ "completionId": "..." }`; bearer is the separate delivered device secret; returns `{ "state": "active" }` |

Start may return `discovering` after a recovered interrupted operation; only a non-null marker permits host approval. The host writes the exact returned marker, polls completion at intervals of at least five seconds, and atomically persists the owner-only enrollment file before acknowledgment. Pending marker coordinates belong in separate host configuration. Completion may instead return pending status while awaiting approval. Responses never include a verifier credential or an observed third-party variable value. `401` denies possession, `409` denies conflicting binding/ownership, `410` denies expiry/terminal transitions, `429` denies admission, and `503` denies unavailable/unverifiable work. Retry the identical start after response loss; never generate a new idempotency ID to recover it.

The immutable candidate repository ID routes admission but is not authority. The broker reserves cost before App-JWT installation discovery, persists the discovered installation, and claims one exact-ID/read-only verifier POST before sending. Its returned sole numeric ID and original requested repository name must both agree, and an independent authenticated repository listing and Environment numeric identity must agree, before any marker is returned. Name/ID substitution cannot redirect the binding. The marker uses canonical returned repository/Environment names and uppercase variable name. No device or incumbent owner is fabricated for bootstrap.

Global admission atomically records idempotency/body/recovery-digest binding and reserves the candidate's share of both caps. Repository admission then records its gate and pending row. Staged and uncertain starts retain capacity; no cross-partition rollback is claimed. Repeated observations reuse the pending row and share a durable work interval across discovery, completion and acknowledgment. Each provider operation reserves eight reads and a signing call; mint attempts additionally reserve mint and outstanding capacity. A sending/unknown mint never retries automatically. A definitely unsent reservation can resume only within its original signing deadline. No expiry timer erases unresolved authority.

Completion conditions the common gate, pending and verifier attempt plus any existing device; it admits only its exact known, validated internal verifier continuation, never an incumbent, another pending attempt or unknown authority. It stores one encrypted completion and unacknowledged device atomically, then publishes a routing-only locator. Lost responses recover the same identity and envelope only after fresh checks. Runtime authentication rejects unacknowledged devices. Ack freshly checks approval and all original deadlines/barriers, activates once, starts 90-day inactivity and retires recovery authority. Duplicate active ack performs no activity extension. Revocation can close an unacknowledged device, and an expired pending deadline cannot be revived.

Approval reads prove absence through a complete bounded variable listing on the verified Environment; isolated 404, denied access, malformed responses or exhausted page/read bounds deny without claiming absence. Before first positive approval, absence/mismatch stays pending; afterward verified withdrawal closes permanently. Conflicts after observation discard that decision. GitHub/Table atomicity and identical delete/recreate history are not claimed.


## Repository Agent-variable enrollment v2

V2 implements [ADR 0006](../../docs/adr/0006-use-repository-agent-variables-for-approval.md) with a direct repository Agent variable. The Environment routes above remain legacy v1. The host installer adaptation, independent conformance review and live provider acceptance remain separate gates; readiness remains 503.

Use `POST /v2/enrollments` with exactly `idempotencyId`, positive numeric `repositoryId`, `repository`, `variable` and `profile` (`polling` or `webhook`). Environment fields, arbitrary permissions, duplicate and unknown properties are rejected. `GET /v2/enrollments/{pendingId}`, `POST /v2/enrollments/{pendingId}/completion` and `POST /v2/devices/{deviceId}/acknowledgment` retain the corresponding legacy bodies, credentials, responses and deadlines. Setup/status/completion/ack routes reject a pending grant of the other setup version before provider work. The shared v1 token/revoke routes and the frozen v1 controller enrollment file remain unchanged; runtime provider selection comes from authenticated durable grant state.

New rows persist approval kind, setup version and policy together. Agent grants use `repository-agent-variable`, `2`, `repository-agent-approval-v2`; new legacy rows use `environment-variable`, `1`, `repository-approval-v1`. Historical rows without discriminators remain subject to their complete legacy binding. Partial, unknown or inconsistent discriminators deny access. Pending/device/verifier relationships must agree. V2 request digests and protected payload purposes are domain-separated; legacy digests and protection purposes remain compatible. Both kinds share one admission/idempotency namespace, all quotas and one repository owner gate. Switching versions cannot bypass an incumbent or unresolved authority.

The canonical compact v2 marker has this property order:

```json
{"version":2,"approvalKind":"repository-agent-variable","githubHost":"github.com","appId":5387625,"appSlug":"repo-agent-connector","repositoryId":123456,"repository":"owner/project","installationId":789,"deviceId":"DEVICE_ID","challenge":"43_CHARACTER_BASE64URL_CHALLENGE_FROM_32_BYTES","profile":"polling","policyVersion":"repository-agent-approval-v2","epoch":"DEPLOYMENT_EPOCH","variable":"APPROVAL"}
```

The example is explanatory. The marker contains no secret, Environment identity or invented immutable variable identity. Setup generates a fresh challenge and uppercases the selected variable name. Private recovery is persisted by the host before start; the private device receipt is persisted before fresh acknowledgment. The broker validates the App installation using its App JWT, then verifies the exact numeric repository and original requested name using both the internal mint response and an independent authenticated repository listing before returning approval material. Human host credentials cannot perform the App-JWT lookup and never enter the broker. The host validates repository intent, trusted broker origin, App/profile and returned binding.

The broker requests only `agent_variables:read` for its exact-repository internal verifier; returned scope must match, allowing only implicit metadata read. Known token material is durably protected/accounted before rejection or revocation. Controller polling/webhook scopes exclude approval access. Names or timestamps are not immutable variable identity; no organization or inherited-variable fallback exists.

Each Agent observation uses only the direct `/repos/{owner}/{repo}/agents/variables` listing with `per_page=30`, at most two pages and 60 entries. Installation and repository validation plus listing consume at most four reads; both runtime observations fit the existing eight-read budget. Counts, cardinality, unique names and string values must be consistent. Freshness starts at the first contributing read. Oversize, incomplete, malformed or denied results and isolated404 are unverifiable.

A complete single response with at most30 entries can establish absence. A selected value actually observed in a complete valid listing can establish presence or mismatch. **Multi-page nonappearance is unverifiable:** concurrent insertions/deletions can move an unchanged marker across page boundaries despite stable totals and unique names. Above30 variables, disappearance may require manual broker closure. No retry or snapshot guarantee is inferred. Before first positive approval, confirmed absence/mismatch leaves setup pending; afterward, confirmed withdrawal closes permanently. Restoration cannot reopen a grant. Identical delete/recreate entirely between observations has no continuous-history proof.

Host marker deletion, broker closure and outstanding-token settlement remain distinct outcomes. The accepted host GET/DELETE race may require manual reapproval. All existing accounting, deadlines, ETag gates, exact known-verifier continuation and old-authority barriers remain in force. This slice adds no cleanup, locks, automatic repair or takeover. Live host write/App-installation read/runtime denial and private-repository availability remain acceptance gates; public App metadata and host CRUD alone do not prove them.
