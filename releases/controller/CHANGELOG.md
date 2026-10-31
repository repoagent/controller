# Changelog

## [1.1.1](https://github.com/repoagent/controller/compare/controller-v1.1.0...controller-v1.1.1) (2026-10-31)


### Bug Fixes

* **controller:** refresh bundled cloudflared from 2026.10.5 to 2026.10.7 ([4da6736](https://github.com/repoagent/controller/commit/4da67365a6053de4059c74ca26d666161c14ce8e))
* **controller:** refresh bundled Codex from 0.165.0 to 0.166.0 ([51df963](https://github.com/repoagent/controller/commit/51df963ede8121b83e2e1904b68977fd00382c39))

## [1.1.0](https://github.com/repoagent/controller/compare/controller-v1.0.2...controller-v1.1.0) (2026-10-29)

### Features

- **Private session activity.** On a running Linux/macOS controller, use `activity --state PATH` to list mapped Discussions, select one with `--discussion NUMBER`, and inspect bounded retained history or `--follow` live responses, tool commands/results and available file-read evidence. Inspection uses the existing agent runtime and never starts a turn. Viewer cancellation leaves agent work running. ([#326](https://github.com/repoagent/controller/pull/326))
- Activity is a local, same-OS-user capability through a private Unix socket, with bounded pages/updates and explicit omissions, truncation or gaps; restart requires a fresh selection/cursor. It supplies no transcript HTTP endpoint or durable live-event replay. Same-UID processes are not isolated. Known controller secrets are redacted, but unknown secrets in tool output can remain visible; treat output and redirected JSON as private operator data. See [activity usage and limits](https://github.com/repoagent/controller/blob/main/docs/controller-operations.md#private-session-activity).
- **Paced accepted-work recovery.** After a correlated failed terminal turn, continue unfinished work after 120 seconds in the same agent session and exact model/reasoning effort. Supported categories are structured `serverOverloaded`, request throttling `rateLimitExceeded`, and `httpConnectionFailed`, `responseStreamConnectionFailed`, `responseStreamDisconnected` or `responseTooManyFailedAttempts` carrying HTTP 408/500/502/503/504. Repeated supported failures get another full wait; pending recovery survives restart and holds later input while releasing the active-session slot. ([#314](https://github.com/repoagent/controller/pull/314), [#328](https://github.com/repoagent/controller/pull/328))
- Recovery waits for the agent's terminal completion and does not compete with its own retries. Usage/quota/credits/plan exhaustion, generic HTTP 429, authentication, context limits, cancellation, generic internal/unknown errors and wrappers without an allowed structured HTTP status are excluded. Continuations inspect prior history, workspace and external effects before finishing the remainder; exactly-once external effects cannot be guaranteed. Status/dashboard retain actual reason, attempts and due time. See [recovery behavior and exclusions](https://github.com/repoagent/controller/blob/main/docs/controller-operations.md#model-capacity-recovery).

### Local development

- Local Aspire AppHost runs explicitly enable private, bounded controller/broker traces in their local collector. Normal production execution has no collector enabled by this feature. Agent/tool children do not inherit tracing exporter configuration or its credentials. Exported diagnostic bundles can include other private process data and must remain private. See [local AppHost tracing](https://github.com/repoagent/controller/blob/main/docs/local-apphost.md). ([#325](https://github.com/repoagent/controller/pull/325))

### Upgrade and qualification

- Deliberately adopt the verified 1.1.0 immutable image or native archive only after completed release metadata is available. Preserve controller enrollment, configuration, state, Codex storage/history and the managed checkout. Keep credential/admission and pending-recovery/checkout fences intact; inspect unfinished or uncertain work before rollback. Resetting state or re-enrolling is not a general recovery fix. This minor release changes no enrollment/profile, wire-protocol or persisted-state schema version.
- Controlled recovery tests and actual shared-runtime normal conversation/activity qualification passed, including live command/read results, viewer cancellation and retained history after restart. Actual failure-specific provider errors, native retry episodes and automatic 120-second/repeated recovery remain unexercised; no live outage or quota exhaustion was induced. These checks do not diagnose historical HTTP 429 incidents or establish production soak coverage. Broker 429 pacing was already present in 1.0.2; process shutdown remains lifecycle ownership, not OS containment.
- Bundled tools are unchanged from 1.0.2: Codex **0.165.0**, GitHub CLI **2.103.3**, cloudflared **2026.10.5**.

## [1.0.2](https://github.com/repoagent/controller/compare/controller-v1.0.1...controller-v1.0.2) (2026-10-29)

### Bug fixes

- Broker-backed controllers on Linux and macOS confirm that captured owned agent processes have stopped before reporting successful shutdown; inability to confirm shutdown is an error. Normal stops still drain accepted work, while loss of broker authority stops owned work. ([#308](https://github.com/repoagent/controller/pull/308))
- Fix trusted Unix launcher startup when Linux or macOS source builds use Windows-style (CRLF) checkout line endings, including native WSL builds. ([#318](https://github.com/repoagent/controller/pull/318))

### Bundled tools

- Update cloudflared from **2026.10.1** to **2026.10.5**. The [qualified tool-refresh run](https://github.com/repoagent/controller/actions/runs/40827240231) passed the real installer, Quick Tunnel, App reply and EYES receipt smoke before updating the pin. Codex **0.165.0** and GitHub CLI **2.103.3** are unchanged.

### Upgrade and limitations

- Adopt a verified `controller-v1.0.2` immutable image or native archive deliberately, preserving configuration, controller state, Codex storage and session history. This patch changes no enrollment/profile, protocol or persisted-state schema; an existing-enrollment live upgrade has not been demonstrated by this release's qualification run.
- Process shutdown confirmation is lifecycle ownership, not OS containment or a guarantee of discovering historical detached processes. See the [ownership limits](https://github.com/repoagent/controller/blob/main/docs/controller-architecture.md#owned-agent-process-shutdown).

## [1.0.1](https://github.com/repoagent/controller/compare/controller-v1.0.0...controller-v1.0.1) (2026-10-28)

### Maintenance

- Repair controller delivery verification on clean environments. The native/archive and image verification gates provision checksum-pinned JMESPath 1.0.1 in a temporary isolated Python environment, without a global package installation. This is a maintainer verification dependency, not a new controller runtime requirement.
- Use the settled controller enrollment, controller secret, agent runtime and agent session terminology in installer status and shipped guidance. Existing legacy credential fields and routes remain supported; do not rewrite private enrollment files or approval markers to rename them.
- Keep controller and broker release proposals independent when ownership-map entries change. Mapping metadata alone does not propose a release; actual owned runtime/build changes still determine component attribution, including genuinely shared changes.

### Upgrade and installation

- Adopt a verified `controller-v1.0.1` immutable image or native archive deliberately. Preserve the existing configuration, controller state, Codex storage and session history. Legacy `baseline-*` installations still require a manual move to a verified semantic image; old release readers may not discover the new tags. There is no self-update.
- No authentication, credential, enrollment/profile, protocol, persisted-state, controller configuration or deployment-epoch migration is required. Existing approval and renewal contracts remain unchanged.
- Bundled tools are unchanged from controller 1.0.0: Codex **0.165.0**, GitHub CLI **2.103.3**, and cloudflared **2026.10.1**. This maintenance release does not change tool pins.
- The native archive still targets **Linux amd64**, requiring **glibc 2.39 or newer** and the normal ICU/OpenSSL dependencies. Windows and ARM native archives are not provided. The archive and image bundle the same version-specific release notes.

### Known limitations

- A [hosted smoke at source dff8a907](https://github.com/repoagent/controller/actions/runs/40786967089) passed installation, Quick Tunnel delivery, an App reply with an EYES receipt, and a second dashboard heartbeat using the unchanged pinned tools. It does not qualify a new tool candidate (#207) or prove renewal of a pre-upgrade existing enrollment against the upgraded deployment (#197).
- Rendered GitHub light/dark and narrow-screen layouts were not inspected. Account usage was unavailable, and no artificial turns were started to measure it. No actual quota-exhaustion, continuous-uptime or recovery guarantee follows from this smoke.
- Cleanup confirmed broker closure but reported outstanding tokens as unsettled. Revocation, positive token settlement and durable owner release remain prerequisites for enrollment replacement; health responses and closure acknowledgments do not establish settlement.

## 1.0.0 (2026-10-28)

### Features and fixes

- First independently versioned controller release, with matching native archives, container version diagnostics and bundled offline release notes. Controller and credential broker versions now advance independently through reviewed Release Please proposals.
- The optional Discussion dashboard leads with a UTC heartbeat, work capacity and current blockers; links to active and idle Discussions; and groups historical failures with expandable evidence. Enable it with installer `-DashboardCategory 'Existing category name'`.
- Dashboard account-usage observations distinguish available values, unsupported or unavailable collection, failed reads and stale data. Account allowances may span repositories; missing observations never mean zero capacity.
- Broker HTTP 429 responses coordinate token-renewal backoff while retaining a current token until its safety cutoff. Startup waits for credentials; definitive credential rejection still stops owned work. Matching upgraded brokers reclaim safely expired issuance capacity and expose bounded rejection diagnostics.
- Controller core lifetime ownership, orderly shutdown and delivery recovery now keep waiting input and retained status/log evidence across restarts. Uncertain checkout activity holds new turns while keeping status available, with an explicit offline recovery command for supported startup cases.
- Private host Codex sign-in files can be imported across different host/container user IDs. Credential replacement waits for active work to finish and preserves session history.

### Upgrade and installation

- Image adoption remains deliberate. Controllers running legacy `baseline-*` images need one manual move to a verified `controller-v1.0.0` image digest; the dashboard reports this as a legacy release migration. Preserve the existing configuration, controller state, Codex storage and session history when replacing the image. A versioned release does not itself migrate or re-enroll an existing installation.
- Existing enrollments retain their original contracts. Only after upgrading both the broker and controller to matching implementations should a fresh enrollment explicitly opt in with `-ControllerEnrollmentVersion 5`. Setup v5 uses canonical enrollment names and a new private-file format; it is not the default. Revocation, broker closure, positive token settlement and owner release remain prerequisites for enrollment replacement.
- Bundled tool pins are unchanged: Codex **0.165.0**, GitHub CLI **2.103.3**, and cloudflared **2026.10.1**. This release does not authorize a tool-pin refresh.
- The native executable is **Linux amd64** and requires **glibc 2.39 or newer**, with normal ICU/OpenSSL dependencies. Windows and ARM native archives are not provided. The archive includes packaged skills, `VERSION`, `RELEASE_NOTES.md` and `SHA256SUMS`; the image bundles the same release-note bytes.

### Acceptance limits

- Usage is observational. No actual quota exhaustion or account-wide capacity guarantee is established by this release; actual provider usage outcomes remain distinct from fixture and local test evidence.
- Dashboard Markdown/API checks do not establish rendered GitHub light/dark or narrow-screen behavior. Actual HTTPS/provider behavior, private-repository availability, native Windows protected-file execution and Docker Desktop credential injection still require their corresponding acceptance evidence.
- Hosted installer acceptance remains limited by [#283](https://github.com/repoagent/controller/issues/283), which records a failed smoke run. No successful tool-pin gate or assembled enrollment/migration acceptance is claimed here. A broker readiness response proves availability only; DELETE 204 proves closure only, never token settlement. Settlement preflight and assembled migration acceptance remain tracked in [#249](https://github.com/repoagent/controller/issues/249).
