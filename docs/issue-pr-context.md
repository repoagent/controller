# Issue and PR reading and reauthorization

New controller approvals include **Issues: read** and **Pull requests: read** on the selected repository. Agents fetch context on demand through their authenticated `gh` wrapper. Runtime tokens request read even when the installation grants write. Requests still arrive through Discussions; no issue editing, PR creation, review submission or merging permission is added.

Existing profile names, setup-v5 documents and runtime protocols 1–4 remain. Revised approval policies distinguish new read grants from historical approvals. Existing enrollments and pending requests keep their original authority; a compatible controller can continue using them and logs that reauthorization is needed for issue/PR reading. There is no context flag or additional profile family. Newly started host recovery state records its intended approval policy before contacting the broker, so retrying against an older broker cannot silently authorize an older grant. Historical pending files without this pin retain their original policy.

## Existing installations using the shared App

1. Accept the GitHub App installation's Issues read and Pull requests read permissions. Deploy a compatible broker before starting new enrollment. This is an explicit operator deployment step, not something the installer performs.
2. Update the existing instance to an available compatible controller image using [Action Update](local-runtime.md#update-an-immutable-image-or-restore-the-prior-image). Keep the original host state directory with `approval.json`, `enrollment.json`, `recovery.json` and `launch.json`.
3. Preview reauthorization:

   ```powershell
   ./install.ps1 -Action Reauthorize -Instance demo -StateDirectory /private/demo -NonInteractive
   ```

4. Repeat with `-Apply`. The operation validates that the approval-variable name is eligible for fresh setup, verifies ownership, the saved approval/credential binding and image support; records recovery intent; stops the selected instance; withdraws the exact old approval; and requires confirmed broker closure. It then uses ordinary setup-v5 enrollment with the same repository, App installation, profile and approval-variable name. Supported legacy approval locations move to direct repository Agent approval.
5. The replacement credential and launch binding are installed under a durable startup fence. Authentication is checked before completing the host mirror and enabling startup. The same instance restarts with all seven volumes, configuration, Codex sign-in, workspace, Discussion checkpoints, waiting input and conversation history retained.

No new instance or manually managed enrollment directory is required. Private previous/replacement enrollment files and a host journal live under the original state directory. A matching nonsecret journal in the enrollment volume prevents ordinary startup, image update or rollback while replacement is incomplete. The selected image must still support the existing enrollment: previously retired Actions-variable clone/settings policies require separate operator recovery and are not re-enabled by this action. Legacy names that current setup cannot reuse (including nonreserved `REPOAGENT_*` names) require explicit approval-coordinate migration and are rejected before the controller is stopped or the old grant is withdrawn. Reauthorization requires complete original host authorization files; an imported-credential-only installation without these files needs explicit operator recovery, rather than guessing approval coordinates.

## Interruption and recovery

Rerun the same Reauthorize command with the same instance, host state directory and image. It resumes the recorded phase and enrollment request. Approval conflicts, unverifiable closure and missing or conflicting recovery state stop the operation before further remote work. Recovery verifies the operation ID, original binding and launch snapshot in both journals. An absent volume intent is accepted only before the initial fence or after the installed replacement is independently verified complete. Reauthorization never restores a withdrawn approval or revives a closed enrollment.

After confirmed closure, recovery proceeds with the replacement. Normal broker rate limits still apply. There is no token-settlement wait: old tokens may remain valid at GitHub until their normal expiry, while the broker permanently denies new issuance for the old enrollment ID ([ADR 0017](adr/0017-keep-broker-tokens-in-request-memory.md)).

Image rollback snapshots containing the previous enrollment binding are cleared when the replacement is installed. Runtime data is retained. Subsequent image transitions use the current enrollment; an older image that cannot consume the new grants will fail its normal authentication checks.

## Your own App

Grant Issues read and Pull requests read, accept the installation permission update, and restart with a compatible controller. No broker enrollment replacement is needed. Missing installation grants produce authentication guidance.

## Verification boundary

Automated controller and broker tests cover standard read-only requests, exact token binding, historical approval authority, unchanged protocols and replacement closure. Installer and container orchestration follow the repository's manual verification policy. Offline recovery exercises do not establish real GitHub approval writes, private-repository API access, Docker Desktop behavior or hosted broker deployment. After operator deployment and reauthorization, verify that the agent's wrapper reads a known issue and PR in the target repository.
