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

On Linux, use the same commands with absolute Linux file paths and `./repoagent.ps1`. Broker configure imports the supplied enrollment automatically. To replace an enrollment for the same configured binding, stop the instance, run `import-enrollment PRIVATE_FILE`, then `check` and `start`. Configuration keeps only the trusted origin and ordinary runtime options; it never stores the host enrollment path or secret JSON. Broker and direct-App inputs cannot be combined. The unchanged native consumer validates the frozen credential and its binding during check/start. A private imported file is not evidence of a successful authorization check.

The selected image's immutable SHA256 ID is recorded in ownership labels and reused for broker configure/import/check/start, even if a tag moves or `REPOAGENT_IMAGE` changes. Use a fresh instance to change the image or credential mode. Seven named volumes belong to the selected broker instance: config, credentials, workspace, history, state, tools and enrollment. Existing same-name resources must carry consistent ownership metadata. Legacy or unknown resources cause refusal before writes or mounts; choose a fresh instance instead of relabeling them.

Enrollment transfer sends at most 64 KiB of raw bytes through redirected Docker stdin, without placing credential contents in arguments, environment or labels. A network-disabled UID1000 importer mounts only the selected enrollment volume. It requires a private Linux directory, writes a 0600 temporary file, flushes it, atomically replaces the enrollment and flushes the directory. Oversize input and unsafe existing files are rejected. Runtime check/start mount the enrollment volume read-only; Codex refresh credentials remain in the separate writable credentials volume. The existing Codex import binds only the selected auth.json file read-only and leaves the host sign-in intact. Secret files are never image inputs.

Run lifecycle commands sequentially. The trust boundary includes the current host account, administrators, the local filesystem, Docker daemon and the selected controller image. Linux checks the opened file handle as well as path identity, rejects writable ancestors except sticky directories owned by the filesystem root owner, and verifies the immediate parent again after reading. Windows holds the file open without write/delete sharing, checks its handle DACL, and rejects reparse traversal. This is not a defense against a compromised current user, administrator or daemon; avoid network filesystems and untrusted shared ancestor directories.

Existing direct-App instances remain accessible with `--key-path`. Ordinary check/start/stop require an existing container to match the selected controller image, UID1000, the controller start entrypoint/arguments, all six expected volume destinations (config read-only), and only its configured read-only PEM bind. Mismatches require manual resolution. These checks do not adopt or relabel legacy data or authorize destructive migration.

Native Windows PowerShell process tests and actual Docker Desktop acceptance are separate gates. Linux execution or a mocked Docker process does not establish Windows ACL, raw-byte transfer, UID/mode, read-only mount or restart persistence acceptance. A real authenticated Codex turn and live broker approval/token-scope acceptance also require their own authorized checks.
