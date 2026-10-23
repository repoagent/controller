# Credential broker policy

These product decisions were approved by the maintainer on 2026-10-20 for [plan #28](https://github.com/repoagent/controller/issues/28), [durable authorization #30](https://github.com/repoagent/controller/issues/30) and [operations #35](https://github.com/repoagent/controller/issues/35). They govern provider behavior. The [#68-owned provider source](../service/auth/README.md#repository-agent-variable-enrollment-v2) implements v2 Agent-variable setup/markers with durable approval-kind separation; source and consumer implementations do not establish a deployed broker. The [v1 consumer contract](broker-protocol.md) remains unchanged.

## Authorization lifetime

Enrollment is a repository-held grant under the [UI-free approval contract](auth-repository-approval.md). Before issuing or renewing a controller token, require all of the following:

- The device is active, acknowledged, unexpired and not revoked.
- The exact enrollment-specific approval remains present in the selected direct repository Actions variable (or, for an existing legacy grant, the Agent variable it was enrolled with), independently verified by the broker under a reviewed bounded-freshness policy.
- The enrolled GitHub host, App, installation, immutable repository and server-defined permission profile remain valid and agree with actual GitHub grants.

The installer uses the operator's host GitHub authority to create or safely reuse a user-named repository Actions variable. Host credentials stay on that host; the broker has no human login or admin UI. A particular person's departure, administrator-role loss or OAuth-consent withdrawal alone does not revoke repository-held approval. Controller tokens receive no approval-variable read or write access. See [the superseding decision](adr/0007-use-repository-actions-variables-for-approval.md), which replaced the [Agent-variable location](adr/0006-use-repository-agent-variables-for-approval.md).

App uninstall and explicit enrollment revocation prevent future issuance. Confirmed missing or changed approval permanently closes the observed grant; restoring an old marker cannot reopen it. Failed GitHub reads deny issuance without inventing evidence of deletion. Fresh enrollment requires a new controller identity and approval challenge. The planned PowerShell installer provides `revoke` without requiring the lost device secret and `uninstall` with persistent data and shared settings preserved by default. The standalone [host revocation tool](repository-approval.md) implements legacy Environment inspection/deletion; the v2 provider enrollment contract is implemented under #68, while guided installation, Agent-variable host commands and uninstall remain #53 work with live and native Windows acceptance. None of these decisions claims instantaneous observation or revocation of an already-issued GitHub token: retain outstanding-token accounting and safe ownership-transfer requirements.

Approval authority means the principals GitHub permits to manage the selected variable, not attestation by a particular human administrator or deployment reviewer. Before first observing approval, ordinary absence may remain pending until the absolute deadline. After positive approval, verified withdrawal or binding change permanently closes the enrollment. A lone404, denial, timeout, malformed response or stale observation is unverifiable, not proof of absence. Only a complete single-page Agent-variable listing can prove absence; multi-page nonappearance remains unverifiable despite stable totals. Confirmed changes require well-formed evidence and freshness from the oldest contributing read. An identical delete/recreate between observations may be undetectable.

## Revocation and manual recovery

The legacy [host revocation tool](repository-approval.md) checks an independently supplied exact-value SHA-256 and makes one DELETE only after a matching observation. GitHub's operation is name-addressed, without atomic compare-delete. An independent writer can replace the value between GET and DELETE, withdrawing a newer approval and temporarily losing service. The maintainer accepts manual inspection/reapproval for this availability race. Do not add distributed locks, leases, automatic restoration/replacement or blind mutation retries.

Report GitHub removal acknowledgment, durable broker closure and outstanding-token settlement separately. Removal neither invalidates tokens already copied into a controller nor authorizes replacement. Preserve durable closure, ETag-based authority gates, issuance accounting, external deployment epoch and old-token barriers. A lost mint response remains an outstanding unknown capability until safely resolved; a client disconnect must not erase its attempt.

## Finite verification and service cost

Preapproval verification is a separate internal capability: exact-repository `actions_variables:read` (`agent_variables:read` for legacy v2 grants), plus implicit metadata, with actual installation scope still requiring live proof, never returned to a controller. Runtime tokens exclude those grants, except that the explicit repository-settings profiles ([ADR 0008](adr/0008-add-an-explicit-repository-settings-runtime-profile.md)) add exactly `actions_variables:read` to their frozen grants. Verification before activation or after closure must not depend on permission to issue runtime tokens.

Anonymous start/poll requests must not mint a token on every poll. Reuse an appropriately scoped verifier token within its lifetime, while re-reading approval for each authorization operation; do not reuse a positive approval decision across operations. Reserve durable mint attempts before outbound calls, account for known and unknown outcomes, and do not automatically retry an ambiguous mint POST.

Before enabling these routes, enforce explicit finite limits on pending cardinality (global and per repository), absolute pending lifetime, request/body sizes, global/repository verification reads, signing/mint calls and outstanding attempts, alongside ingress throttling/concurrency. Quota exhaustion denies work without displacing the incumbent enrollment. Invalid requests/polls cannot extend expiry. HTTP replica limits and expiry alone do not bound cost. Concrete budgets and concurrency evidence belong to [issuer #51](https://github.com/repoagent/controller/issues/51) and merged legacy [pending enrollment #52](https://github.com/repoagent/controller/issues/52), preserved and adapted under [Agent-variable provider #68](https://github.com/repoagent/controller/issues/68); those paths are not implemented by the host tool.

## Hosting behavior

The maintainer hosts and funds the small authorization service in Azure, separately from users' controllers. Use HTTP scale-to-zero when idle; a cold-start delay after inactivity is an accepted tradeoff. Durable state, expiry and cleanup cannot rely on the API process remaining alive, and a maximum replica count cannot replace transactional authorization.

The implementation should use .NET framework and first-party libraries, managed identities where supported, and deployment from this GitHub repository. Storage, registry, signing, logs and other resources still have their own cost and lifecycle. Selecting the actual subscription, resources and apply plan remains a later deployment step.

## Configurable retention defaults

| Data | Default | Required behavior |
| --- | --- | --- |
| Pending approval | 15 minutes | Approval after expiry is rejected, even if physical cleanup has not run. |
| Abandoned enrollment data | Clean within 24 hours | Expired/unacknowledged completion cannot leave an active usable device. |
| Operational audit logs | 30 days | Redact secrets; bound event size, admission rate, retained volume and recovery copies. |
| Inactive enrollment | 90 days | Deny renewal and require fresh enrollment and repository-held approval after expiry. |

These are configurable defaults. Activity means acknowledged enrollment or a successful authorized token issuance; invalid requests, polling or failed renewals cannot keep a device alive. Active controllers normally renew tokens, so normal use does not require re-enrollment every 90 days.

Outstanding-token accounting, revocation/recovery records and protection keys are retained until their relevant safety windows end, even if that exceeds a routine cleanup window. Define each safety window and its finite cleanup bound in the storage/recovery design; do not retain everything forever or delete evidence while an old token could authorize work. Missing devices deny access, identifiers are not reused, and restoring a backup cannot resurrect a revoked or expired enrollment. Data Protection keys used for encrypted completion/recovery payloads need a separate lifetime policy; broker browser sessions are no longer in scope.

The 24-hour abandonment-cleanup bound needs a mechanism independent of HTTP traffic, such as managed database expiry with bounded-lag monitoring or an independently scheduled cleanup job. It is a requirement to demonstrate, not a guarantee supplied merely by enabling TTL. Admission caps and rate/size limits remain necessary because expiry alone does not bound cardinality or cost.

## Remaining engineering work

Azure Table Storage is selected; review and prove the repository partition/common gate, per-entity ETags, expiry, independent cleanup and external restore-epoch design in the implementation slices. Prove actual GitHub marker/scope checks, broker-only approval-read permissions, marker freshness/recreation semantics, real store races/restarts, Key Vault access/signing, cleanup while scaled to zero and hosted recovery before claiming service readiness. The detailed [approval contract](auth-repository-approval.md) and [issue #31](https://github.com/repoagent/controller/issues/31) govern the handover.
