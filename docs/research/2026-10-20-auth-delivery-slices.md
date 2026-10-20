# Shared-bot authentication delivery slices

Design proposal, 2026-10-20. No broker deployment or live authentication is claimed. GitHub facts and primary citations are in [the delegated-auth investigation](2026-10-20-controller-delegated-auth.md); the protocol below is proposed project behavior.

Deployment access has since been bootstrapped and verified through GitHub OIDC and an outputs-only Bicep deployment; see the [deployment runbook](../azure-auth-deployment.md). This does not complete the maintainer broker deployment slice or its hosted acceptance gates.

## Recommendation and first deliverable

Preserve installation-token bot identity. GitHub App device login authorizes a human to enroll a controller; it does not replace the App JWT needed to mint installation tokens. Operate one maintainer-controlled broker separately from users' local or Azure controllers. Only the broker receives the shared App PEM.

Implement the controller's credential-provider boundary and broker-backed token adapter first. This is a useful, bounded integration slice that can be executed and tested without Azure access. It must include real CLI wiring, validation, actor checking, cache publication, renewal and failure behavior, rather than an unused interface. Report it as “broker consumption implemented; enrollment and hosted broker pending,” never as completed safe onboarding.

The current concrete `GitHubAppCredentials` owns PEM parsing, JWT signing, installation discovery, bot identity, token refresh, cache writes and refresh scheduling. Both `Program.cs` and `GitHub.cs` depend on it. `AppGitHubCommand` consumes an atomic cache and rejects unavailable/expired credentials; its existing integration tests execute the native wrapper. Reuse these proven behaviors behind a narrow runtime credential contract. Avoid duplicating cache and refresh logic between providers.

A reasonable shape is one runtime credential manager with `Initialize`, `GetToken`, `RefreshUntilCancelled`, `TokenFile`, `AgentLogin` and actor matching, plus a token-source boundary returning a validated identity/binding and a token lease. The direct-App source and broker source are real alternatives, so the seam has a production purpose. An equivalent smaller refactor is acceptable if it preserves a single cache/renewal implementation.

Direct-App mode remains explicit for operators using their own App. It must never become an automatic fallback when broker authentication fails. Broker mode accepts an enrollment file and a trusted HTTPS broker origin, mutually exclusive with PEM settings; it must not require or read an App key.

## Security contract

1. An enrollment is bound to GitHub host, numeric App ID, immutable repository ID, installation ID, immutable authorizing user ID, device ID, permission ceiling and authorization-policy version. Names are display/routing information. The broker resolves and verifies all bindings against GitHub; client-supplied IDs are selectors, not authority.
2. Initial enrollment requires current repository administrator authority through the selected App installation, not merely read/write access. The broker validates the actual authenticated GitHub user and membership of the selected repository in the installation. Installation ownership alone or a client claim is insufficient.
3. Permission profiles are server-defined. The current bridge needs `discussions:write`; webhook mode additionally needs `repository_hooks:write`, plus GitHub's implicit metadata grant. Any future code checkout/push or issue-management permissions are a separately reviewed product requirement. Neither the device nor an agent may request arbitrary permission maps.
4. Every issuance, including renewal after restart, rechecks current App installation, repository membership, required permissions, enrollment status and the responsible user's administrator authorization. Failure to prove any condition denies issuance. A repository transfer or installation change requires fresh enrollment approval; no automatic rebinding by owner/name.
5. Mint tokens with `repository_ids: [boundRepositoryId]` and the explicit approved permission map. Never omit either field. Validate GitHub's returned repository scope and permission metadata where provided; an unverifiable/broader result is not returned. Return the actual GitHub expiry, with a maximum acceptable lifetime of one hour plus narrowly documented clock tolerance.
6. The device credential permits only renewal and self-revocation for its stored enrollment. It is not a GitHub credential, App JWT, general broker administrator credential or authority to rebind the repository. Devices cannot enumerate other enrollments.
7. Revocation is durable and checked before minting and again before returning a freshly minted token. Serialize issuance and revocation per enrollment, or use transactional generation checks. If revocation wins a race, suppress and revoke the newly minted token. Already delivered tokens have a documented residual lifetime of up to their GitHub expiry. Broker cancellation cannot retract a copied bearer token.
8. Broker errors, malformed responses, identity/scope mismatches and failed renewal invalidate the local cache immediately. No ambient `gh` credentials, stale-cache fallback or direct-key fallback. The native process may already hold a token; local cache deletion does not revoke that token at GitHub.
9. Disable redirects on authenticated broker and GitHub HTTP clients. Require HTTPS with ordinary certificate validation and configured origins; reject credentials, query and fragment in configured broker origins. Never put secrets in URLs, process arguments, logs, deployment outputs or image layers.
10. The controller and native agent currently share UID and filesystem access. Owner-only files protect against other users, not against that agent. The local device credential and installation token are therefore potentially usable by the agent, but only for the approved repository/permissions. Isolation from the agent requires a separate process/container identity boundary and is not a property of this delivery.

## Proposed v1 protocol

Prefer broker-owned GitHub device flow. This keeps the broad human access/refresh token out of the controller runtime.

* `POST /v1/enrollments/start` accepts repository name, human-readable device label and a server-defined permission-profile identifier. The broker starts GitHub App device flow using its configured public client ID. It returns a short-lived enrollment ID, a high-entropy enrollment poll secret, GitHub verification URI/user code, expiry and polling interval. The broker retains GitHub's device code. Rate-limit anonymous starts and polls; do not persist abandoned authorizations indefinitely.
* `POST /v1/enrollments/{id}/poll`, authenticated with the enrollment poll secret, advances the broker's GitHub polling state within GitHub's interval/slow-down rules. Pending, denied and expired are distinct safe results. On successful GitHub login, the broker verifies the human and resolves the requested repository/installation. It returns an approval preview containing canonical repository name and ID, App identity and exact permission profile. It must not return an installation token yet.
* Final approval happens in a trusted broker-origin browser session authenticated as the responsible repository administrator, with CSRF protection and an immutable transaction/device/repository/installation/permissions binding. An initiator holding the enrollment poll secret cannot approve its own pairing. The installer may display the preview and retrieve completion, but that is not independent administrator consent. This corrects the original unsafe same-poll-secret `/confirm` proposal; GitHub device login alone is insufficient.
* Completion returns a device ID and a random 256-bit device bearer secret once. Keep only a keyed hash of the device secret in broker storage; use constant-time comparison and globally unique random IDs. Encrypt retained GitHub user/refresh tokens at rest with a separately protected key. Refresh rotation must be serialized and committed atomically; uncertain refresh state requires renewed human login.
* For response loss, use a bounded, encrypted completion envelope retrievable with the enrollment poll secret until the device acknowledges durable local storage or the envelope expires. Expiry without acknowledgment revokes the unclaimed device; retaining an active orphan is forbidden. Do not rotate or silently create multiple devices on retry. A transaction must make enrollment completion single-use.
* The controller persists a versioned enrollment file containing trusted broker origin, device ID/secret and immutable expected binding. Write atomically with owner-only permissions. In Azure, provide it through a secret mechanism or private persistent mount; do not copy it into the image. Installation must be resumable without printing the credential.
* `POST /v1/devices/{id}/installation-token`, authenticated with the device bearer secret, has no repository or permission selector. It returns `protocolVersion`, `appId`, `appSlug`, `installationId`, `repositoryId`, canonical repository name, approved permissions, `token` and `expiresAt`. All identity/binding fields are compared with the persisted enrollment before publishing the cache. The broker resolves fresh GitHub authorization each time.
* `DELETE /v1/devices/{id}`, authenticated as the device, permanently disables its renewal. A separate authenticated administrator surface supports listing/revoking the administrator's devices and disabling compromised users/installations. Revocation state must survive broker restarts and backup restoration procedures must not resurrect revoked devices.

The durable broker stores device bindings/status, human authorization records, rotating user refresh credentials, pending enrollment transactions and a credential-free audit trail. A real durable transactional store is a release requirement; an in-memory dictionary is a development fixture only. Human token expiry or revoked authorization produces “reauthorization required”; it never becomes an excuse to skip the responsible-user check. Retaining user refresh credentials makes the broker a high-value service beyond its App key and needs explicit operational ownership.

## Delivery sequence and exit criteria

| Slice | Concrete outcome | Required evidence |
| --- | --- | --- |
| 1. Controller token consumption | Explicit direct-App/broker mode, reusable cache/renewal, broker adapter, binding checks, bot identity check and no PEM requirement in broker mode | Local HTTP transport-boundary tests, real cache and wrapper execution, CLI validation; existing direct-App and webhook tests pass |
| 2. Durable broker authorization and issuance | Real hosted service code, durable bindings/revocation, GitHub adapters, exact one-repository minting, renewal authorization checks | Real production database integration tests including restart/races; outbound GitHub request assertions; versioned consumer/provider contract tests |
| 3. Human device enrollment and installer pairing | Start/poll/preview/confirm/ack flow, encrypted human credential persistence, explicit admin policy, local enrollment file, revocation command | Pending/slow-down/denied/expiry paths, unauthorized user denial, replay/retry/response-loss tests, no broad user token in controller state |
| 4. Maintainer broker deployment | Separate deployment/configuration for HTTPS service, durable store, protected signing/refresh keys, key rotation, recovery and audit operations | Actual staged GitHub App login, token issue, admin-loss/revocation and renewal checks; no claim of deployed readiness until executed |
| 5. Consumer deployment/installers and image publication | Local Docker and Azure controller deployment use enrollment; chosen agent CLI is installed/configured; local build, Docker Hub and ACR paths publish reusable credential-free images | Installer tests and image inspection; local container smoke; Azure staging deploy with persistent enrollment and real renewal |
| 6. Azure HTTPS webhook lifecycle | Deployment supplies the actual public HTTPS origin from provisioned FQDN; controller registers/reconciles the repository webhook with approved hook permission | Reachable TLS endpoint, real GitHub webhook delivery/restart/reconciliation; secret mismatch rejected; polling fallback policy explicit |

Slices 2 and 3 can be developed locally without deploying Azure, but both precede usable unattended enrollment. The broker deployment is performed by the shared-App maintainer, not independently by every user. Azure provisioning and registry publication remain external acceptance work if credentials are unavailable; buildable templates are not proof those resources exist.

Do not assume an Azure FQDN automatically provides trusted HTTPS. The chosen deployment must actually terminate TLS, route the listener and expose the final origin. A future ingress implementation choice should not be embedded prematurely into the authentication adapter.

## First-slice test contract and meaningful red

Protected behavior: when a bound controller renews through its broker, both bridge calls and an already-running native wrapper use only a validated, unexpired token for that binding; rejection prevents further wrapper launches.

Use the existing real filesystem, native wrapper and `TimeProvider` approach. Stub only the outbound unmanaged broker/GitHub HTTP boundary. Tests should assert observable authorization outcomes, not internal interface calls.

* Broker mode starts without a PEM file, checks the configured App viewer, publishes a usable token and retains bot loop prevention.
* A wrapper created before renewal reads the fresh cache after expiry; concurrent callers cannot publish an older lease over a newer one.
* Wrong App, repository ID, installation, permissions, malformed response, missing token, near-expiry token or implausibly long expiry prevents cache publication.
* An unsuccessful scheduled refresh deletes the cache and prevents native `gh` execution even when ambient human credentials exist. A later successful authorization can recover.
* Conflicting direct/broker options and insecure/redirecting broker endpoints are rejected without sending a device credential to another origin.
* Error bodies containing tokens or device secrets never appear in surfaced errors/logs. The provider's non-success body is not diagnostic text.

For meaningful red, first add an executable CLI broker-mode scenario that reaches normal command parsing and fails because the supported broker path is absent; distinguish that feature absence from a compilation failure. After establishing the path, temporarily disable a binding check and demonstrate that the negative case launches or publishes a token, then restore it. Record exact commands and results during implementation. Existing direct-App tests serve as preservation evidence, not proof of broker authorization.

A hand-written HTTP response fixture is local adapter evidence only. Slice 2 must exercise the real broker endpoint with the consumer contract; the final hosted smoke must show an actual GitHub installation token, expected bot viewer, approved repository access and failed access to an unrelated repository. None of those hosted checks has been run in this design task.

## Reviewed consumer contract and ownership gate

The frozen first-slice fields, origin restrictions, profiles, expiry bounds and failed-viewer cache behavior are in [protocol v1](../broker-protocol.md), which takes precedence over sketches above. Hosted enrollment additionally needs the administrator-bound approval ceremony described above. A broker lease cannot fence a token already held by a native agent: ownership handoff must account for all outstanding tokens with confirmed revocation or expiry, including mint/response-loss races. Partition and suspend/resume tests are required before hosted release. See the [independent plan review](2026-10-20-controller-installation-plan-review.md).
