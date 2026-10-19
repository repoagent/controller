# Prototype manual acceptance

Use real GitHub and Codex with an authorized dedicated agent account and disposable Discussions. Automated test projects, fake providers and CI harnesses are intentionally outside issue #2. Record actual results below or in the issue; an unchecked row remains outstanding.

Build with `dotnet build src/DiscussionBridge`, inspect `--help`, and start the long-running command from the README with stdout captured to an operator-chosen file. Record the SDK/Codex versions, repository, authenticated agent login, state path and workspace. Keep participant credentials separate from the agent's environment.

| Check | Evidence to record |
| --- | --- |
| Mention the agent in a human post/comment | Discussion URL, source URL, native session ID, `native-accepted` then `receipt` log, agent reply URL |
| Explicitly enroll a second Discussion | Second URL and distinct native session ID |
| Reply in two threads of one Discussion | Both source/reply URLs and same session ID; replies target their own thread roots |
| Edit a message; add and remove a reaction | Source URL, attributed deltas and agent responses when useful, no replay of the reaction subject as a request |
| React/reply to agent-authored content | Human activity reaches the same native session |
| Send while the native agent is working | Native acceptance of the new input while the first turn is active, subsequent response reflects it |
| Exercise an installed skill | Requested skill and visible result in GitHub |
| Source-specific EYES | Discussion body, comment and reply each get the agent's EYES on the actual source, including reaction subjects |
| Existing EYES reuse | Edit an acknowledged source; `receipt-reused`; no duplicate work from the receipt itself |
| Quiet polls and own output | At least two unchanged polls; own comment/reaction causes no forwarding/response loop |
| Normal restart | Same state and session ID; a new participant message resumes native history and replies in GitHub |
| Failed input | With disposable state, make a recorded session ID invalid, post a new source without EYES, confirm native rejection leaves `pending` and no new EYES; restore/reconcile manually |
| Receipt failure | With accepted native input, provoke a real receipt permission failure; log error and verify next unchanged poll does not resend input |

For every failure record whether GitHub observation, native acceptance, checkpoint persistence, receipt, or agent publication failed. Avoid declaring full acceptance from a build or transport-only probe. Input acceptance and successful GitHub publication are separate observations.

Protocol reference: [Codex app-server](https://learn.chatgpt.com/docs/app-server). The native `turn/start` protocol accepts input on an active turn; history, execution and compaction remain owned by Codex. GitHub [Discussion mutations](https://docs.github.com/en/graphql/reference/mutations#adddiscussioncomment) do not promise idempotent publication through `clientMutationId`.
