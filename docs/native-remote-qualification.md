# Native remote-access qualification

Updated 2026-10-31 for [#332](https://github.com/repoagent/controller/issues/332) and [#338](https://github.com/repoagent/controller/issues/338), under [ADR 0016](adr/0016-trust-container-owner-for-native-remote-access.md). Container/native-host access authorizes the trusted account owner. The earlier ADR 0015 isolation/admission restrictions are superseded; the implementation enables full native interaction on the owned runtime.

## Pinned provider evidence

The image pins Codex 0.166.0. Its [remote schemas](https://github.com/openai/codex/blob/rust-v0.166.0/codex-rs/app-server-protocol/src/protocol/v2/remote_control.rs) and [method definitions](https://github.com/openai/codex/blob/rust-v0.166.0/codex-rs/app-server-protocol/src/protocol/common.rs) define experimental `remoteControl/enable`, `disable`, `status/read`, `pairing/start`, `client/list` and `client/revoke`, plus `remoteControl/status/changed`. Initialization opts into the experimental API. Intent belongs to RepoAgent; enable/disable use `ephemeral:true`. Pairing requests `manualCode:true`; expiry and client last-seen timestamps are Unix seconds. Client pagination uses `nextCursor`, up to 100 clients per provider page.

A credential-free Linux probe ran the checksum-verified 0.166.0 binary in a disposable Docker container with networking disabled, a fresh whitelisted environment, an empty private home and no credential mounts or account sign-in. It verified:

- Initialization with `experimentalApi:true` is accepted.
- `CODEX_INTERNAL_APP_SERVER_REMOTE_CONTROL_DISABLED=1` starts the runtime disabled. An explicit ephemeral enable then returns connecting; the marker does not prohibit enablement.
- Without credentials, the next status is errored. Pairing requires completed enrollment, and client list/revoke require ChatGPT authentication; all methods are recognized.
- Explicit ephemeral disable returns disabled, followed by a disabled status and orderly runtime exit. No authentication file was created, and the scratch home was removed.

The [startup marker](https://github.com/openai/codex/blob/rust-v0.166.0/codex-rs/app-server-transport/src/transport/remote_control/mod.rs) controls initial disabled state. The controller reapplies its own retained intent after initialization on each owned generation. Remote failures do not replace or terminate the runtime merely to change native access.

The earlier 0.165.0 scratch-session experiment recorded successful enrollment, pairing, desktop session opening, client revocation and disable. It establishes feasibility for that version, separately from the current 0.166.0 no-account evidence. It is not a current-version live acceptance pass.

## Project verification

`RemoteAccessTests` exercises the approved core/application command seam with the existing state fake, a domain runtime fake and controlled time: default intent, connecting versus connected, pairing prerequisites/expiry and privacy, pagination and targeted revocation, persistent disable before uncertain effects, restart restoration, authorization and runtime generation changes. `ControllerStateFilesTests` exercises durable intent through the actual existing state adapter. `ActivitySocketTests` exercises the actual Unix HTTP and CLI contract, including same-UID administration, stale-lifetime refusal before enablement and independent activity reads. The existing owned-process fixture retains its default-disabled launch check. Build, format, source-inventory and Native AOT gates apply to the assembled change.

A focused disposable blocked-stdin regression harness at the existing runtime adapter seam fills the pipe with one remote frame while the peer withholds reads. Caller cancellation must return before draining; a queued canceled command must not be written. After draining, the peer parses the complete retained JSON frame and a subsequent status request succeeds. This checks bounded cancellation without introducing a permanent fake-provider suite.

## Remaining live acceptance

Successful 0.166.0 account enrollment, code issuance/expiry, desktop session rendering and optional direct interaction, live client pagination/revocation, and connected disable/reconnect have not been rerun against an authenticated account for this change. They require applicable explicit owner authorization and disposable pairing/client cleanup. They are unexecuted acceptance checks, not a feature blocker or a passing test. No new account sign-in, production deployment, release or main-branch merge is part of this work.
