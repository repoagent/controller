# Repository settings

Nonsecret controller settings live in direct repository **Actions variables** named `REPOAGENT_*`. Operators change them after installation, without reinstalling. The operator's admin authority writes them; the controller's GitHub token can only read them.

> [!WARNING]
> Repository Actions variables must never hold secrets. A controller that reads repository settings, and so the agent it runs, can read every direct repository Actions variable, not only `REPOAGENT_*` settings: the agent's `gh` uses the controller's runtime token ([ADR 0008](adr/0008-add-an-explicit-repository-settings-runtime-profile.md)).

The supported names and validation rules are in [`src/RepoAgentController/repository-settings.json`](../src/RepoAgentController/repository-settings.json). The controller and the operator script use this same catalogue.

The catalogue's `reserved` list names `REPOAGENT_` variables that other features own. Today it holds `REPOAGENT_APPROVAL`, the default controller approval marker, which enrollment and revocation manage. A reserved name can never be a setting. The controller never reads it as a setting, and the settings command refuses to write or delete it before contacting GitHub.

| Name | Value | Status |
| --- | --- | --- |
| `REPOAGENT_MAX_ACTIVE_SESSIONS` | Integer from 1 to 64 | The most native Discussion turns running at once. Default `4`. See [Active-session limit](#active-session-limit). |
| `REPOAGENT_ENROLLMENT_CATEGORIES` | 1 to 25 Discussion category names, separated by commas | New Discussions in these categories enroll without a mention. Absent means no category enrollment. See [Category enrollment](#category-enrollment). |
| `REPOAGENT_MODEL` | Model alias such as `sol6.1`, or a model ID such as `gpt-6.1-sol` | Default model for Discussions without a model label. Unset: the native Codex configuration's `model`, then the catalogue's default model. |
| `REPOAGENT_REASONING` | Reasoning effort such as `xhigh` | Default effort for Discussions without a model label. Unset: the native configuration's `model_reasoning_effort`, then the selected model's default effort. |

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

Enable reading with `--repository-settings`. For the container runtime, run `repoagent configure ... --repository-settings`. The GitHub App installation must grant **Variables: read**. In direct-App mode, the flag makes the runtime token request `actions_variables: read` in addition to its existing permissions. It never requests write, even when the installation grants write. In broker mode, the flag requires a controller enrollment with a repository-settings profile (below). Without the flag, the controller requests no new permission and logs `repository-settings state=disabled`.

### Broker enrollments

The existing broker profiles (`polling`, `webhook`, `polling-clone`, `webhook-clone`) are frozen and grant no Variables access. The controller does not widen them: with a v1 or v2 enrollment file it refuses `--repository-settings` before contacting the broker, and the broker refuses `actions_variables` for those profiles.

To read settings through the broker, choose a repository-settings profile at enrollment ([ADR 0008](adr/0008-add-an-explicit-repository-settings-runtime-profile.md)). These profiles are `polling-settings`, `webhook-settings`, `polling-clone-settings` and `webhook-clone-settings`. Each grants exactly its frozen counterpart's permissions plus `actions_variables: read`, under the policy `repository-actions-settings-approval-v3`. They require Actions-variable approval (setup v3); legacy Agent-variable approval is refused before any work. The enrollment file, leases and runtime routes are protocol v3, and the controller consumes a v3 file only with `--repository-settings`. See [the consumer contract](broker-protocol.md#explicit-repository-settings-consumer-v3).

- **Guided installer.** Add `-RepositorySettings` to `./install.ps1` for a fresh instance. The installer selects the matching `*-settings` profile for the chosen mode and clone choice, and it configures the runtime with `--repository-settings`.
- **Controller enrollment.** Pass `-EnrollmentProfile polling-settings` (or another settings profile) to `scripts/enroll-controller.ps1`, then start the controller or `repoagent configure` with `--repository-settings`. A container instance keeps this choice for its lifetime (`REPOAGENT_REPOSITORY_SETTINGS=true` for externally supplied enrollments).

An existing broker-enrolled controller is never upgraded in place. To move it to a settings profile:

1. Revoke its current approval (`./install.ps1 -Action Revoke` with the saved instance, or `scripts/revoke-approval.ps1`).
2. Wait until the broker has closed the grant and its outstanding tokens have settled. One owner gate serves each repository, so a new enrollment is refused (`409`) until then.
3. Enroll afresh in a new state directory with a settings profile (`-RepositorySettings` for a new installer instance). The new controller gets a new ID and a new approval marker that names the settings profile.

Rollback is the same procedure in reverse: revoke the settings grant, wait for settlement, and enroll a frozen profile in a new state directory (omit `-RepositorySettings`). A broker build without runtime v3 refuses settings profiles at setup (`400`) and answers the v3 runtime routes with `404`, so the controller fails closed rather than receiving a narrower or wider token.

A repository-settings profile token is read-only for variables, so the controller still cannot write or restore its own approval. It can read every direct repository Actions variable, however, including the nonsecret approval marker. Keep secrets out of repository variables.

The controller reads each supported variable at startup, including under `--check-auth`. It reads them again a minute after the agent runtime starts, then a minute after each refresh finishes, with one `GET` per catalogue entry. A refresh that changed a setting finishes only after a running observation or dispatch does, so the next refresh can start later. A change in a setting's state, value or reason is logged once:

```text
repository-setting name=REPOAGENT_MAX_ACTIVE_SESSIONS state=configured value=4
repository-setting name=REPOAGENT_MAX_ACTIVE_SESSIONS state=invalid value=4 reason="must be an integer from 1 to 64"
repository-setting name=REPOAGENT_MAX_ACTIVE_SESSIONS state=read-failed value=4 reason="HTTP 503"
repository-setting name=REPOAGENT_MAX_ACTIVE_SESSIONS state=absent
```

`value` is the effective value. HTTP 404 confirms absence, and then no repository value applies, so the consuming feature uses its documented default. An invalid value or a failed read keeps the last effective value, and is never reported as absence. A failed read does not stop the controller; the next refresh retries it. Invalid raw values are not echoed.

Each refresh records every setting's state, effective value and reason in `<state>.settings.json` beside the controller state. A restarted controller starts from the effective values recorded there, so an invalid value or a failed read at startup keeps the value an earlier lifetime read. `status` reports the same record under `settings`, and the [dashboard](controller-operations.md#discussion-status-dashboard) lists every invalid or unreadable setting with the value that still applies, until a read succeeds.

## Active-session limit

`REPOAGENT_MAX_ACTIVE_SESSIONS` bounds how many native Discussion turns one controller runs at the same time. A Discussion holds a slot from native acceptance of its turn until the native reports that turn completed or failed; a native `active` status report also holds one. Stored sessions that are idle, not loaded or failed hold no slot, however many there are.

At the limit, newly observed input waits in the controller, as it does for a busy session. It shows as `waiting` in `status`, gets no EYES, and the observation logs `input-waiting ... active=N limit=M`. When a turn finishes, waiting Discussions are dispatched again. `status` reports running turns as `work.running` against the effective limit `work.limit`, and the [dashboard](controller-operations.md#discussion-status-dashboard) shows that count and lists such input as waiting for a free active-session slot.

- **Default.** `4` applies whenever no repository value is effective: the variable is absent, the controller runs without `--repository-settings` (including every broker enrollment with a frozen profile), or the value is invalid or unreadable and no controller lifetime has read a valid one. `status` and the dashboard show that problem.
- **Refresh.** A change applies when a settings refresh reads it, normally within a minute. Raising the limit dispatches waiting input right after that refresh. Lowering it never stops or steers a running turn; new turns wait until running turns fall below the new limit. An invalid value or a failed read keeps the last effective limit.
- **Order.** There is no fairness guarantee. When a slot frees, the controller tries the oldest waiting input first, but an observation that finds a free slot can dispatch its own Discussion first. Each Discussion still runs at most one turn at a time.

## Category enrollment

`REPOAGENT_ENROLLMENT_CATEGORIES` selects Discussion categories whose new Discussions start their agent session without a mention. Mentions and `--enroll` URLs keep working in every category. Each Discussion still has one agent session; labels never enroll a Discussion.

**Value format.** List 1 to 25 category names, separated by commas, as they appear in the repository's Discussion categories. Matching ignores letter case and the spaces around each name. A name has at most 100 characters, no control characters, and appears once. A category whose name contains a comma cannot be selected. The controller and the settings command use the normalized form, for example `Agents, Q&A`. In PowerShell, quote the whole argument, because an unquoted comma makes an array:

```powershell
./install.ps1 -Action Settings -Repository owner/repo -Setting 'REPOAGENT_ENROLLMENT_CATEGORIES=Agents, Q&A' -Apply
```

**New and preexisting Discussions.** For each selected category, the controller records in its state file when its settings read first saw that category selected. A Discussion in the category enrolls at its next observation (poll, webhook or reconciliation) when GitHub's creation time is at or after the recorded time. Its body and existing comments form the bootstrap input, as for a mention. A Discussion created earlier is preexisting and never enrolls by category, even if it moves into the category later; mention the App or pass its URL to `--enroll` instead. A Discussion created after the recorded time in another category enrolls when it moves into a selected category.

**Timing and changes.**

- A running controller normally reads a change within a minute. A Discussion created between the variable change and that read counts as preexisting, so wait for the `repository-setting name=REPOAGENT_ENROLLMENT_CATEGORIES state=configured` log line before relying on a new category.
- The recorded time survives restarts, so Discussions created while the controller is stopped enroll when it starts again. A category first selected while the controller is stopped is recorded when the controller next starts.
- Removing a category stops new category enrollment from it and forgets its recorded time. Discussions already enrolled keep their sessions. Selecting the category again starts afresh, so Discussions created while it was unselected stay preexisting.
- Clearing the variable, or running without `--repository-settings`, removes every selection. An invalid value or a failed read keeps the previous selection and its recorded times, also across a restart.

**Never enrolled by category.** Discussions authored by the controller's own App, such as its status dashboard, never enroll by category. This prevents a controller-written Discussion from starting agent work.

Category enrollment needs no permission beyond Discussions read and the Variables read that settings already require. Broker-enrolled controllers with a frozen profile cannot read settings, so their Discussions enroll only by mention or URL; a controller with a [repository-settings profile](#broker-enrollments) can use categories.

## Model and reasoning selection

Every agent turn carries an explicit model and reasoning effort. Pinned Codex keeps a `turn/start` model or effort for later turns of the same thread, so the defaults are sent explicitly too; that is how removing a label restores them.

For each turn, the controller uses the Discussion's model label if it has one. Otherwise each part falls back separately: the repository default (`REPOAGENT_MODEL`, `REPOAGENT_REASONING`), then the native Codex configuration for the workspace (`model`, `model_reasoning_effort`, read through `config/read`), then the catalogue's default model and that model's default effort.

**Label grammar.** `repoagent:<model>:<reasoning>`, for example `repoagent:sol6.1:xhigh`. Every label whose name starts with `repoagent:` (any case) is a model label:

- `<model>` and `<reasoning>` are names of letters, digits, `.`, `_` or `-`, at most 64 characters. They are compared case-insensitively.
- `<model>` is a model ID from the account's native catalogue, or its alias. `gpt-<version>-<family>` has the alias `<family><version>`. For example, `gpt-6.1-sol` has the alias `sol6.1`. Other IDs, such as `gpt-5.5`, have no alias.
- `<reasoning>` must be one of the efforts the catalogue lists for that model.

**Validation.**

- The native catalogue is `model/list` without hidden models. It reflects the signed-in account, so syntax and availability are checked separately.
- One model label selects that Discussion's model and effort. Several model labels are a conflict.
- A conflict, a malformed label, an unavailable model or an unsupported effort is never substituted. Neither is an invalid or unreadable `REPOAGENT_MODEL` or `REPOAGENT_REASONING` when no earlier value is known: the native configuration does not stand in for it. This applies equally to a default. Without validation, pinned Codex would clamp an unsupported effort, for example `ultra` to `max`. Instead, the Discussion's input waits without taking an [active-session](#active-session-limit) slot, and a `selection-error` names the label or setting, plus the available models or supported efforts. Status lists it under `failures`. The next observation after a fix delivers the waiting input.
- Each accepted turn logs its selection: `native-accepted session=… model=gpt-6-luna effort=xhigh selection=label turn=…`.

**Label changes.** The controller reads a Discussion's labels each time it observes that Discussion, and keeps them in its state. A label change is not input: it never starts or steers a turn, and it takes effect on the next turn. If labels cannot be read, `label-read-error` is logged once and the last observed labels still apply.

Aliases of the pinned Codex 0.163.0 catalogue without an account (`model/list`, hidden models excluded):

| Alias | Model ID | Supported reasoning | Default |
| --- | --- | --- | --- |
| `sol6.1` | `gpt-6.1-sol` (catalogue default) | low, medium, high, xhigh, max, ultra | low |
| `astra6` | `gpt-6-astra` | low, medium, high, xhigh, max, ultra | low |
| `sol6` | `gpt-6-sol` | low, medium, high, xhigh, max, ultra | medium |
| `luna6` | `gpt-6-luna` | low, medium, high, xhigh, max | medium |
| `sol5.6` | `gpt-5.6-sol` | low, medium, high, xhigh, max, ultra | low |
| `terra5.6` | `gpt-5.6-terra` | low, medium, high, xhigh, max, ultra | medium |
| `luna5.6` | `gpt-5.6-luna` | low, medium, high, xhigh, max | medium |
| none | `gpt-5.5` | low, medium, high, xhigh | medium |

A signed-in account can offer a different list; error messages always show the current one.

## Adding a setting

1. Add an entry to the catalogue. It has `name` (`REPOAGENT_` plus uppercase letters, digits or `_`, and not a `reserved` name), `description` and `type`.
   - `integer` also has `minimum` and `maximum`.
   - `category-names` also has `minimum` and `maximum`, which bound the name count.
   - `token` is a lowercase name of letters, digits, `.`, `_` or `-`, at most 64 characters, and has no further fields.
   - A new type needs matching validation in `RepositorySetting.Normalize` and in `Get-Normalized` in [`scripts/repository-settings.ps1`](../scripts/repository-settings.ps1).
2. Read the effective value from the `RepositorySettings` instance that `BridgeCommand` creates, as `settings["REPOAGENT_…"].Value`. A `null` value means the feature's own default applies. The value changes at most once per refresh, so read it at each decision point instead of caching it.
3. Document the default and when a change takes effect. Then add a row to the table above.
