# Repository settings

Nonsecret controller settings live in direct repository **Actions variables** named `REPOAGENT_*`. Operators change them after installation, without reinstalling. The operator's host GitHub authority writes them; the controller's runtime credential can only read them. Never store a secret in these variables: the runtime installation token is shared with the agent's `gh`, so the agent can read them too.

The supported names and validation rules are in [`src/RepoAgentController/repository-settings.json`](../src/RepoAgentController/repository-settings.json). The controller and the operator script use this same catalogue.

The catalogue's `reserved` list names `REPOAGENT_` variables that other features own. Today it holds `REPOAGENT_APPROVAL`, the default controller approval marker, which enrollment and revocation manage. A reserved name can never be a setting. The controller never reads it as a setting, and the settings command refuses to write or delete it before contacting GitHub.

| Name | Value | Status |
| --- | --- | --- |
| `REPOAGENT_MAX_ACTIVE_SESSIONS` | Integer from 1 to 64 | The controller validates and reports it. A separate slice adds active-session admission and its default. |

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

Enable reading with `--repository-settings`. For the container runtime, run `repoagent configure ... --repository-settings`. The GitHub App installation must grant **Variables: read**. In direct-App mode, the flag makes the runtime token request `actions_variables: read` in addition to its existing permissions. In broker mode, the flag selects a repository-settings enrollment (below). It never requests write, even when the installation grants write. Without the flag, the controller requests no new permission and logs `repository-settings state=disabled`.

### Broker enrollments

The existing broker profiles (`polling`, `webhook`, `polling-clone`, `webhook-clone`) are frozen and grant no Variables access. The controller does not widen them: with a v1 or v2 enrollment file it refuses `--repository-settings` before contacting the broker, and the broker refuses `actions_variables` for those profiles.

To read settings through the broker, choose a repository-settings profile at enrollment ([ADR 0008](adr/0008-add-an-explicit-repository-settings-runtime-profile.md)). These profiles are `polling-settings`, `webhook-settings`, `polling-clone-settings` and `webhook-clone-settings`. Each grants exactly its frozen counterpart's permissions plus `actions_variables: read`, under the policy `repository-actions-settings-approval-v3`. They require Actions-variable approval (setup v3); legacy Agent-variable approval is refused before any work. The enrollment file, leases and runtime routes are protocol v3, and the controller consumes a v3 file only with `--repository-settings`. See [the consumer contract](broker-protocol.md#explicit-repository-settings-consumer-v3).

- **Guided installer.** Add `-RepositorySettings` to `./install.ps1` for a fresh instance. The installer selects the matching `*-settings` profile for the chosen mode and clone choice, and it configures the runtime with `--repository-settings`.
- **Host enrollment.** Pass `-EnrollmentProfile polling-settings` (or another settings profile) to `scripts/enroll-controller.ps1`, then start the controller or `repoagent configure` with `--repository-settings`. A container instance keeps this choice for its lifetime (`REPOAGENT_REPOSITORY_SETTINGS=true` for externally supplied enrollments).

An existing broker-enrolled controller is never upgraded in place. To move it to a settings profile:

1. Revoke its current approval (`./install.ps1 -Action Revoke` with the saved instance, or `scripts/revoke-approval.ps1`).
2. Wait until the broker has closed the grant and its outstanding tokens have settled. One owner gate serves each repository, so a new enrollment is refused (`409`) until then.
3. Enroll afresh in a new state directory with a settings profile (`-RepositorySettings` for a new installer instance). The new controller gets a new ID and a new approval marker that names the settings profile.

Rollback is the same procedure in reverse: revoke the settings grant, wait for settlement, and enroll a frozen profile in a new state directory (omit `-RepositorySettings`). A broker build without runtime v3 refuses settings profiles at setup (`400`) and answers the v3 runtime routes with `404`, so the controller fails closed rather than receiving a narrower or wider token.

A settings token is read-only for variables, so the controller still cannot write or restore its own approval. It can read every direct repository Actions variable, however, including the nonsecret approval marker. Keep secrets out of repository variables.

The controller reads each supported variable at startup, including under `--check-auth`. It then reads them again every minute while running, with one `GET` per catalogue entry. A change in a setting's state, value or reason is logged once:

```text
repository-setting name=REPOAGENT_MAX_ACTIVE_SESSIONS state=configured value=4
repository-setting name=REPOAGENT_MAX_ACTIVE_SESSIONS state=invalid value=4 reason="must be an integer from 1 to 64"
repository-setting name=REPOAGENT_MAX_ACTIVE_SESSIONS state=read-failed value=4 reason="HTTP 503"
repository-setting name=REPOAGENT_MAX_ACTIVE_SESSIONS state=absent
```

`value` is the effective value. HTTP 404 confirms absence, and then no repository value applies, so the consuming feature uses its documented default. An invalid value or a failed read keeps the last effective value, and is never reported as absence. A failed read does not stop the controller; the next refresh retries it. Invalid raw values are not echoed.

## Adding a setting

1. Add an entry to the catalogue. It has `name` (`REPOAGENT_` plus uppercase letters, digits or `_`, and not a `reserved` name), `description`, `type`, `minimum` and `maximum`. Only `integer` exists today. A new type needs matching validation in `RepositorySetting.Normalize` and in `Get-Normalized` in [`scripts/repository-settings.ps1`](../scripts/repository-settings.ps1).
2. Read the effective value from the `RepositorySettings` instance that `BridgeCommand` creates, as `settings["REPOAGENT_…"].Value`. A `null` value means the feature's own default applies. The value changes at most once per refresh, so read it at each decision point instead of caching it.
3. Document the default and when a change takes effect. Then add a row to the table above.
