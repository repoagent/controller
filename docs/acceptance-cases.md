# Packaged bridge acceptance catalog

Use a disposable repository or explicitly authorized disposable Discussions. App provisioning, installation approval, Codex browser/device approval and a distinct human participant's GitHub approval are setup prerequisites. Provider UI login is not automated with Playwright.

## Run

```sh
export REPOAGENT_INSTANCE=acceptance
./repoagent build
./repoagent configure --repository OWNER/REPO --app-id APP_ID \
  --key-path /absolute/path/key.pem --mention-user '' --tool-updates pinned
./repoagent login
./repoagent participant-login
./repoagent skills install /absolute/path/acceptance-skills.json /optional/source-token
REPOAGENT_LIVE=1 ./repoagent live-test
```

For C06/C07 install the skill at `tests/fixtures/acceptance-marker` from this repository, pinned to a full commit SHA containing that fixture. Public source credentials are unnecessary; a private source requires the explicitly authorized source-read token. The fixture computes a correlated result using its own script and supports a bounded gate for observing input while agent work is active. C07 checks its unexpired active lease while the additional input waits unaccepted; the skill removes the lease on completion or timeout.

The main journey uses explicit `--tool-updates pinned` for reproducibility. Normal agent setup also defaults to the image's pinned tools. Record separate opt-in startup-update evidence using `--tool-updates latest`, including actual before/after versions and a container restart.

The xUnit suite runs in its own container, with the human `gh` sign-in mounted only there. It operates through real GraphQL mutations and observations. Its mailbox asks the host wrapper for replacement, logs, the documented rejected-session fixture or the credential-revocation fixture; neither the runner nor agent receives a Docker socket. The agent uses its App installation and container-owned Codex account. No provider mocks are used.

An already authorized test participant token can be imported explicitly with `./repoagent import-participant-token /absolute/path/token`. Only that file is mounted for the import; the resulting participant configuration is isolated from the agent and other instances.

For focused skill validation, use `REPOAGENT_LIVE=1 REPOAGENT_LIVE_CASE=skill ./repoagent live-test`. It exercises the same packaged path with one disposable skill request. Without the configured skill, an actual `SKILL_UNAVAILABLE` reply is a behavioral failure; install the pinned selection and rerun for the green result.

`REPOAGENT_LIVE_CASE=explicit-journey` explicitly enrolls the first fixture too. This permits isolated diagnostic work while another bridge is running a nonoverlapping explicitly enrolled test in the same repository. It reports `SETUP_EXPLICIT` rather than claiming C01; the default journey is still required to verify mention enrollment.

`REPOAGENT_LIVE_CASE=recovery` creates one fresh explicitly enrolled fixture and runs C10–C12. Use it after diagnosing and fixing a recovery observer failure to preserve already passed cases without retrying the whole journey. Record the original failure and both runs; clean diagnosed fixtures before a new run. The default journey still covers every automated case. The multiline recovery-log parser also has a deterministic regression check in the same test project.

Canonical App mentions are used in this suite. Empty `--mention-user ''` avoids triggering unrelated historical `@repoagent` conversations. Existing canonical App mentions can still enroll: choose the target repository deliberately. Use the same instance across a diagnostic rerun so previously accepted fixture inputs stay checkpointed.

## Cases

Automated cases live in [DiscussionJourney.cs](../tests/RepoAgent.LiveTests/DiscussionJourney.cs), method `Packaged_bridge_preserves_conversations_skills_and_receipts_across_replacement`. Each records a stable ID, correlated sources/responses and its observed outcome.

| ID | Mode | Action and observable expected outcome |
| --- | --- | --- |
| C01 | Automated | Create a uniquely named canonical-App-mention Discussion; observe App reply, source EYES and agent session ID. |
| C02 | Automated | Create a second unmentioned Discussion, explicitly enroll its URL through the operator CLI and replace the container; observe a distinct agent session and App reply/receipt. |
| C03 | Automated | Post to two comment threads and a nested reply; responses remain in the corresponding roots and EYES acknowledges the nested source. |
| C04 | Automated | Edit a previously acknowledged human comment; observe a response to the edit with one existing App EYES, preserving source-specific receipt semantics. |
| C05 | Automated | Add/remove a human reaction on App-authored content; each change is accepted in native operational events, with EYES on its actual subject. |
| C06 | Automated | Invoke the optionally installed `acceptance-marker` skill; observe the script's computed result, correlated to the run. |
| C07 | Automated | Hold a native skill action on a bounded release gate, post more input, observe it waiting without native acceptance or EYES before release, then its acceptance as a new turn and responses in both source threads. |
| C08 | Automated | After turns complete, observe at least two fully completed polling cycles with no forwarded inputs from unchanged content or App output/receipts. |
| C09 | Automated | Replace the agent container using the same volumes; subsequent real reply retains the original session and remembered context. |
| C10 | Automated | Stop, back up and deliberately invalidate one agent session ID; a new source produces native rejection, `delivery-retry` with backoff and no EYES. Observe no accepted input, then explicitly restore the session and observe the retried source answered without editing delivery state. |
| C11 | Automated | Mark an App comment as the accepted answer, then continue; observe a subsequent App response in the same session. |
| C12 | Automated | Revoke the controller's live GitHub token at GitHub with the `revoke-credential` fixture, then post input. GitHub rejects the controller's polls (`poll-error` with HTTP 401); observe no accepted input and no receipt. Replace the container; the new lifetime acquires a fresh token. Observe one App reply and one EYES receipt for the input, and two fully completed unchanged polling cycles that do not resend it. |
| C13 | Manual | Fresh interactive setup in a terminal/tmux: browser approvals work, repeated setup reuses valid configuration and sign-in, and invalid App/key/permissions stop before agent work. Record exact tool/configuration versions. |
| C14 | Automated smoke | `./repoagent versions` and `check` show the pinned tools, UID 1000, real App viewer/installation and valid Codex sign-in in the built image. Record volume persistence and configured skill revisions. |

C12 runs `control.py revoke-credential` as the agent user in the running container. It calls the controller's own App `gh` wrapper (`gh api --method DELETE installation/token`), which reaches the live token through the credential handoff, as the agent's `gh` does. The token never reaches the fixture, a file, a log or an argument. The controller keeps the rejected token until a new lifetime starts; only the managed Git credential helper reports a rejected token and triggers an earlier renewal, so this case uses a non-clone instance.

Before tokens became memory-only (#183), C12 put a genuine read-only App token in the persisted credential cache, so GitHub rejected only the EYES receipt after native acceptance. Tokens are no longer persisted, and by design the controller accepts no injected token, so that fault cannot be produced against a live controller. A permanent receipt rejection after acceptance (GitHub's `FORBIDDEN` "Resource not accessible by integration"), with no replay of the accepted input, is covered deterministically by `GitHubOutboxTests.Permanent_receipt_failure_leaves_active_delivery_with_an_actionable_nonsecret_log`. The fixture's command path is tested against the real credential manager in `tests/installers/test_live_fault_fixture.py`.

C10 uses stopped-process state edits solely to invalidate and restore the agent session ID. Normal cases observe GitHub and native operational output. C08, C10 and C12 observe `poll-completed` operational events, allowing for one in-flight cycle before requiring two full unchanged cycles. These checks have a twelve-minute bound; other waits stop on correlated conditions rather than sleeping for a guessed model duration. No whole-test retries are performed.

## Evidence and cleanup

The wrapper stops the test agent when the runner finishes. Successful fixture Discussions are deleted only after all assertions pass. Failed fixtures remain for inspection; their IDs/URLs appear in `fixtures.json`. A failure is not automatically rerun or marked passed. Missing sign-in, skill configuration or provider availability is an unmet prerequisite or failed run.

Evidence stays in `repoagent-INSTANCE-live-evidence` (case outcomes and API snapshots) and `repoagent-INSTANCE-live-control` (lifecycle mailbox, archived/current bridge logs and the rejected-session backup). The stopped test runner remains available for copying:

```sh
docker cp repoagent-acceptance-live-tests:/evidence ./live-evidence
```

Retain failed Discussions and evidence until diagnosis is complete. Then delete only the fixture IDs listed for that run with GitHub's `deleteDiscussion` mutation using the participant account. Stop the agent before any manual checkpoint reconciliation. Explicitly remove the two live evidence/control volumes when no longer needed; `reset --delete-stored-data` removes only the normal instance volumes. Evidence contains conversation text, IDs and URLs but must not contain signing keys or tokens.

## Completion record

Record the tested image ID/platform, tool versions, configured source revisions, case outcomes, Discussion/source/reply URLs, original/resumed agent session IDs, relevant redacted logs and any unexercised manual cases in issue #5. A successful image build or xUnit discovery alone is not live acceptance evidence.
