# Authentication service: durable device self-revocation

This partial credential broker implements `DELETE /v1/devices/{deviceId}`. A valid enrolled device secret closes its grant and records a durable receipt without deleting outstanding-token evidence. `204` means future broker issuance is closed; it does not invalidate already issued GitHub tokens. Enrollment and token issuance are not implemented. `/health/live` reports process liveness; `/health/ready` always returns `503` until the full provider exists.

## Configuration

Production requires these environment variables:

| Variable | Value |
| --- | --- |
| `Broker__TableEndpoint` | HTTPS Table service origin, such as `https://ACCOUNT.table.core.windows.net` |
| `Broker__TableName` | Existing dedicated broker table |
| `Broker__DeploymentEpoch` | External epoch identifier, 1–128 ASCII letters/digits, `-` or `_` |
| `Broker__ManagedIdentityClientId` | Optional user-assigned identity client ID; otherwise system-assigned identity |

The service uses `ManagedIdentityCredential` only. It never creates the table or loads account keys, developer credentials, or emulator connection strings. Provision the table in the storage account planned for auth infrastructure and grant the runtime identity **Storage Table Data Contributor** at that table's scope. Deployment Contributor and Blob key-ring access do not grant Table data access. Actual Azure managed-identity acceptance remains required before hosted release. Production ingress must require TLS; the container listens on internal HTTP port 8080 behind that ingress.

Keep the deployment epoch outside the restored table backup. Recovery must fence old workers/access and establish a new epoch; restoring both data and its old epoch must never resurrect grants. Explicit cleanup and backup/restore are separate delivery work. See [the storage decision](../../docs/adr/0003-use-table-storage-for-broker-state.md).

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
