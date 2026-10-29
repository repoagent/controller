# Changelog

## [1.2.0](https://github.com/repoagent/controller/compare/broker-v1.1.0...broker-v1.2.0) (2026-10-29)


### Operator diagnostics

* Add explicit main-only `operation=authority-probe` to the existing Broker workflow. It invokes the internal retained-authority inspector already shipped in broker 1.1.0, binding the accepted current-main source, published stable broker release, immutable image, ready revision and unique healthy replica before and after the fixed command. It uses the existing console authority and runtime identity; no new role, public HTTP endpoint or arbitrary command/target is introduced. See the [operator procedure](https://github.com/repoagent/controller/blob/broker-v1.2.0/docs/azure-auth-deployment.md#internal-retained-authority-observation).
* Extend GET-only `operation=diagnostics` with fixed historical request summaries for October 27, 2026 21:30–22:15 UTC and October 28 05:30–06:30 UTC. The existing production Insights/workspace query emits only one-minute endpoint/status categories and bounded counts. Unknown routes become `other`; raw request names, URLs, properties, identifiers and caller/session data are not selected or exported. Stored rows, sampling-weighted represented requests and missing weights remain separate, with explicit incomplete coverage.

### Upgrade and compatibility

* Adopt a verified `broker-v1.2.0` immutable image deliberately through the existing paired API/cleanup deployment. Require matching release metadata, verified image identities, deployed digests and HTTPS readiness before reporting deployment complete. The workflow additions use accepted current-main source; the internal inspector requires broker 1.1.0 or newer and its fixed production runtime identity/Table bindings.
* No controller upgrade, tool-pin update, credential or authorization migration, protocol/storage-schema migration, deployment-epoch change or RBAC expansion is required. Version selection does not authorize accounting repair, credential recovery, owner reassignment or storage restore. Existing enrollment, approval, renewal and settlement contracts remain unchanged.

### Evidence and limitations

* The real [authority probe 40786288383](https://github.com/repoagent/controller/actions/runs/40786288383) observed an unchanged vacant gate with zero pending and outstanding counts in its fixed operational test repository. The following [hosted smoke 40786967089](https://github.com/repoagent/controller/actions/runs/40786967089) passed fresh installation, Quick Tunnel delivery, EYES receipt, App reply and dashboard heartbeat. These are observations of those runs, not a general readiness, recovery or settlement guarantee. Hosted cleanup confirmed enrollment closure while still reporting outstanding tokens as unsettled.
* The actual [historical diagnostics 40794497776](https://github.com/repoagent/controller/actions/runs/40794497776) successfully collected an empty request aggregate for both reported windows. Empty results do not prove zero traffic or explain the historical HTTP 429s. The affected source sampled request traces at 1%; retention, ingestion loss and caps can also limit coverage. Weighted counts are estimates, and aggregates cannot identify callers or historical limiter reasons. The investigation remains open under #269.
* Retained-authority inspection still covers referenced rows only, uses bounded exact reads and detects observed drift without locking the deployment. Missing, malformed, changed or incomplete evidence stays explicit. Stored states, expired clocks and console-session completion do not prove capability settlement or remote process exit status.
* This release does not establish physical historical rollback/restore acceptance, real renewal of a pre-upgrade enrollment, rendered GitHub light/dark or narrow-screen UI, or compatibility of a newer upstream tool candidate. Those require their own evidence; no automatic recovery or additional authority is granted.

## [1.1.0](https://github.com/repoagent/controller/compare/broker-v1.0.0...broker-v1.1.0) (2026-10-28)

### Features and fixes

* Add maintainer-only `dotnet RepoAgent.Auth.dll --inspect-retained-authority`. It observes one operational test repository using the accepted production runtime identity and dedicated Table. It performs at most six exact reads, including a gate reread, with a 20-second budget and 16 KiB output cap. It never scans, lists, writes, repairs accounting or composes the HTTP host, signing, protection, GitHub or telemetry.
* The main Broker workflow provides bounded GET-only deployment, cleanup, alert and aggregate-metric diagnostics, plus a separate explicit version-only runtime console probe. The probe binds the published release mapping, immutable image, ready revision and one selected healthy replica before running the fixed early version command.
* Execution-history diagnostics now discard private container snapshots before bounded output and retain useful samples from large Azure pages. They inspect only the first 100 rows of one returned page, publish at most 20 samples and three replica observations, and disclose truncation. Samples are not guaranteed to be globally latest or complete history.

### Upgrade and compatibility

* Adopt the verified `broker-v1.1.0` immutable image deliberately through the existing paired API/cleanup deployment. Require successful image/release verification, matching deployed digests and HTTPS readiness checks before reporting deployment complete. Broker 1.0.0 does not support the new inspection command.
* The internal command requires the accepted production runtime user-assigned identity, Table endpoint/name and existing deployment epoch. Other identities, development credentials and connection-string fallback are refused. A different target or identity rotation requires a reviewed source change; see the [operator guidance](https://github.com/repoagent/controller/blob/broker-v1.1.0/docs/azure-auth-deployment.md#internal-retained-authority-observation).
* No controller upgrade, protocol or persisted-state migration, deployment-epoch change, RBAC expansion, credential migration or tool-pin update is required. Existing enrollment and permission contracts remain unchanged.

### Known limitations

* The inspection covers referenced rows only. Missing, malformed, mismatched and concurrently changed evidence remains explicit; expired clocks and observed stored states never establish settlement or replacement readiness. Reports omit private record identifiers, epoch values, protected payloads and provider errors. This release adds no workflow invocation mode for the internal inspection command or public HTTP inspection endpoint.
* Successful GET diagnostics and the version-only console observation establish their respective collection scopes, not actual retained-Table access, complete cleanup, historical HTTP 429 causation, hosted replacement, restore or notification acceptance. Console-session completion does not establish remote process exit status.
* Version-selected rollback remains documented and regression-tested; a physical historical rollback has not been observed. Retained-state and hosted acceptance remain open in #283/#269.

## 1.0.0 (2026-10-28)

### Features

* First independently versioned credential broker release, with matching compiled/OCI identity and verified version-to-digest metadata.
* Bounded reclamation of positively expired issuance capacity and fixed-category rejection diagnostics for renewal pressure.
* Opt-in setup v5 uses canonical controller terminology while preserving existing setup v1-v4 contracts, approval markers and permission profiles.

### Compatibility and operations

* API and cleanup deploy from the same verified immutable digest; version-selected rollback preserves that pairing.
* Existing retained state, deployment epoch and retention policy are preserved. Revocation acknowledgements do not establish capability settlement.
* Live replacement acceptance remains open in #283. The new diagnostics and mitigation do not establish the historical cause of production HTTP 429.
