# Credential broker policy

These product decisions were approved by the maintainer on 2026-10-20 for [plan #28](https://github.com/repoagent/controller/issues/28), [durable authorization #30](https://github.com/repoagent/controller/issues/30) and [operations #35](https://github.com/repoagent/controller/issues/35). They specify future provider behavior; the merged controller consumer does not establish a deployed broker. The [v1 consumer contract](broker-protocol.md) remains unchanged.

## Authorization lifetime

Enrollment is an independent grant approved by a repository administrator. Before issuing or renewing a controller token, require all of the following:

- The device is active, acknowledged, unexpired and not revoked.
- The approving person's immutable GitHub user identity still has repository-admin authority. A renamed or reused login cannot substitute for that identity.
- The enrolled GitHub host, App, installation, immutable repository and server-defined permission profile remain valid and agree with actual GitHub grants.

Discard personal GitHub OAuth access/refresh credentials after enrollment; never put them in the controller, image or browser authentication ticket. Personal OAuth-consent withdrawal alone does not revoke the independent enrollment. Losing administrator authority prevents further issuance; a new approving person requires new approval. See [the decision and tradeoff](adr/0002-bind-enrollment-renewal-to-the-approving-administrator.md).

App uninstall and explicit enrollment revocation always prevent future issuance. Failure to establish current authority fails closed. None of these decisions claims to revoke a GitHub token already issued: retain the v1 outstanding-token accounting and safe ownership-transfer requirements.

## Hosting behavior

The maintainer hosts and funds the small authorization service in Azure, separately from users' controllers. Use HTTP scale-to-zero when idle; a cold-start delay after inactivity is an accepted tradeoff. Durable state, expiry and cleanup cannot rely on the API process remaining alive, and a maximum replica count cannot replace transactional authorization.

The implementation should use .NET framework and first-party libraries, managed identities where supported, and deployment from this GitHub repository. Storage, registry, signing, logs and other resources still have their own cost and lifecycle. Selecting the actual subscription, resources and apply plan remains a later deployment step.

## Configurable retention defaults

| Data | Default | Required behavior |
| --- | --- | --- |
| Pending approval | 15 minutes | Approval after expiry is rejected, even if physical cleanup has not run. |
| Abandoned enrollment data | Clean within 24 hours | Expired/unacknowledged completion cannot leave an active usable device. |
| Operational audit logs | 30 days | Redact secrets; bound event size, admission rate, retained volume and recovery copies. |
| Inactive enrollment | 90 days | Deny renewal and require fresh sign-in/administrator approval after expiry. |

These are configurable defaults. Activity means acknowledged enrollment or a successful authorized token issuance; invalid requests, polling or failed renewals cannot keep a device alive. Active controllers normally renew tokens, so normal use does not require re-enrollment every 90 days.

Outstanding-token accounting, revocation/recovery records and protection keys are retained until their relevant safety windows end, even if that exceeds a routine cleanup window. Define each safety window and its finite cleanup bound in the storage/recovery design; do not retain everything forever or delete evidence while an old token could authorize work. Missing devices deny access, identifiers are not reused, and restoring a backup cannot resurrect a revoked or expired enrollment. Browser Data Protection keys need a separate policy that accounts for protected-payload and recovery lifetimes.

The 24-hour abandonment-cleanup bound needs a mechanism independent of HTTP traffic, such as managed database expiry with bounded-lag monitoring or an independently scheduled cleanup job. It is a requirement to demonstrate, not a guarantee supplied merely by enabling TTL. Admission caps and rate/size limits remain necessary because expiry alone does not bound cardinality or cost.

## Remaining engineering work

Choose and review the durable transaction, partition, concurrency, expiry and recovery design; split it into bounded implementation issues. Prove actual GitHub current-admin/scope checks, trusted browser approval, real store races/restarts, Key Vault access/signing, cleanup while scaled to zero and hosted recovery before claiming service readiness. These engineering choices do not require the maintainer to select database mechanics without a recommendation.
