# Native remote-access qualification

Evidence recorded 2026-10-30 for [#335](https://github.com/repoagent/controller/issues/335), under [ADR 0015](adr/0015-keep-native-remote-access-unavailable.md). This records a fail-closed implementation, not supported desktop access or established OS isolation. The image pin is Codex 0.166.0; the earlier 0.165.0 scratch-session experiment in #332 does not qualify the current controller.

## Source and core evidence

- The pinned provider's remote requests enter its own dispatcher, including mutations, without a RepoAgent preexecution round trip. Primary source links and the concrete same-identity process arrangement are in ADR 0015. No supported read-only/preexecution boundary was found in the inspected code.
- The pinned CLI recognizes `CODEX_INTERNAL_APP_SERVER_REMOTE_CONTROL_DISABLED=1` and chooses disabled ephemeral startup unless explicitly launched with `--remote-control`. The controller overwrites the inherited marker for every owned launch, before starting the supervisor, and never supplies that flag. This avoids restoring a reused home's enabled remote intent on owned startup.
- `RemoteAccessTests` exercises the approved controller-core seam: every named operation refuses inspection-only/unknown/invalid permissions; every named administrator operation reports unavailability and unconfirmed access/revocation; unknown operation values are forbidden. The core has no state/provider dependency, performs no I/O and returns no enabled/current-access/client/pairing data.
- Meaningful red evidence: the initial unavailable-only implementation failed all six permission cases; an all-forbidden implementation failed all six administrator cases; unrestricted administrator unavailability failed unknown values 0, -1 and 999. The final gate passes all 15 cases.
- Existing checkout-refresh behavior continues to block unmatched agent lifecycle events. The unavailable gate does not alter admission, session state, runtime lifecycle or authority-loss ownership/termination.

These in-process/source checks establish refusal logic and the owned launch configuration only. They do not establish real Unix peer identity, tool isolation, provider remote state, successful disable/revocation or live desktop safety. No account sign-in, pairing, provider call, model turn, deployment or release was performed for this slice.

Verification command after merging the command-rename integration tip `61bfa4d90fd9de1a37259ba3c3e129325294e668`:

```powershell
dotnet run --project tests/RepoAgentController.Tests -- -class '*RemoteAccessTests' -class '*CheckoutRefreshTests' -class '*AgentSessionsTests' -class '*ControllerShutdownTests' -class '*AgentCredentialReplacementTests' -noLogo
```

Result: 88 passed, 0 failed, 0 skipped (including 15 remote gate cases). `git diff --check` passed. The test host ran on Windows; this is not Linux/container process-boundary acceptance.

## Required before supporting native remote access

[#332](https://github.com/repoagent/controller/issues/332) remains open. Acceptance must establish OS protection of administration and native account/control authority from tools, provider preexecution admission of native commands, active-session/checkout/completion invariants and authority-loss teardown. Use the shared Aspire environment and explicitly authorized disposable desktop account for enable/pair/populated history/live progress/client enumeration/targeted revocation/disable/restart/expiry acceptance. Record versions and sanitized outcomes, clean up clients and credentials, and treat unavailable account/provider access as unexecuted acceptance.

Real local-transport acceptance must additionally exercise authenticated administrator, inspection-only, unrelated and agent-tool identities against the same Unix socket, direct HTTP bypass attempts and route exclusion from every TCP/tunnel listener. It cannot establish native support while the blockers above remain. The pinned-tool qualification process must recheck the owned startup marker and provider command-admission behavior on each proposed Codex update; source checks alone are insufficient to enable remote access.
