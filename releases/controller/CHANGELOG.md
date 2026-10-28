# Changelog

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

- Packaging, source tests and versioned publication do not establish successful hosted enrollment/replacement, rendered GitHub light/dark or narrow-screen behavior, actual provider usage outcomes or renewal of a pre-upgrade existing enrollment against the upgraded deployment. Those acceptance items remain open; no new uptime, recovery or settlement proof is claimed.
- Revocation, positive token settlement and durable owner release remain prerequisites for enrollment replacement. Health responses and closure acknowledgments do not establish settlement; remaining hosted and renewal acceptance is tracked in #207/#283/#197.

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
