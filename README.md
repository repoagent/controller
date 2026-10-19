# GhDiscussionAgentBridge

Use GitHub Discussions as the conversation interface for a native coding-agent session. A small bridge forwards observed activity; the agent replies through normal tools under a dedicated GitHub identity.

## Start here

- [Minimal PoC — issue #2](https://github.com/egil/gh-discussion-agent-bridge/issues/2): the implementation starting point, with manual verification.
- [Full specification — issue #1](https://github.com/egil/gh-discussion-agent-bridge/issues/1): broader design reference. The PoC's smaller scope takes precedence for the initial build.
- [Flows, payloads and session instructions](https://github.com/egil/gh-discussion-agent-bridge/issues/2#issuecomment-6276915447).

The PoC uses one native Codex session per Discussion, a small JSON mapping/checkpoint, GraphQL polling, an ordinary skill plus `gh` for replies, and timestamped stdout logging. After the native harness accepts input, the bridge adds 👀 to the forwarded source Discussion, comment or reply.

Each bridge process monitors one configured repository. Specifications and tickets live in this repository's GitHub Issues.

## Run the prototype

Install the SDK pinned in `global.json` (.NET 11 RC2), `gh`, and `codex`. The only NuGet dependency is [System.CommandLine](https://www.nuget.org/packages/System.CommandLine/2.0.9). Sign into GitHub with the dedicated agent account and into Codex normally. Enable Discussions on the repository and grant that account permission to read Discussions and write comments/reactions. The bridge verifies `viewer.login` against `--agent-login` before starting Codex.

```sh
dotnet build src/DiscussionBridge
dotnet run --project src/DiscussionBridge --no-build -- \
  --repo OWNER/REPO --agent-login AGENT_LOGIN --workspace /path/to/workspace \
  --state /path/to/bridge-state.json
```

Mention `@AGENT_LOGIN` in a Discussion body/comment/reply, or add `--enroll https://github.com/OWNER/REPO/discussions/123` (repeatable). Explicit enrollment persists in the state file and need not be repeated. Each Discussion gets one native Codex session across all comment threads. The default polling interval is ten seconds after each completed poll; every connection is paginated, so large repositories may take longer. Only observed versions are forwarded; changes reverted between polls can be missed.

The response skill is copied beside the executable and injected through Codex's native skill input. Additional skills and workspace guidance load normally. Choose an existing Codex configuration whose normal tool permissions allow the agent's intended work, including `gh` publication. The bridge preserves those permissions and has no approval UI: interactive approval/input requests are logged and rejected for operator attention. Participant clarification questions should be published to GitHub by the response skill. Run this in a workspace and under an identity intended for remote participant requests.

Logs go to timestamped stdout. Keep the process running for agent work and future activity. Ctrl+C stops the process. `--once` is an observation/acceptance check, **not a complete conversation run**: it exits after one poll and can interrupt unfinished native work. Native acceptance means the input reached Codex, not that the answer finished. On normal restart, reuse the same state file and Codex sign-in/storage; the next change resumes the recorded session and its native history.

## Checkpoints and manual recovery

One JSON file holds the repository/account binding, session IDs and last accepted content/reaction versions. An adjacent `.lock` file prevents concurrent writers for that state file; keep one bridge per repository/account. The state stores participant content, so place it in an appropriate local directory. Codex retains conversation history itself.

Before creating or sending input the bridge persists `pending`. Only native acknowledgement clears that marker and advances the source checkpoint. Errors, timeout, or interruption leave that Discussion stopped for manual recovery; other Discussions continue. Failed or uncertain input receives no new EYES. After acceptance the bridge checkpoints **before** adding or reusing EYES on every source item. Receipt errors are logged without resending input, and receipts are excluded from wake triggers. There is no automatic receipt repair.

For a `manual-recovery` log, stop the bridge and inspect the JSON record plus the native session (`codex resume SESSION_ID`, using the same Codex profile). Inspect GitHub for any published response. Back up the state before editing it. If no input was accepted, clear `pending` to retry with the existing `sessionId`; if creation never succeeded, remove that Discussion record to enroll it again. If input was accepted, reconcile `sources` against the accepted observation before clearing `pending`, or leave it paused until you can establish that checkpoint. A lost creation acknowledgement may require recovering the session ID from Codex history first. Do not delete a record merely to clear an uncertain send. Crash-proof exactly-once input or publication is outside this prototype.

See [manual verification](docs/manual-verification.md) for the acceptance gate.
