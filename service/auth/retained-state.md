# Current broker state (schema 2)

The broker stores no GitHub token, encrypted token, issuance history, unknown-outcome quarantine or release receipt. [ADR 0017](../../docs/adr/0017-keep-broker-tokens-in-request-memory.md) records the deliberate expiry-based revocation trade-off.

## State layout

All current records use partition `broker:current-v2`:

- `quota`: fixed global minute counters and admitted repository count.
- `repository:<numeric ID>`: one current enrollment/setup, its repository work counters and `NextCleanupAt`.
- `legacy:<UUID>`: one routing field for each imported active UUID-only enrollment; removed in the same transaction when that enrollment closes, expires or is replaced. New enrollments need no locator.

At most `Broker:CandidateRepositoryLimit` repositories are admitted (default and maximum 128). Closed records keep only their current random enrollment ID, epoch, state and minute counters. A fresh setup replaces that same record with a fresh random ID/challenge. The row count stops growing with token renewals, including failed renewals. Existing entity/transaction size bounds and configured global/per-repository admission, read, sign and mint limits still apply. Old cumulative retained-issuance and outstanding-slot quotas do not apply to this model.

The global and repository minute charges commit atomically. A request reserves its maximum eight GitHub reads, one App signature and one setup verifier mint or two runtime/verifier mints. Failed requests consume their minute charge; nothing creates an audit row or compensates a lost commit. A fresh token request can be rate-limited even after another failed request; clients respect 429/backoff.

## Fresh Table initialization

Schema 1 is not upgraded online. Stop/fence old writers and plan a deliberate cutover to a new dedicated Table, with the runtime and cleanup identities scoped to it. Initialize only after positively confirming that Table was freshly created. Add this one entity with Int64 numeric properties:

| PartitionKey | RowKey | Properties |
| --- | --- | --- |
| `broker:current-v2` | `quota` | `SchemaVersion=2`, `Repositories=0`, `Window=0`, `Admissions=0`, `Reads=0`, `Signs=0`, `Mints=0` |

Missing or malformed metadata denies admission and cleanup; neither runtime nor cleanup initializes or resets it. Do not seed it over an existing Table to infer freshness. The local emulator initializer uses the same schema and refuses an old Table; recreate local emulator state for this cutover.

A fenced one-time import can preserve valid acknowledged active schema-1 enrollments, their exact IDs, secret hashes, approval bindings, original expiries and minute counters. Existing controllers and approval values need no changes. The pure `LegacyEnrollmentImport` converter refuses revoked, expired, unacknowledged, mismatched or corrupt authority; no token state, pending completion or historical record is imported. Validate matching source gates/locators, absent revocation receipts and unchanged source versions before committing the fresh target. Otherwise use fresh enrollment; never infer authority from token history. New enrollment IDs are opaque `<repository ID>_<random UUID>` values within the existing wire ID grammar. Setup versions 1–5 and runtime versions 1–3 retain their exact field names, permission profiles and marker formats. The installer accepts the new routing prefix and verifies that it matches the approved repository; it still reads historical UUID-only markers.

The old Table is never read or mutated by the new request/cleanup path. Historical schemas support only the explicit read-only authority inspector, backup fixtures and the one-time current-authority conversion. Delete/archive old state only through a separately planned operator cutover after fencing its writers; a source change does not perform that action. Rolling back requires the matching old storage and explicit operator recovery, not pointing an older binary at schema 2.

## Credential lifecycle

Runtime and internal verifier tokens exist only in request memory. The broker checks current approval before minting a runtime token and again before publication. A conditional repository update fences concurrent closure/replacement and extends successful activity to the existing 90-day inactivity expiry. It stores no token material.

The internal verifier and a runtime token that could not be published receive one bounded best-effort revocation. An uncertain mint/save/revocation outcome is discarded. Closure stops new publication; it does not recall copied bearer tokens or prove their individual settlement. Tokens may remain valid until GitHub's expiry, normally one hour from creation. A timeout does not establish the creation time. No automatic App uninstall occurs.

Only the setup completion envelope is encrypted with the existing Data Protection key ring. Successful acknowledgment clears it and the setup-secret hash immediately. Otherwise cleanup closes the setup and clears its secrets at the original 15-minute deadline. Retries never extend that deadline. Revocation or inactive-enrollment cleanup removes the controller-secret hash while preserving the small current-generation fence. A delayed writer cannot recreate an old grant: every write replaces the existing record with its observed ETag, and an old enrollment ID never matches a replacement.

## One cleanup pass

The independent Table-only Job runs `cleanup --once` every five minutes. It validates the fixed schema row, queries `NextCleanupAt <= now`, follows ordinary continuation pages, and processes every returned record. Each due record needs one conditional replacement and no verification read. A conflict leaves newer state alone and waits for the next invocation. Other records continue after a per-record failure. No due work needs one schema read, one query page and zero writes.

There are no durable cursors or separate payload/audit passes. A 60-second cooperative deadline and defensive transport ceiling bound an invocation; an incomplete invocation returns failure. Under ordinary healthy scheduling, expired state is removed on the next five-minute sweep; outages and repeated conflicts do not establish a maximum wall-clock deletion lag. Authentication rejects expired enrollments immediately.

The summary/metrics report completion, failures, examined/changed records, conflicts and storage dispatches. Monitoring emails on reported cleanup failures, incomplete coverage or missing completion/failure metrics in the 15-minute observation window. Native Azure execution failures remain diagnostic alert incidents without separate emails, because the platform can time out a Job after its cleanup container exits successfully. Old cycle-age alerts and 30-day audit/24-hour secret-recovery retention no longer describe this schema.

## Renewal and rejection diagnostics

The controller caches its own token in memory and renews near expiry. A definite GitHub 401 invalidates only the rejected token and retries once with fresh broker credentials. A 403 or server/network error does not blindly replay a possibly applied GitHub mutation. Broker HTTP 429 and temporary unavailability use bounded retry/backoff while an existing credential remains within its safety cutoff. A temporary failure without a usable token can fail that call but does not permanently invalidate its enrollment; a later call/restart can retry. Real authorization/binding failures still end the controller lifetime.

The read-only Azure diagnostics command includes the new cleanup counters. Historical schema-1 counts/authority projections cannot establish readiness for schema 2 or immediate token revocation under this policy.
