# Connected apps

Account-connected apps are **disabled by default**, including on upgrades from saved configuration without an app policy. Signing RepoAgent in with a personal or dedicated Codex account does not opt it in. The policy affects the controller's owned Codex runtime, not the operator's host Codex configuration or global account connections.

An enabled app may read private information or send messages outside the repository as the Codex account owner, in response to repository participants. GitHub App repository permissions do not limit that account authority. Choose the narrowest access needed:

| Mode | Access |
| --- | --- |
| `disabled` | Account-connected apps unavailable. Default for new and legacy instances. |
| `selected` | Only explicitly listed stable app IDs enabled. Newly connected apps stay unavailable. App access is not read-only access. |
| `all` | All current and future connected apps enabled, subject to Codex/account permissions and tool restrictions. An explicit choice, including for dedicated accounts. |

## Configure

PowerShell installer preview, then apply the reviewed configuration with `-Apply`:

```powershell
./install.ps1 -Instance repo-one -ConnectedApps selected -AllowedApp connector_example -NonInteractive
```

Supply the other required installation inputs on first use, as described in [local installation](local-installation.md). Existing instance choices are retained unless replaced. To disable an opted-in instance, use `-ConnectedApps disabled` without `-AllowedApp`. Explicit allowlists are accepted only with `selected`; selected mode needs at least one ID. IDs are case-sensitive letters, digits, underscores and hyphens, at most 200 characters; `_default` is reserved.

The Bash wizard accepts `--connected-apps selected --allowed-app connector_example` (repeat `--allowed-app` for several apps), or `--connected-apps all`. It offers saved choices as defaults and explains the authority before configuring. Both guided paths show whether the choice was explicit, retained or defaulted.

Container configuration accepts the same flags:

```sh
./repoagent configure <existing-required-configuration-options> --connected-apps selected --allowed-app connector_example
```

Native controller startup accepts `--connected-apps disabled|selected|all` and repeatable `--allowed-app ID`. Native operators must retain and pass their explicit flags on each launch. Container configuration persists them in the private config volume’s `connected-apps.json`, independently of immutable image/broker launch snapshots. Ordinary starts, image updates and image rollback retain this local policy. The updated host wrappers require the image capability label `io.repoagent.connected-apps-policy=1` before startup or an image transition, including rollback. Older images are refused before starting agent work; retain a policy-capable image for rollback. Old host wrappers do not provide this check. Guided/direct PowerShell reconfiguration also updates the host launch mirror so installer reruns retain it. With externally injected enrollment, set `REPOAGENT_CONNECTED_APPS` and, for selected mode, `REPOAGENT_ALLOWED_APP` to comma-separated IDs. These are local container environment inputs, not repository Agent variables.

Stop the instance before reconfiguring and restart it afterwards. Changes apply to the new runtime and resumed sessions; editing configuration cannot revoke a tool in a running turn. Missing legacy fields become disabled on the next start of this version. Legacy configurations with no saved app policy require explicit opt-in to keep apps after upgrading; a previously saved explicit choice is retained. Replacing Codex credentials retains policy; check the new account's app availability before relying on it.

## Find app IDs and verify

On a stopped, signed-in instance, run `./repoagent apps` or `./repoagent.ps1 apps`. Native operators use `repoagent apps --workspace /absolute/workspace` with their private `CODEX_HOME`. These commands start an isolated discovery runtime with apps enabled solely to list the account catalogue, without agent turns, then exit. They do not change the saved policy. Container discovery applies an explicitly supplied credential replacement before reading the catalogue. Catalogue output is private operator output; it contains stable `id`, `name`, `isAccessible` and `isEnabled` fields. Use accessible app IDs; never guess an ID from a display name. Discovery failure is not evidence of an empty account.

After startup inspect private `status`. `connectedApps` reports `mode`, `allowedApps`, `verification` and `unavailableApps`. Correlate the status with this controller's start time and `connected-apps ... policy=verified` diagnostic. Verification confirms configuration enforcement, not that every app tool will succeed. Selected mode checks availability at runtime initialization, including credential-triggered restarts; inaccessible or missing IDs appear under `unavailableApps` and are not substituted. Account connections can change later; rerun discovery/restart to refresh that observation. All/disabled mode does not enumerate availability in status. An unsupported or unverifiable policy stops startup or refuses new agent work.

## Enforcement and limits

Qualified against the checksum-pinned Codex **0.167.1** in `image/default.json`. Codex merges app tables: setting `apps._default.enabled=false` alone preserves explicit per-app enables. RepoAgent therefore starts discovery with `features.apps=false`, reads merged app IDs, then pins each existing app's enabled state plus the default at command-line precedence. The actual runtime configuration is checked before admitting work and before session start/resume and turns. Session start/resume receive the same explicit policy overrides. New unlisted explicit enables encountered later cause selected mode to refuse work until configuration is corrected and the runtime restarted. Default disabled mode pins the feature gate off even if app entries are enabled.

The bounded manual qualification below exercises stale user entries and trusted repository configuration with the real pinned binary without account credentials or model calls. Core policy/status behavior has in-process unit coverage; provider compatibility stays in manual/hosted smoke verification (ADR 0010). A live Gmail-connected account/model-turn acceptance check remains an operator verification: inspect availability without invoking send or making unrelated external writes. No real-email test is required or authorized by installation.

This policy governs Codex account-connected apps. Custom MCP servers, plugin-provided non-app tools, web search, browser sign-ins, command-line credentials and arbitrary agent-launched processes have their own authority. Shell/file tools and the repository-scoped GitHub App wrapper remain available. Under [ADR 0016](adr/0016-trust-container-owner-for-native-remote-access.md), container/host owners and native owner access remain trusted; this is not isolation against a hostile container owner or agent with arbitrary process access.

## Manual runtime qualification

Run `python3 scripts/qualify-connected-apps.py --codex /absolute/path/to/pinned/codex`. Use the binary matching `image/default.json`. This bounded check uses temporary private homes, runs no model turns, and imports no account credentials. It checks effective disabled/selected/all configuration despite stale user and trusted project app enables. For native credential replacement, use the private `apps --codex-auth PATH` command in a stopped isolated instance whose configuration selects keyring storage; verify that discovery uses the supplied file account. A real-account check needs explicit operator authorization and must not send email.

## Agent-assisted setup

The repository ships `.agents/skills/setup-repoagent/SKILL.md`. Agents working from this checkout can discover it automatically. For use elsewhere, copy that skill directory into your agent's personal skills directory (for Codex, `~/.agents/skills/setup-repoagent/`) and invoke `$setup-repoagent`; provide a reviewed checkout/release to install. The skill reads that version's installation docs, distinguishes GitHub and Codex account authority, resolves unanswered operator choices, prepares a concrete preview, applies authorized choices and verifies private status. Script-based installation remains supported without the skill.
