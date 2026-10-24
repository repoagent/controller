# Supplied broker enrollment in PowerShell 7

This path configures a Linux controller from an already-issued frozen-v1 enrollment and an existing Codex sign-in. It does not issue or recover enrollments. Use PowerShell 7 on Windows with Docker Desktop Linux containers, or on Linux. The controller image requires Ubuntu 26.04 linux/amd64.

Keep the enrollment outside the checkout in a private local directory. Both the file and its immediate parent must belong to the current user and grant access only to that user. Windows requires protected DACLs owned by the current SID, with no Allow rules for other SIDs. Linux requires owner-only permission bits (normally directory 0700, file 0600) and a single file link. Links/reparse points in the path are rejected. The command refuses unsafe files instead of repairing permissions.

```powershell
# Build from this checkout, or select an existing image built from this version.
.\repoagent.ps1 build
$env:REPOAGENT_IMAGE = 'repoagent:local'
$env:REPOAGENT_INSTANCE = 'broker-example'

# Supply the trusted broker origin separately from the private credential.
.\repoagent.ps1 configure --repository owner/repository --app-id 123 `
    --broker-url https://broker.example.org `
    --enrollment-file 'C:\Private RepoAgent\enrollment.json'
.\repoagent.ps1 import-codex-auth 'C:\Users\Operator\.codex\auth.json'
.\repoagent.ps1 check
.\repoagent.ps1 start
.\repoagent.ps1 restart
```

On Linux, use the same commands with absolute Linux file paths and `./repoagent.ps1`. Broker configure imports the supplied enrollment automatically. To replace an enrollment for the same configured binding, stop the instance, run `import-enrollment PRIVATE_FILE`, then `check` and `start`. Configuration keeps only the trusted origin and ordinary runtime options; it never stores the controller enrollment path or secret JSON. Broker and direct-App inputs cannot be combined. The unchanged native consumer validates the frozen credential and its binding during check/start. A private imported file is not evidence of a successful authorization check.

The selected image's immutable SHA256 ID is recorded in ownership labels and reused for broker configure/import/check/start, even if a tag moves or `REPOAGENT_IMAGE` changes. Use explicit Update below to change the image of a managed instance; changing credential mode requires a fresh instance. Seven named volumes belong to the selected broker instance: config, credentials, workspace, history, state, tools and enrollment. Existing same-name resources must carry consistent ownership metadata. Legacy or unknown resources cause refusal before writes or mounts; choose a fresh instance instead of relabeling them.

Enrollment transfer sends at most 64 KiB of raw bytes through redirected Docker stdin, without placing credential contents in arguments, environment or labels. A network-disabled UID1000 importer mounts only the selected enrollment volume. It requires a private Linux directory, writes a 0600 temporary file, flushes it, atomically replaces the enrollment and flushes the directory. Oversize input and unsafe existing files are rejected. Runtime check/start mount the enrollment volume read-only; Codex refresh credentials remain in the separate writable credentials volume. The existing Codex import binds only the selected auth.json file read-only and leaves the host sign-in intact. Secret files are never image inputs.

Run lifecycle commands sequentially. The trust boundary includes the current host account, administrators, the local filesystem, Docker daemon and the selected controller image. Linux checks the opened file handle as well as path identity, rejects writable ancestors except sticky directories owned by the filesystem root owner, and verifies the immediate parent again after reading. Windows holds the file open without write/delete sharing, checks its handle DACL, and rejects reparse traversal. This is not a defense against a compromised current user, administrator or daemon; avoid network filesystems and untrusted shared ancestor directories.

Existing direct-App instances remain accessible with `--key-path`. Ordinary check/start/stop require an existing container to match the selected controller image, UID1000, the controller start entrypoint/arguments, all six expected volume destinations (config read-only), and only its configured read-only PEM bind. Mismatches require manual resolution. These checks do not adopt or relabel legacy data or authorize destructive migration.

Native Windows PowerShell and actual Docker Desktop acceptance are manual gates. Linux execution does not establish Windows ACL, raw-byte transfer, UID/mode, read-only mount or restart persistence acceptance. A real authenticated Codex turn and live broker approval/token-scope acceptance also require their own authorized checks.

## Disable and uninstall a managed instance

Use PowerShell for instances installed with broker enrollment. The legacy Bash
entrypoint refuses managed-labelled resources before commands can change them.

Preview one instance with its original private host state directory:

```powershell
./install.ps1 -Action Uninstall -Instance demo -StateDirectory /private/demo -NonInteractive
```

Add `-Apply` to disable, stop and remove that selected runtime and clear its
container enrollment, Codex auth and any legacy installation-token cache files. All seven
volumes, workspace/history, settings, skills, tools and wrappers remain. Preview
uses host records and Docker metadata only; private-volume binding and disable
state are unverified until Apply.

With `-Apply`, the installer may inspect the selected instance’s disable state
read-only before reporting a conflicting saved setting or clone option. This
inspection does not start controller work or import credentials.

The selected enrollment volume retains `disabled.json`. Supported PowerShell work,
configuration and credential-import commands refuse this durable state. Safe stop,
passive inspection, uninstall retry and standalone revoke remain available. Do not
remove or repair the marker to restart an instance; resolve old authority through
existing enrollment recovery, normally using a fresh instance.

GitHub denial or timeout does not prevent proven local teardown. Results separate
local launch/runtime/credential state from GitHub marker removal, broker closure
and unsettled tokens. A removed container can correctly return a nonzero partial
result. Retain `lifecycle.json`, `approval.json`, `launch.json` and `recovery.json`.
Only an acknowledged matching guided enrollment copy whose recovery secret is
already null may be removed; unresolved recovery material stays protected.

Optional self-revocation uses an explicitly supplied `-EnrollmentFile` with trusted
`-ApprovalStatePath`, through the existing revoke client. GitHub deletion failure
can prevent that optional attempt; broker closure then remains unconfirmed.
Supplied source enrollment and host Codex auth/key files are never deleted.

A supplied-enrollment installation can pass complete nonsecret coordinates using
`-Repository`, `-ExpectedRepositoryId`, `-DeviceId`, `-AppId`, `-InstallationId`,
`-BrokerOrigin`, `-EnrollmentProfile`, `-ApprovalVariable` and
`-ExpectedValueSha256`. Without saved state these name an Actions-variable
approval; add `-ApprovalLocation AgentVariable` for a legacy Agent-variable
approval. Legacy Environment approval additionally requires explicit
`-GitHubEnvironment` and `-ExpectedEnvironmentId`. These coordinates identify
operator scope; they do not authenticate a controller or prove broker closure.

Standalone `-Action Revoke` remains usable on another machine without Docker or a
device credential. Use its exact saved/explicit approval coordinates. These are
sequential operator safeguards; old script copies, direct Docker administration
and hostile current-user changes are outside the guard guarantee.

## Update an immutable image or restore the prior image

Use the current checkout's PowerShell entrypoints and the original private host
state directory. Build or obtain the desired image separately; Update never
pulls, builds, moves a tag or changes approval/enrollment. Preview reads only
host/Docker metadata and defers authoritative volume verification until Apply.

```powershell
./install.ps1 -Action Update -Instance demo -StateDirectory /private/demo `
    -Image sha256:YOUR_EXISTING_LOCAL_IMAGE_ID -NonInteractive
# Add -Apply to switch the selected instance.
./install.ps1 -Action Update -Instance demo -StateDirectory /private/demo `
    -Rollback -NonInteractive -Apply
```

Image accepts an exact local SHA256 ID or an available repository@sha256 digest;
a mutable tag is refused. The target must retain the UID1000 controller entrypoint.
Rollback and Image are mutually exclusive. Keep the current and prior images
available locally. An older storage-origin image is not required after both
recorded launch images have advanced.

Image transitions require the private enrollment file. Guided reruns keep the
recorded runtime settings; differing overrides are refused before modifying the
host mirror. Direct configure also refuses conflicting recorded choices and
retains recorded values for omitted options.

Update retains all seven volume identities, enrollment, Codex sign-in, workspace,
history, state, tools and pending publication journals. It replaces only the
selected proven container, with controlled downtime. Unrelated instances remain
untouched. A successful update retains the prior image and launch settings until
a subsequent explicit transition supersedes them.

The enrollment volume owns `launch-state.json`; `launch.json` is its checked host
mirror. A retained `launch-state.required` marker refuses lost authority even
with another host state directory. Pending intent is flushed before stopping the old runtime. Completion
writes the host mirror before committing the volume record. Startup refuses
pending, missing or conflicting mirrors, including with another state directory.
Readiness alone cannot repair an interrupted transition. Use explicit Rollback
with Apply; retries retain the same prior destination. Stop and Uninstall remain
available when the selected identity is proven. Disabled state always wins.

For nondefault host state locations, also set `REPOAGENT_STATE_DIRECTORY` alongside
`REPOAGENT_INSTANCE` before ordinary `repoagent.ps1` commands. A deliberately
recovered missing/corrupt host mirror contains the authoritative runtime choices;
restore lost host-only installation inputs before using guided Install again.
Unknown/corrupt volume identity requires manual inspection, not adoption/reset.

Rollback changes image/launch settings, without restoring a filesystem snapshot
or guaranteeing older software can read newer data. Operations must remain
sequential. These guards do not fence direct Docker access, old scripts or hostile
administrators, and do not establish broker capability settlement or Azure support.
