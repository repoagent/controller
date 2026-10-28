# Changelog

## [1.1.0](https://github.com/repoagent/controller/compare/broker-v1.0.0...broker-v1.1.0) (2026-10-28)


### Features

* **broker:** add bounded read-only operational diagnostics ([2348ca9](https://github.com/repoagent/controller/commit/2348ca94b2cb8dcfebceb45095e746f39c66d731))
* **broker:** add bounded retained-authority observation ([8c0e598](https://github.com/repoagent/controller/commit/8c0e5986e032796ac7643f190d5a9f757424e194))
* **broker:** add guarded version-only runtime probe ([4cb8b89](https://github.com/repoagent/controller/commit/4cb8b89969a0b0183bfe0693d5845714c1b3fb1a))


### Bug Fixes

* **broker:** bound execution history before diagnostic output ([9e282ae](https://github.com/repoagent/controller/commit/9e282ae21b7969226f1e9f96585c6bc88db201ef))
* **broker:** retain bounded prefix of large execution pages ([#292](https://github.com/repoagent/controller/issues/292)) ([79398e5](https://github.com/repoagent/controller/commit/79398e530516fd078227cc2b9f5121ba29cfc0d1))

## 1.0.0 (2026-10-28)

### Features

* First independently versioned credential broker release, with matching compiled/OCI identity and verified version-to-digest metadata.
* Bounded reclamation of positively expired issuance capacity and fixed-category rejection diagnostics for renewal pressure.
* Opt-in setup v5 uses canonical controller terminology while preserving existing setup v1-v4 contracts, approval markers and permission profiles.

### Compatibility and operations

* API and cleanup deploy from the same verified immutable digest; version-selected rollback preserves that pairing.
* Existing retained state, deployment epoch and retention policy are preserved. Revocation acknowledgements do not establish capability settlement.
* Live replacement acceptance remains open in #283. The new diagnostics and mitigation do not establish the historical cause of production HTTP 429.
