# Retained broker state

Admission bounds total retained state even when cleanup stops. These limits are separate from per-minute work and outstanding-token limits. Only successful initial acknowledgment or authorized token release extends activity; pending expiry and inactive-device denial still operate before physical cleanup.

| Configuration | Default and maximum | Minimum |
|---|---:|---:|
| `Broker:CandidateRepositoryLimit` | 128 | 1 |
| `Broker:RetainedEnrollmentLimit` | 256 | 1 |
| `Broker:RetainedIssuanceLimit` | 4096 | 1 |

Environment variables replace `:` with `__`. Invalid values fail startup. Lowering a limit does not delete rows or reset accounting; new allocations deny with 429 when their retained capacity is unavailable. Identical enrollment retries use their original reservation. Per-minute and outstanding limits can stop admissions earlier: the current global pending limit is 64 and outstanding-attempt limit is 128.

Candidates include anonymous false repository hints. The lifetime 128-candidate directory can be exhausted without repository authority. This is an explicit pilot availability tradeoff. There is no automatic retirement or unlimited-enrollment claim. Manual recovery requires stopping admissions, stopping/fencing old writers, positively settling authority and completing publication/release obligations. JWT expiry, timeout, age and a new deployment epoch do not settle unknown authority.

## Activation

Admission requires `RetentionSchemaVersion` equal to Int64 `1` on **both** existing global quota rows, with complete nonnegative accounting. Missing, malformed or inconsistent metadata denies new admission with 503. Runtime never creates these quota rows, scans a table to infer freshness, resets counters, or enables a legacy admission fallback.

For first deployment, provision a new dedicated table while broker traffic is disabled. A successful table creation, rather than absent metadata in an arbitrary table, establishes freshness. Using administrative Table access, Add these two entities before enabling traffic; every listed numeric value is an Int64:

| PartitionKey | RowKey | Properties |
|---|---|---|
| `global:enrollment-v1` | `quota` | `RetentionSchemaVersion=1`, `CandidateCount=0`, `RetainedEnrollmentCount=0`, `Count=0` |
| `global:issuance-v1` | `quota` | `RetentionSchemaVersion=1`, `RetainedIssuanceCount=0`, `Window=0`, `Admissions=0`, `Reads=0`, `Signs=0`, `Mints=0`, `Slots=0` |

Verify both rows before enabling admissions. Partial initialization remains denied; no cross-partition initialization transaction is needed while traffic is disabled. Service deployment does not itself provision the table or seed entities. No public initialization endpoint exists.

An existing nonempty table requires a complete **offline** inventory with all writers stopped/fenced. Every existing operation and its routing, receipt, gate, quota and outstanding-token rows must be represented by the directory, retained ledgers and counters below before activation. Merely adding zero-valued metadata over old rows is unsupported. This slice provides no migration utility; a newly provisioned table is the simple supported activation path. Existing unactivated revoke-only use is outside this activated-table allocation proof. Table restore, backup retention and Data Protection key-ring retention remain separate operational concerns; restore must stay disabled until old workers and authority are safely handled.

## Allocation proof and writer inventory

| Allocation | Rows per allocation | Covered keys and writers |
|---|---:|---|
| Enrollment, maximum 256 | 8 | Global `pending:<idempotencyId>` ledger and repository pending (`EnrollmentStore.AdmitAsync`); device (`EnrollmentService.CompleteAsync`); `locator:<deviceId>/locator` (`EnrollmentStore.PublishLocatorAsync`); repository `revocation:<deviceId>` (enrollment closure, issuance closure, self-revocation); reserved release, audit and publication-obligation rows. |
| Issuance, maximum 4096 | 4 | Global `operation:<operationId>` ledger and repository `attempt:<operationId>`, created by **both** `EnrollmentStore.ReserveWorkAsync` and `IssuanceStore.ReserveAsync`; reserved release and audit rows. All subsequent send/known/release/revoke observations replace the same attempt. |
| Fixed metadata | 514 currently | 128 global enrollment `repository:<repositoryId>` directory entries; 128 repository gates; 128 repository quotas; 128 global issuance slots; two global quota rows. |
| Future fixed cursors | 2 reserved | At most two singleton cursors for the later independent cleanup process. Not currently created. |

Total allocation is `256 × 8 + 4096 × 4 + 516 = 18,948` rows. Unused credits remain reserved; they do not permit arbitrary additional row types. A future redaction tombstone occupies its original pending/attempt key, not an extra row. No append-only diagnostic or retry log exists. Non-mint observations update the existing pending row and quota; their transient observation entity is never persisted.

The directory's `PendingCount` replaces dynamically named repository properties on the global enrollment quota. Enrollment admission conditionally updates global quota, directory and new ledger in one EGT. The existing reservation becomes the immutable retained enrollment ledger: its `Generation`, `RowCredits=8`, `CreatedAt`, original `Deadline=CreatedAt+15m`, `PayloadPurgeAt=Deadline+24h`, `PendingId` and `DeviceId` name its allocation. Repository pending copies these values. Device/locator/receipt keys are fixed by that device identity.

Both mint paths generate an operation ID and generation **before** charging. Their global EGT updates rate/outstanding/retained counters and Adds the slot and immutable `operation:<operationId>` ledger. Ledger properties are `OperationId`, `Generation`, `RepositoryId`, `DeviceId`, `Purpose`, `Epoch`, `ReservedAt`, `PayloadPurgeAt=ReservedAt+24h`, `GlobalSlot`, `AttemptRowKey`, `RowCredits=4`, and approval discriminator fields. Enrollment verification additionally includes `PendingId`. Slot and repository attempt carry the same operation/generation, reservation clock and global slot. Definite conflicts can retry within the existing operation budget; ambiguous outcomes remain charged. No quota or retained credit is refunded on repository publication failure or token settlement.

All production Table writes go through `BrokerTable`: at most 128 properties and 64 KiB of JSON per entity, at most 32 distinct same-partition actions, at most 1 MiB using twice the serialized row size plus an 8 KiB per-action allowance for OData annotations and multipart framing. Updates require explicit ETags; upserts/deletes/wildcard updates are unavailable through that helper. Existing authorizing transitions still condition the common repository gate and every independently read authority entity. `EnrollmentStore`, `IssuanceStore` and `DeviceRevocation` are the only durable writer modules; `EnrollmentService` creates entities submitted through those stores.

Immutable directory membership and unreleased ledger charges make the count bound independent of cleanup progress. The bound assumes the dedicated table is written only by these broker paths or an explicitly stopped, complete offline inventory procedure. Administrative arbitrary writes cannot be inferred or repaired by admission.

## Remaining lifecycle work

The original payload clocks are allocation metadata, not an implemented redaction guarantee. #78 supplies first-terminal clocks, Table-only secret removal and fencing against late pending/attempt publication; #71 supplies generation-safe reclamation; #72 supplies bounded independent cleanup execution; #35 supplies hosting and deadline acceptance. Until then rows and payloads remain retained. A late locator Add remains within its charged enrollment envelope; absence never proves publisher cessation or permits credit release.

No physical 24-hour deletion, automatic refund, scheduler, unknown-token settlement or hosted availability guarantee is made by this admission slice. Live managed identity/provider/signing, zero-HTTP-replica cleanup and restore acceptance remain separate release gates.
