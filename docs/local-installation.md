# Guided PowerShell local installation

Use PowerShell 7.4 or later and Docker Desktop with Linux containers (or Docker on Linux). The supported controller image uses Ubuntu 26.04, linux/amd64. Host `gh` must already be authenticated for direct repository Agent-variable writes. Supply a trusted broker HTTPS origin and an existing Codex `auth.json`, or explicitly request container device sign-in. This workflow does not install or approve the shared GitHub App on your behalf; the selected repository must already have a compatible App installation.

Preview first, then run the same selection with `-Apply`:

```powershell
.\install.ps1 -Repository owner/repo -BrokerOrigin https://broker.example.org `
    -Instance repo-one -CodexAuthPath "$HOME/.codex/auth.json" -NonInteractive

.\install.ps1 -Repository owner/repo -BrokerOrigin https://broker.example.org `
    -Instance repo-one -CodexAuthPath "$HOME/.codex/auth.json" -NonInteractive -Apply
```

Without `-Apply`, the command only reads inputs and inspects available Docker metadata. It creates no host files, volumes, containers, approval markers or enrollments, and pulls/builds no images. It shows the repository, trusted broker, App/profile/Agent-variable coordinate, image and tool choices, instance mounts and retained data. A fresh installation/device is explicitly pending until enrollment. Download/build time and size depend on caches and selected tools/base; the preview cannot estimate them reliably. Source and ABI build validation still run through the existing image composer during Apply.

Complete inputs never prompt or open a browser. Interactive runs prompt for missing required values; `-NonInteractive` fails with the missing option. `-NoStart` still configures and checks the runtime, then leaves it stopped. Guided shared-App setup currently supports only `-Mode polling -EnrollmentProfile polling`; other profile/mode combinations fail before writes.

## Saved choices and recovery

`-Instance` selects `repoagent-INSTANCE` and its named volumes. Its default comes from `REPOAGENT_INSTANCE`, then `default`; use separate names for separate controllers. The host state defaults to `$HOME/.repoagent/instances/INSTANCE`. Override that exact directory with `-StateDirectory`; keep using the same directory to resume. Paths must be local and privately owned; links and unsafe access are refused rather than repaired. Missing directories are created privately only on Apply.

The separate `launch.json` contains nonsecret choices: instance/repository, broker/variable/profile, selected input paths and hashes, immutable runtime image ID, model and runtime options. It contains no token, credential contents, approval marker or recovery secret. The existing enrollment client owns `approval.json`, `recovery.json` and the frozen private `enrollment.json`; retain them together. Only the frozen enrollment is imported privately into the runtime; the launch record and recovery journal stay on the host.

Explicit arguments override saved launch choices, then defaults apply. Changing an existing repository, broker, authentication mode, App binding or image inputs requires a fresh instance. A conflicting enrollment coordinate is refused, never replaced or automatically approved. `-ApprovalVariable NAME` selects a direct repository Agent variable; fresh installs default to `APPROVED_CONTROLLER`, and reruns reuse the saved custom name. No GitHub Environment or inherited organization variable is involved.

Apply delegates creation/recovery to the [host enrollment client](host-enrollment.md), then uses the [private runtime commands](local-runtime.md) to configure, import, check and start. A pending/failed enrollment stops before runtime setup. A later build/import/login/check/start failure retains the same enrollment and instance data. Retry the original command, or use the printed instance/state resume command; do not delete recovery files to force a new identity. Matching retries reuse the saved approval and immutable image, without new approval writes. Expired, withdrawn, conflicting or unverifiable authority requires inspection; the installer does not restore it.

Stop a running instance before applying configuration. The installer never stops/removes it automatically. Config, credentials, workspace, history, state and updated tools persist in selected named volumes; broker enrollment is a seventh volume, mounted read-only for runtime authorization. No host home or Docker socket is mounted. Runtime tokens never receive the host's approval authority.

## Codex sign-in and image choices

The installer detects `CODEX_HOME/auth.json`, then `$HOME/.codex/auth.json`; `-CodexAuthPath` overrides detection and saved paths. It imports only that file, leaving host config/history/sign-in unchanged. Existing container credentials are retained on reruns unless an explicit `-CodexAuthPath` requests a new import. `-DeviceLogin -Apply` explicitly enables interactive device sign-in using the existing runtime login flow; it cannot be combined with `-NonInteractive`. A host OS-keyring-only sign-in cannot be copied as `auth.json`.

The default `image/default.json` selects reviewed Codex, gh and optional tools on the maintained base. Use `-ImageManifest PATH` for a custom digest-pinned `baseImage.reference` or other supported manifest choices; explicit selection wins over defaults. Custom bases must satisfy the existing Ubuntu 26.04/linux-amd64 compatibility checks. Named OpenAI universal/.NET presets remain gated by issue #46. No host Python installation is required.

`-DerivedDockerfile` and `-BuildArg` retain the existing image builder API. Build argument values are forwarded explicitly but not stored in launch state or printed by the installer; resupply them if a failed build must be retried. Keep credentials out of image/build inputs. To reuse an already-built controller image, set `REPOAGENT_IMAGE` and pass `-SkipBuild`. Once resolved, the immutable SHA256 image ID is saved and reused even if a tag moves. Local setup performs no registry write. Publishing uses `scripts/publish-image.ps1`; Azure planning remains the separate read-only `scripts/plan-azure.ps1`. Neither is Azure apply.

## Own-App mode and acceptance

Existing PEM users can select `-Authentication OwnApp`; explicit `-AppId`/`-KeyPath` also select it on a fresh instance. Own-App setup now follows the same preview/Apply gate. It keeps existing polling, fixed-URL and quick-tunnel options, PEM readability checks and image customization API. It needs no host `gh`. For example:

```powershell
.\install.ps1 -Authentication OwnApp -Repository owner/repo -AppId 123 `
    -KeyPath C:\Private\app.pem -CodexAuthPath "$HOME/.codex/auth.json" `
    -Instance own-app -NonInteractive -Apply
```

Own-App `-Clone` explicitly adds Contents read on a fresh instance and persists that choice for retries. Older launch records remain non-clone; changing this choice requires a fresh instance. Guided Broker setup remains ordinary polling; explicit clone enrollment is available through `-Action Enroll -EnrollmentProfile polling-clone` or `webhook-clone` and the separate runtime commands.

Choose a fresh instance when changing credential mode; existing storage is not silently adopted or relabeled. Existing Own-App Docker settings without a host launch record require explicit inputs for the first guided run, because preview does not launch a container to retrieve settings. `-Action Enroll` and `-Action Revoke` retain their independent host command behavior.

A successful command reports either verified/stopped or local runtime readiness. Native Windows PowerShell with actual Docker Desktop, a real authenticated Codex turn/history, live Agent-variable host-write/broker-read/runtime-denial, private-repository availability and hosted/Azure/signing recovery remain separate acceptance gates. Process fixtures and source merge do not establish those outcomes.
