# RepoAgent

RepoAgent connects GitHub conversations to native coding-agent sessions. Its CLI, `RepoAgentController`, forwards observed activity; the agent replies through normal tools under a GitHub App installation identity. Discussions are the first supported interface; the name leaves room for issues and pull requests.

## Start here

- [Minimal PoC — issue #2](https://github.com/repoagent/controller/issues/2): the implementation starting point, with manual verification.
- [Full specification — issue #1](https://github.com/repoagent/controller/issues/1): broader design reference. The PoC's smaller scope takes precedence for the initial build.
- [Flows, payloads and session instructions](https://github.com/repoagent/controller/issues/2#issuecomment-6276915447).
- [Maintainer authentication-service deployment](docs/azure-auth-deployment.md): Bicep/OIDC setup and continuous deployment of the broker API and cleanup service from `main`.
- [Azure controller setup](infra/controller/README.md): public GHCR image, private persistent storage and a dedicated controller resource group, without ACR; includes cloud sign-in and the first-activation handover procedure.
- [Operate StoryBeam](docs/storybeam-operations.md): the selected lean app, private update/recovery directory, idle scaling and retained data.

The PoC uses one native Codex session per Discussion, a small JSON mapping/checkpoint, GraphQL observation, an ordinary skill plus `gh` for replies, and timestamped stdout logging. After the native harness accepts input, the bridge adds 👀 to the forwarded source Discussion, comment or reply.

Each bridge process monitors one configured repository. Specifications and tickets live in this repository's GitHub Issues.

## Run RepoAgent

For guided shared-App setup, use [PowerShell 7.4+ local installation](docs/local-installation.md): `.\install.ps1 -Repository owner/repo -BrokerOrigin https://broker.example.org -Instance example -Clone -NonInteractive` previews the plan; add `-Apply` to enroll and start. `-Clone` selects repository context with fresh Contents-read approval and exact-root native trust; omit it for the ordinary no-context path. It reuses host `gh` and existing Codex sign-in, persists choices per instance and supports explicit own-App mode. Windows uses Docker Desktop Linux containers directly; WSL is optional. Bash `./install.sh` retains its existing own-App setup. See [installation, skills and storage](docs/container-setup.md) and the [executable acceptance catalog](docs/acceptance-cases.md). For event-triggered operation behind your own HTTPS ingress, see [fixed-URL webhooks](docs/webhooks.md).

For an already-issued frozen broker enrollment, use the [PowerShell 7 local runtime](docs/local-runtime.md). It imports the private file into owned Linux storage and pins the selected image without requiring an App PEM.

PowerShell also supports [inspecting or withdrawing legacy Environment approval](docs/repository-approval.md) using host `gh`, without Docker or a controller credential. The [repository Agent-variable provider](service/auth/README.md#repository-agent-variable-enrollment-v2), owned by [#68](https://github.com/repoagent/controller/issues/68), implements v2 setup and markers while preserving the frozen v1 controller file and runtime. The [guided installer](docs/local-installation.md) composes the delivered host enrollment and private-runtime commands; legacy Environment revoke remains separate. Live GitHub, Azure and native Windows acceptance remain required before reporting the assembled service as available.
After installation, inspect or change nonsecret [repository settings](docs/repository-settings.md) in `REPOAGENT_*` Actions variables with `./install.ps1 -Action Settings`. The controller reads them only with `--repository-settings` and an App installation that grants Variables: read.

For local development without a public hostname, select [Quick Tunnel mode](docs/quick-tunnel.md).

Run the bridge in a Linux container containing `gh` and `codex`. The packaged bridge is Native AOT compiled and needs no installed .NET runtime. Native Codex uses its normal sign-in. GitHub access uses an installed GitHub App with **Discussions: read/write** and a read-only mounted PEM signing key. An agent GitHub-user login is unnecessary. Container bootstrap and live verification are tracked in [#5](https://github.com/repoagent/controller/issues/5).

Pass these arguments to the bridge **inside the configured container**:

```text
RepoAgentController --repo repoagent/controller --app-id APP_ID_OR_CLIENT_ID --mention-user repoagent \
--private-key /run/secrets/github-app.pem --workspace /workspace \
--state /data/bridge-state.json
```

The bridge discovers the App's installation for the configured repository; `--installation-id` optionally checks a specific expected installation. It verifies the token's actual `viewer.login` matches the App bot before starting Codex. Use `--check-auth` to check App credentials and prepare its `gh` wrapper without starting a native session. See [App setup, token renewal and verification](docs/github-app-auth.md).

Mention the bot's actual login, such as `@repo-agent-connector[bot]`, in a Discussion body/comment/reply, or add `--enroll https://github.com/OWNER/REPO/discussions/123` (repeatable). Enrollment uses literal polled text and does not require GitHub mention notifications or autocomplete. Explicit enrollment persists in the state file and need not be repeated. Each Discussion gets one native Codex session across all comment threads. The default polling interval is ten seconds after each completed poll; every connection is paginated, so large repositories may take longer. Only observed versions are forwarded; changes reverted between polls can be missed.

Add `--mention-user repoagent` to enroll through the dedicated `@repoagent` account. Startup verifies that the configured user exists; enrollment recognizes GitHub-rendered mentions, including emphasized mentions, while profile links, code examples and email addresses stay inactive. The App still authors replies and EYES; the mention account needs no runtime login or token. See [mention-account setup](docs/github-app-auth.md#dedicated-mention-account) for repository access and autocomplete eligibility. Native agent registration remains a separate integration.

The response skill is copied beside the executable and injected through Codex's native skill input. Additional skills and workspace guidance load normally. Native sessions receive the absolute path to an authenticated `gh` wrapper, which reads renewed credentials for every command. The native process inherits neither a static installation token nor the operator's GitHub-user token/configuration. Choose a Codex configuration whose normal tool permissions allow the agent's intended work and network access for `gh` publication. The bridge preserves those permissions and has no approval UI: interactive approval/input requests are logged and rejected for operator attention. Participant clarification questions should be published to GitHub by the response skill.

Logs go to timestamped stdout. Keep the process running for agent work and future activity. Ctrl+C stops the process. `--once` is an observation/acceptance check, **not a complete conversation run**: it exits after one poll and can interrupt unfinished native work. Native acceptance means the input reached Codex, not that the answer finished. On normal restart, reuse the same state file and Codex sign-in/storage; the next change resumes the recorded session and its native history.

## Checkpoints and manual recovery

One JSON file holds the repository/bot binding, session IDs and last accepted content/reaction versions. An adjacent `.lock` file prevents concurrent writers for that state file; keep one bridge per repository/App. A private adjacent `.github-app` directory holds the short-lived credential, isolated `gh` configuration and native command wrapper. Persist this directory with the state volume and keep it out of source control. Signing keys and tokens are never stored in the conversation checkpoint. Use a fresh state file when switching from a user identity to an App; the bridge rejects a mismatched identity instead of silently replaying or migrating old conversations. Codex retains conversation history itself.

Before creating or sending input the bridge persists `pending`. Only native acknowledgement clears that marker and advances the source checkpoint. Errors, timeout, or interruption leave that Discussion stopped for manual recovery; other Discussions continue. Failed or uncertain input receives no new EYES. After acceptance the bridge checkpoints **before** adding or reusing EYES on every source item. Receipt errors are logged without resending input, and receipts are excluded from wake triggers. There is no automatic receipt repair.

For a `manual-recovery` log, stop the bridge and inspect the JSON record plus the native session (`codex resume SESSION_ID`, using the same Codex profile). Inspect GitHub for any published response. Back up the state before editing it. If no input was accepted, clear `pending` to retry with the existing `sessionId`; if creation never succeeded, remove that Discussion record to enroll it again. If input was accepted, reconcile `sources` against the accepted observation before clearing `pending`, or leave it paused until you can establish that checkpoint. A lost creation acknowledgement may require recovering the session ID from Codex history first. Do not delete a record merely to clear an uncertain send. Crash-proof exactly-once input or publication is outside this prototype.

See [manual verification](docs/manual-verification.md) for the acceptance gate.

## Containerized authentication checks

```sh
docker build -f tests/Containerfile -t repoagent-controller-tests .
docker run --rm repoagent-controller-tests
```

These focused xUnit tests exercise real RSA signing, private credential files and child-command dispatch with controlled HTTP responses/time. They verify renewal, expiry and failure isolation; they do not prove real App permissions or native Codex compatibility. The separate live checks in the [App guide](docs/github-app-auth.md) provide that evidence.

### Shared-App host enrollment

PowerShell 7.4+ can create approval in one direct repository Agent variable and obtain a private shared-App enrollment through `install.ps1 -Action Enroll`, using separate authenticated host `gh`. See [host setup, recovery and revoke](docs/host-enrollment.md). Use the [guided local installer](docs/local-installation.md) to compose enrollment with Docker startup. Azure setup and live broker acceptance remain separate gates.
