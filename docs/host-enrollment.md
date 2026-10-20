# Host enrollment for the shared GitHub App

Run these commands from a trusted checkout of this repository. Host enrollment requires **PowerShell 7.4 or later** and an authenticated host **GitHub CLI (`gh`)**. The host's GitHub identity creates repository-held approval; its credentials stay in gh and are never copied to the broker, controller, image or enrollment file. No broker browser sign-in or marker copying is involved.

The broker must already be deployed and configured with its canonical HTTPS origin. Source-level enrollment support does not establish hosted readiness. Use the origin supplied by the broker operator; responses cannot redirect credentials elsewhere.

```powershell
# Read-only preview; no GitHub or broker writes, and no private state is created.
./install.ps1 -Action Enroll -Repository owner/project `
  -BrokerOrigin https://credentials.example.org `
  -GitHubEnvironment 'controller/production' -ApprovalVariable CONTROLLER_APPROVAL `
  -StateDirectory "$HOME/project-enrollment" -NonInteractive

# Execute the same plan explicitly.
./install.ps1 -Action Enroll -Repository owner/project `
  -BrokerOrigin https://credentials.example.org `
  -GitHubEnvironment 'controller/production' -ApprovalVariable CONTROLLER_APPROVAL `
  -StateDirectory "$HOME/project-enrollment" -Apply -NonInteractive

# Resume with saved coordinates and profile. Do not delete recovery state.
./install.ps1 -Action Enroll -StateDirectory "$HOME/project-enrollment" -Apply -NonInteractive
```

The state directory's parent must exist. Without `-NonInteractive`, missing repository/origin and the two GitHub names are prompted. Environment defaults to `repoagent`; variable defaults to `APPROVED_CONTROLLER`. Both names persist independently of Azure environment/resource names. Variables are normalized to uppercase and enrollment accepts at most 100 characters; `GITHUB_` names are rejected. Environment names may contain spaces or slashes and are encoded as one path segment.

`-EnrollmentProfile polling` is the default, granting Discussions write. Select `webhook` explicitly to include repository_hooks write. The shared App is `repo-agent-connector`, ID `5387625`. Direct-App `Install` and native coding-agent sign-in/history are unchanged. This command only obtains enrollment; complete local/Azure runtime setup and uninstall are tracked in #39. Docker Desktop secret delivery from Windows into the Linux runtime still needs integration evidence. The unchanged controller consumer runs on Linux/macOS, not Windows. Do not relax its file permissions to mount a Windows file. Azure CLI is needed only for later Azure provisioning.

## GitHub access and apply

Use `gh auth status --hostname github.com` to inspect the host account separately. Environment variables require repository Environments read/write permission for fine-grained credentials; creating an Environment additionally requires Administration write. Classic OAuth/PAT credentials use `repo` scope. Private/internal repositories need a plan that supports Environments. Successful reads alone do not prove write permission or plan support. GitHub authorizes the variable writer; enrollment does not attest a particular human administrator. See [GitHub variable permissions](https://docs.github.com/en/rest/actions/variables#create-an-environment-variable) and [Environment permissions and availability](https://docs.github.com/en/rest/deployments/environments#create-or-update-an-environment).

Explicit apply may first create the Environment and then start one pending enrollment. Therefore the fresh server marker cannot appear in the read-only preview. Before writing the variable, the host validates its exact repository, App, installation, Environment numeric identity, profile and device binding against trusted setup intent. Existing Environments are reused without PUT. Existing matching variable values are reused without PATCH. Conflicts stop; there is no takeover, lock, lease or automatic repair.

A lost GitHub write response is unresolved. Rerun with the same state directory to inspect it. If a previous marker write was attempted and its value is absent, the client stops instead of restoring it. Independent writers can race GitHub reads and mutations; inspect and recover manually. No GitHub compare-and-swap guarantee is claimed.

## Private state and recovery

The selected directory is owner-only: 0700 on Linux, a protected current-user-only DACL on Windows. Files are 0600/current-user-only. Unsafe existing modes, owners, links or reparse points fail closed. Writes use a fresh private temporary file, flushed contents and an atomic, flushed rename before acknowledgment. Keep the directory on a local filesystem supporting these guarantees; failures stop enrollment.

- `approval.json` contains nonsecret selected coordinates, immutable IDs, exact public marker, fingerprint and write-attempt evidence. A copy can be retained independently for credential-free revoke.
- `recovery.json` privately stores the original request, random recovery credential, completion receipt and operation progress. It exists before the first broker start. Never publish or copy it into the controller.
- `enrollment.json` is exactly the frozen v1 private consumer document. It is saved and reopened before acknowledgment. It contains the device secret and must never be logged, committed or included in an image.

Reruns preserve the idempotency ID and original pending deadline. Completion and acknowledgment honor the broker's shared five-second work interval. A crash before local receipt recovers the same envelope; a crash after receipt retries with the saved device credential. A lost acknowledgment retries the same device/completion pair, including after the original pending deadline when activation may already have committed. Only the broker can confirm activation. Confirmed activation retires the local recovery credential. Expired/closed grants cannot be revived by restoring an old marker. Unknown outcomes remain unconfirmed, and unresolved old authority may require operator intervention.

## Revoke without the original machine

Copy only the nonsecret `approval.json`, or supply repository/Environment/variable and expected IDs/fingerprint explicitly. Existing revoke accepts variable names up to its original 255-character limit.

```powershell
./install.ps1 -Action Revoke -ApprovalStatePath ./approval.json -NonInteractive
./install.ps1 -Action Revoke -ApprovalStatePath ./approval.json -Apply -NonInteractive

# If the original private directory remains, also attempt device self-revocation.
./install.ps1 -Action Revoke -ApprovalStatePath "$HOME/project-enrollment/approval.json" `
  -EnrollmentFile "$HOME/project-enrollment/enrollment.json" -Apply -NonInteractive
```

Revoke independently reads repository and Environment IDs and the exact variable immediately before at most one DELETE. An independent writer may replace the value between read and DELETE, withdrawing a newer approval and causing temporary outage; manual inspection/reapproval is the accepted recovery. There is no automatic restoration. The JSON result reports `githubMarker`, `brokerClosure` and `outstandingTokens` separately. A successful variable DELETE does not prove durable broker closure or settle outstanding GitHub tokens. Optional self-revoke uses the saved trusted origin and matching private device; its failure leaves closure unconfirmed. This action does not stop/remove runtimes or delete persistent data, shared Environments or App installations.

## Verification boundary

Actual PowerShell/gh process tests, real private filesystem behavior, native Windows ACL acceptance, and an assembled broker/Azurite/DataProtection journey are required verification. Linux validates the unchanged consumer; native Windows validates the host and private output. Deterministic provider substitutes do not prove live GitHub permissions, broker hosting or Windows Docker Desktop delivery. Those remain separate acceptance gates.
