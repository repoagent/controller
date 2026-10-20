# Controller image composition

Build a Linux amd64 image with Docker and the existing Bash or PowerShell host tools. No host Python, .NET SDK, GitHub CLI or agent installation is required:

```sh
./repoagent build --manifest image/default.json
./install.sh --image-manifest image/default.json
```

```powershell
.\repoagent.ps1 build --manifest image/default.json
.\install.ps1 -ImageManifest image/default.json
```

The installers accept the same manifest. The build first runs a digest-pinned Python helper container with networking disabled, individual read-only source-file and manifest mounts, and a fresh writable temporary context. It validates choices before Docker **build** runs. It never mounts the host home or Docker socket. Docker may pull this helper using the host's registry authentication; those credentials do not enter the context or runtime. The helper prints the resolved nonsecret inventory, and the host prints the resulting image ID. Temporary contexts are removed on success or failure.

## Version 1 manifest

`image/default.json` is the maintained default. Objects reject unknown or duplicate fields; versions must match the reviewed catalog in `scripts/compose-image.py`. Arbitrary version strings, URLs, environment variables and credentials are not accepted.

| Field | Contract |
| --- | --- |
| `schemaVersion` | Integer `1` |
| `platform` | `linux/amd64` |
| `controller.source` | `checkout`; build the explicitly allowlisted source files |
| `controller.sha256` | Optional expected source digest; a mismatch fails before build |
| `agent.profile`, `agent.version` | `codex`, `0.163.0`; includes matching code-mode host |
| `githubCli.version` | `2.102.0` |
| `tools.cloudflared` | Optional `2026.10.1`; needed for Quick Tunnel mode |
| `tools.developerUtilities` | Optional boolean, default false; add ripgrep, file, patch, diffutils, unzip, zip, less and OpenSSH client |
| `baseImage.reference` | Optional digest-pinned registry/repository identity; omitted uses the maintained runtime-deps base |

The default manifest enables Cloudflared and developer utilities, preserving the current default capabilities. Omitting them produces a smaller overlay. Bash, certificates, curl, Git, Python/PyYAML, jq, procps and native runtime libraries are required by the controller and its runtime tooling.

Software archives use fixed HTTPS release URLs and SHA-256 verification before extraction. SDK and runtime bases are pinned by digest. Ubuntu packages come from the signed `20261020T000000Z` snapshot. The final image contains `/opt/repoagent/image-inventory.json` with resolved selections, artifact hashes, base identities and controller source digest, plus `/opt/repoagent/os-packages.txt` with actual installed package versions. There is no fallback to `latest`. Runtime `toolUpdates=latest` remains the existing, separately configured startup behavior; choose `pinned` during setup to keep running the image's tools.

The shared `image/source-files.txt` inventory drives both host mounts and helper context creation. Host adapters reject symbolic links, reparse points and unsafe relative paths before mounting source files. Excluded checkout credentials are therefore invisible even to the helper.

The source digest is SHA-256 over sorted allowlisted path names, a NUL byte, and each file's binary SHA-256 digest. To freeze a checkout, copy the emitted `controller.sha256` into your manifest. Any allowlisted source/build asset change then fails validation until you deliberately update that identity. New source files require updating that single explicit allowlist.

## Build on your repository's image

Add a digest identity to a copy of the manifest, and remove optional tools you already provide or do not need:

```json
"baseImage": {
  "reference": "registry.example.com/team/development@sha256:REPLACE_WITH_64_LOWERCASE_HEX_DIGITS"
}
```

An immutable local `sha256:<image-id>` from `docker image inspect --format '{{.Id}}' IMAGE` is also accepted, without publishing that image. The host verifies the ID and tags it as `repoagent-local-base:<id>` for BuildKit, then verifies that local tag again. This reusable local tag remains after the build. Use the registry's reported digest, for example from `docker buildx imagetools inspect IMAGE:TAG`. Private registry authentication belongs to the host Docker client. No registry publication is performed by the builder.

The initial supported base is **Ubuntu 26.04, Linux amd64**, with its standard Ubuntu archive keyring and working package manager. This matches the actual .NET 11 Native AOT build environment (glibc 2.43) and its tested ICU/OpenSSL dependency set. Older glibc, musl/Alpine, other distribution packaging, and other architectures are not validated. The build rejects a different OS/release/architecture before installing software and explains how to supply a compatible base. A compatible base's package dependency conflicts still fail explicitly; the builder does not downgrade or upgrade its already-installed requested packages.

Composition retains base layers, files, toolchain, PATH entries and package repository configuration. Controller binaries and pinned tools are installed under `/opt/repoagent`, and their paths are prepended so the selected agent/gh versions take precedence. Those paths are reserved for this overlay. Existing UID 1000 accounts and their home contents are retained. A home under `/home` is required; `/home/agent` aliases that home when necessary. Conflicting `/home/agent` content fails the build instead of being removed. If UID 1000 is absent, an `agent` account is created. Existing home skills are preserved; otherwise the standard skills-volume link is installed.

The runtime uses `/usr/bin/python3` with its system PyYAML dependency, even when the base has a different Python earlier on PATH. The base Python remains available to repository tools. The `io.repoagent.role=controller` label overrides any inherited service role; other base labels remain. The result intentionally runs as UID 1000, uses `/workspace`, and replaces the base ENTRYPOINT/CMD with the controller runtime. Runtime state, sessions and credentials retain the existing `/data` volume layout. Repo-local skills in `/workspace` remain available. Neither repository cloning nor credential enrollment is part of image composition. Future authenticated clone/agent HTTPS operations must use the runtime's configured trust roots; host Docker authentication is not inherited by those processes.

## Explicit derived Dockerfile

Use a derived Dockerfile for an additional user-owned layer after composition:

```dockerfile
ARG REPOAGENT_BASE
FROM ${REPOAGENT_BASE}
ARG PROJECT_LABEL
LABEL project=${PROJECT_LABEL}
```

```sh
./repoagent build --manifest image/default.json \
  --derived-dockerfile '/path with spaces/Project.Dockerfile' \
  --build-arg 'PROJECT_LABEL=team; literal text'
```

```powershell
.\install.ps1 -ImageManifest image/default.json `
  -DerivedDockerfile 'C:\path with spaces\Project.Dockerfile' `
  -BuildArg 'PROJECT_LABEL=team; literal text'
```

Paths and argument values are passed as process arguments, without shell evaluation. Docker bind mount paths containing commas/newlines are rejected. Derived build arguments are nonsecret user-owned inputs; do not pass tokens or private keys. The derived Dockerfile receives only the allowlisted context, with its own file copied to a fixed filename. It receives `REPOAGENT_BASE` pointing to the locally composed base image. Arbitrary derived instructions are not reviewed agent profiles and may alter the runtime contract. Only Codex's native app-server adapter is supported.

Credential exclusion guarantees concern the context and overlay controlled here. An explicitly supplied base or Dockerfile is trusted user content: existing secrets in its layers cannot be removed by adding this overlay. Keep those inputs credential-free as well. No host home, PEM, enrollment secret, auth cache or token is automatically copied; symlinks in allowlisted source paths are rejected.

## Verification limits

Process tests use actual Bash and Linux PowerShell with a Docker executable substitute for argument/error behavior. Real local Docker builds, inventory/layer inspection, compiled-controller execution and a credential-free Codex app-server handshake provide assembled-image evidence. Native Windows Docker Desktop, private registries, registry publication, live enrollment, Azure, authenticated Codex turns and unsupported base families require separate acceptance. See the PR verification evidence for the exact candidate tested.
