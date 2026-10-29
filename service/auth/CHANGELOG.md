# Changelog

## [1.2.0](https://github.com/repoagent/controller/compare/broker-v1.1.0...broker-v1.2.0) (2026-10-29)


### Features

* **broker:** add guarded retained-authority probe ([dff8a90](https://github.com/repoagent/controller/commit/dff8a90797165a6e7e690a44eeb52a9570dd759b))
* **broker:** inspect fixed historical throttle request windows ([1336e76](https://github.com/repoagent/controller/commit/1336e7667331417729c673e9983b78b3954f6b12))

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
