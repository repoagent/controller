# Credential broker durable storage proposal

Historical proposal: the maintainer subsequently selected Azure Table Storage and [repository-held approval](../auth-repository-approval.md) with no broker browser/admin UI. The Cosmos recommendation and administrator/browser ceremony below are superseded. Retain this research as background; use the current approval contract, [broker policy](../broker-policy.md) and implementation issues for delivery requirements.

Research date: 2026-10-20. Engineering proposal for #30/#35, following [runtime research](2026-10-20-azure-auth-service-operations.md) and [the .NET stack](2026-10-20-dotnet-authorization-stack.md). No resources, credentials or live-store tests were used. Storage is not yet approved or implemented.

## Recommendation and evidence

Choose **Azure Cosmos DB for NoSQL serverless**, one account and one partitioned broker container. Use managed identity, direct SDK transactions and session reads with mandatory conditional writes for authorization. This recommendation favors managed cleanup and backup over the fewest Azure resources. Serverless charges for consumed RUs and storage without provisioned throughput, supports ordinary container operations, and is restricted to one region. It suits this small intermittent workload; measured costs and admission limits remain necessary. [Serverless](https://learn.microsoft.com/en-us/azure/cosmos-db/serverless)

| Capability | Cosmos DB for NoSQL serverless | Azure Table Storage |
| --- | --- | --- |
| Atomic changes | Same-container, same-logical-partition ACID batches with snapshot isolation; 100 operations, 2 MB, five seconds. [Batch documentation](https://learn.microsoft.com/en-us/azure/cosmos-db/transactional-batch) | Same-table, same-partition atomic changes; 100 entities, 4 MiB. [Entity transactions](https://learn.microsoft.com/en-us/rest/api/storageservices/performing-entity-group-transactions) |
| Concurrency | Conditional ETag replacement rejects stale versions with 412. [Concurrency](https://learn.microsoft.com/en-us/azure/cosmos-db/database-transactions-optimistic-concurrency) | `If-Match` updates reject stale versions with 412. [Update Entity](https://learn.microsoft.com/en-us/rest/api/storageservices/update-entity2) |
| Retention | Managed TTL; physical deletion can lag and serverless deletion consumes billed RUs. [TTL](https://learn.microsoft.com/en-us/azure/cosmos-db/time-to-live) | Plan application deletion and a scheduled sweeper; the documented service operations provide explicit deletes. [Table transactions](https://learn.microsoft.com/en-us/rest/api/storageservices/performing-entity-group-transactions) |
| Recovery | Periodic or continuous backup available on serverless. [Serverless backup options](https://azure.microsoft.com/en-us/pricing/details/cosmos-db/serverless/) | No built-in point-in-time recovery; custom export/backup required. [Table reliability](https://learn.microsoft.com/en-us/azure/reliability/reliability-storage-table) |

Table Storage is technically viable with the same aggregate protocol, and could share the existing Blob storage account. However, custom backup and cleanup add code and operational responsibility. Do not implement a second provider absent a demonstrated Cosmos blocker.

The first-party `Microsoft.Azure.Cosmos` v3 SDK targets .NET Standard 2.0, compatible with .NET 10; its `TokenCredential` integration supports managed identity. Use `ManagedIdentityCredential` explicitly in production and a container-scoped data role. This establishes a supported library path, not tested deployment compatibility. Table Storage also supports Entra managed identities and the first-party `Azure.Data.Tables` SDK. [Cosmos SDK](https://learn.microsoft.com/en-us/azure/cosmos-db/nosql/sdk-dotnet-v3), [.NET compatibility](https://learn.microsoft.com/en-us/dotnet/standard/net-standard), [Cosmos identity](https://learn.microsoft.com/en-us/azure/cosmos-db/how-to-dotnet-get-started), [credential guidance](https://learn.microsoft.com/en-us/dotnet/azure/sdk/authentication/best-practices), [Table identity](https://learn.microsoft.com/en-us/azure/storage/tables/authorize-access-azure-active-directory), [Tables SDK](https://learn.microsoft.com/en-us/dotnet/api/overview/azure/data.tables-readme?view=azure-dotnet)

## Proposed transaction boundary

The following is broker design, not a guarantee supplied by Cosmos.

Use partition key `repository:<immutableGitHubRepositoryId>`. Keep a repository gate, enrollment records, approval/completion records, issuance attempts and ownership accounting together. The gate contains status, generation, current owner and outstanding-token upper bound. Never TTL-delete this gate while authority or uncertainty remains; a missing gate denies access and requires deliberate enrollment initialization, never automatic renewal recreation.

Each authorization-changing transaction conditionally replaces the **same gate item** and changes its generation or sequence. Include ETag conditions for every device, approval or other record whose stale value could authorize the operation; a current gate combined with an unchecked stale device is insufficient. This forces conflicting mint, revoke, completion and handover operations to contend on a common record; snapshot isolation alone would not prevent write skew across different records. Microsoft's single-write-region ETag semantics compare against the current server version. A stale session read therefore cannot produce a successful authorization commit. [Concurrency contract](https://learn.microsoft.com/en-us/azure/cosmos-db/database-transactions-optimistic-concurrency)

Do not claim session reads see every other replica's revocations. Strong consistency provides current committed reads, but the reviewed serverless documentation does not explicitly enumerate supported consistency levels. This proposal does not depend on strong being selectable; verify actual configuration during provisioning. [Consistency semantics](https://learn.microsoft.com/en-us/azure/cosmos-db/consistency-levels)

A random `deviceId` resolves through a broker-written, immutable locator to the repository partition. Locators are routing hints, never authority: load the repository's device record, compare secret digest, identity, generation and epoch, then pass the conditional gate. Never trust caller-supplied repository scope. Missing, stale or orphan locators deny. Publish locators idempotently after authoritative records exist; never reuse IDs.

Resolve the requested repository through GitHub before storing a pending approval in its repository partition. Bind the browser request and installer poll digest to that immutable ID. Browser approval checks numeric approver identity/admin authority, App installation and exact permissions. Atomically consume approval and create the device plus encrypted, deadline-bound completion envelope. A lost response retries the same completion; acknowledgment activates it atomically. Unacknowledged devices cannot mint and expire closed. Pre-resolution rendezvous records, if necessary, are separate and non-authoritative; no cross-partition transaction is assumed. Discard personal OAuth credentials after enrollment under the approved policy.

## Mint, revoke and uncertainty

Before **every** GitHub token POST, including internal administrator checks, durably reserve an attempt under the gate. Record operation ID, owner generation, worker lease, and a conservative latest possible token expiry. Do not retry an ambiguous POST automatically: each additional POST needs another accounted reservation. GitHub tokens last one hour; App JWTs expire within ten minutes. [Token issuance](https://docs.github.com/en/rest/apps/apps#create-an-installation-access-token-for-an-app), [JWT lifetime](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-json-web-token-jwt-for-a-github-app)

A worker lease cannot fence GitHub. Bind each attempt to a fixed-expiry App JWT, prohibit refreshing it under that reservation, and bound late request acceptance plus token lifetime and clock/network uncertainty. Otherwise a paused worker could mint after the accounting deadline. This external-call bound is a technical proof gate, not an assumed SDK property.

After mint, persist actual expiry and scope; release only after another conditional gate transaction confirms active enrollment, unchanged ownership and current GitHub authorization. Revoke immediately closes that gate and prevents new issuance admissions/releases. Already admitted external calls can finish later; record and revoke their known tokens, never deliver them to a revoked device. A release committed before revoke is already-issued authority, even if its network response arrives afterward.

If a token is unknown or revocation unconfirmed, retain its conservative bound. Handover waits until every old-owner token is confirmed revoked or expired plus uncertainty. Lease expiry only permits recovery work; it does not erase attempts, grant ownership or permit redoing an uncertain mint. Include response loss after database commits and after GitHub responses. GitHub's revoke endpoint requires the token itself. [Revocation endpoint](https://docs.github.com/en/rest/apps/installations#revoke-an-installation-access-token)

## Cleanup and recovery

Apply approved configurable defaults: approvals 15 minutes, abandoned enrollment cleanup within 24 hours, logs 30 days, inactive enrollment expiry 90 days. Check absolute logical deadlines on every operation. Cosmos TTL is relative to last modification, hides expired items from queries before physical deletion, and has no documented maximum deletion lag. It cannot establish the 24-hour physical-cleanup promise. [TTL behavior](https://learn.microsoft.com/en-us/azure/cosmos-db/time-to-live)

Use a scheduled Container Apps job with bounded batches, retries and lag alerts, independent of HTTP replicas. Delete pending payloads well before 24 hours; retain separate minimal cleanup receipts and security accounting. Do not depend on querying already TTL-hidden items to discover failures. Keep safety-critical accounting exempt until safe, then schedule deletion. Native TTL is a fallback. Scheduled jobs support cron execution. [Jobs](https://learn.microsoft.com/en-us/azure/container-apps/jobs)

Treat 24 hours as the approved live-data cleanup target to demonstrate under normal operation, with outage recovery and missed-target alerts documented. Do not promise physical deletion through a total Azure outage. Report backup retention separately because recovery copies outlive live-data deletion. Continuous seven-day backup avoids bespoke backup code; restore is charged and retains TTL settings unless explicitly disabled. [Continuous backup](https://learn.microsoft.com/en-us/azure/cosmos-db/continuous-backup-restore-introduction)

Restore with issuance disabled. Stop/fence old workers and signing access; select a fresh deployment epoch outside the restored database, reject all older enrollments, wait the full possible old-JWT/token acceptance-and-expiry window, then require re-enrollment. Never resume restored grants automatically. Keep the Blob Data Protection key ring separate.

## Bounded implementation slices and release gates

1. Implement the repository aggregate and conditional transaction adapter; verify stale-client conflicts, rollback, response-loss reconciliation and managed identity against a real disposable store.
2. Implement approval, locators and completion recovery; test replay, wrong repository, abandoned completion and concurrent acknowledgment/revoke.
3. Implement accounted issuance and handover; prove late-worker bounds, all internal tokens, concurrent revoke, ambiguous POST and deployment overlap with controlled faults.
4. Deploy cleanup and fail-closed restore tooling; measure deletion lag at zero HTTP replicas, exercise backup recovery and verify configuration/cost limits.

These acceptance experiments validate this composition; do not add permanent tests merely asserting Microsoft's documented database guarantees. No hosted readiness claim is justified until the external-call bound, cleanup semantics and real-store results pass review.
