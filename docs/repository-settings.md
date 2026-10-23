# Repository settings

Nonsecret controller settings live in direct repository **Actions variables** named `REPOAGENT_*`. Operators change them after installation, without reinstalling. The operator's host GitHub authority writes them; the controller's runtime credential can only read them. Never store a secret in these variables: the runtime installation token is shared with the agent's `gh`, so the agent can read them too.

The supported names and validation rules are in [`src/RepoAgentController/repository-settings.json`](../src/RepoAgentController/repository-settings.json). The controller and the operator script use this same catalogue.

The catalogue's `reserved` list names `REPOAGENT_` variables that other features own. Today it holds `REPOAGENT_APPROVAL`, the default controller approval marker, which enrollment and revocation manage. A reserved name can never be a setting. The controller never reads it as a setting, and the settings command refuses to write or delete it before contacting GitHub.

| Name | Value | Status |
| --- | --- | --- |
| `REPOAGENT_MAX_ACTIVE_SESSIONS` | Integer from 1 to 64 | The controller validates and reports it. A separate slice adds active-session admission and its default. |
| `REPOAGENT_ENROLLMENT_CATEGORIES` | 1 to 25 Discussion category names, separated by commas | New Discussions in these categories enroll without a mention. Absent means no category enrollment. See [Category enrollment](#category-enrollment). |

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

## Category enrollment

`REPOAGENT_ENROLLMENT_CATEGORIES` selects Discussion categories whose new Discussions start their native session without a mention. Mentions and `--enroll` URLs keep working in every category. Each Discussion still has one native session; labels never enroll a Discussion.

**Value format.** List 1 to 25 category names, separated by commas, as they appear in the repository's Discussion categories. Matching ignores letter case and the spaces around each name. A name has at most 100 characters, no control characters, and appears once. A category whose name contains a comma cannot be selected. The controller and the settings command use the normalized form, for example `Agents, Q&A`. In PowerShell, quote the whole argument, because an unquoted comma makes an array:

```powershell
./install.ps1 -Action Settings -Repository owner/repo -Setting 'REPOAGENT_ENROLLMENT_CATEGORIES=Agents, Q&A' -Apply
```

**New and preexisting Discussions.** For each selected category, the controller records in its state file when its settings read first saw that category selected. A Discussion in the category enrolls at its next observation (poll, webhook or reconciliation) when GitHub's creation time is at or after the recorded time. Its body and existing comments form the bootstrap input, as for a mention. A Discussion created earlier is preexisting and never enrolls by category, even if it moves into the category later; mention the App or pass its URL to `--enroll` instead. A Discussion created after the recorded time in another category enrolls when it moves into a selected category.

**Timing and changes.**

- A running controller reads a change within one minute. A Discussion created between the variable change and that read counts as preexisting, so wait for the `repository-setting name=REPOAGENT_ENROLLMENT_CATEGORIES state=configured` log line before relying on a new category.
- The recorded time survives restarts, so Discussions created while the controller is stopped enroll when it starts again. A category first selected while the controller is stopped is recorded when the controller next starts.
- Removing a category stops new enrollment from it and forgets its recorded time. Discussions already enrolled keep their sessions. Selecting the category again starts afresh, so Discussions created while it was unselected stay preexisting.
- Clearing the variable, or running without `--repository-settings`, removes every selection. An invalid value or a failed read keeps the previous selection.

**Never enrolled by category.** Discussions authored by the controller's own App, such as its status dashboard, never enroll by category. This prevents a controller-written Discussion from starting agent work.

Category enrollment needs no permission beyond Discussions read and the Variables read that settings already require. Broker-enrolled controllers cannot read settings yet (see [Controller behavior](#controller-behavior)), so they enroll only by mention or URL.

## Adding a setting

1. Add an entry to the catalogue. It has `name` (`REPOAGENT_` plus uppercase letters, digits or `_`, and not a `reserved` name), `description`, `type`, `minimum` and `maximum`. Types are `integer` (bounded by `minimum` and `maximum`) and `category-names` (whose `minimum` and `maximum` bound the name count). A new type needs matching validation in `RepositorySetting.Normalize` and in `Get-Normalized` in [`scripts/repository-settings.ps1`](../scripts/repository-settings.ps1).
2. Read the effective value from the `RepositorySettings` instance that `BridgeCommand` creates, as `settings["REPOAGENT_…"].Value`. A `null` value means the feature's own default applies. The value changes at most once per refresh, so read it at each decision point instead of caching it.
3. Document the default and when a change takes effect. Then add a row to the table above.
