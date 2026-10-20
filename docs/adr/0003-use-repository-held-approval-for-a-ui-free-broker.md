# Use repository-held approval for a UI-free broker

Accepted on 2026-10-20. Supersedes [ADR 0002](0002-bind-enrollment-renewal-to-the-approving-administrator.md).

Use a user-selected GitHub Environment and environment variable in the target repository as the approval and revocation surface for controller enrollment. The install scripts create or safely reuse the environment and write the exact nonsecret enrollment marker through the operator's host GitHub identity; the broker independently reads that marker before authorizing enrollment and token issuance. Repository-held approval replaces the requirement that a particular approving person remain an administrator and removes the broker-hosted browser/login/admin UI.

The shared-App separation in ADR 0001 remains. Setup uses administrative host authority, while the broker has only the GitHub access required to verify approval and the controller's tokens retain their explicit runtime profile. Removing or changing approval closes the observed grant permanently; fresh enrollment is needed to authorize a replacement. GitHub observation and durable broker state are not one atomic transaction, and existing installation tokens retain their separate revocation/expiry barrier. See [the contract](../auth-repository-approval.md) and [issue #31](https://github.com/repoagent/controller/issues/31).
