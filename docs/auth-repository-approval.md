# Repository-held approval and a UI-free auth service

Approved by the maintainer on 2026-10-20. This replaces broker-hosted administrator browser approval, broker human OAuth/cookie/CSRF/PKCE flows, and continuing authorization tied to a particular approving person. It is a design contract; main's existing v1 broker consumer does not implement this provider or installer flow. Canonical delivery parents are [#30](https://github.com/repoagent/controller/issues/30) and [#31](https://github.com/repoagent/controller/issues/31).

## Authority and credentials

The GitHub App must be installed with access to the target repository before enrollment. The installer uses the operator's separately authenticated host GitHub authority to create or safely reuse a GitHub Environment and create its approval variable. GitHub authenticates that setup authority; the broker hosts no user login, browser approval, device-login ceremony or administrator dashboard.

The approval belongs to the repository, not to the person running setup. Their later departure or personal OAuth-consent withdrawal does not revoke it. A human identity may appear in an audit record but is not a continuing grant condition. GitHub App uninstall, loss of repository/required permissions, missing or changed approval, explicit device revocation, expiry or recovery-epoch mismatch deny further issuance.

The marker is nonsecret. It binds a broker-issued pending controller ID and fresh challenge/fingerprint to the immutable GitHub host, numeric repository/App/installation IDs, exact server-defined permission profile, authorization-policy version and chosen marker coordinates. Readable binding information must let the operator assess the requested grant before writing it. The controller must separately prove possession of its private pending/device credential; reading or copying a public marker is insufficient. The broker validates the marker against its stored pending request, not client-supplied authority claims.

The broker reads the selected environment variable directly through GitHub, with TLS, fixed GitHub host, bounded freshness and no client-supplied screenshots or cached copies as authorization. The additional App permissions needed to read approval remain broker-only. Verify GitHub's actual API permission requirements: environment creation uses Administration write; variable management uses Environments write; variable reading uses Environments read. Reading environment identity can require Actions read. Controller runtime installation tokens retain the frozen v1 polling/webhook profiles; no environment-management permissions, host GitHub credentials or App PEM are delegated to them. Account for internally minted verification tokens as well as returned runtime tokens.

## Installer setup and names

Both `install.sh` and `install.ps1` expose equivalent `install`, `revoke` and `uninstall` actions and explicit, distinct inputs for the GitHub environment name and approval-variable name. Suggested defaults are `repoagent` and `APPROVED_CONTROLLER`; users may choose both names. Keep these inputs separate from Azure Container Apps environment selection and the native Discussion-enrollment option.

Arguments fill answers; interactive mode prompts for missing values and previews the exact repository, App installation, controller, profile and marker location. Noninteractive mode requires explicit apply authorization and the necessary values; it never prompts. Persist selected marker coordinates in the broker's authoritative enrollment state and the installer's nonsecret instance configuration. Preserve the frozen v1 enrollment-file/token-response schema; add a reviewed version only if wire-format changes are genuinely necessary.

The scripts perform environment and variable creation through structured host `gh` API arguments, not manual copy/paste instructions. They verify the host identity's setup authority, the actual repository and App installation, and feature/plan availability. GitHub Environments in private/internal repositories require a supported plan. Permission denial, unsupported plans and transport failures must be actionable; none becomes an approval bypass or permission broadening. No host GitHub credential is copied into the controller or retained by the auth service.

Read before writing. Reuse only a compatible selected environment; preserve unrelated protection rules, variables, secrets and deployment settings. A matching pending/device binding is idempotently resumable. A marker owned by another enrollment is a conflict, not an automatic overwrite; explicit replacement must close/fence the old grant first. Validate names using GitHub's rules, encode path segments correctly, use safe process arguments, and cap data/work. Do not interpolate names into shell commands.

## Enrollment and renewal

1. Start a bounded pending enrollment at the configured broker origin. Bind the repository, installation, server-defined profile and selected marker coordinates; return a pending controller ID, nonsecret approval material and a private recovery/possession credential.
2. Preview and apply the GitHub approval through the host installer, creating the selected environment and variable as needed. The controller and the broker's delegated runtime tokens cannot write that approval.
3. The broker independently verifies the actual installation and exact repository marker before preparing one recoverable enrollment completion. An initiating poll secret alone cannot authorize completion.
4. Persist the private v1 enrollment file atomically with owner-only permissions before acknowledging it. Retry recovers the same device/credential. Acknowledgment activates only a still-approved, unexpired grant; missing approval, concurrent revocation or expired completion cannot reactivate it.
5. Before every issuance/renewal and release, verify the live repository-held approval under a reviewed bounded-freshness policy along with the durable gate, device, profile, installation and expiry. Reserve every mint attempt; sign an App JWT; request exactly `repository_ids: [approvedRepositoryId]` plus approved runtime permissions; validate actual scope and expiry. Internal approval-check tokens require the same durable authority accounting.

## Revocation and uninstall

`revoke` works from a trusted host using current GitHub setup authority, explicit repository/environment/variable inputs or saved instance configuration. It does not require the lost controller's device secret or access to its machine. Verify the selected marker and binding before mutation; delete only that approval variable. If the device credential is available, the existing self-revocation route is an additional closure mechanism, not the recovery prerequisite. Ask the broker to reconcile where the API supports it; report GitHub marker removal, broker closure and outstanding-token settlement as distinct observable states. An unavailable broker never becomes a false successful remote-revocation result.

`uninstall` performs revocation and stops/removes only the selected managed controller runtime. Preserve persistent volumes, workspace/history, shared GitHub Environment, other variables/secrets and the GitHub App installation by default. Local teardown is not evidence that remote authority has closed; report failures and remaining access explicitly. This decision authorizes future script behavior, not a live uninstall or deletion now.

Confirmed marker absence or a changed binding permanently closes that observed enrollment in durable broker state. Restoring an old marker must not reopen it; replacement needs a new controller ID/credential and fresh approval challenge. A timeout, 403 or malformed GitHub response denies issuance but is not fabricated evidence of deletion. Missing/recreated environments, marker replay, rename, version changes, stale observations and deletion/recreation between checks need concrete provider tests. GitHub state and broker transactions are not atomic: document observation latency, rechecks and the remaining check-to-release race; do not claim instantaneous or continuously observed revocation.

Already-issued bearer tokens are separate authority. Preserve known-token revocation, unknown-attempt quarantine, response-loss accounting and the #34 replacement barrier. Removal of a marker, cache deletion or a replica count cannot recall a copied token. The minimal broker ledger therefore remains necessary even though approval lives in GitHub.

## Evidence and retained constraints

Use transport-boundary tests to assert exact GitHub lookup/mint permissions and repository scope. Use real host Bash and native Windows PowerShell process boundaries for named environment/variable setup, reuse, conflict, denied/unsupported setup, revoke with no local credential, and uninstall preservation. Verify the actual broker API, private filesystem/ACLs and real Azure Table Storage for completion, revocation, replay, restart, conditional gates and restore rejection; no in-memory substitute.

Live GitHub acceptance must prove that the operator can create/remove the chosen marker, the broker can read it, and the exact controller token cannot create, change or restore it. Exercise missing/changed approval and failed GitHub reads before token release, duplicate/resumed installation, both names customized, lost-secret recovery, and an old token surviving enrollment closure until separately settled. Detailed marker format, observation/recreation semantics, minimal permission profile and setup ceremony require security review before affected slices become implementation-ready.

Azure Table Storage, one repository partition/common gate, per-entity ETags, external restore epoch, bounded work/rate/size/cardinality and fail-closed readiness remain. Keep configurable 15-minute pending approval, 24-hour abandonment-cleanup target, 30-day audit and 90-day inactivity defaults. Independent cleanup must work with zero HTTP replicas. Blob Data Protection keys may still protect encrypted completion/recovery payloads; they are not evidence that browser sessions remain in scope. No deployment, App-permission change or live repository mutation is authorized by this documentation update.

## Primary references

- [Create or update a GitHub Environment](https://docs.github.com/en/rest/deployments/environments#create-or-update-an-environment)
- [GitHub environment-variable API](https://docs.github.com/en/rest/actions/variables#get-an-environment-variable)
- [GitHub variable ownership and management](https://docs.github.com/en/actions/how-tos/write-workflows/choose-what-workflows-do/use-variables)
- [Environment availability and configuration](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments)
- [Exact-repository installation token issuance](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-an-installation-access-token-for-a-github-app)
