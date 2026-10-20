# Low-maintenance Azure authorization service

Research date: 2026-10-20. This is a proposal for issues [#30](https://github.com/repoagent/controller/issues/30) and [#35](https://github.com/repoagent/controller/issues/35), not an approved storage decision or a deployed service. The maintainer has offered to own and fund the small Azure service. Users own their separately hosted controllers. The [existing ADR](../adr/0001-keep-the-shared-app-key-out-of-controller-installations.md) keeps the shared App key in the trusted broker; the [library research](2026-10-20-dotnet-authorization-stack.md) describes the proposed .NET implementation.

Subsequent setup selected Azure Container Apps and established the resource group, GitHub OIDC identity and a successful Bicep access check. The [deployment runbook](../azure-auth-deployment.md) owns the current configuration and evidence. Runtime resources and hosted broker acceptance remain pending; the other recommendations below remain proposals.

## Runtime and traffic

Azure Container Apps is the selected hosting platform. Propose a separate .NET 10 LTS ASP.NET Core service with HTTPS ingress, HTTP scaling, minimum zero and an initial maximum of one replica. HTTP can wake the app from zero. Cold starts are an explicit availability/latency tradeoff. Platform maintenance can temporarily produce extra replicas, and limits apply per revision, so maximum one is not authorization serialization. All mint/revoke coordination must be durable. [Container Apps scaling](https://learn.microsoft.com/en-us/azure/container-apps/scale-app)

The controller caches GitHub's one-hour installation token and renews before expiry. At one renewal every 55 minutes, a continuously running controller makes about 26 routine issuance requests per day, plus enrollment, retries and restarts. This is an estimate, not a measured request budget. GitHub content requests and agent traffic go directly to GitHub; the broker does not proxy them. The provider may reuse a short-lived App JWT under a bounded expiry policy to reduce Key Vault signing operations, while still rechecking enrollment and GitHub authorization for each issuance. Add renewal jitter, bounded retries, rate limits and admission caps.

Container Apps consumption usage stops at zero replicas; storage, registry, Key Vault and logging remain separate costs. Do not promise a zero monthly bill from the Container Apps free allowance. [Container Apps billing](https://learn.microsoft.com/en-us/azure/container-apps/billing)

## Deployment and identities

Use GitHub Actions to test, build and deploy an immutable image from this repository. Authenticate Azure deployment using the official Azure Login action with OIDC and a federated credential restricted to the intended repository and protected branch/environment. This avoids a long-lived Azure client secret in GitHub. Grant workflow `id-token: write` only where necessary. [Azure Login OIDC guidance](https://github.com/Azure/login)

Use managed identity for runtime Azure access and image pulling where supported. The deployment identity and runtime identity have different duties. Protect the deployment workflow: an identity allowed to replace this service can indirectly exercise its runtime authority. Retain the preceding image/configuration for rollback and run staged acceptance before advertising an endpoint. Microsoft documents the Actions-to-Container-Apps build/deploy path; older examples using a stored Azure credential are not a reason to prefer it over OIDC. [Container Apps GitHub Actions](https://learn.microsoft.com/en-us/azure/container-apps/github-actions)

Keep browser-cookie and antiforgery Data Protection keys durable across restarts and revisions. First-party Blob persistence supports sharing the key ring across instances through managed identity. Use a stable application discriminator. The key ring and its protection keys have a separate lifetime from enrollment rows. [Data Protection key storage](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/implementation/key-storage-providers?view=aspnetcore-10.0)

The auth service receives traffic from users' browsers and arbitrary controller hosts. GitHub-only ingress filtering belongs to a customer's webhook endpoint; it would block this service's enrollment and renewal traffic. Keep the service's canonical HTTPS origin fixed, configure proxy handling deliberately, and apply authentication, antiforgery and rate limits to the appropriate routes.

## Storage and cleanup options

No broker database has been approved. Choose it based on both transactional authorization and operations, then record the decision before provider implementation. In-memory state, local container files and a replica count are insufficient for restart-safe revocation.

Azure Cosmos DB for NoSQL is one candidate because it offers managed per-item TTL. TTL is based on last modification, requires container TTL to be enabled, and removes expired items from query results before physical deletion necessarily finishes. Serverless expiry deletion consumes request units. Application authorization must check explicit expiry; garbage collection is not the grant state machine. [Cosmos DB TTL](https://learn.microsoft.com/en-us/azure/cosmos-db/time-to-live)

Cosmos transactional batches are ACID within one logical partition. A candidate schema must place every item needed for a repository's mint/revoke/ownership transaction within the chosen transaction boundary. Do not assume cross-partition atomicity. [Transactional batches](https://learn.microsoft.com/en-us/azure/cosmos-db/transactional-batch)

Read consistency also matters: session consistency alone is not a promise that a different process immediately sees a revocation. Validate the chosen account configuration and concurrency protocol with real database tests, including deployment overlap and response loss. Serverless cost characteristics and supported account settings need confirmation against the final resource plan. These sources do not establish that an arbitrary serverless configuration meets the broker's contract. [Consistency semantics](https://learn.microsoft.com/en-us/azure/cosmos-db/consistency-levels), [Serverless accounts](https://learn.microsoft.com/en-us/azure/cosmos-db/serverless)

For a store without managed TTL, run an independent scheduled cleanup job with bounded batches and retries. A scheduled Container Apps job is available, but it is an additional deployed component; a background loop inside a scaled-to-zero API cannot supply continuous cleanup. [Container Apps jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs)

## Proposed retention policy, pending specification

The durations below are starting points for review, not approved product defaults:

| Record | Logical lifetime | Cleanup requirement |
| --- | --- | --- |
| Pending browser approval | About 15 minutes | Reject after expiry immediately; delete abandoned data within a bounded window, such as 24 hours. |
| Completion recovery envelope | Short acknowledgment deadline | An unacknowledged enrollment becomes unusable at the deadline; deletion alone must not leave an active unclaimed device. |
| Issued-token accounting | Actual token expiry plus uncertainty/safety window | Include internal-check tokens and ambiguous mint results; never delete evidence while a token could still authorize an old owner. |
| Active device grant | Until revoked or a defined inactivity limit, initially consider 90 days | Renew activity under policy; never delete an active grant merely because its creation date is old. |
| Revoked device | Finite audit/recovery window | Missing records deny access; identifiers are never reused; restoring an older backup must not reactivate a deleted revocation. |
| Operational audit/logs | Initially consider 30 days | No token/secret/body logging; cap event sizes, sample routine success and bound retained volume. |
| Cookie/protection keys | Until every protected payload and recovery copy is outside its supported lifetime | Separate key management policy; do not apply the pending-enrollment TTL to key rings. |

Retention bounds age, not cardinality. Cap pending requests, enrollments and request/body sizes per principal/repository and globally. Bound issuance history and retry envelopes, throttle unauthenticated endpoints, and reject admission before storage/cost bounds are exceeded. Define cleanup-lag metrics and alert on failure without requiring daily operator checks. Logs and backups need their own retention and recovery rules.

For backup recovery, choose a concrete fail-closed mechanism: for example, invalidate pre-recovery device grants and require re-enrollment, while delaying ownership reassignment until outstanding tokens cannot remain usable. An old database snapshot plus today's signing authority must not resurrect a revoked grant. This is application policy that TTL cannot provide.

## Key rotation and ongoing maintenance

Use Key Vault for remote signing via the first-party SDK composition verified in the [local compatibility check](2026-10-20-dotnet-key-vault-signing-check.md). The runtime need not export the GitHub App PEM. Initial import and permissions still require a separately reviewed provisioning step.

GitHub generates App private keys and retains their public halves; keys do not expire automatically, and multiple keys allow overlap during replacement. Therefore Key Vault automatic generation of a new RSA pair alone cannot rotate a GitHub App credential: GitHub would not recognize the new public key. Coordinate GitHub key generation, protected import, verified switch and old-key removal. Keep this signing key distinct from any independently rotating data-protection wrapping key. [GitHub App private-key lifecycle](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/managing-private-keys-for-github-apps)

Propose scheduled dependency/base-image update checks, automated test/build/deploy checks, budget and failure alerts, and a short restore/revocation/key-rotation runbook. Normal expiry and cleanup should be automatic. Incidents, a compromised App key, authorization-policy changes and occasional coordinated key rotation still require maintainer judgment; there is no demonstrated zero-maintenance security boundary.

## Gates before hosted readiness

- Approve the storage/partition/consistency, grant-lifetime and retention policies; record any change from retained human refresh credentials explicitly.
- Prove real database expiry, conditional writes, transactions, restart, concurrent mint/revoke and backup recovery.
- Prove real Key Vault managed identity, minimum operations, signing, GitHub acceptance, timeout and version rotation.
- Prove browser approval cannot be performed by someone holding only the installer's poll secret.
- Prove administrator-access loss, installation/repository changes and revocation fail closed; account for already-issued tokens during ownership handover.
- Prove cleanup and expiry work while the HTTP service has zero replicas, including bounded backlog and request bursts.
- Execute the staged GitHub/Azure deployment journey and document cold-start behavior, measured traffic, cost controls and recovery.

Live broker behavior remains unverified. The subsequent deployment-access bootstrap and its evidence are recorded in the deployment runbook.
