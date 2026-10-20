# Credential broker policy

These decisions follow the updated [repository approval contract #31](https://github.com/repoagent/controller/issues/31) and its security review. They describe future provider behavior; main contains the controller consumer and a host approval-revocation tool, not a deployed enrollment/issuance service. The strict [v1 consumer contract](broker-protocol.md) remains unchanged.

## Authorization lifetime

Enrollment requires both a repository-held approval in the selected GitHub Environment variable (defaults `repoagent` / `APPROVED_CONTROLLER`) and a separate private possession credential. The marker is nonsecret and bound to the pending enrollment, immutable repository/environment, App installation and server-defined runtime profile. Copying its public value is insufficient to complete enrollment.

Approval authority means a principal GitHub permits to write the selected variable. The marker does not attest a particular human, continuing administrator identity or an Environment's deployment reviewers. The host installer uses its separately authenticated gh; its GitHub credentials never enter the broker, controller or image. Supported GitHub roles, token grants and repository plans require live acceptance; there is no fallback to repository variables when Environments are unavailable.

Before issuing or renewing a runtime token, verify the active, acknowledged, unexpired device, its current bound approval and actual GitHub installation/scope. Runtime profiles stay exactly v1, scoped to one repository, with no approval-management permissions. The broker retains its shared signing key. A copied device bearer credential represents the same logical device, not proof of one physical machine.

Before first observing approval, ordinary absence may remain pending until the absolute deadline. After positive approval, observed withdrawal or binding change permanently closes the enrollment; restoring its old marker cannot reopen it. A lone GitHub 404, denial, timeout or malformed response is unverifiable, not positive absence. An identical delete/recreate entirely between observations may be undetectable; current reads cannot prove continuous historical approval.

## Revocation and manual recovery

The [host revocation tool](repository-approval.md) checks an independently supplied exact-value SHA-256 and makes one DELETE only after a matching observation. GitHub's operation is name-addressed, without atomic compare-delete. An independent writer can replace the value between GET and DELETE, withdrawing a newer approval and temporarily losing service. The maintainer accepts manual inspection/reapproval for this availability race. Do not add distributed locks, leases, automatic restoration/replacement or blind mutation retries.

Report GitHub removal acknowledgment, durable broker closure and outstanding-token settlement separately. Removal neither invalidates tokens already copied into a controller nor authorizes replacement. Preserve durable closure, ETag-based authority gates, issuance accounting, external deployment epoch and old-token barriers. A lost mint response remains an outstanding unknown capability until safely resolved; a client disconnect must not erase its attempt.

## Finite verification and service cost

Preapproval verification is a separate internal capability: exact-repository `environments:read` and `actions:read` (plus implicit metadata), never returned to a controller. Runtime tokens exclude those grants. Verification before activation or after closure must not depend on permission to issue runtime tokens.

Anonymous start/poll requests must not mint a token on every poll. Reuse an appropriately scoped verifier token within its lifetime, while re-reading approval for each authorization operation; do not reuse a positive approval decision across operations. Reserve durable mint attempts before outbound calls, account for known and unknown outcomes, and do not automatically retry an ambiguous mint POST.

Before enabling these routes, enforce explicit finite limits on pending cardinality (global and per repository), absolute pending lifetime, request/body sizes, global/repository verification reads, signing/mint calls and outstanding attempts, alongside ingress throttling/concurrency. Quota exhaustion denies work without displacing the incumbent enrollment. Invalid requests/polls cannot extend expiry. HTTP replica limits and expiry alone do not bound cost. Concrete budgets and concurrency evidence belong to [issuer #51](https://github.com/repoagent/controller/issues/51) and [pending enrollment #52](https://github.com/repoagent/controller/issues/52); those paths are not implemented by the host tool.

## Hosting and retention

The maintainer hosts the small .NET broker in Azure, using first-party libraries, managed identity and Table Storage. HTTP scale-to-zero is accepted; durable authorization, expiry and cleanup cannot rely on API traffic or process memory.

| Data | Default | Required behavior |
| --- | --- | --- |
| Pending approval | 15 minutes absolute | Expired approval/completion is denied even before physical cleanup. |
| Abandoned enrollment | Clean within 24 hours | No active usable unacknowledged device remains. |
| Operational audit logs | 30 days | Redact secrets and bound event size, admission and retained volume. |
| Inactive enrollment | 90 days | Deny renewal and require fresh repository approval. |

Defaults are configurable. Activity is acknowledged enrollment or successful authorized runtime issuance; polling and failed requests do not keep a device alive. Retain token attempts, closure/recovery evidence and relevant protection keys through their defined finite safety windows, even beyond routine cleanup. Missing devices deny access, identifiers are never reused, and restoring old storage cannot resurrect grants. Cleanup needs independently scheduled work and bounded-lag evidence while HTTP scales to zero.

## Remaining release evidence

Prove actual GitHub role/scope behavior, durable Table races/restarts, bounded verification under request/disconnect storms, Key Vault signing, cleanup and fenced recovery before claiming readiness. Existing PR holds and hosted acceptance gates remain in effect.
