For versioned software choices, a repository-owned base image, and shared Bash/PowerShell image builds, see [image composition](image-composition.md).

# Container setup

Use Docker Engine/Desktop with Linux containers and either Bash or PowerShell 7. Run every command below from the checkout. The host runs a small launcher; the bridge, Codex, GitHub CLI and setup helpers execute inside containers. The direct-App installation below needs no host .NET, Node.js or GitHub CLI installation. The separate [PowerShell approval-revocation tool](repository-approval.md) uses authenticated host `gh` and needs no Docker. New broker installer work targets PowerShell first; Bash enrollment parity is deferred.

## Install on Windows

With Docker Desktop running in Linux container mode, clone or download this repository, then open PowerShell 7 (`pwsh`) in the checkout:

```powershell
git clone https://github.com/repoagent/controller.git
cd controller
.\install.ps1
```

The installer asks questions for missing values and explains each operation before performing it. It checks Docker, builds the image, guides GitHub App configuration, imports an existing Codex sign-in or offers device sign-in, verifies authentication, and starts the agent. It uses Docker Desktop directly; no WSL distribution or Linux shell is needed.

You can supply some answers and let it ask for the rest, or supply the required values and disable all prompts:

```powershell
.\install.ps1 `
  -Repository 'my-org/my-repo' `
  -AppId '12345678' `
  -KeyPath 'C:\Users\me\Keys\github-app.pem' `
  -Instance 'my-repo' `
  -NonInteractive
```

Replace the repository, App ID and key path with your values. The target repository needs **Discussions** enabled. For a new organization-owned App, the script describes **organization Settings → Developer settings → GitHub Apps → New GitHub App**, repository **Discussions: read and write**, private-key generation and installation on the selected repository. Polling is the default and needs no public URL or active webhook. Registering the App under the target organization lets you choose **Only on this account**. See [GitHub App registration](https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/registering-a-github-app) and [project authentication details](github-app-auth.md).

For an already authenticated Windows Codex CLI, the installer offers `%USERPROFILE%\.codex\auth.json`, or `auth.json` under `CODEX_HOME` when that variable is set. Use `-CodexAuthPath 'C:\path\auth.json'` to select another file. Only that file is mounted read-only during import, copied into this instance's credentials volume, and unmounted afterward. The host's configuration, skills and session history are not copied. The container runs its own Codex executable.

If credentials are held in the OS credential store, an `auth.json` may not exist. Omit `-NonInteractive` and use `-DeviceLogin` to skip host-file import and sign in inside Docker. Device login requires enabling it in ChatGPT security/workspace settings. In noninteractive mode, missing/invalid container credentials fail instead of opening a login flow. See [official OpenAI authentication documentation](https://learn.chatgpt.com/docs/auth).

Follow logs and manage the same instance from PowerShell:

```powershell
$env:REPOAGENT_INSTANCE = 'my-repo'
.\repoagent.ps1 logs --follow
.\repoagent.ps1 stop
.\repoagent.ps1 start
.\repoagent.ps1 restart
```

Ctrl+C exits the log viewer; the container keeps running. Keep Docker Desktop and the machine running for the agent to receive activity. The launcher does not install a Windows service or configure startup at login. It also supports `get`, `configure`, `check`, `login`, `import-codex-auth`, `versions`, `status`, `logs --retained`, `state`, `enroll` and `skills`. The Bash-only live-test runner remains available from a Bash environment.

After startup, use the actor login printed in the logs, including `[bot]`, in a Discussion body or comment. A 👀 receipt means Codex accepted the input; wait for the App-authored reply. Alternatively, stop the agent, explicitly enroll a Discussion, then start it:

```powershell
.\repoagent.ps1 stop
.\repoagent.ps1 enroll https://github.com/my-org/my-repo/discussions/123
.\repoagent.ps1 start
```

This installs the current Discussion interface. Its repository-scoped token does not grant code pushes or PR creation.

## Install with Bash

```sh
./install.sh
# Or supply answers without prompts:
./install.sh --repository my-org/my-repo --app-id 12345678 \
  --key-path /absolute/path/github-app.pem --instance my-repo --non-interactive
```

The Bash installer uses the same setup wizard as `./repoagent setup`, detecting `${CODEX_HOME:-$HOME/.codex}/auth.json` for optional import. Within WSL, this is the Linux home by default. To reuse a Windows-native login from WSL, explicitly supply `--codex-auth /mnt/c/Users/WINDOWS_USER/.codex/auth.json`. Docker Desktop must enable integration for that WSL distribution; use Linux paths for `--key-path`. See [Docker's WSL integration guide](https://docs.docker.com/desktop/features/wsl/).

Both installers preserve saved settings as defaults, reject reconfiguration of a running instance, and stop on build, import, authentication or startup failure. Reuse the same instance to preserve conversations; select a new instance to change the repository/App. They never delete volumes or register a GitHub App automatically. App keys must be readable by container UID 1000; installers verify this without changing host permissions. The normal agent has access to its container filesystem/network; its only host-file mount is the selected read-only App key.

Useful options are available in both shells:

| PowerShell | Bash | Behavior |
| --- | --- | --- |
| `-Repository`, `-AppId`, `-KeyPath` | `--repository`, `--app-id`, `--key-path` | Required answers for a new instance |
| `-CodexAuthPath PATH` | `--codex-auth PATH` | Select an existing auth cache |
| `-DeviceLogin` | `--device-login` | Skip host auth import; reuse container login or offer device sign-in |
| `-Instance NAME` | `--instance NAME` | Select isolated container/volumes; default `default` or `REPOAGENT_INSTANCE` |
| `-NonInteractive` | `--non-interactive` | Use supplied/saved/default values; no prompts or browser opening |
| `-NoStart` | `--no-start` | Configure and verify, then leave the agent stopped |
| `-SkipBuild` | `--skip-build` | Use an existing image |
| `-Model MODEL` | `--model MODEL` | Choose Codex model; new-instance default `gpt-6.1-sol` |
| `-MentionUser LOGIN` | `--mention-user LOGIN` | Optional existing mention account; default empty, using App mentions/enrollment |
| `-InstallationId ID` | `--installation-id ID` | Check an expected App installation |
| `-ToolUpdates latest\|pinned` | `--tool-updates latest\|pinned` | Opt in to startup tool updates; default `pinned` runs the image's tools |
| `-WebSearch live\|cached\|disabled` | `--web-search live\|cached\|disabled` | Choose web search; default `live` |
| `-Mode polling\|fixed-url\|quick-tunnel` | `--mode polling\|fixed-url\|quick-tunnel` | Choose deployment mode; default `polling` |
| `-PublicUrl`, `-ListenPort`, `-ReconcileInterval` | `--public-url`, `--listen-port`, `--reconcile-interval` | Webhook configuration; see the hosting guides |

Run `.\install.ps1 -?` / `Get-Help .\install.ps1 -Full` or `./install.sh --help` for usage. PowerShell parameter names use one leading hyphen; Bash options use two. With the PowerShell launcher, use `configure --mention-user=` when explicitly clearing the mention account; this preserves the empty value on Windows PowerShell 5.1.

## Build and configure

The individual Bash commands remain available when you want to manage each step yourself:

```sh
./repoagent build
./repoagent setup
./repoagent start
./repoagent logs --follow
```

`./repoagent setup` uses the installer wizard with build/start skipped. It accepts the same options, imports a selected Codex auth cache or offers device sign-in, and checks Codex authentication and the actual GitHub App installation. Visit printed device URLs and enter displayed codes in your browser; this also works from tmux. App registration, installation and permission approval happen in GitHub, as described in [App setup](github-app-auth.md).

The built image bundles the Codex and GitHub CLI versions pinned in `image/default.json`, and pins the bridge's build SDK **11.0.100-rc.2**. Each [baseline release](ci-cd.md#baseline-releases) records the exact bundled versions; newer supported versions arrive as new baseline images through the [tool refresh](ci-cd.md#tool-refresh). Downloaded binaries have checked SHA-256 digests. The bridge is Native AOT compiled; the final image contains no .NET runtime or SDK. `./repoagent versions` reports the selected tools and non-root UID. Linux amd64 and arm64 builds are configured; record the platform actually verified when reporting acceptance.

By default a container runs the image's bundled tools, so a published baseline image runs exactly the versions its release records; adopt newer tools by updating to a newer baseline image. Startup updates are an explicit opt-in: with `--tool-updates latest`, every agent-container start checks for the latest stable CLI versions before agent work, including `docker restart`. These versions are not verified with the controller or recorded by any release. Codex is installed through its [official managed installer and `codex update`](https://learn.chatgpt.com/docs/cli/reference#codex-update), including companion binaries. GitHub CLI has no built-in updater; setup installs its latest published Linux release with the release's SHA-256 digest verified. Updates run as UID 1000 in a persistent tools volume, without a root package-manager step. Startup logs report the actual versions. A failed update check stops startup visibly and retains previous files; it does not claim they are current.

Existing instances keep their saved choice; rerunning setup does not change it. To return to the image's bundled versions, for reproducible acceptance or as a rollback after a failed startup update, configure `--tool-updates pinned`. It selects the bundled binaries even if newer tools remain in storage. Configure `--tool-updates latest` to opt in to startup updates again. The bridge and base operating-system packages still require an image rebuild to update.

The default toolbox has Bash, Git, `gh`, Codex and its code-mode companion, Python, `ripgrep`, `curl`, `jq`, file/patch/diff tools, SSH client and archive utilities. It does not choose a project language or framework. Add project-specific tools in an image derived from `repoagent:local`, restore `USER agent` after installing them, and select it with `REPOAGENT_IMAGE=your-image ./repoagent start`. A `dotnet-development` build target is available for work on this bridge itself, selected explicitly with `REPOAGENT_TARGET=dotnet-development ./repoagent build`.

Codex uses live web search by default, configurable with `configure --web-search live|cached|disabled`. This follows the [Codex web-search configuration](https://learn.chatgpt.com/docs/config-file/config-reference). Networked tools such as `curl` and `gh` also run inside the container. Repository content/write permissions remain those explicitly authorized for the App; packaging tools does not grant GitHub permissions beyond the configured installation token.

For repeatable noninteractive configuration:

```sh
./repoagent configure --repository OWNER/REPO --app-id APP_ID_OR_CLIENT_ID \
  --key-path /absolute/path/app.pem --mention-user repoagent
./repoagent login
./repoagent check
```

An empty `--mention-user ''` keeps canonical App mentions and explicit URL enrollment only. Changing the repository/App requires a new instance, so existing accepted conversations cannot silently acquire another owner.

Device authentication requires enabling device-code login in ChatGPT security or workspace settings. If unavailable, the documented [headless authentication fallback](https://learn.chatgpt.com/docs/auth) is to copy an existing authorized Codex auth cache explicitly:

```sh
./repoagent import-codex-auth /absolute/path/to/auth.json
./repoagent check
```

Only that selected file is mounted during the import, copied with owner-only permissions, and unmounted afterward. Normal runtime mounts neither host home nor host credential directories. Rerunning `login` reuses a valid container sign-in. Setup errors stop visibly; they never select a human GitHub identity for the agent.

### Replace Codex credentials without restarting the container

`import-codex-auth` requires a stopped agent. To supply or replace Codex account credentials while the agent runs, stage them on the instance's credentials volume with the runtime's `supply-codex-auth` command. Only the selected file is mounted:

```sh
docker run --rm \
  --mount type=volume,src=repoagent-default-credentials,dst=/data/credentials \
  --mount type=bind,src=/absolute/path/to/auth.json,dst=/run/secrets/codex-auth.json,readonly \
  repoagent:local supply-codex-auth
```

The command atomically replaces `/data/credentials/codex-supply/auth.json` (owner-only). Once that file exists, `start` and `check` pass it to the controller as `--codex-auth` instead of checking the container sign-in. A running agent applies a replacement within about five seconds when no Codex turn is running: it restarts only its owned Codex app-server, and each Discussion's session resumes its native history on the next input. The pinned Codex cannot reload a replaced `auth.json` in a running app-server, so that owned restart is required. Rejected credentials appear as `codexAuthentication.state` `failed` in `status`, and new input waits until replacement credentials are applied. See [supplied Codex credentials](../README.md#supplied-codex-credentials).

## Skills

Two required skills are built alongside the bridge: `github-discussion` (how to answer each input) and `repoagent-orientation` (participants, compact and redelivered inputs, reply routing, App identity, and the read-only mainline checkout with separate worktrees for other revisions or edits). The controller registers their directory with native skill discovery. Every turn receives `github-discussion`. The first turn after a session starts or resumes in a native process also receives `repoagent-orientation`. Optional skills are configured independently:

```json
[
  {
    "repository": "OWNER/SKILL_REPOSITORY",
    "revision": "FULL_40_CHARACTER_COMMIT_SHA",
    "path": "relative/path/to/skill"
  }
]
```

```sh
./repoagent stop  # if running
./repoagent skills install /absolute/path/skills.json
./repoagent skills
./repoagent start
```

Use an immutable full commit SHA. Setup parses the skill's YAML frontmatter, validates its name and description, reports repository/revision/path, and installs into the native user skill location. Missing sources, invalid paths, duplicate/reserved names and invalid skill metadata fail before replacing the current selection. Installing an empty array explicitly clears optional skills. The required skills stay installed.

For a private source, set `"private": true` and explicitly supply a token file:

```sh
./repoagent skills install /absolute/path/skills.json /absolute/path/source-read-token
```

The supplied credential must be authorized to read that exact source repository/revision; an App credential also requires the installation and Contents permission for the source repository. The bridge's normal Discussions-only token is not sufficient to download private skill code. The token file is mounted read-only in the setup container only and is not persisted or passed to the native agent. Authentication/source failures report the source and HTTP status without printing token contents. No host skills directory or plugin installation is required.

## Storage and lifecycle

`REPOAGENT_INSTANCE` selects an isolated set of containers and volumes. Default instance `default` uses:

| Volume | Contents |
| --- | --- |
| `repoagent-default-config` | Operator configuration and selected skills; mounted read-only during agent work |
| `repoagent-default-credentials` | Codex sign-in/configuration |
| `repoagent-default-workspace` | Agent working files |
| `repoagent-default-history` | Native Codex session histories |
| `repoagent-default-state` | JSON checkpoint, lease, GitHub App wrappers and credential handoff pointer (no tokens), operator status and bounded retained logs |
| `repoagent-default-tools` | Updated `gh` and managed Codex executables and update metadata |
| `repoagent-default-participant` | Separate human GitHub sign-in, used only by opt-in live tests |

Run configuration, skill installation and lifecycle commands sequentially for each instance; concurrent operator commands are not supported. The CLI rejects setup changes when it observes the agent running.

The runtime user is UID 1000. The explicitly selected PEM must be readable by that UID; setup does not change its host permissions. The key is mounted read-only separately from the agent workspace. Container replacement and image rebuild retain the volumes; `restart` replaces the container, updates tools when enabled, and checks authentication before native work.

Polling mode publishes no ports. Explicit [fixed-URL webhook mode](webhooks.md) adds an HTTP listener and a loopback-only local port mapping for external HTTPS ingress. Neither mode receives a Docker socket, privileged mode, host networking or host home directory. Normal tool permissions apply inside the container (`approval_policy=never`, full container filesystem/network access), because this bridge has no interactive approval channel. Skills and participant instructions run with that container user's authority.

```sh
./repoagent stop
./repoagent enroll https://github.com/OWNER/REPO/discussions/123
./repoagent start
./repoagent state
```

`./repoagent status` (or `.\repoagent.ps1 status`) shows identity, lifetime, authentication health, sessions, pending/running work and recent failures as JSON while the agent runs or after it stops. `./repoagent logs --retained` prints the retained logs from the state volume, which survive container replacement unlike `docker logs`. Retention defaults to five 1 MiB files; see [status and retained logs](../README.md#status-and-retained-logs) for fields, bounds and exclusions.

Keep one running bridge per repository/App; separate instances do not make concurrent processing of the same conversations safe. Back up the state and native history before any manual recovery described in the [README](../README.md#checkpoints-and-manual-recovery).

To intentionally remove this instance's stored credentials, selected skills, workspace, native history and checkpoint:

```sh
./repoagent reset --delete-stored-data
```

This does not revoke upstream OAuth authorizations or uninstall the GitHub App. Live-test evidence volumes and retained failed Discussions have separate cleanup described in the [acceptance catalog](acceptance-cases.md).

## External controller credentials

The same image can start from broker enrollment and configuration supplied at startup. This path needs no `configure`, no `import-enrollment` and no enrollment volume, so no second durable copy of the credential exists in the container. It suits hosts that inject secrets as environment variables. It supports broker enrollment only; direct-App instances keep their PEM mount.

Supply these container environment variables. They are not repository Actions variables.

| Variable | Contents |
| --- | --- |
| `REPOAGENT_ENROLLMENT` | **Secret.** The complete private `enrollment.json` from [host enrollment](host-enrollment.md), unchanged |
| `REPOAGENT_REPOSITORY`, `REPOAGENT_APP_ID`, `REPOAGENT_BROKER_URL` | Required binding: `OWNER/REPO`, numeric App ID and the broker's HTTPS origin |
| `REPOAGENT_CLONE` | `true` for clone enrollments; omitted or `false` otherwise |
| `REPOAGENT_REPOSITORY_SETTINGS` | `true` for [repository-settings enrollments](repository-settings.md#broker-enrollments) (runtime v3); omitted or `false` otherwise |
| `REPOAGENT_REPOSITORY_ID`, `REPOAGENT_INSTALLATION_ID` | Optional expected immutable identities |
| `REPOAGENT_MENTION_USER`, `REPOAGENT_MODEL`, `REPOAGENT_WEB_SEARCH`, `REPOAGENT_TOOL_UPDATES`, `REPOAGENT_MODE`, `REPOAGENT_PUBLIC_URL`, `REPOAGENT_LISTEN_PORT`, `REPOAGENT_RECONCILE_INTERVAL`, `REPOAGENT_DASHBOARD_CATEGORY` | Optional runtime choices |

Each configuration variable stands for the `configure` option of the same name, with the same values, defaults and validation. `check` and `start` validate them on every start and never save them. An instance uses either saved configuration from `configure` or external configuration, never both. An injected credential without the three required variables is refused.

The entrypoint passes `REPOAGENT_ENROLLMENT` only to the controller process; tool updates, the Codex sign-in check and the tunnel never receive it. The controller reads the variable once and removes it from its environment before it starts `gh`, Git or Codex. It then validates the document exactly as it validates an enrollment file: format, the repository/App/installation/broker binding, profile and permissions. Missing, empty or mismatched input stops startup before any broker contact. A refused secret stops startup before any GitHub work. Diagnostics name the variable, never its value. Nothing falls back to an enrollment volume, a PEM, a personal `gh` sign-in or an ambient `GH_TOKEN`.

Keep the credentials, workspace, history, state and tools volumes. Native sessions, history, the checkpoint, Codex sign-in and installed tools persist as before: injecting credentials does not make the agent stateless. Mount a config volume only for optional skills, and only if it holds no saved settings.

Put the nonsecret variables in an env file, for example `repoagent.env`:

```sh
REPOAGENT_REPOSITORY=my-org/my-repo
REPOAGENT_APP_ID=5387625
REPOAGENT_BROKER_URL=https://credentials.example.org
REPOAGENT_CLONE=true
```

Then give `docker run` the secret by name only. Docker copies the value from the client's own environment, so it never appears on a command line:

```sh
REPOAGENT_ENROLLMENT=$(cat "$HOME/project-enrollment/enrollment.json") \
docker run -d --init --name repoagent-external \
  --env REPOAGENT_ENROLLMENT --env-file ./repoagent.env \
  --mount type=volume,src=repoagent-external-credentials,dst=/data/credentials \
  --mount type=volume,src=repoagent-external-workspace,dst=/workspace \
  --mount type=volume,src=repoagent-external-history,dst=/data/history \
  --mount type=volume,src=repoagent-external-state,dst=/data/state \
  --mount type=volume,src=repoagent-external-tools,dst=/data/tools \
  ghcr.io/repoagent/controller@sha256:IMAGE_DIGEST start
```

In PowerShell, read the file with the installer's private-file check, and clear the variable afterwards:

```powershell
Import-Module ./scripts/private-file.psm1
$env:REPOAGENT_ENROLLMENT = [Text.Encoding]::UTF8.GetString((Read-PrivateFileBytes "$HOME/project-enrollment/enrollment.json"))
try { docker run -d --init --name repoagent-external --env REPOAGENT_ENROLLMENT --env-file ./repoagent.env @mounts ghcr.io/repoagent/controller@sha256:IMAGE_DIGEST start }
finally { Remove-Item Env:REPOAGENT_ENROLLMENT }
```

Here `@mounts` holds the same five `--mount` arguments. Sign Codex in once into the credentials volume, for example with `docker run --rm -it` and the same mounts plus `login`, or stage [separately supplied Codex account credentials](#replace-codex-credentials-without-restarting-the-container) with `supply-codex-auth`. Codex credentials stay a separate input; `REPOAGENT_ENROLLMENT` never carries them. Run `check` instead of `start` to verify authorization without starting the agent.

To restart, stop and remove the container, then run it again with the same volumes and the injected value. Docker keeps an injected value in the container's configuration until the container is removed, so `docker restart` reuses it. Anyone with access to the Docker daemon can read it with `docker inspect`, and processes running as the container user can read the controller's initial environment, just as they could read the former 0600 enrollment file. Remove stopped containers, and replace the container to rotate or withdraw the value.

Your host enrollment state directory stays the authoritative secret and recovery source. Only `enrollment.json` is injected; `approval.json` and `recovery.json` never enter the container. Revoke and broker self-revoke continue to use that host state, as described in [host enrollment](host-enrollment.md#revoke-and-prototype-compatibility).

Instances managed by `repoagent.ps1` or `install.ps1 -Action Install` keep their enrollment volume. That volume also holds the disable marker and launch state used by Update, Rollback and Uninstall. Do not mix the two paths on one instance.

## Installer verification

Run the host orchestration checks without Docker or account credentials:

```sh
python3 -m unittest discover -s tests/installers -v
```

These tests execute the real Bash installer and, when `pwsh` is installed, the PowerShell installer with a controlled Docker executable at the external process boundary. They cover prompted/argument-based setup, paths containing spaces, instance isolation, auth import, noninteractive failure, and stopping before work when verification fails. A focused configuration check exercises real Windows bind-source validation. This is local orchestration evidence, not live proof of Docker Desktop file sharing, GitHub permissions or Codex OAuth on Windows.

For a temporary local HTTPS endpoint without a Cloudflare account/domain or host tools, choose `--mode quick-tunnel`; see [Quick Tunnel setup and lifecycle](quick-tunnel.md). It publishes no host port and owns cloudflared inside the agent container. Fixed-URL and polling modes do not start a tunnel.
