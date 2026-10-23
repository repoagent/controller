# Repository settings

Nonsecret controller settings live in direct repository **Actions variables** named `REPOAGENT_*`. Operators change them after installation, without reinstalling. The operator's host GitHub authority writes them; the controller's runtime credential can only read them. Never store a secret in these variables: the runtime installation token is shared with the agent's `gh`, so the agent can read them too.

The supported names and validation rules are in [`src/RepoAgentController/repository-settings.json`](../src/RepoAgentController/repository-settings.json). The controller and the operator script use this same catalogue.

The catalogue's `reserved` list names `REPOAGENT_` variables that other features own. Today it holds `REPOAGENT_APPROVAL`, the default controller approval marker, which enrollment and revocation manage. A reserved name can never be a setting. The controller never reads it as a setting, and the settings command refuses to write or delete it before contacting GitHub.

| Name | Value | Status |
| --- | --- | --- |
| `REPOAGENT_MAX_ACTIVE_SESSIONS` | Integer from 1 to 64 | The most native Discussion turns running at once. Default `4`. See [Active-session limit](#active-session-limit). |

## Inspect or change settings

Run this from a checkout with PowerShell 7.4+ and an authenticated host `gh`:

```powershell
./install.ps1 -Action Settings -Repository owner/repo                       # read-only inspection
./install.ps1 -Action Settings -Repository owner/repo -Setting REPOAGENT_MAX_ACTIVE_SESSIONS=4          # preview
./install.ps1 -Action Settings -Repository owner/repo -Setting REPOAGENT_MAX_ACTIVE_SESSIONS=4 -Apply   # write
./install.ps1 -Action Settings -Repository owner/repo -ClearSetting REPOAGENT_MAX_ACTIVE_SESSIONS -Apply
```

Inspection needs repository Variables read access, and changes need write access. The script needs no Docker or instance state, and it never reads or prints other variables. It rejects unsupported names and invalid values before contacting GitHub, and it writes the value in the controller's normalized form. Before each write it reads the current value, and afterwards it reads the variable again to confirm the write. If a read fails, the current value is unknown, so the script writes nothing. GitHub has no compare-and-set for variables, so the last writer wins. The final stdout line is a JSON summary.

## Controller behavior

Enable reading with `--repository-settings`. For the container runtime, run `repoagent configure ... --repository-settings`. The GitHub App installation must grant **Variables: read**. With the flag, the runtime token requests `actions_variables: read` in addition to its existing permissions. It never requests write, even when the installation grants write. Without the flag, the controller requests no new permission and logs `repository-settings state=disabled`.

Broker enrollments are frozen, versioned permission profiles that grant no Variables access. The controller does not widen them: it refuses `--repository-settings` in broker mode. Settings for broker-enrolled controllers need a new explicitly versioned enrollment profile that includes `actions_variables: read`, plus a fresh approval for that profile.

The controller reads each supported variable at startup, including under `--check-auth`. It then reads them again every minute while running, with one `GET` per catalogue entry. A change in a setting's state, value or reason is logged once:

```text
repository-setting name=REPOAGENT_MAX_ACTIVE_SESSIONS state=configured value=4
repository-setting name=REPOAGENT_MAX_ACTIVE_SESSIONS state=invalid value=4 reason="must be an integer from 1 to 64"
repository-setting name=REPOAGENT_MAX_ACTIVE_SESSIONS state=read-failed value=4 reason="HTTP 503"
repository-setting name=REPOAGENT_MAX_ACTIVE_SESSIONS state=absent
```

`value` is the effective value. HTTP 404 confirms absence, and then no repository value applies, so the consuming feature uses its documented default. An invalid value or a failed read keeps the last effective value, and is never reported as absence. A failed read does not stop the controller; the next refresh retries it. Invalid raw values are not echoed.

## Active-session limit

`REPOAGENT_MAX_ACTIVE_SESSIONS` bounds how many native Discussion turns one controller runs at the same time. A Discussion holds a slot from native acceptance of its turn until the native reports that turn completed or failed; a native `active` status report also holds one. Stored sessions that are idle, not loaded or failed hold no slot, however many there are.

At the limit, newly observed input waits in the controller, as it does for a busy session. It shows as `waiting` in `status`, gets no EYES, and the observation logs `input-waiting ... active=N limit=M`. When a turn finishes, waiting Discussions are dispatched again.

- **Default.** `4` applies whenever no repository value is effective: the variable is absent, the controller runs without `--repository-settings` (including every broker enrollment today), or the read failed before any value was known.
- **Refresh.** A change applies within one settings refresh, at most one minute. Raising the limit dispatches waiting input right after that refresh. Lowering it never stops or steers a running turn; new turns wait until running turns fall below the new limit. An invalid value or a failed read keeps the last effective limit.
- **Order.** There is no fairness guarantee. When a slot frees, the controller tries the oldest waiting input first, but an observation that finds a free slot can dispatch its own Discussion first. Each Discussion still runs at most one turn at a time.

## Adding a setting

1. Add an entry to the catalogue. It has `name` (`REPOAGENT_` plus uppercase letters, digits or `_`, and not a `reserved` name), `description`, `type`, `minimum` and `maximum`. Only `integer` exists today. A new type needs matching validation in `RepositorySetting.Normalize` and in `Get-Normalized` in [`scripts/repository-settings.ps1`](../scripts/repository-settings.ps1).
2. Read the effective value from the `RepositorySettings` instance that `BridgeCommand` creates, as `settings["REPOAGENT_…"].Value`. A `null` value means the feature's own default applies. The value changes at most once per refresh, so read it at each decision point instead of caching it.
3. Document the default and when a change takes effect. Then add a row to the table above.
