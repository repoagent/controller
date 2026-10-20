# Credential broker policy

These product decisions were approved by the maintainer on 2026-10-20 for [plan #28](https://github.com/repoagent/controller/issues/28), [durable authorization #30](https://github.com/repoagent/controller/issues/30) and [operations #35](https://github.com/repoagent/controller/issues/35). They specify future provider behavior; the merged controller consumer does not establish a deployed broker. The [v1 consumer contract](broker-protocol.md) remains unchanged.

## Authorization lifetime

Enrollment is a repository-held grant under the [UI-free approval contract](auth-repository-approval.md). Before issuing or renewing a controller token, require all of the following:

- The device is active, acknowledged, unexpired and not revoked.
- The exact enrollment-specific approval remains present in the selected GitHub Environment variable, independently verified by the broker under a reviewed bounded-freshness policy.
- The enrolled GitHub host, App, installation, immutable repository and server-defined permission profile remain valid and agree with actual GitHub grants.

The installer uses the operator's host GitHub authority to create or safely reuse a user-named environment and write a user-named approval variable. Host credentials stay on that host; the broker has no human login or admin UI. A particular person's departure, administrator-role loss or OAuth-consent withdrawal alone does not revoke repository-held approval. Controller tokens cannot modify the marker. See [the superseding decision](adr/0003-use-repository-held-approval-for-a-ui-free-broker.md).

App uninstall and explicit enrollment revocation prevent future issuance. Confirmed missing or changed approval permanently closes the observed grant; restoring an old marker cannot reopen it. Failed GitHub reads deny issuance without inventing evidence of deletion. Fresh enrollment requires a new controller identity and approval challenge. The install scripts expose `revoke` without requiring the lost device secret and `uninstall` with persistent data and shared settings preserved by default. None of these decisions claims instantaneous observation or revocation of an already-issued GitHub token: retain outstanding-token accounting and safe ownership-transfer requirements.

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
