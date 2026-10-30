# GitHub App authentication

The bridge and its agent sessions publish as a GitHub App installation. GitHub user sign-in is needed only for a human/test participant, not for the agent. Codex authentication is separate and still uses its normal sign-in.

## Configure the App

1. Open the [App settings](https://github.com/settings/apps/repo-agent-connector). The **Repo Agent Connector** App has ID `5387625` and client ID `Iv23liKvyvzZ60qGguDm`; either identifier can be supplied to the bridge. Other Apps are supported through configuration.
2. Grant repository **Discussions: read/write** permission. Install the App on the target repository and accept updated permissions if the installation already exists. Polling does not require active webhooks or an inbound endpoint.
3. Generate/download a PEM private key from the App settings. Keep its contents private and mount that one file read-only into the bridge container. Do not put it in an image, source checkout, log, issue or conversation. Mount the state directory as a separate persistent volume; keep the key outside the agent workspace.
4. In a Linux container with the .NET 11 RC2 runtime and `gh`, invoke the bridge with `--repo OWNER/REPO --app-id APP_ID_OR_CLIENT_ID --private-key /run/secrets/github-app.pem --state /data/bridge-state.json`. The normal conversation runtime also needs `codex`, its sign-in, a workspace and the response skill.

The bridge authenticates the App, discovers the installation for the selected repository, checks Discussions permission and mints a token narrowed to that repository. `--installation-id ID` optionally verifies the expected installation. A GraphQL viewer check confirms the actual bot login matches the authenticated App before any agent session is started.

`--check-auth` performs this validation and creates the authenticated `gh` wrapper, then exits without launching Codex or enrolling/forwarding Discussions. It is useful before the first conversation run. Runtime sign-in, permission and networking failures must be fixed in the configured container; the bridge does not fall back to a personal GitHub login.

Webhook mode additionally requires owner-approved **Webhooks: read and write** and includes `repository_hooks: write` in renewed GitHub tokens. Polling-only tokens remain Discussions-only. See [repository webhook setup](webhooks.md).

## Broker credentials (consumer integration)

Obtain a private enrollment through the [controller enrollment client](controller-enrollment.md), using the broker operator's canonical HTTPS origin. The deployed maintainer broker is described in [Azure operations](azure-auth-deployment.md). The protected [v1 enrollment file](broker-protocol.md) lets the Linux/macOS controller authenticate without an App PEM; full hosted controller handover and lifecycle acceptance remain separate gates.

```sh
repoagent --repo OWNER/REPO --app-id NUMERIC_APP_ID \
  --broker-url https://credentials.example.org \
  --enrollment-file /run/secrets/controller-enrollment.json \
  --state /data/bridge-state.json --check-auth
```

Windows hosts use the Linux container through Docker Desktop; native Windows broker execution is unsupported. The example origin is a placeholder, not a deployed service. Keep the enrollment outside the workspace and image, readable/writable only by its owner (`chmod 600`). The origin must match enrollment exactly by HTTPS scheme, DNS host and effective port. The numeric App ID, repository and optional `--installation-id` must match enrollment. `--broker-url` requires exactly one enrollment input and cannot be combined with `--private-key`. Instead of `--enrollment-file`, `--enrollment-environment NAME` reads the same document from an injected environment variable. The controller removes that variable before it starts any child process and validates the document exactly like a file; the container uses this for [external controller credentials](container-setup.md#external-controller-credentials). Direct mode continues accepting an App ID or client ID and an operator-owned PEM.

Polling enrollment permits only Discussions write, with optional implicit metadata read. Webhook enrollment additionally requires repository hooks write; select it with `--webhook-url`. Every initial and renewed lease is validated against enrollment before the controller holds it. The real GitHub viewer must match the enrolled App bot before startup completes. Failed authentication or renewal drops the held credential so no later launch can use it; another process failing to acquire the state lock leaves the active owner's credential handoff intact. Both credential sources share the same in-memory credential, refresh scheduler and native wrapper.

These checks trust the enrolled broker's claims. They do not prove the token's actual GitHub repository scope, implement enrollment approval, or fence tokens already copied into a child. Hosted broker acceptance, administrator approval and outstanding-token fencing remain separate release gates.

### Run or debug the authentication check on Linux / WSL

The C# controller can consume an existing enrollment directly on Linux/WSL,
without starting a container. Use the SDK selected by `global.json` and put
`gh` on `PATH`. The standalone `scripts/enroll-controller.ps1` client obtains
enrollment without a container runtime; the guided Docker installer is a
separate entry point. Keep the enrollment outside the repository workspace in
owned private storage, with directory mode 0700 and file mode 0600.

For a `polling-clone` enrollment, substitute its exact repository, App,
installation, repository ID and trusted broker origin:

```sh
enrollment="$HOME/.repoagent/instances/YOUR_INSTANCE/enrollment.json"
check_state="$HOME/.repoagent/agent-auth-check"
install -d -m 700 "$check_state"

dotnet run --project src/repoagent -- \
  --repo OWNER/REPO --app-id APP_ID \
  --broker-url https://credentials.example.org \
  --enrollment-file "$enrollment" \
  --installation-id INSTALLATION_ID --repository-id REPOSITORY_ID --clone \
  --state "$check_state/state.json" --check-auth
```

Use the same arguments in a C# debugger. For a non-clone polling or webhook
enrollment, omit `--clone` and `--repository-id`. For either webhook profile,
also pass `--webhook-url` with the controller's public HTTPS origin. Match the
saved profile and immutable binding.
The state path above isolates this check from the running controller's files.

This path requests a bounded broker lease and verifies the real GitHub App
viewer, then exits before checkout, Codex launch or webhook registration. It
needs no Codex sign-in and leaves no refresh daemon or stored token. The
lease it held in memory still participates in normal broker settlement until
expiry/revocation. Full agent conversations need the usual Codex
setup and a single active controller. Stop the existing runtime through its
supported lifecycle before starting another conversation runtime; keep its
enrollment, checkpoint and agent session history intact.

On 2026-10-23, this Debug path authenticated StoryBeam/StoryBeam through the
deployed broker and confirmed `repo-agent-connector[bot]` while the existing
Docker controller stayed running. This proves native broker consumption,
not full agent conversation or hosted Azure controller acceptance.

## App ownership

**Repo Agent Connector** is owned by the `repoagent` account. The controller repository is `repoagent/controller`; App registration ownership and repository ownership are managed separately.

On 20 October 2026, GitHub blocked this App's transfer from `egil` while the App was private. The operator made the App public and completed the transfer to `repoagent`. The public API confirmed the same App ID `5387625` and slug `repo-agent-connector` afterward. This is the observed fix for this transfer; GitHub's [ownership-transfer documentation](https://docs.github.com/en/apps/maintaining-github-apps/transferring-ownership-of-a-github-app) permits personal-account destinations but does not explain this specific rejection. See the [transfer investigation](research/2026-10-20-github-app-transfer.md) for the evidence and limits.

## Renewal and native commands

The direct PEM-based setup is for an operator who owns the App registration and trusts the controller machine. Do not distribute the shared Repo Agent Connector App's PEM to other users: a private key grants authentication across the App's installations, even though this controller normally narrows its issued tokens to one repository. A read-only Docker mount prevents modification of the host file; processes that can read the mount can still use its contents. GitHub explicitly advises against shipping an App private key to user devices. See [GitHub's credential guidance](https://docs.github.com/en/apps/creating-github-apps/about-creating-github-apps/best-practices-for-creating-a-github-app#private-keys).

[GitHub tokens expire after one hour](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation). The bridge checks the credential every minute independently of polling, as well as before every GitHub operation, and renews it when five minutes or less remain. This continues during long polls and while agent work is active. The returned expiry is authoritative. A signed short-lived JWT authenticates App and token-issuance requests; the signing key is not passed as a agent runtime process environment variable.

The current token and expiry exist only in the running controller's memory; no token is written to the state, logs, wrappers, command arguments or Git configuration. The controller hands its current credential to its own `gh` and Git child commands through a credential handoff: an owner-only Unix socket in a private (`0700`) local temporary directory that exists only while that controller lifetime runs. The owner-only `<state-file>.github-app` directory holds isolated `gh` configuration, the small native command wrappers and a nonsecret pointer to the current lifetime's socket; it must remain readable at the same paths while agent sessions run. Startup removes the `installation-token.json` cache that earlier versions persisted there. A stopped, failed or `--check-auth` lifetime leaves nothing a wrapper can use, so every restart acquires a fresh lease. The handoff needs a local filesystem for its socket (the state directory may be SMB-backed) and a Codex permission profile that can reach Unix sockets and the network; the shipped image's `danger-full-access` profile does.

Codex receives the wrapper's absolute path in its standing instructions and the wrapper directory at the front of `PATH`. The response skill uses the absolute path so a login shell cannot reset command lookup. The wrapper starts the bridge's `github-command` entry point, requests the current credential from the running controller on every invocation, passes it to `gh` only through its process environment, replaces inherited GitHub tokens, selects `github.com` and uses isolated `gh` configuration before executing the real CLI. The agent runtime itself does not inherit a static token that will later expire. Other normal `gh` arguments, stdin, stdout and exit status pass through.

If token renewal fails, the bridge drops the held credential and logs a redacted authentication error. Native commands reject an unavailable controller, a missing token or a token with less than 30 seconds left before starting `gh`. There is no fallback to ambient `GH_TOKEN`, `GITHUB_TOKEN` or host `gh` sign-ins. The bridge must keep running for automatic renewal; `--check-auth` does not leave a credential-refresh daemon behind. Stop/start the bridge to reconfigure an App or key.

In broker mode, a renewal denial, rejected response, transport failure or acquisition timeout permanently stops that running credential lifetime. The controller cancels polling, webhook and checkout admission and exits with failure. Every broker-owned agent runtime teardown terminates the process tree before stdin EOF, including normal exit, trust bootstrap and checkout reload, so a concurrent renewal failure cannot lose an orphaned token holder. Each agent runtime process-exit or output-drain wait has a three-second limit. State and workspace leases remain held through agent runtime disposal. Agent session history, enrollment and pending publication/checkpoint journals are retained: inspect uncertain work before explicitly restarting through normal authorization. A recovered broker cannot automatically revive the failed lifetime. Direct PEM mode retains scheduled retry and recovery and its three-second graceful shutdown before forced termination; ordinary user cancellation retains a successful exit.

The failure is observed when renewal is attempted; a still-valid held lease does not provide immediate revocation detection. Process termination covers compliant descendants attached to the owned app-server, not escaped or hostile same-UID processes. Copied bearer capabilities still require the broker's positive settlement before ownership reassignment; stopping a process or dropping its in-memory credential is not that proof. Full live handover remains tracked in [#34](https://github.com/repoagent/controller/issues/34).

The wrapper is the supported GitHub command path. Directly invoking another `gh` binary bypasses it and is not supported for agent publication. This is credential plumbing within the configured runtime, not a security boundary against arbitrary code running with the same operating-system identity.

## Bot identity and existing state

The bot name is derived from the registered App. Choosing an App with a shorter name shortens its actual bot handle; a local text alias would not register that identity with GitHub. For example, `@bridge` belongs to an unrelated real GitHub user and must not be used as this App's identity. For a short registered user handle, configure a dedicated mention account as described below.

Mention the actual bot login in polled text, for example `@repo-agent-connector[bot]`, or enroll the Discussion URL explicitly. Literal mention matching includes the suffix; autocomplete/notification behavior is not needed for enrollment. GitHub's GraphQL viewer exposes the suffixed login, while Discussion authors can expose a `Bot` with the bare App slug. Loop suppression recognizes both forms; the bare slug is accepted only for a `Bot`, so a human with that name remains participant activity. A human reaction/reply to a bot-authored post is still user activity.

A checkpoint is bound to its repository and authenticated actor. Renaming an App can change its bot login: this App changed from `discussion-agent-bridge[bot]` to `repo-agent-connector[bot]` while retaining App ID `5387625` and installation ID `173189777`. Stop the bridge, back up the checkpoint and verify the same App and installation before deliberately migrating its actor binding; preserve agent session IDs, source checkpoints and pending recovery markers. Start with a fresh state file when converting an existing user-backed run to an App, or review/migrate it deliberately with the process stopped. Automatic migration could replay historical instructions or misclassify previous user output, so the bridge rejects mismatched identity. Preserve the old checkpoint and agent session history for manual inspection. Agent session history and compaction remain owned by Codex. When the repository itself is renamed, stop the bridge and back up its checkpoint. After confirming the old and new names identify the same GitHub repository, update only its `repository` field; preserve session IDs, source checkpoints and any pending recovery markers.

## Dedicated mention account

RepoAgent uses `@repoagent` as its operator-controlled mention identity; the GitHub App remains the authenticated publisher. Configure `repoagent ... --mention-user repoagent` (omit `@`). This optional setting supports another existing dedicated user too. Startup resolves the real GitHub user through the App-authenticated API and rejects malformed, missing or organization accounts. The mention identity is reported separately from the actual App actor and does not change checkpoint ownership or existing agent sessions.

The bridge reads GitHub's rendered `bodyHTML` and recognizes `a.user-mention` links to that account. Emphasized mentions work; plain profile links, inline code, fenced code and email addresses are not mention links. Rendering metadata stays out of checkpoints and forwarded context, so render-only changes do not replay accepted content. The actual App bot mention and explicit URL enrollment remain available.

By default, do not invite the mention account to the repository or grant it access. A real registered account can render as a clickable mention even when it cannot read the private repository; the App sees that mention through its own authorized API access. No password, OAuth session or PAT for this account belongs in the bridge container. The [profile README source](profile/README.md) explains the two identities; publish it as the root README of the public `repoagent/repoagent` profile repository.

Clickability and autocomplete are distinct. GitHub's documented [autocomplete results](https://docs.github.com/en/get-started/writing-on-github/getting-started-with-writing-and-formatting-on-github/basic-writing-and-formatting-syntax#mentioning-people-and-teams) include repository collaborators and thread participants; behavior for an account with no repository access needs a browser check. That uncertainty is not a reason to grant access. This workaround does not register a native coding agent or add issue/PR processing to the Discussion transport.

## Native GitHub agent UI

GitHub's [agent apps](https://docs.github.com/en/copilot/concepts/agent-capabilities/about-agents/agent-apps) can appear in issue Assignees, pull request mention autocomplete and the Agents UI. This requires an App configured with agent features, not just a normal App installation or a local text alias. GitHub also advertises [early access for developers creating agent apps](https://github.com/features/preview/agent-apps-for-github); availability for this operator-owned App must be checked in its settings.

On 19 October 2026 the operator found no agent configuration in this App's settings. GitHub's assignable-actor API returned Copilot, Claude and Codex for both `egil/framework` and this repository, but did not return this App. Early-access onboarding is the next platform prerequisite; adding repository permissions or changing the local CLI cannot be claimed to enable native agent registration.

The documented agent-app runtime uses Copilot cloud agent, with partner functionality through MCP. The documented entry points are issue assignment, pull request comments and the Agents UI. These docs do not establish either a Discussion mention picker or a way to substitute this bridge's existing self-hosted Codex sessions as the runtime. Native agent registration is therefore a separate compatibility investigation; this authentication conversion does not claim to provide it or silently change the runtime.

## Verification

The containerized xUnit suite tests the project-owned authentication/dispatch behavior: valid RSA-signed JWTs, installation/permission validation, repository-scoped tokens, renewal before expiry, unchanged wrapper paths reading renewed tokens, expired/stopped-lifetime rejection, no token canary in persistent outputs and no ambient human fallback. It uses controlled HTTP responses and time; this is local adapter evidence, not proof that the real App is installed or has working GitHub permissions.

```sh
docker build -f tests/Containerfile -t repoagent-controller-tests .
docker run --rm repoagent-controller-tests
```

For real compatibility, use the operator's App credentials in a configured container and a distinct human participant. Record the App/bot/installation identity, repository, tool versions, Discussion/source/reply URLs, agent session IDs and short redacted log excerpts:

1. Run `--check-auth`; require a confirmed App viewer and repository installation. A missing key, missing installation or insufficient permission is a failed prerequisite, not a passed compatibility check.
2. Mention the actual bot in a disposable post and explicitly enroll another Discussion. Verify bot-authored EYES on the actual source and visible replies in separate agent sessions.
3. Add a top-level comment, nested reply and human reaction to bot content. Confirm correct attribution, source receipts, thread routing, own-activity filtering and quiet unchanged polls.
4. Leave the bridge and the same agent session running across the refresh window, then request another bot-authored publication. Correlate the refresh log, unchanged session ID and new response. The deterministic suite advances time to exercise this path quickly; it does not replace a real same-session renewal check or one-hour soak.
5. Exercise a renewal failure and verify the native wrapper cannot launch authenticated work or fall through to a participant's credentials. Reconcile any accepted input separately from failed publication/receipt writes. Preserve the prototype's manual uncertainty and no-replay rules.

The complete container bootstrap, configurable additional skills and automated real-provider journey remain tracked in [#5](https://github.com/repoagent/controller/issues/5). This change implements App authentication; it does not provision an App, modify permissions, embed credentials or claim live compatibility without recorded real-provider evidence.
