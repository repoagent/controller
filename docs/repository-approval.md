# Inspect or withdraw legacy Environment approval

[ADR 0007](adr/0007-use-repository-actions-variables-for-approval.md) selects direct repository Actions variables for approval, superseding the Agent-variable location of [ADR 0006](adr/0006-use-repository-agent-variables-for-approval.md). Use `install.ps1 -Action Revoke` from [controller enrollment](host-enrollment.md#revoke-and-prototype-compatibility) to withdraw Actions-variable (v3) or explicit legacy Agent-variable (v2) approval. The commands below describe the existing Environment tool only; they do not inspect or withdraw repository Actions or GitHub Agent variables. Draft PR #67 is an unmerged Environment prototype.

This tool requires PowerShell 7 and host `gh` already signed in with authority to inspect the repository and Environment and manage its selected variable. It needs no Docker, PEM or controller credential. It does not create approval or implement broker enrollment/issuance; those remain under the [provider contract](auth-repository-approval.md) and [#31](https://github.com/repoagent/controller/issues/31).

Run a read-only preview:

```powershell
pwsh ./install.ps1 -Action Revoke -Repository owner/repository -NonInteractive
```

Review the JSON result's repository/environment IDs, selected variable and `observedValueSha256`. Supply `-GitHubEnvironment` and `-ApprovalVariable` for custom names; defaults are `repoagent` and `APPROVED_CONTROLLER`. The hash covers the exact UTF-8 bytes of the JSON-decoded value, without trimming, newline conversion or embedded JSON reserialization. The variable's contents are never printed.

To withdraw the intended value, explicitly apply its independently reviewed hash. Optional immutable IDs protect against selecting a recreated or changed resource:

```powershell
pwsh ./install.ps1 -Action Revoke -Repository owner/repository `
  -ExpectedValueSha256 '<64-character hash from the reviewed preview>' `
  -ExpectedRepositoryId 123 -ExpectedEnvironmentId 456 -Apply -NonInteractive
```

Without `-Apply`, the tool only reads. Interactive mode can ask for missing repository/hash before GitHub calls; it never silently enables Apply. A changed hash or mismatched ID prevents deletion. Approval means GitHub-authorized variable management, not attestation by a particular human administrator or deployment reviewer.

Each run makes at most three GETs and one DELETE: inspect repository, inspect Environment, GET the exact variable, then optionally delete it once. Each child has a 15-second deadline and a combined stdout/stderr limit of 256 KiB, within a 60-second remote-work budget. Failed or ambiguous calls are not retried. A 403, lone 404, malformed response, timeout or lost DELETE result reports `unverifiable`, never “already removed”. Inspect GitHub before deciding whether to retry.

The final JSON distinguishes `githubMarker` (`preview`, `conflict`, `removed`, `unverifiable`), `brokerClosure` (`unconfirmed`) and `outstandingTokens` (`unsettled`). `removed` means GitHub acknowledged deletion at the selected coordinate with HTTP 204; it proves neither which concurrent value was deleted nor continued absence. This tool makes no broker calls and cannot invalidate previously issued tokens or authorize replacement.

GitHub has no atomic compare-delete. An independent writer may replace approval after the final GET and lose that newer value to DELETE, causing temporary service loss. The accepted recovery is manual inspection and reapproval through setup, after resolving the old enrollment and its outstanding tokens. The tool does not lock, restore approval, replace a controller or automatically retry mutations. Preserve the shared Environment, unrelated variables/secrets and App installation.

The revoke command is an installer script and has no automated tests; it is tested manually against the local AppHost ([#204](https://github.com/repoagent/controller/issues/204), not built yet) when it changes. Real GitHub compatibility is checked separately with a uniquely named disposable variable, preserving the shared Environment and existing approvals.
