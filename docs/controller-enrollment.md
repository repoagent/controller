# Controller enrollment for the shared GitHub App

Run the installer from a trusted checkout. Fresh enrollment uses **setup v4**, **PowerShell 7.4 or later**, an authenticated host **GitHub CLI (`gh`)**, and the broker operator's canonical HTTPS origin. The host writes one direct repository Agent variable ([ADR 0012](adr/0012-use-repository-agent-variables-for-settings-and-approval.md)). It creates no deployment Environment and performs no broker browser sign-in. Its GitHub credential stays in gh, outside the broker, controller and image.

Public `-Action Enroll -Apply` also requires the Docker CLI and a working local
engine to check the selected instance’s disable state. Enroll preview and
standalone `-Action Revoke` do not require Docker.

## Canonical names and upgrade order

Deploy the upgraded broker before selecting `-ControllerEnrollmentVersion 5` on `install.ps1` or `scripts/enroll-controller.ps1`. This explicit option creates a fresh setup v5 enrollment with `enrollmentId` and `controllerSecret` in its private file and `enrollmentId` in its approval marker. The controller renews through `/v{runtimeVersion}/controller-enrollments/{enrollmentId}/github-token` and self-revokes through the same identity route. Runtime versions 1–3 and their permission profiles are unchanged. Setup v5 has distinct approval policies and uses repository Agent variables.

The default remains setup v4 during the compatibility window so the installer continues to work with already deployed brokers. There is no automatic downgrade or replacement after a failed or ambiguous setup request. A saved request always resumes its original setup version; choosing a conflicting version fails before broker work. Use a new state directory for a deliberately fresh enrollment after revocation and settlement.

Existing private enrollment files and raw approval markers retain their exact frozen formats. Legacy `/devices/...` and `/installation-token` routes remain supported, and old approvals renew with their original permissions. The owned document readers accept either spelling, reject ambiguous alias pairs, and keep provider JSON separate. Recovery journals use `setupSecret`, accepting historical `secret` on read. Do not edit credentials or markers to rename their fields manually.

In code, the public domain names are `ControllerSecret`, `ControllerSelfRevocation`, `GitHubTokenIssuer`, `AgentModel`, `RepositorySettingsRefresh` and `BaselineReleaseCheck`. Agent log events use `agent-accepted`, `agent-event`, `agent-disconnected` and `agent-request-blocked`; update operator filters accordingly. The setup helper is `controller-enrollment.psm1`. Azure resource, assembly/project and `service/auth`/`infra/auth` artifact names are retained. Legacy field literals, durable row keys and frozen compatibility fixtures deliberately remain at the storage and protocol boundaries; changing them would invalidate live enrollments.

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

The state directory's parent must exist. Interactive mode prompts for missing repository/origin and the variable name; noninteractive mode never prompts. The variable defaults to `REPOAGENT_APPROVAL`. That name belongs to approval: generic `REPOAGENT_` settings tooling must never write or restore it. The name is normalized to uppercase and accepts at most100 ASCII letters/digits/underscores, with no initial digit or `GITHUB_` prefix. Fresh approval may use the `REPOAGENT_` prefix only as a name the [settings catalogue](repository-settings.md) reserves for approval (`REPOAGENT_APPROVAL`). The installer refuses other `REPOAGENT_` names before remote work; legacy setup v2 retains its original name grammar. `-EnrollmentProfile polling` grants Discussions write; explicit `webhook` adds repository_hooks write. The frozen clone profiles add Contents read. The four explicit [repository-settings profiles](repository-settings.md#broker-enrollments) add only `agent_variables: read` to their frozen counterpart, use policy `repository-agent-settings-approval-v4`, and issue a runtime v3 enrollment. Frozen profiles never gain variables access. The shared App is `repo-agent-connector`, ID5387625. It already declares **Agent variables: Read**, but each selected installation's acceptance still needs verification.

> [!WARNING]
> Repository Agent variables must never hold secrets. A controller with a repository-settings profile, and so the agent it runs, can read every direct repository Agent variable, including approval. GitHub also exposes Agent variables to Copilot cloud agent sessions ([ADR 0012](adr/0012-use-repository-agent-variables-for-settings-and-approval.md)).

`-ApprovalLocation AgentVariable` selects the fresh default, setup v4. Fresh setup v2/v3 is no longer offered. A saved state directory keeps its exact setup version and location; v2 and v4 share the Agent store but never share policies. Existing v2/v3 recovery journals can resume only their original request. Saved approval metadata without its original recovery journal, or a conflicting location, stops before remote work. See [legacy approvals, migration and rollback](auth-repository-approval.md#legacy-approvals-migration-and-rollback).

Direct-App `Install` and agent sign-in/history remain unchanged. This action obtains enrollment only; see [local installation](local-installation.md) for runtime setup. Docker Desktop delivery from Windows into the Linux runtime remains an integration gate: do not weaken private-file permissions to mount a Windows file. The controller consumer runs on Linux/macOS, not Windows. Hosting lifecycle remains outside the controller.

## GitHub authority and bounded setup

Inspect the host account with `gh auth status --hostname github.com`. Fine-grained host credentials need repository **Agent variables read/write** (**Variables read/write** for legacy Actions-variable revocation); classic OAuth/PAT credentials use `repo` scope. No Administration or deployment-Environment permission is required for this flow. GitHub authorizes the writer; the broker neither authenticates nor tracks a named human administrator. A successful host read does not establish write authority, private-repository availability or App-installation grants. See [Agent-variable API permissions](https://docs.github.com/en/rest/agents/variables).

The host validates the repository's numeric ID/name and the chosen broker origin/App/profile. The broker independently validates the installation and exact repository through its App JWT and scoped verifier. The host pins that returned installation/enrollment/challenge/epoch binding; ordinary human gh credentials cannot perform the App-JWT lookup. Runtime tokens never receive variables write access, so a controller cannot write or restore its approval. Only the repository-settings profiles add variables read, which also exposes the nonsecret marker. Agent variables reach Copilot's environment; legacy Actions variables reach workflows as `vars.<NAME>`. The marker contains no setup secret or controller secret, and the controller secret is never stored in a repository variable.

Explicit apply starts one v4 pending enrollment after validating repository access and direct variable API availability. The fresh marker therefore appears only after start. Its exact marker version, kind and policy and its full binding are checked before variable creation. Existing matching approval is reused without PATCH. Conflicts stop; there is no takeover, migration, automatic repair or restoration. A broker without v4 fails closed; the host never retries through v2/v3.

Only `/repos/{owner}/{repo}/agents/variables` authorizes v4 (and legacy v2); saved v3 uses only `actions/variables`. Organization/inherited variables and the other location cannot substitute. A selected GET can establish the exact observed value. An isolated 404 cannot establish absence, because GitHub also answers 404 without read access: creation requires a well-formed listing page, read after the 404, that proves host access and does not show the selected name. The broker verifies the same way. The number of variables never limits enrollment or broker verification. No snapshot or immutable variable-ID guarantee is claimed.

A variable POST is attempted at most once per invocation, with its attempt recorded beforehand. A lost response stays unresolved. Rerun with the same state to inspect it; a previously attempted or observed marker that disappeared is never automatically restored. Enrollment limits host GitHub work to12 calls, individual transport calls to15seconds, and polling to the original pending deadline with at leastfive seconds between expensive broker operations.

Temporary broker HTTP429 responses during start, completion or acknowledgment retry the same private request at most twice. The host waits for `Retry-After` (at leastfive seconds), or sixty seconds when the broker supplies no header. Invalid delays, delays over120seconds, exhausted retries or waits that would exceed the original pending deadline or fifteen-minute host work budget stop with private state retained. Other failures still require inspection and resuming the same state. Public approval writes are never automatically retried.

## Private state and recovery

The directory must be owned by the current user: Linux0700 with files0600, or Windows protected current-user-only DACLs. Unsafe ownership/modes, links and reparse points fail closed. The host uses exclusive temporary files, flushed contents and an atomic flushed rename, then reopens the private enrollment before acknowledging it. Use a local filesystem supporting these operations.

- `approval.json` is nonsecret metadata: operator format and setupVersion 4 (or explicitly selected 5) with approval kind `repository-agent-variable`, repository/variable/profile, exact marker/hash and operation progress. Saved v2 has the same kind with version 2; saved v3 has kind `repository-actions-variable` and version 3. Retain a separate copy for credential-free revoke.
- `recovery.json` is private. It contains the same explicit setup discriminators, immutable request, random256-bit setup secret, completion receipt and ack progress. It is saved before the first broker request and must not enter the controller.
- `enrollment.json` contains exactly the frozen **v1** consumer fields (v2 with `profile` and `policyVersion` for clone profiles; v3 with the same fields for repository-settings profiles), including the controller secret. Its protocolVersion does not select a setup backend. Never log, commit or include it in an image. To run a container from it without copying it into an enrollment volume, inject it at startup as described in [external controller credentials](container-setup.md#external-controller-credentials); this directory remains its source.

Default setup uses `/v4/enrollments`, its status/completion routes, and the frozen `/v4/devices/{deviceId}/acknowledgment`. Explicit setup v5 uses `/v5/enrollments` and `/v5/controller-enrollments/{enrollmentId}/acknowledgment`. Saved recovery uses its original routes. Lost start reuses the same request/idempotency ID; lost completion recovers the same authorized envelope. CompletionId is persisted before the enrollment file, and both precede fresh acknowledgment. Lost ack retries the saved enrollment/completion pair even if the original pending deadline has elapsed and activation may already have committed; the broker remains authoritative. Confirmed activation clears local setup authority. No rerun extends pending deadlines or reopens a closed grant. HTTPS responses cannot redirect credentials.

## Revoke and prototype compatibility

Revoke uses the saved approval file's exact setup contract and location, including v2/v3. Without saved state, it defaults to the **Agent-variable** coordinate and `REPOAGENT_APPROVAL`; pass the original variable name for v2, or `-ApprovalLocation ActionsVariable` for v3. Use a copied nonsecret approval file or explicit repository/variable plus the expected exact-value SHA256 and optional repository ID. It does not require the original machine or controller secret.

```powershell
./install.ps1 -Action Revoke -ApprovalStatePath ./approval.json -NonInteractive
./install.ps1 -Action Revoke -ApprovalStatePath ./approval.json -Apply -NonInteractive

# Optionally attempt broker self-revocation when private enrollment remains.
./install.ps1 -Action Revoke -ApprovalStatePath "$HOME/project-enrollment/approval.json" `
  -EnrollmentFile "$HOME/project-enrollment/enrollment.json" -Apply -NonInteractive
```

Revoke validates repository binding and the exact selected value immediately before at mostone DELETE. An independent writer can replace the value between read and DELETE, causing withdrawal of a newer approval and temporary outage; manual inspection/reapproval after old authority is resolved is the accepted recovery. There are no CAS, locks or restoration. Reported `githubMarker`, `brokerClosure` and `outstandingTokens` are separate: removal does not prove broker closure or settle outstanding tokens. Optional self-revoke uses the saved trusted origin/matching enrollment and `DELETE /v1/devices/{deviceId}` (`/v2` for clone profiles, `/v3` for repository-settings profiles). Its failure leaves closure unconfirmed. If approval withdrawal succeeded but broker closure did not, rerun Apply with the same original approval and private enrollment files: a selected-variable HTTP 404 followed by verified access to the same variable store permits another self-revocation attempt without a GitHub write. Only a broker HTTP 204 confirms closure; outstanding tokens remain unsettled. Runtime/data/App-installation teardown belongs to #39.

The unmerged Environment host prototype's format1 state cannot resume through repository-variable setup. It is recognized only as the complete historical schema, retained unchanged, and refused before remote work. Settle/revoke the old enrollment separately and choose a new state directory for fresh enrollment; the broker's shared owner barrier still prevents replacement of unsettled authority.

To move an older enrollment to setup v4 (including v2 in the same Agent store):

1. Revoke it with its saved `approval.json` and, if available, `-EnrollmentFile`.
2. Establish broker closure, positive settlement of every old runtime/verifier capability and durable owner release. A device DELETE204 alone proves closure, not settlement. Do not repeatedly start replacement discovery while waiting; it can create another verifier with a lifetime beyond the pending deadline (see #249 below).
3. Enroll in a new state directory.

A v4 completion is refused while the old owner is active or unsettled. Nothing migrates a live marker automatically. New controllers refuse retired Actions-v3 clone/settings policies; their saved files remain usable for revocation. Rollback to a frozen profile also requires revocation, settlement and fresh setup v4, rather than a policy rewrite.

There is one installer and no legacy enrollment wizard. Narrow legacy **revoke only** remains: supply its complete format1 `approval.json`, or explicitly use `-GitHubEnvironment <name>` with the repository, variable and expected hash/ID. That explicit legacy path retains its255-character variable limit. No Environment is defaulted; repository-variable state mixed with Environment arguments, or `-ApprovalLocation` combined with `-GitHubEnvironment`, is rejected. This default differs from the old prototype, so preview the coordinate before applying.

## Verification boundary

ADR 0010 keeps installer verification manual. Release acceptance must bind the host checkout, broker source/image and controller source/image to an assembled v4 journey on a selected repository: host write, broker read, exact runtime grants, settings read, renewal and withdrawal. Frozen runtime tokens must lack variables access; settings runtime tokens may read approval but must never write or restore it. Actual HTTPS/provider behavior, private-repository availability, native Windows protected DACL/atomic-file execution and Docker Desktop private injection remain separate evidence. Linux previews and disposable transport fixtures do not establish those outcomes.

[#249](https://github.com/repoagent/controller/issues/249) remains open for broker settlement preflight before another verifier mint and assembled migration acceptance. On enrollment start, the exact `409` body `{"code":"enrollment_not_ready","retryAfterSeconds":60}` asks the operator to recheck the same private request after at least60 seconds; it never promises readiness. The exact `503` body `{"code":"enrollment_settlement_unconfirmed"}` asks for inspection with no safe-ready time. Both stop with state retained and no approval write. Empty `409` conflicts, unknown/malformed diagnostics and failures on other operations remain generic stop-and-inspect. No response automatically retries or selects another backend, directory or identity. These host tools cannot replace the broker gate: until the matching preflight is accepted and deployed, separately establish settlement and owner release before replacement.
