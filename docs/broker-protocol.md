# Controller credential protocol v1

This is the consumer contract for [deployment plan #28](https://github.com/repoagent/controller/issues/28). It does not establish that an enrollment service or hosted broker exists. Direct App authentication remains available for an operator's own App.

The approved provider/installer design is now [repository-held approval with a UI-free auth service](auth-repository-approval.md). It supersedes the earlier browser/personal-administrator ceremony below without changing the frozen v1 consumer fields, runtime permission profiles or credential lifecycle.

## Enrollment file

The installer will eventually write this file atomically after the broker verifies repository-held approval. The approved installer will create or safely reuse a user-named direct repository Actions variable through the host's GitHub authority. [#184](https://github.com/repoagent/controller/issues/184) implements the separate [v3 setup/marker contract](../service/auth/README.md#repository-actions-variable-enrollment-v3); legacy Agent-variable setup stays v2 and Environment setup stays v1. Host #53 owns guided installation and private-file/Windows acceptance. Until that assembled flow is available, this file remains an operator-supplied integration input. Treat it as a secret: owner-only access (0600 on Unix, a private user ACL on Windows), never an image build input or command-line argument value. The CLI receives its path through `--enrollment-file`, or receives the same document as an injected secret through the environment variable named by `--enrollment-environment`, and separately requires `--broker-url`. Marker coordinates belong in broker state and separate nonsecret installer configuration, preserving this v1 schema.

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

## Credential and identity lifecycle

One credential manager owns the held credential, renewal serialization and scheduled refresh for both direct-App and broker sources. Tokens are held only in the controller's memory; no lease is persisted. After acquiring the controller state lock, remove any token cache left by an earlier version **before** authenticating a new configuration. A failure to acquire the state lock must leave the active owner's credential alone.

Only validated leases may be held and handed to the controller's own `gh` and Git children. The handoff is an owner-only Unix socket in a private local temporary directory that exists only for the running controller lifetime; the credential directory holds only a nonsecret pointer to it. The real GitHub viewer must also equal `appSlug + "[bot]"` before any native agent work. Every failed check, startup authentication failure, `--check-auth` exit or controller stop leaves no usable credential. A previously installed native wrapper must then fail without invoking `gh`, even if human GitHub credentials are present. Failed renewal likewise drops the held credential; subsequent successful authorization may recover. Dropping the credential prevents new compliant launches, but cannot revoke tokens already copied into a running child.

## Provider and hosted release gates

The broker source validates actual GitHub installation grants, repository-held approval and returned token scope; repeating client metadata is not authorization. Shared App signing authority and durable grant/attempt accounting remain in the trusted service. Human GitHub setup credentials stay with the installer host; the broker has no human login or admin UI. The [approved provider policy](broker-policy.md) retains HTTP scale-to-zero and configurable expiry/retention defaults; source implementation does not establish hosted availability.

Approval is a nonsecret, enrollment-specific marker in the selected direct repository Actions variable (the enrolled Agent variable for a legacy grant), independently read by the broker. Bind the fresh pending/device challenge, immutable repository/App/installation/profile and marker location; require separate controller-credential possession. Setup authority can create/remove the marker, but controller tokens receive no approval-variable read or write access. The installer poll secret or a copied public marker alone cannot approve enrollment. Unacknowledged completion expiry revokes the unclaimed device. A particular person's continuing administrator role or OAuth consent is no longer a grant condition. Marker removal/change closes the observed enrollment permanently; restoration cannot reactivate it, and fresh enrollment is required. Failed GitHub reads deny issuance. The [provider source contract](../service/auth/README.md#repository-actions-variable-enrollment-v3) defines oldest-read freshness and the named read: a 404 confirmed by a later access proof may close, while an isolated 404 remains unverifiable. This is not instantaneous token revocation.

Approval authority is a principal GitHub permits to write the selected variable, not a named human attestation. Under [ADR 0007](adr/0007-use-repository-actions-variables-for-approval.md), the provider uses explicit v3 setup/markers and durable kind/version/policy binding. Legacy Agent-variable (v2) and Environment (v1) markers never become another kind implicitly. These provider versions do not change this frozen v1 consumer contract. Internal approval-verification tokens are separate, exact-repository variables read-only capabilities (`actions_variables` for v3, `agent_variables` for legacy v2) that never reach controllers; preapproval and polling must have finite durable admission, mint, signing, read and outstanding-attempt budgets under the [provider policy](broker-policy.md).

The existing [legacy Environment host revocation tool](repository-approval.md) checks the exact decoded value and makes one DELETE, accepting an independent-writer availability race and manual recovery. GitHub removal acknowledgment, durable broker closure and outstanding-token settlement are separate results. No continuous-history guarantee is made for identical delete/recreate between observations, and terminal enrollments never reopen.

A lease alone does not fence outstanding GitHub bearer tokens. Before hosted ownership transfer, account for every issued token, including mint/response-loss races: confirm revocation or wait for expiry plus documented uncertainty before admitting another owner. Compliant owners must stop work, disable local launches and drain/terminate native children on ownership loss. Partition, suspend/resume and retained-token takeover evidence is a release gate. These guarantees are outside the first consumer slice.

## Explicit clone-capable consumer v2

Clone enrollment is a separate, explicit authority family. Setup and marker versions (3 for Actions variables, 2 for legacy Agent variables) describe approval location, not runtime scope; they alone do not grant Contents. Select `polling-clone` or `webhook-clone` in host enrollment to request the immutable clone policy for that location: `repository-actions-clone-approval-v3`, or `repository-agent-clone-approval-v2` for the legacy location. Polling clone has exactly Discussions write and Contents read; webhook clone also has repository hooks write. Only implicit Metadata read may additionally appear. Fresh repository-held approval and re-enrollment are required; existing owners, markers and enrollment files are never automatically upgraded.

The v2 private file and lease contain precisely the respective v1 fields above plus required string fields `profile` and `policyVersion`. `protocolVersion` is 2. The profile must be the selected clone profile and the policy must be `repository-actions-clone-approval-v3` or `repository-agent-clone-approval-v2`. Every lease must repeat the enrolled file's exact policy. All original size, strict-field, identity, expiry, origin and private-file rules apply. The consumer selects the explicit file version and rejects mismatched runtime selection; no fallback is attempted. Start clone mode with `--clone`, additionally selecting webhook mode when appropriate. Container configuration accepts explicit `--clone` on a fresh instance. Direct-App mode uses the same explicit option and verifies exact returned repository/permissions before publishing each lease.

For broker clone mode, native startup and container `configure` also accept optional `--repository-id ID`. The positive numeric ID must match the private enrollment before any broker request. Use it when a host retains an immutable repository binding: a repository recreated under the same name must not reuse that storage. Container configuration retains the expected ID when omitted on later configuration and refuses changing it within the instance. This option does not alter the enrollment schema or enable clone authority; callers without it keep existing behavior.

Renewal uses empty-body `POST /v2/devices/{id}/installation-token`; self-revocation uses `DELETE /v2/devices/{id}`. The broker checks the route against durable authority before reservation or mint. Agent setup completion and acknowledgment still use v2 routes for both old and clone profiles. Legacy Environment approval and existing Agent polling/webhook approval continue issuing only frozen v1 files/leases; their parsers reject Contents.

### Managed Git operations

The authenticated `gh` wrapper supports `gh repo clone OWNER/REPO [DESTINATION]` for the approved repository, normalized to its HTTPS URL with `--no-upstream`. No arbitrary clone flags are accepted. A private Git shim exists only inside that clone child to replace gh's injected host-wide helper. It recognizes the supported clone shape and the fixed local fork bookkeeping command, and rejects other shapes. Ordinary native-session Git is unchanged.

Use the sibling `repoagent-git fetch --directory CHECKOUT` wrapper for later fetches. The approved URL is implicit; caller URLs, remotes and Git flags are not accepted. Fetch refuses repository authentication/header/rewrite/include configuration and does not recurse into submodules. Checkout selection, trust and update scheduling remain separate work.

The helper validates HTTPS, github.com and the exact repository path, requests the running controller's current validated lease on every request, never stores Git credentials, and forwards a rejected lease to the controller, which drops it only if it is still the current one so old A cannot erase new B. No token is embedded in a wrapper, remote URL or command argument. Child processes isolate ambient helpers, gh/human tokens, askpass, netrc and Git configuration without changing global settings. Normal TLS verification remains required. Background renewal remains owned by the existing credential manager; an unavailable lease denies new launches. Already copied bearer tokens retain the existing revocation/expiry limits.

Supported native wrappers remain Linux/macOS, including the Linux container/AOT runtime; Windows is the PowerShell installation host. Source tests and local transport fixtures do not prove actual App permission approval, private GitHub clone/renewed fetch, deployed Azure signing or pilot acceptance.

## Explicit repository-settings consumer v3

Repository-settings enrollment is a third explicit authority family ([ADR 0008](adr/0008-add-an-explicit-repository-settings-runtime-profile.md)). It exists only under Actions-variable approval (setup and marker version 3); Agent-variable and Environment setup refuse its profiles with `400`. Select one of these profiles in host enrollment, or `-RepositorySettings` in the guided installer:

| Profile | Exact runtime grants |
| --- | --- |
| `polling-settings` | Discussions write, Actions variables read |
| `webhook-settings` | Discussions write, repository hooks write, Actions variables read |
| `polling-clone-settings` | Discussions write, Contents read, Actions variables read |
| `webhook-clone-settings` | Discussions write, Contents read, repository hooks write, Actions variables read |

Only implicit Metadata read may additionally appear. All four use the single immutable policy `repository-actions-settings-approval-v3`, and the approval marker names the profile. The frozen `polling`, `webhook`, `polling-clone` and `webhook-clone` profiles keep their exact grants: the broker never requests `actions_variables` for them and treats a GitHub response that adds it as an invalid scope. Existing owners, markers and enrollment files are never upgraded; moving to or from this family needs revocation, settlement and a fresh enrollment (see [repository settings](repository-settings.md#broker-enrollments)).

The v3 private file and lease contain precisely the v1 fields plus required string fields `profile` and `policyVersion`. `protocolVersion` is 3. The profile must be one of the four settings profiles and the policy must be `repository-actions-settings-approval-v3`. Permissions must be exactly the profile's grants with `actions_variables` equal to `read`. Every lease must repeat the enrolled file's profile family, policy and exact grants. All original size, strict-field, identity, expiry, origin and private-file rules apply.

Selection is explicit on both sides. Start the controller with `--repository-settings` (plus `--clone` for the clone variants) to consume a v3 file; a v3 file without the flag, or the flag with a v1 or v2 file, is refused before any broker request. Container configuration stores the choice for the instance: changing it later requires a fresh instance and enrollment. Externally supplied enrollments select it with `REPOAGENT_REPOSITORY_SETTINGS=true`.

Renewal uses empty-body `POST /v3/devices/{id}/installation-token`; self-revocation uses `DELETE /v3/devices/{id}`. The broker checks the route against the durable family before reservation or mint, so a settings device gets `401` on the v1 and v2 runtime routes and a frozen device gets `401` on the v3 routes. Setup completion and acknowledgment use the ordinary `/v3` setup routes. An older controller rejects a v3 file, and an older broker refuses the settings profiles, so neither side fails open.
