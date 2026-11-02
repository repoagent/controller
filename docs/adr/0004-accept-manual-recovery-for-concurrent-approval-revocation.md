# Accept manual recovery for concurrent approval revocation

Under [ADR 0003's controller approval](0003-use-repository-held-approval-for-a-ui-free-broker.md), host revocation checks the exact observed value and makes one name-addressed deletion. GitHub offers no atomic compare-delete, so an independent writer may lose a newer approval; accept temporary service loss and manual inspection/reapproval rather than adding distributed locks, leases or automatic repair. Broker closure and settlement of already-issued tokens remain separate safety gates; the availability tradeoff never authorizes revival or replacement before old authority is safely resolved.

The outstanding-token settlement/replacement requirement is superseded by [ADR 0017](0017-keep-broker-tokens-in-request-memory.md), accepted on 2026-11-02. Shared-key separation and permanent closure of the old enrollment remain; previously issued tokens rely on provider expiry.
