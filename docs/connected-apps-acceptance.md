# Connected-app live acceptance, 2026-11-11

Operator-authorized disposable Linux/rootless-Docker run of the `setup-repoagent` skill against `repoagent/ControllerTestRepo`, with GitHub CLI authenticated as `repoagent`. Codex used the operator's existing personal ChatGPT account with Gmail connected. Account credentials were imported into private instance storage; the host Codex configuration and account connections were not changed.

Source baseline: `6bba24f1b1337df32da0c00740ce8f3b8bf8a0bc`; pinned Codex 0.167.1. The initial image exercised default-disabled behavior. The revised image included the acceptance fixes described below and was adopted through supported immutable-image Update while retaining all seven volumes and the existing session.

The controlled Discussion requested only currently exposed tool metadata. Every turn prohibited Gmail/app data calls, sends, drafts, message reads, account connections and policy changes. App-authenticated GitHub replies were the only intended external writes beyond setup/teardown. Private status and activity were audited after each turn.

## Live results

| Check | Result |
| --- | --- |
| Fresh noninteractive install with policy omitted | Disabled, verified; Gmail tools absent in a controlled turn. |
| Selected Gmail plus an intentionally nonexistent ID | Gmail tools visible; nonexistent ID reported unavailable. |
| Selected Calendar, leaving connected Gmail unlisted | Gmail tools absent. |
| All apps | Gmail tools visible; explicit broader authority retained in preview/configuration. |
| All → disabled, restart and resume | Gmail tools absent from the existing session. |
| Same-account credential resupply and restart | Disabled retained and verified; Gmail tools absent. |
| Immutable image update | Selected policy, all seven volumes and session retained. |
| Private activity | Six completed turns in one session; no Gmail tool calls observed. Later bounded snapshots truncate only an old skill-read command already captured completely in the earlier audit. |
| Solution build and format gate | Passed, zero build warnings/errors. |
| Cleanup | Marked Discussion deleted; approval removed, broker closure confirmed, runtime removed and credentials cleared through supported uninstall. Only the seven owned disposable volumes removed. |

No email was sent, no Gmail data tool was invoked, and no account app connection was changed. Authenticated Discussion replies and temporary JSON-file creation also confirmed ordinary GitHub App and file/command work under disabled mode.

## Findings and fixes

- Guided setup rejected the current broker's opaque enrollment ID because display validation still required a legacy 32-character hex ID. Match the native consumer's bounded safe identifier grammar. Real saved-enrollment preview/apply and bounded valid/invalid display checks passed after the fix.
- Private activity failed because the PowerShell wrapper used an unexported module function. Export `Get-Resource`; the live audit then succeeded.
- Selected-mode availability cancelled after 30 seconds while the pinned Codex app-list implementation permits a 90-second refresh. Align request, initialization and private-discovery deadlines; refresh the catalogue only on the first page. The original failure blocked agent work; the corrected live startup verified Gmail and accurately reported the nonexistent test ID unavailable.
- The rootless Docker build helper could not write its private host context with its requested UID mapping. This run used the native image composer and ordinary Docker build instead. Rootless helper compatibility remains a separate build-path limitation.

## Coverage limits

This does not exercise a second Codex account, a newly connected app, or a dedicated-account sign-in. Credential replacement uses the same account. The live instance starts with current schema; missing legacy policy and hostile/stale configuration qualification remain covered by the separate pinned-runtime manual qualification and core checks, not by this fresh-instance run. Model-reported tool visibility plus private activity is acceptance evidence, not proof against an agent with arbitrary process access under the documented trusted-owner model.

## Runtime refresh qualification, 2026-11-16

Before adopting the fixes on current mainline, the credential-free manual qualification passed for disabled, selected and all modes with checksum-pinned Codex 0.169.0. Stale user and trusted-project app settings were overridden in each case. This follow-up used no credentials or model turns and does not extend the six live results above to the newer runtime.
