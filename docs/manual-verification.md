# Prototype manual acceptance

Use real GitHub and Codex with an authorized GitHub App installation, a distinct human participant and disposable Discussions. Issue #2's original manual evidence used a GitHub user before the App conversion; preserve it as historical evidence. The focused App-authentication tests are documented in the [App guide](github-app-auth.md), and broader container/live automation is tracked separately in #5. Record actual results below or in the issue; an unchecked row remains outstanding.

Build/run inside a Linux container, inspect `--help`, and start the long-running command from the README with stdout captured to an operator-chosen file. Record the SDK/Codex versions, repository, authenticated App bot/installation, state path and workspace. Keep participant credentials separate from the agent's environment.

| Check | Evidence to record |
| --- | --- |
| Mention the agent in a human post/comment | Discussion URL, source URL, agent session ID, `agent-accepted` then `receipt` log, agent reply URL |
| Explicitly enroll a second Discussion | Second URL and distinct agent session ID |
| Reply in two threads of one Discussion | Both source/reply URLs and same session ID; replies target their own thread roots |
| Edit a message; add and remove a reaction | Source URL, attributed deltas and agent responses when useful, no replay of the reaction subject as a request |
| React/reply to agent-authored content | Human activity reaches the same agent session |
| Send while the agent is working | `input-waiting` and no EYES for the new input while the first turn is active; after that turn's idle report, `agent-accepted` for a new turn, then `receipt`; subsequent response reflects it |
| Model label and defaults | With `repoagent:<model>:<reasoning>` applied, the next human post logs `agent-accepted … model=… effort=… selection=label`. Changing or removing the label starts no turn. The following post uses the new label, or the defaults (`selection=default`). A second model label, or a model or effort the account does not offer, logs `selection-error` and its input waits. |
| Exercise an installed skill | Requested skill and visible result in GitHub |
| Source-specific EYES | Discussion body, comment and reply each get the agent's EYES on the actual source, including reaction subjects |
| Existing EYES reuse | Edit an acknowledged source; `receipt-reused`; no duplicate work from the receipt itself |
| Quiet polls and own output | At least two unchanged polls; own comment/reaction causes no forwarding/response loop |
| Normal restart | Same state and session ID; a new participant message resumes agent session history and replies in GitHub |
| Failed input | With disposable state, make a recorded session ID invalid, post a new source without EYES, confirm agent runtime rejection logs `delivery-retry` with growing backoff and no acceptance or new EYES; restore the session ID while stopped and confirm the retry delivers and answers it |
| Receipt failure | With accepted agent input, provoke a real receipt permission failure; log error and verify next unchanged poll does not resend input |

For every failure record whether GitHub observation, agent acceptance, checkpoint persistence, receipt, or agent publication failed. Avoid declaring full acceptance from a build or transport-only probe. Input acceptance and successful GitHub publication are separate observations.

Protocol reference: [Codex app-server](https://learn.chatgpt.com/docs/app-server). The Codex `turn/start` protocol adds input to an active turn, so the bridge sends a batch only after `thread/status/changed` reports the session idle; history, execution and compaction remain owned by Codex. GitHub [Discussion mutations](https://docs.github.com/en/graphql/reference/mutations#adddiscussioncomment) do not promise idempotent publication through `clientMutationId`.
