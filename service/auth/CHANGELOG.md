# Changelog

## 1.0.0 (2026-10-28)

### Features

* First independently versioned credential broker release, with matching compiled/OCI identity and verified version-to-digest metadata.
* Bounded reclamation of positively expired issuance capacity and fixed-category rejection diagnostics for renewal pressure.
* Opt-in setup v5 uses canonical controller terminology while preserving existing setup v1-v4 contracts, approval markers and permission profiles.

### Compatibility and operations

* API and cleanup deploy from the same verified immutable digest; version-selected rollback preserves that pairing.
* Existing retained state, deployment epoch and retention policy are preserved. Revocation acknowledgements do not establish capability settlement.
* Live replacement acceptance remains open in #283. The new diagnostics and mitigation do not establish the historical cause of production HTTP 429.
