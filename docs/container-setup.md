# Container setup

Use Docker Engine/Desktop and Bash. Run every command below from the checkout. The host runs the `repoagent` shell wrapper; the bridge, Codex, GitHub CLI, setup helpers and tests execute inside containers.

## Build and configure

```sh
./repoagent build
./repoagent setup
./repoagent start
./repoagent logs --follow
```

The setup wizard collects the repository, installed App/client ID, absolute PEM file path, optional expected installation ID, optional dedicated mention login and Codex model. It runs Codex device sign-in and checks both Codex authentication and the actual GitHub App installation before allowing work. Visit the printed URLs and enter the displayed codes in your browser; this also works from tmux. App registration, installation and permission approval happen in GitHub, as described in [App setup](github-app-auth.md).

The built image seeds Codex **0.163.0**, GitHub CLI **2.102.0** and pins the bridge's build SDK **11.0.100-rc.2**. Downloaded seed binaries have checked SHA-256 digests. The bridge is Native AOT compiled; the final image contains no .NET runtime or SDK. `./repoagent versions` reports the selected tools and non-root UID. Linux amd64 and arm64 builds are configured; record the platform actually verified when reporting acceptance.

By default every agent-container start checks for the latest stable CLI versions before agent work, including `docker restart`. Codex is installed through its [official managed installer and `codex update`](https://learn.chatgpt.com/docs/cli/reference#codex-update), including companion binaries. GitHub CLI has no built-in updater; setup installs its latest published Linux release with the release's SHA-256 digest verified. Updates run as UID 1000 in a persistent tools volume, without a root package-manager step. Startup logs report the actual versions. A failed update check stops startup visibly and retains previous files; it does not claim they are current.

For reproducible acceptance or a temporary rollback to the image's seed versions, configure `--tool-updates pinned`. It selects the bundled binaries even if newer tools remain in storage. Configure `--tool-updates latest` to resume normal restart updates. The bridge and base operating-system packages still require an image rebuild to update.

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

## Skills

The required `github-discussion` response skill is built alongside the bridge and injected through the native skill protocol. Optional skills are configured independently:

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

Use an immutable full commit SHA. Setup parses the skill's YAML frontmatter, validates its name and description, reports repository/revision/path, and installs into the native user skill location. Missing sources, invalid paths, duplicate/reserved names and invalid skill metadata fail before replacing the current selection. Installing an empty array explicitly clears optional skills. The response skill remains required.

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
| `repoagent-default-state` | JSON checkpoint, lease and renewable GitHub App credential/wrapper |
| `repoagent-default-tools` | Updated `gh` and managed Codex executables and update metadata |
| `repoagent-default-participant` | Separate human GitHub sign-in, used only by opt-in live tests |

The runtime user is UID 1000. The explicitly selected PEM must be readable by that UID; setup does not change its host permissions. The key is mounted read-only separately from the agent workspace. Container replacement and image rebuild retain the volumes; `restart` replaces the container and checks authentication before starting it again.

The runtime publishes no ports and receives no Docker socket, privileged mode, host networking or host home directory. Normal tool permissions apply inside the container (`approval_policy=never`, full container filesystem/network access), because this bridge has no interactive approval channel. Skills and participant instructions run with that container user's authority.

```sh
./repoagent stop
./repoagent enroll https://github.com/OWNER/REPO/discussions/123
./repoagent start
./repoagent state
```

Keep one running bridge per repository/App; separate instances do not make concurrent processing of the same conversations safe. Back up the state and native history before any manual recovery described in the [README](../README.md#checkpoints-and-manual-recovery).

To intentionally remove this instance's stored credentials, selected skills, workspace, native history and checkpoint:

```sh
./repoagent reset --delete-stored-data
```

This does not revoke upstream OAuth authorizations or uninstall the GitHub App. Live-test evidence volumes and retained failed Discussions have separate cleanup described in the [acceptance catalog](acceptance-cases.md).
