# Controller credential protocol v1

This is the consumer contract for [deployment plan #28](https://github.com/repoagent/controller/issues/28). It does not establish that an enrollment service or hosted broker exists. Direct App authentication remains available for an operator's own App.

## Enrollment file

The installer will eventually write this file atomically after administrator approval. Until that provider is implemented, it is an operator-supplied integration input. Treat it as a secret: owner-only access (0600 on Unix, a private user ACL on Windows), never an image build input or command-line argument value. The CLI receives its path through `--enrollment-file` and separately requires `--broker-url`.

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

The future broker validates actual GitHub grants and token scope; repeating client metadata is not authorization. The App key and durable authorization state remain in the separate trusted service. Personal GitHub OAuth credentials are used for enrollment and discarded afterward. The [approved provider policy](broker-policy.md) requires current authority of the same approving administrator on issuance, permits HTTP scale-to-zero, and defines configurable expiry/retention defaults; it does not claim the provider exists.

Device flow proves login, not approval of a particular enrollment. Final approval requires a trusted broker-origin browser surface authenticated as the repository administrator, protected against CSRF and bound to the exact pending device, repository ID, installation and permissions. Possession of the installer poll secret cannot approve enrollment. Unacknowledged completion expiry must revoke the unclaimed device. When human device tokens are used during enrollment, narrow them with GitHub's documented `repository_id` where applicable and verify actual scope; GitHub can ignore an inaccessible selector. Do not add durable human refresh-token storage for the approved independent-enrollment policy.

A lease alone does not fence outstanding GitHub bearer tokens. Before hosted ownership transfer, account for every issued token, including mint/response-loss races: confirm revocation or wait for expiry plus documented uncertainty before admitting another owner. Compliant owners must stop work, disable local launches and drain/terminate native children on ownership loss. Partition, suspend/resume and retained-token takeover evidence is a release gate. These guarantees are outside the first consumer slice.
