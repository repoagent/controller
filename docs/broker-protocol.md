# Controller credential protocol v1

This is the consumer contract for [deployment plan #28](https://github.com/repoagent/controller/issues/28). It does not establish that an enrollment service or hosted broker exists. Direct App authentication remains available for an operator's own App.

The approved provider/installer design is now [repository-held approval with a UI-free auth service](auth-repository-approval.md). It supersedes the earlier browser/personal-administrator ceremony below without changing the frozen v1 consumer fields, runtime permission profiles or cache lifecycle.

## Enrollment file

The installer will eventually write this file atomically after the broker verifies repository-held approval. The approved installer will create or safely reuse a user-named direct repository Agent variable through the host's GitHub authority. [Provider #68](https://github.com/repoagent/controller/issues/68) implements the separate [v2 setup/marker contract](../service/auth/README.md#repository-agent-variable-enrollment-v2); legacy Environment setup remains v1. Host #53 owns guided installation and private-file/Windows acceptance. Until that assembled flow is available, this file remains an operator-supplied integration input. Treat it as a secret: owner-only access (0600 on Unix, a private user ACL on Windows), never an image build input or command-line argument value. The CLI receives its path through `--enrollment-file` and separately requires `--broker-url`. Marker coordinates belong in broker state and separate nonsecret installer configuration, preserving this v1 schema.

```json
{
  "protocolVersion": 1,
  "brokerOrigin": "https://credentials.example.org",
  "githubHost": "github.com",
  "deviceId": "example-device",
  "deviceSecret": "EXAMPLE_ONLY_REPLACE_WITH_256_BITS_OF_RANDOMNESS",
  "appId": 5387625,
  "appSlug": "repo-agent-connector",
  "installationId": 123,
  "repositoryId": 456,
  "repository": "owner/repository",
  "permissions": { "discussions": "write" }
}
```

All shown fields are required with the shown JSON types; IDs are positive 64-bit integers and the version is exactly 1. Duplicate or unknown fields are invalid. Files and responses are limited to 64 KiB. `deviceId` is 1–128 ASCII letters, digits, hyphens or underscores; it cannot add URL path segments. `deviceSecret` is a 43–128 character base64url value generated from at least 256 random bits. The example is a placeholder, not a usable enrollment. App slugs and repository owner/name use GitHub-style ASCII letters, digits, dots, underscores and hyphens, without path traversal segments.

Only `github.com` is supported by v1. `brokerOrigin` and `--broker-url` must identify the same HTTPS origin (scheme, DNS host and effective port); root slash is optional. Reject user-info, non-root paths, query and fragment. Neither an option override nor an HTTP redirect may move an existing secret to a different origin. The production HTTP client disables redirects and uses normal TLS certificate validation. There is no insecure development switch.

Broker mode requires numeric `--app-id`, matching the enrollment, and `--repo`, matching its canonical repository name case-insensitively. An optional `--installation-id` must also match. `--private-key` is mutually exclusive with both broker options. Broker mode never reads a PEM or falls back to direct or ambient user authentication.

The polling profile is exactly `discussions:write`; webhook mode is exactly `discussions:write` plus `repository_hooks:write`. GitHub's implicit `metadata:read` is permitted in either profile. No other keys or levels are accepted. Enrollment must match the requested runtime profile. Permission maps in responses must match that enrollment profile, allowing only the same optional implicit metadata grant.

## Token request and response

Send an empty-body `POST /v1/devices/{deviceId}/installation-token` to the enrolled origin, with `Authorization: Bearer <deviceSecret>` and `Accept: application/json`. There is no client-selected repository or permission map. Never log the authorization header, enrollment content or response body. Non-success responses are status-only diagnostics and invalidate local credentials; redirects are failures.

The successful response is a JSON object with exactly these fields:

```json
{
  "protocolVersion": 1,
  "appId": 5387625,
  "appSlug": "repo-agent-connector",
  "installationId": 123,
  "repositoryId": 456,
  "repository": "owner/repository",
  "permissions": { "discussions": "write" },
  "token": "opaque-installation-token",
  "expiresAt": "2026-10-20T11:00:00Z"
}
```

All fields are required with the shown types. Bind App ID/slug, installation ID, repository ID/name and permission profile to the enrollment on **every** response, including renewal. Reject unknown or duplicate fields, unsupported versions, malformed JSON and empty tokens. Treat the token as opaque rather than requiring a GitHub prefix or fixed length; reject whitespace/control characters unsuitable for bearer authentication. `expiresAt` is an RFC 3339 timestamp strictly more than five minutes and at most 65 minutes after the consumer clock. The five-minute upper tolerance accommodates clock skew around GitHub's one-hour lifetime; it does not authorize the broker to extend that lifetime. Renew within the existing five-minute safety window.

## Cache and identity lifecycle

One credential manager owns cache publication, renewal serialization and scheduled refresh for both direct-App and broker sources. After acquiring the controller state lock, invalidate the prior process's cache **before** authenticating a new configuration. A failure to acquire the state lock must leave the active owner's cache alone.

Only validated leases may be atomically published into the owner-only token cache. The real GitHub viewer must also equal `appSlug + "[bot]"` before any native agent work. If a temporary cache is required for that viewer check, every failed check or startup authentication failure removes it and any temporary cache. A previously installed native wrapper must then fail without invoking `gh`, even if human GitHub credentials are present. Failed renewal likewise clears the cache; subsequent successful authorization may recover. Cache deletion prevents new compliant launches, but cannot revoke tokens already copied into a running child.

## Provider and hosted release gates

The broker source validates actual GitHub installation grants, repository-held approval and returned token scope; repeating client metadata is not authorization. Shared App signing authority and durable grant/attempt accounting remain in the trusted service. Human GitHub setup credentials stay with the installer host; the broker has no human login or admin UI. The [approved provider policy](broker-policy.md) retains HTTP scale-to-zero and configurable expiry/retention defaults; source implementation does not establish hosted availability.

Approval is a nonsecret, enrollment-specific marker in the selected direct repository Agent variable, independently read by the broker. Bind the fresh pending/device challenge, immutable repository/App/installation/profile and marker location; require separate controller-credential possession. Setup authority can create/remove the marker, but controller tokens receive no approval-variable read or write access. The installer poll secret or a copied public marker alone cannot approve enrollment. Unacknowledged completion expiry revokes the unclaimed device. A particular person's continuing administrator role or OAuth consent is no longer a grant condition. Marker removal/change closes the observed enrollment permanently; restoration cannot reactivate it, and fresh enrollment is required. Failed GitHub reads deny issuance. The [provider source contract](../service/auth/README.md#repository-agent-variable-enrollment-v2) defines oldest-read freshness and bounded pagination: complete single-page absence may close, while multi-page nonappearance remains unverifiable. This is not instantaneous token revocation.

Approval authority is a principal GitHub permits to write the selected variable, not a named human attestation. Under [ADR 0006](adr/0006-use-repository-agent-variables-for-approval.md), the provider uses explicit v2 setup/markers and durable kind/version/policy binding; legacy Environment markers cannot become Agent-variable grants implicitly. These provider versions do not change this frozen v1 consumer contract. Internal approval-verification tokens are separate, exact-repository Agent variables read-only capabilities that never reach controllers; preapproval and polling must have finite durable admission, mint, signing, read and outstanding-attempt budgets under the [provider policy](broker-policy.md).

The existing [legacy Environment host revocation tool](repository-approval.md) checks the exact decoded value and makes one DELETE, accepting an independent-writer availability race and manual recovery. GitHub removal acknowledgment, durable broker closure and outstanding-token settlement are separate results. No continuous-history guarantee is made for identical delete/recreate between observations, and terminal enrollments never reopen.

A lease alone does not fence outstanding GitHub bearer tokens. Before hosted ownership transfer, account for every issued token, including mint/response-loss races: confirm revocation or wait for expiry plus documented uncertainty before admitting another owner. Compliant owners must stop work, disable local launches and drain/terminate native children on ownership loss. Partition, suspend/resume and retained-token takeover evidence is a release gate. These guarantees are outside the first consumer slice.

## Explicit clone-capable consumer v2

Clone enrollment is a separate, explicit authority family. Setup and marker version 2 continue to describe Agent-variable approval; they alone do not grant Contents. Select `polling-clone` or `webhook-clone` in host enrollment to request the immutable `repository-agent-clone-approval-v2` policy. Polling clone has exactly Discussions write and Contents read; webhook clone also has repository hooks write. Only implicit Metadata read may additionally appear. Fresh repository-held approval and re-enrollment are required; existing owners, markers and enrollment files are never automatically upgraded.

The v2 private file and lease contain precisely the respective v1 fields above plus required string fields `profile` and `policyVersion`. `protocolVersion` is 2. The profile must be the selected clone profile and the policy must be `repository-agent-clone-approval-v2`. All original size, strict-field, identity, expiry, origin and private-file rules apply. The consumer selects the explicit file version and rejects mismatched runtime selection; no fallback is attempted. Start clone mode with `--clone`, additionally selecting webhook mode when appropriate. Container configuration accepts explicit `--clone` on a fresh instance. Direct-App mode uses the same explicit option and verifies exact returned repository/permissions before publishing each lease.

Renewal uses empty-body `POST /v2/devices/{id}/installation-token`; self-revocation uses `DELETE /v2/devices/{id}`. The broker checks the route against durable authority before reservation or mint. Agent setup completion and acknowledgment still use v2 routes for both old and clone profiles. Legacy Environment approval and existing Agent polling/webhook approval continue issuing only frozen v1 files/leases; their parsers reject Contents.

### Managed Git operations

The authenticated `gh` wrapper supports `gh repo clone OWNER/REPO [DESTINATION]` for the approved repository, normalized to its HTTPS URL with `--no-upstream`. No arbitrary clone flags are accepted. A private Git shim exists only inside that clone child to replace gh's injected host-wide helper. It recognizes the supported clone shape and the fixed local fork bookkeeping command, and rejects other shapes. Ordinary native-session Git is unchanged.

Use the sibling `repoagent-git fetch --directory CHECKOUT` wrapper for later fetches. The approved URL is implicit; caller URLs, remotes and Git flags are not accepted. Fetch refuses repository authentication/header/rewrite/include configuration and does not recurse into submodules. Checkout selection, trust and update scheduling remain separate work.

The helper validates HTTPS, github.com and the exact repository path, reads the current validated private lease on every request, never stores Git credentials, and coordinates rejected-lease deletion with atomic publication so old A cannot erase new B. No token is embedded in a wrapper, remote URL or command argument. Child processes isolate ambient helpers, gh/human tokens, askpass, netrc and Git configuration without changing global settings. Normal TLS verification remains required. Background renewal remains owned by the existing credential manager; an unavailable lease denies new launches. Already copied bearer tokens retain the existing revocation/expiry limits.

Supported native wrappers remain Linux/macOS, including the Linux container/AOT runtime; Windows is the PowerShell installation host. Source tests and local transport fixtures do not prove actual App permission approval, private GitHub clone/renewed fetch, deployed Azure signing or pilot acceptance.
