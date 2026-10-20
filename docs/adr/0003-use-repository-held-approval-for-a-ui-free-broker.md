# Use repository-held approval for a UI-free broker

Environment storage choice superseded on 2026-10-20 by [ADR 0006](0006-use-repository-agent-variables-for-approval.md). The UI-free repository-held approval and authority model below remain accepted; Environment-specific text records the earlier storage choice.

Accepted on 2026-10-20. Supersedes [ADR 0002](0002-bind-enrollment-renewal-to-the-approving-administrator.md).

Use a user-selected GitHub Environment and environment variable in the target repository as the approval and revocation surface for controller enrollment. The PowerShell 7 MVP installer creates or safely reuse the environment and write the exact nonsecret enrollment marker through the operator's host GitHub identity; the broker independently reads that marker before authorizing enrollment and token issuance. Repository-held approval replaces the requirement that a particular approving person remain an administrator and removes the broker-hosted browser/login/admin UI.

The shared-App separation in ADR 0001 remains. Setup uses host authority GitHub permits to create the Environment or manage the selected variable, without claiming a particular human approved it, while the broker has only the GitHub access required to verify approval and the controller's tokens retain their explicit runtime profile. Removing or changing approval closes the observed grant permanently; fresh enrollment is needed to authorize a replacement. GitHub observation and durable broker state are not one atomic transaction, and existing installation tokens retain their separate revocation/expiry barrier. See [the contract](../auth-repository-approval.md) and [issue #31](https://github.com/repoagent/controller/issues/31).
