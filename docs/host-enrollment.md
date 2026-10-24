# Host enrollment for the shared GitHub App

Run the installer from a trusted checkout. Enrollment requires **PowerShell 7.4 or later**, an authenticated host **GitHub CLI (`gh`)**, and the broker operator's canonical HTTPS origin. The host writes one direct repository Actions variable ([ADR 0007](adr/0007-use-repository-actions-variables-for-approval.md)). It creates no deployment Environment and performs no broker browser sign-in. Its GitHub credential stays in gh, outside the broker, controller and image.

Public `-Action Enroll -Apply` also requires the Docker CLI and a working local
engine to check the selected instance’s disable state. Enroll preview and
standalone `-Action Revoke` do not require Docker.

```powershell
# Read-only preview: no GitHub/broker writes or private state allocation.
./install.ps1 -Action Enroll -Repository owner/project `
  -BrokerOrigin https://credentials.example.org -ApprovalVariable TEAM_CONTROLLER_APPROVAL `
  -StateDirectory "$HOME/project-enrollment" -NonInteractive

# Apply the plan explicitly.
./install.ps1 -Action Enroll -Repository owner/project `
  -BrokerOrigin https://credentials.example.org -ApprovalVariable TEAM_CONTROLLER_APPROVAL `
  -StateDirectory "$HOME/project-enrollment" -Apply -NonInteractive

# Resume the same setup using its saved coordinates and private possession.
./install.ps1 -Action Enroll -StateDirectory "$HOME/project-enrollment" -Apply -NonInteractive
```

The state directory's parent must exist. Interactive mode prompts for missing repository/origin and the variable name; noninteractive mode never prompts. The variable defaults to `REPOAGENT_APPROVAL`. That name belongs to approval: generic `REPOAGENT_` settings tooling must never write or restore it. The name is normalized to uppercase and accepts at most100 ASCII letters/digits/underscores, with no initial digit or `GITHUB_` prefix. An Actions-variable approval may use the `REPOAGENT_` prefix only as a name the [settings catalogue](repository-settings.md) reserves for approval (`REPOAGENT_APPROVAL`), so settings changes can never overwrite it. The installer refuses other `REPOAGENT_` names before any GitHub or broker work, and the broker answers `400` for them at setup v3. Legacy Agent variables are a separate store and keep the general rule. It is unrelated to Azure resource names. `-EnrollmentProfile polling` grants Discussions write; explicit `webhook` adds repository_hooks write. The frozen `polling-clone` and `webhook-clone` profiles add Contents read. The explicit [repository-settings profiles](repository-settings.md#broker-enrollments) (`polling-settings`, `webhook-settings`, `polling-clone-settings`, `webhook-clone-settings`) add only `actions_variables: read` to their frozen counterpart, need Actions-variable approval and issue a runtime v3 enrollment ([ADR 0008](adr/0008-add-an-explicit-repository-settings-runtime-profile.md)). The shared App is `repo-agent-connector`, ID5387625. Before v3 verification can succeed, it needs Repository permission **Variables: Read** (`actions_variables`), accepted by the installation.

> [!WARNING]
> Repository Actions variables must never hold secrets. A controller with a repository-settings profile, and so the agent it runs, can read every direct repository Actions variable, not only `REPOAGENT_*` settings ([ADR 0008](adr/0008-add-an-explicit-repository-settings-runtime-profile.md)).

`-ApprovalLocation AgentVariable` explicitly selects legacy repository Agent-variable setup v2, whose default name is `APPROVED_CONTROLLER`. Use it only for rollback, or where the broker or App installation cannot yet verify Actions variables. A saved state directory keeps its own location. Naming a different location for it stops before any broker or GitHub work. See [legacy approvals, migration and rollback](auth-repository-approval.md#legacy-approvals-migration-and-rollback).

Direct-App `Install` and native coding-agent sign-in/history remain unchanged. This action obtains enrollment only; #39 owns guided runtime/Azure setup and uninstall. Docker Desktop delivery from Windows into the Linux runtime remains an integration gate: do not weaken private-file permissions to mount a Windows file. The unchanged controller consumer runs on Linux/macOS, not Windows. Azure CLI is required only for later Azure provisioning.

## GitHub authority and bounded setup

Inspect the host account with `gh auth status --hostname github.com`. Fine-grained host credentials need **Variables read/write** on the repository (**Agent variables read/write** for the legacy location); classic OAuth/PAT credentials use `repo` scope. No Administration or deployment-Environment permission is required for this flow. GitHub authorizes the writer; the broker neither authenticates nor tracks a named human administrator. A successful host read does not establish write authority, private-repository availability or App-installation grants. See [Actions-variable API permissions](https://docs.github.com/en/rest/actions/variables).

The host validates the repository's numeric ID/name and the chosen broker origin/App/profile. The broker independently validates the installation and exact repository through its App JWT and scoped verifier. The host pins that returned installation/device/challenge/epoch binding; ordinary human gh credentials cannot perform the App-JWT lookup. Runtime tokens never receive variables write access, so a controller cannot write or restore its approval. Only the repository-settings profiles add variables read, which also exposes the nonsecret marker. The nonsecret marker is visible to repository workflows as `vars.<NAME>` (and, for legacy Agent variables, in Copilot's environment). It contains no recovery or device credential, and the device secret is never stored in a repository variable.

Explicit apply starts one v3 pending enrollment (v2 for the legacy location) after validating repository access and direct variable API availability. The fresh marker therefore appears only after start. Its exact marker version, kind and policy and its full binding are checked before variable creation. Existing matching approval is reused without PATCH. Conflicts stop; there is no takeover, migration, automatic repair or restoration.

Only `/repos/{owner}/{repo}/actions/variables` authorizes this flow (`/repos/{owner}/{repo}/agents/variables` for the legacy location). Organization/inherited variables and the other location cannot substitute. A selected GET can establish the exact observed value. An isolated 404 cannot establish absence, because GitHub also answers 404 without read access: creation requires a well-formed listing page, read after the 404, that proves host access and does not show the selected name. The broker verifies the same way. The number of variables never limits enrollment or broker verification. No snapshot or immutable variable-ID guarantee is claimed.

A variable POST is attempted at most once per invocation, with its attempt recorded beforehand. A lost response stays unresolved. Rerun with the same state to inspect it; a previously attempted or observed marker that disappeared is never automatically restored. Enrollment limits host GitHub work to12 calls, individual transport calls to15seconds, and polling to the original pending deadline with at leastfive seconds between expensive broker operations.

Temporary broker HTTP429 responses during start, completion or acknowledgment retry the same private request at most twice. The host waits for `Retry-After` (at leastfive seconds), or sixty seconds when the broker supplies no header. Invalid delays, delays over120seconds, exhausted retries or waits that would exceed the original pending deadline or fifteen-minute host work budget stop with private state retained. Other failures still require inspection and resuming the same state. Public approval writes are never automatically retried.

## Private state and recovery

The directory must be owned by the current user: Linux0700 with files0600, or Windows protected current-user-only DACLs. Unsafe ownership/modes, links and reparse points fail closed. The host uses exclusive temporary files, flushed contents and an atomic flushed rename, then reopens the private enrollment before acknowledging it. Use a local filesystem supporting these operations.

- `approval.json` is nonsecret metadata: host format and setupVersion 3 with approval kind `repository-actions-variable` (format and setupVersion 2 with `repository-agent-variable` for the legacy location), repository/variable/profile, exact marker/hash and operation progress. Retain a separate copy for credential-free revoke.
- `recovery.json` is private. It contains the same explicit setup discriminators, immutable request, random256-bit recovery credential, completion receipt and ack progress. It is saved before the first broker request and must not enter the controller.
- `enrollment.json` contains exactly the frozen **v1** consumer fields (v2 with `profile` and `policyVersion` for clone profiles; v3 with the same fields for repository-settings profiles), including the device secret. Its protocolVersion does not select a setup backend. Never log, commit or include it in an image. To run a container from it without copying it into an enrollment volume, inject it at startup as described in [external controller credentials](container-setup.md#external-controller-credentials); this directory remains its source.

Setup uses `/v3/enrollments`, its status/completion routes, and `/v3/devices/{deviceId}/acknowledgment` (the `/v2` equivalents for the legacy location). Lost start reuses the same request/idempotency ID; lost completion recovers the same authorized envelope. CompletionId is persisted before the device file, and both precede fresh acknowledgment. Lost ack retries the saved device/completion pair even if the original pending deadline has elapsed and activation may already have committed; the broker remains authoritative. Confirmed activation clears local recovery authority. No rerun extends pending deadlines or reopens a closed grant. HTTPS responses cannot redirect credentials.

## Revoke and prototype compatibility

Revoke uses the saved approval file's location. Without saved state, it defaults to the **Actions-variable** coordinate; add `-ApprovalLocation AgentVariable` to revoke a legacy Agent-variable grant. Use a copied nonsecret approval file or explicit repository/variable plus the expected exact-value SHA256 and optional repository ID. It does not require the original machine or device secret.

```powershell
./install.ps1 -Action Revoke -ApprovalStatePath ./approval.json -NonInteractive
./install.ps1 -Action Revoke -ApprovalStatePath ./approval.json -Apply -NonInteractive

# Optionally attempt broker self-revocation when private enrollment remains.
./install.ps1 -Action Revoke -ApprovalStatePath "$HOME/project-enrollment/approval.json" `
  -EnrollmentFile "$HOME/project-enrollment/enrollment.json" -Apply -NonInteractive
```

Revoke validates repository binding and the exact selected value immediately before at mostone DELETE. An independent writer can replace the value between read and DELETE, causing withdrawal of a newer approval and temporary outage; manual inspection/reapproval after old authority is resolved is the accepted recovery. There are no CAS, locks or restoration. Reported `githubMarker`, `brokerClosure` and `outstandingTokens` are separate: removal does not prove broker closure or settle outstanding tokens. Optional self-revoke uses the saved trusted origin/matching device and the unchanged `DELETE /v1/devices/{deviceId}` (`/v2` for clone profiles). Its failure leaves closure unconfirmed. Runtime/data/App-installation teardown belongs to #39.

The unmerged Environment host prototype's format1 state cannot resume through repository-variable setup. It is recognized only as the complete historical schema, retained unchanged, and refused before remote work. Settle/revoke the old enrollment separately and choose a new state directory for fresh enrollment; the broker's shared owner barrier still prevents replacement of unsettled authority.

To move an Agent-variable (format 2) enrollment to Actions variables:

1. Revoke it with its saved `approval.json` and, if available, `-EnrollmentFile`.
2. Wait for broker closure and token settlement.
3. Enroll in a new state directory.

A v3 completion is refused while the old owner is still active. Nothing migrates a live marker automatically.

There is one installer and no legacy enrollment wizard. Narrow legacy **revoke only** remains: supply its complete format1 `approval.json`, or explicitly use `-GitHubEnvironment <name>` with the repository, variable and expected hash/ID. That explicit legacy path retains its255-character variable limit. No Environment is defaulted; repository-variable state mixed with Environment arguments, or `-ApprovalLocation` combined with `-GitHubEnvironment`, is rejected. This default differs from the old prototype, so preview the coordinate before applying.

## Verification boundary

Required evidence includes actual pwsh/gh process and HTTPS journeys, native Windows DACL/atomic-file execution, the unchanged Linux consumer and an assembled real v3 broker/Azurite/DataProtection journey with controlled external GitHub/signing transports. Linux preview success alone is not conformance. Live selected-repository host-write/App-read/runtime-denial, private-repository availability, hosted service readiness and Windows Docker Desktop delivery remain separate gates.
