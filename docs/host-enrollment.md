# Host enrollment for the shared GitHub App

Run the installer from a trusted checkout. Enrollment requires **PowerShell 7.4 or later**, an authenticated host **GitHub CLI (`gh`)**, and the broker operator's canonical HTTPS origin. The host writes one direct repository Agent variable; it creates no deployment Environment and performs no broker browser sign-in. Its GitHub credential stays in gh, outside the broker, controller and image.

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

The state directory's parent must exist. Interactive mode prompts for missing repository/origin and the variable name; noninteractive mode never prompts. The variable defaults to `APPROVED_CONTROLLER`, is normalized to uppercase and accepts at most100 ASCII letters/digits/underscores, with no initial digit or `GITHUB_` prefix. It is unrelated to Azure resource names. `-EnrollmentProfile polling` grants Discussions write; explicit `webhook` adds repository_hooks write. The shared App is `repo-agent-connector`, ID5387625.

Direct-App `Install` and native coding-agent sign-in/history remain unchanged. This action obtains enrollment only; #39 owns guided runtime/Azure setup and uninstall. Docker Desktop delivery from Windows into the Linux runtime remains an integration gate: do not weaken private-file permissions to mount a Windows file. The unchanged controller consumer runs on Linux/macOS, not Windows. Azure CLI is required only for later Azure provisioning.

## GitHub authority and bounded setup

Inspect the host account with `gh auth status --hostname github.com`. Fine-grained host credentials need **Agent variables read/write** on the repository; classic OAuth/PAT credentials use `repo` scope. No Administration or deployment-Environment permission is required for this flow. GitHub authorizes the writer; the broker neither authenticates nor tracks a named human administrator. A successful host read does not establish write authority, private-repository availability or App-installation grants. See [Agent-variable API permissions](https://docs.github.com/en/rest/agents/variables).

The host validates the repository's numeric ID/name and the chosen broker origin/App/profile. The broker independently validates the installation and exact repository through its App JWT and scoped verifier. The host pins that returned installation/device/challenge/epoch binding; ordinary human gh credentials cannot perform the App-JWT lookup. Runtime tokens receive neither approval-variable read nor write access. The nonsecret marker may be exposed in Copilot's environment; it contains no recovery or device credential.

Explicit apply starts one v2 pending enrollment after validating repository access and direct Agent-variable API availability. The fresh marker therefore appears only after start. Its exact marker2/kind/policy and full binding are checked before variable creation. Existing matching approval is reused without PATCH. Conflicts stop; there is no takeover, migration, automatic repair or restoration.

Only `/repos/{owner}/{repo}/agents/variables` authorizes this flow; organization/inherited variables cannot substitute. A selected GET can establish the exact observed value. An isolated404 cannot establish absence: creation requires a valid complete single listing containing at most30 entries. Multi-page nonappearance is unverifiable because concurrent changes can move an unchanged marker between pages. Creation stops in that case. The broker can observe selected values within at most60 variables, but disappearance above30 may require manual broker closure. No snapshot or immutable variable-ID guarantee is claimed.

A variable POST is attempted at most once per invocation, with its attempt recorded beforehand. A lost response stays unresolved. Rerun with the same state to inspect it; a previously attempted or observed marker that disappeared is never automatically restored. Enrollment limits host GitHub work to12 calls, individual transport calls to15seconds, and polling to the original pending deadline with at leastfive seconds between expensive broker operations.

Temporary broker HTTP429 responses during start, completion or acknowledgment retry the same private request at most twice. The host waits for `Retry-After` (at leastfive seconds), or sixty seconds when the broker supplies no header. Invalid delays, delays over120seconds, exhausted retries or waits that would exceed the original pending deadline or fifteen-minute host work budget stop with private state retained. Other failures still require inspection and resuming the same state. Public approval writes are never automatically retried.

## Private state and recovery

The directory must be owned by the current user: Linux0700 with files0600, or Windows protected current-user-only DACLs. Unsafe ownership/modes, links and reparse points fail closed. The host uses exclusive temporary files, flushed contents and an atomic flushed rename, then reopens the private enrollment before acknowledging it. Use a local filesystem supporting these operations.

- `approval.json` is nonsecret metadata: host format2, setupVersion2, approval kind `repository-agent-variable`, repository/variable/profile, exact marker/hash and operation progress. Retain a separate copy for credential-free revoke.
- `recovery.json` is private. It contains the same explicit setup discriminators, immutable request, random256-bit recovery credential, completion receipt and ack progress. It is saved before the first broker request and must not enter the controller.
- `enrollment.json` contains exactly the frozen **v1** consumer fields, including the device secret. Its protocolVersion1 does not select a setup backend. Never log, commit or include it in an image.

Setup uses `/v2/enrollments`, its status/completion routes, and `/v2/devices/{deviceId}/acknowledgment`. Lost start reuses the same request/idempotency ID; lost completion recovers the same authorized envelope. CompletionId is persisted before the device file, and both precede fresh acknowledgment. Lost ack retries the saved device/completion pair even if the original pending deadline has elapsed and activation may already have committed; the broker remains authoritative. Confirmed activation clears local recovery authority. No rerun extends pending deadlines or reopens a closed grant. HTTPS responses cannot redirect credentials.

## Revoke and prototype compatibility

Revoke defaults to the **Agent-variable** coordinate. Use a copied nonsecret approval file or explicit repository/variable plus the expected exact-value SHA256 and optional repository ID. It does not require the original machine or device secret.

```powershell
./install.ps1 -Action Revoke -ApprovalStatePath ./approval.json -NonInteractive
./install.ps1 -Action Revoke -ApprovalStatePath ./approval.json -Apply -NonInteractive

# Optionally attempt broker self-revocation when private enrollment remains.
./install.ps1 -Action Revoke -ApprovalStatePath "$HOME/project-enrollment/approval.json" `
  -EnrollmentFile "$HOME/project-enrollment/enrollment.json" -Apply -NonInteractive
```

Revoke validates repository binding and the exact selected value immediately before at mostone DELETE. An independent writer can replace the value between read and DELETE, causing withdrawal of a newer approval and temporary outage; manual inspection/reapproval after old authority is resolved is the accepted recovery. There are no CAS, locks or restoration. Reported `githubMarker`, `brokerClosure` and `outstandingTokens` are separate: removal does not prove broker closure or settle outstanding tokens. Optional self-revoke uses the saved trusted origin/matching device and the unchanged `DELETE /v1/devices/{deviceId}`. Its failure leaves closure unconfirmed. Runtime/data/App-installation teardown belongs to #39.

The unmerged Environment host prototype's format1 state cannot resume through Agent setup. It is recognized only as the complete historical schema, retained unchanged, and refused before remote work. Settle/revoke the old enrollment separately and choose a new state directory for fresh Agent enrollment; the broker's shared owner barrier still prevents replacement of unsettled authority.

There is one installer and no legacy enrollment wizard. Narrow legacy **revoke only** remains: supply its complete format1 `approval.json`, or explicitly use `-GitHubEnvironment <name>` with the repository, variable and expected hash/ID. That explicit legacy path retains its255-character variable limit. No Environment is defaulted; Agent state mixed with Environment arguments is rejected. This default differs from the old prototype, so preview the coordinate before applying.

## Verification boundary

Required evidence includes actual pwsh/gh process and HTTPS journeys, native Windows DACL/atomic-file execution, the unchanged Linux consumer and an assembled real v2 broker/Azurite/DataProtection journey with controlled external GitHub/signing transports. Linux preview success alone is not conformance. Live selected-repository host-write/App-read/runtime-denial, private-repository availability, hosted service readiness and Windows Docker Desktop delivery remain separate gates.
