# Independent deployment-plan review

Reviewed 2026-10-20. Reviewer did not author the plan. Inputs: canonical issue [#28](https://github.com/repoagent/controller/issues/28), its matching draft `/tmp/controller-deployment-plan.md`, the three named research notes, and implementation at `d865b15e79e84fabf080321dcc4d688a4babce2c`. GitHub issue was read successfully with the CLI. No repository or external state was changed.

**Decision: changes required, with a bounded consumer-first slice approved once R1 and R2 are incorporated into its durable contract.** R3 and R4 block the relevant hosted release, not this local integration slice. They must remain explicit tracked prerequisites. No live GitHub authentication, registry publication, Azure storage, rollout, or hosted broker acceptance was executed.

## R1 — Correct the native-auth comparison before first-slice implementation

The delegated-auth research says device flow has no documented repository selector. Current official GitHub documentation explicitly lists `repository_id` in the device-flow token exchange. It also warns that the parameter is ignored if the App or user cannot access that repository. The same parameter appears in the web-flow exchange. The research is therefore overstating why direct native login cannot satisfy repository restriction.

Correct that paragraph and the options comparison. Native device login is a simpler PEM-free option when human attribution is acceptable; repository restriction now has a documented mechanism whose actual scope and refresh behavior require verification. Do not assume the selector succeeded merely because token exchange succeeded. Consider its use to narrow broker-held human credentials when enrollment already identifies one repository.

The broker recommendation still stands for this user's **shared App bot** requirement: user tokens attribute actions to a human, while the installation-token creation endpoint requires an App JWT and does not accept a user token. A custom broker is a proposed service architecture, not a GitHub prerequisite for device login itself.

Evidence: [GitHub user-token/device-flow documentation](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-user-access-token-for-a-github-app#using-the-device-flow-to-generate-a-user-access-token), especially the token-exchange parameter table; [installation-token endpoint](https://docs.github.com/en/rest/apps/apps#create-an-installation-access-token-for-an-app). This correction does not require a new authentication implementation or additional user decision.

## R2 — Close the first-slice binding and failed-identity cache contract

The proposed consumer path is implementable, but “preserve fail-closed behavior” alone misses a current failure path. `GitHubAppCredentials.Initialize` publishes the cache through `GetToken`; `Program.cs:67` verifies the real viewer afterward. A viewer mismatch exits without invalidating that published cache, and `Dispose` only disposes the key and semaphore. An existing wrapper at the persisted path can still launch with the rejected credential. The new source must not inherit that gap.

Before assigning the first slice, record these concrete requirements:

- Freeze the v1 enrollment/response field names, types and required fields, supported version, expiry bounds and permission profiles in the child contract or checked-in protocol artifact. This is a small contract, not another architectural phase.
- Limit v1 to `github.com`, matching the hard-coded API/CLI host in the current implementation. Reject other enrolled GitHub hosts. Require the supplied broker origin to agree with the enrollment's trusted origin before sending its bearer secret; reject path/query/fragment/user-info and authenticated redirects. An override must not silently redirect an existing enrollment credential.
- Compare CLI repository/App/optional installation selection with the enrollment, and validate every response against that enrollment. Canonical names remain routing/display values; no automatic immutable-ID rebinding is permitted. Unknown/excess permission grants and near-expiry/overlong leases fail closed.
- Once this process owns the state lock, clear any prior cache before authenticating the new configuration. Any authentication or viewer-verification failure must leave no usable cache or temporary cache. If temporary cache publication is needed to run the existing viewer check, invalidate it on every failed check before exiting. Do not remove another running controller's cache on lock-acquisition failure.
- Add one observable regression scenario: seed an existing wrapper, return the expected response metadata but a wrong actual GitHub viewer, run broker startup, and assert startup fails and that wrapper cannot invoke `gh`. Keep the already-planned rejected-binding and renewal-failure scenarios.

Evidence: `src/DiscussionBridge/GitHubAppCredentials.cs:35`, `:60`, `:99`, and `Dispose`; `src/DiscussionBridge/Program.cs:59`–`:70`; `src/DiscussionBridge/AppGitHubCommand.cs:11`; the draft's consumer contract. These are first-slice obligations. They do not expand scope into enrollment or broker implementation.

## R3 — Poll-secret confirmation is not independent administrator approval

**Blocks hosted enrollment release; resolve before implementing its final authorization ceremony.**

In the research protocol, the party starting enrollment receives the poll secret and can call both `poll` and `confirm`. A malicious initiator can start a flow for a target repository, induce its administrator to enter the GitHub device code, then confirm its own preview. GitHub proves who logged in, but the broker receives no independent evidence that this administrator approved the specific repository/device/permission pairing. A preview displayed only in the initiator's installer does not add that evidence.

Require approval in a trusted broker surface authenticated as the responsible administrator, with CSRF protection and an immutable binding to the pending transaction, repository ID, installation, device and permissions. The poll secret may retrieve status/completion but must not substitute for this administrator approval. A broker-hosted browser sign-in followed by approval is a candidate; do not add a second flow blindly without reviewing the resulting ceremony. Alternatively, explicitly narrow the security claim to trusted-client confirmation and document the remaining remote device-flow phishing exposure, but that does not satisfy the draft's stronger independent pairing assertion.

Required test: an attacker holding every client-side enrollment secret can obtain successful GitHub login status yet cannot finalize the device without the bound administrator approval; replay or changed bindings fail. Preserve the existing response-loss completion envelope and acknowledgment design, and specify what happens to an unacknowledged device when its recovery envelope expires.

Evidence: `docs/research/2026-10-20-auth-delivery-slices.md`, proposed `start/poll/confirm` protocol. The attack is a review inference from that protocol; [RFC 8628 §5.4](https://www.rfc-editor.org/rfc/rfc8628#section-5.4) documents remote device-flow phishing and the need to confirm the intended device.

## R4 — Distinguish ownership coordination from enforceable fencing

**Blocks hosted singleton/rollout release; resolve before delivering slice 5's ownership protocol.**

The plan correctly rejects `maxReplicas=1` and a local file lock as global singleton mechanisms. It also correctly acknowledges residual bearer-token lifetime. Those statements need connecting: a repository-bound broker lease only prevents future issuance. It cannot stop an old process or native agent from using an already-issued GitHub token; GitHub writes do not carry the broker's lease epoch. An expired lease followed by immediate reassignment can therefore leave two effective writers.

Define the failure model and the actual fence. At minimum, compliant controllers must stop accepting work, revoke local launch capability, and terminate/drain native children on ownership loss; ownership acquisition must cover writes as well as the bridge poll loop. For stronger bearer-capability fencing, handoff must account for **all** outstanding tokens, including mint/response-loss races: revoke with confirmed completion where supported, or wait for their expiry plus bounded uncertainty before admitting another owner. A write-mediating service is another possible design, but is not required for the first slice. Do not describe a lease alone as preventing a compromised same-UID agent from writing.

Required staged evidence: partition an owner while a native child holds a still-valid token; attempt takeover and an old-token write; demonstrate the documented safety boundary. Also test process suspension/resumption and platform-initiated restarts. Stop-before-start Azure updates remain useful operational controls, but do not replace this test.

Evidence: the plan's acknowledged same-UID bearer access; `AppGitHubCommand.cs` injects the token into each child environment; `Codex.cs` launches a persistent native process; [GitHub installation-token revocation](https://docs.github.com/en/rest/apps/installations#revoke-an-installation-access-token); [ACA revision behavior](https://learn.microsoft.com/en-us/azure/container-apps/revisions#zero-downtime-deployment). The fencing conclusion is a review inference, not a claim that Azure proves an old process unable to write.

## Accepted boundaries and delivery judgment

The plan's key storage, image, and Azure boundaries are sound as requirements: shared PEM stays in a separate trusted service; one repository and explicit permissions are enforced server-side; user authority is rechecked; human refresh rotation and revocation are durable; enrollment retries have a bounded encrypted recovery envelope. The server must verify actual minted token scope, not merely repeat requested metadata.

Image composition correctly excludes credentials from the context and layers, keeps registry publishing separate from runtime pull identity, pins reviewed software selections, and treats other agent binaries as unsupported until their native protocols work. Current `Codex.cs` is specifically an app-server adapter. Keep Codex as the first supported profile; split “enrollment plus independent adapters” into separate children so future agents do not block image delivery. The large Azure slice should likewise split discovery/plan, storage proof, controlled apply/update, and live webhook acceptance before implementation.

The Azure storage gate is necessary and appropriately unresolved. UID/modes, atomic replacement, Git/symlinks/executable tools, native history, crash recovery and cross-client locking need real backing-storage evidence. Neither `nobrl`, generated YAML, a scale limit nor a control-plane stopped-replica observation proves these properties. No additional first-slice cloud work is required.

The first slice has real utility: an operator can configure an explicit broker source without a PEM and exercise the existing controller/cache/wrapper lifecycle, while the provider is still pending. Its accepted scope is the shared credential lifecycle, both sources, explicit CLI mode, v1 validation, viewer/actor preservation and focused local failure/renewal tests. It must remain labeled **broker consumption implemented; enrollment and hosted broker pending**.

Preserve existing direct-App tests and container Native AOT publication with warnings treated as errors. Existing `tests/NativeSmoke/Program.cs` exercises rendered mentions, not the new authentication path; a successful ordinary xUnit run is not new broker AOT evidence. Exercise the compiled/AOT CLI's broker parsing/validation path where the build environment permits, and report any unavailable build evidence explicitly. A broad test rewrite, new agent adapter, live broker, registry publish and Azure apply are outside this first slice.

After R1's factual correction and R2's contract additions are recorded, no further architecture approval is needed to start this bounded slice. R3/R4 and the existing real-storage/hosted-acceptance gates remain open prerequisites for later releases.

## Implementor dispositions (root, 2026-10-20)

- R1: addressed before implementation. The delegated-auth note now documents device-flow `repository_id`, its ignored-selector warning, and the remaining human-actor distinction.
- R2: addressed in the assigned contract before implementation. [Protocol v1](../broker-protocol.md) and [issue #29](https://github.com/repoagent/controller/issues/29) freeze origin/host/schema/profile/expiry binding and require failed-viewer/stale-cache/active-lock regressions. Implementation and independent code review must still demonstrate those behaviors.
- R3: tracked as a hosted release prerequisite in [#31](https://github.com/repoagent/controller/issues/31); the unsafe poll-secret confirmation sketch is replaced with trusted administrator browser approval. The ceremony and implementation remain pending.
- R4: tracked as a hosted release prerequisite in [#34](https://github.com/repoagent/controller/issues/34). Outstanding bearer tokens, mint/response-loss races and partition/suspend evidence are explicit; a lease alone is not a fence. Implementation remains pending.

The reviewer's conditional authorization permits the consumer slice after R1/R2 are recorded. This disposition records that gate; it does not claim that hosted enrollment, fencing or Azure storage is complete.
