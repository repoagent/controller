# Authentication service: accounted token issuance and self-revocation

This partial credential broker implements `DELETE /v1/devices/{deviceId}`. A valid enrolled device secret closes its grant and records a durable receipt without deleting outstanding-token evidence. `204` means future broker issuance is closed; it does not invalidate already issued GitHub tokens. The empty-body `POST /v1/devices/{deviceId}/installation-token` implements the frozen v1 token contract for fixture-created, acknowledged active grants. Public enrollment remains issue #52. `/health/live` reports process liveness; `/health/ready` always returns `503` until the full provider exists.

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

The request has a 15-second issuance deadline and eight GitHub reads. Each of the two fresh observations uses the repository's App installation lookup, the verifier's exact accessible-repository listing, the bound environment identity, and the selected variable. Release checks both observations from their oldest read start against ten seconds. No positive observation is cached. Before any GitHub observation, a definite global or repository admission ETag conflict permits a full fresh authority/quota evaluation, with at most four admission evaluations shared by verifier and runtime acquisition. The original operation ID and JWT timestamps remain fixed; a committed global precharge is reused, while a definitively rolled-back global charge is freshly evaluated. Changed binding/state, exhausted budgets and unknown outcomes deny. Post-observation conflicts deny rather than reusing stale observations; no mint is replayed. These checks do not make GitHub and Table atomic or establish continuous approval history. An isolated 404 denies without declaring withdrawal. A successfully read changed identity/value closes the grant monotonically.

The verifier profile is exactly `environments:read` and `actions:read`, allowing only implicit `metadata:read`. It is never returned, never renews device activity and can be acquired internally without an active runtime grant. Its encrypted durable credential is reused until five minutes before actual expiry. A reserved/sending/unverifiable acquisition blocks subsequent acquisition indefinitely. Runtime profiles remain exactly polling `discussions:write` or webhook plus `repository_hooks:write`; actual returned permissions and repository scope must match.

A single `global:issuance-v1` partition precharges fixed UTC minute counters and one of 128 fixed slot keys before the repository transaction. Per-minute admission/read/sign/mint limits are 120/600/20/20 globally and 12/60/2/2 per repository. Optional `Broker__Issuance__{Repository|Global}{Admissions|Reads|Signs|Mints|Attempts}` settings may only lower the corresponding reviewed ceiling; no new setting is required. A runtime reservation conservatively precharges all eight reads; a verifier reservation charges its own admission/sign/mint. Repository capacity is eight combined attempts, including existing gate accounting. The global precharge and repository commit are separate transactions; failed or unknown second commits burn the global slot and work charges. No timeout compensation exists. Pending limits (4/repository, 64/global, absolute 15 minutes) are defined for #52; no pending route is exposed here.

Every repository authority change conditions the common gate, device and relevant attempt ETags. A persisted send claim precedes exactly one POST; restart never replays it. Known tokens are protected under repository, epoch, operation and purpose before release. Revoke-first prevents release; release-first remains accounted even if its response is delayed. Client disconnect after the send claim does not cancel server-owned completion. Failed known-token operations attempt one DELETE and durable settlement under an additional bounded six-second finalization allowance; failed finalization preserves evidence. Only a committed authorized runtime release advances 90-day inactivity.

This source slice deliberately does not reclaim capacity, including settled/expired rows. The independent cleanup/reconciliation slice #54 must prove durable settlement and cessation of the matching worker before reclaiming slots; fixed-window charges are never refunded. An unresolved mint, lost commit response or orphan global slot does not age out. At saturation the service denies; explicit operator reconciliation may be required. Neither JWT expiry nor changing the restore epoch settles a previously unknown token. Do not enable public enrollment or claim sustained production availability until cleanup and live provider acceptance are complete.

## Approval marker encoding

`ApprovalMarker.Create` defines the compact canonical JSON v1 marker, in this property order:

```json
{"version":1,"githubHost":"github.com","appId":5387625,"appSlug":"repo-agent-connector","repositoryId":123456,"repository":"owner/project","installationId":789,"deviceId":"DEVICE_ID","challenge":"43_CHARACTER_BASE64URL_CHALLENGE_FROM_32_BYTES","profile":"polling","policyVersion":"repository-approval-v1","epoch":"DEPLOYMENT_EPOCH","environment":"approval/environment","environmentId":987,"variable":"APPROVAL"}
```

The example identifiers/challenge are explanatory placeholders. Enrollment must generate a fresh 32-byte challenge, use unpadded base64url (43 characters), and generate the marker with this canonical serializer. The pending identity becomes the device identity; both the environment name and numeric ID are bound. The device secret is separate and never included. The operator preview may pretty-print these fields, but the written variable value is the exact compact string. This encoding does not change the frozen consumer enrollment or token-response schemas.

Before any issuance, the service recomputes the marker from authoritative durable fields and rejects a stored marker that disagrees. It then compares the independently fetched GitHub value exactly. Copying either the public marker or device ID cannot authenticate a caller. The host revoke tool continues to fingerprint the exact decoded value; it need not parse the marker.

## Verification and remaining acceptance

Tests use real pinned Azurite HTTP/Table transactions and real Data Protection envelopes with temporary disk key rings. Only GitHub and Key Vault signing transports are substituted for deterministic failures. These prove project orchestration and durable accounting, not real GitHub permission compatibility, managed-identity roles, Key Vault JWT acceptance, production Blob/key-ring recovery, Azure Table consistency, scale-to-zero cleanup or safe owner replacement. Those remain mandatory live release gates (#50/#35/#34); `/health/ready` stays 503.

The test project pins ASP.NET hosting, `System.Security.Cryptography.Pkcs` and `System.Security.Cryptography.Xml` to 10.0.13 with NuGet auditing enabled. Azure Data Protection introduced transitive 10.0.10 cryptography packages flagged by [PKCS advisory](https://github.com/advisories/GHSA-846f-f764-76mx) and [XML advisory](https://github.com/advisories/GHSA-r955-8q5j-3h83); 10.0.13 is their documented patched .NET 10 version. Production shared-framework servicing remains an image/release responsibility.
