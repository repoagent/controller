# Verification, controller releases and auth-service delivery

`Verify controller` runs on pull requests and pushes to `main`, with read-only permissions and no persisted checkout credentials. It uses the root `global.json` (.NET 11 RC), executes the actual xUnit runner, checks nonzero discovery, runs installer and delivery process tests, then builds Native AOT and executes native help plus NativeSmoke. The auth service independently pins .NET 10 LTS.

```sh
bash scripts/delivery/verify.sh /tmp/controller-verification
```

Local prerequisites are the pinned SDK, Python 3, PowerShell 7, clang and zlib development headers. The separate installer workflow runs the expanded Linux suite and native Windows PowerShell revocation checks. These process tests use controlled GitHub/Docker boundaries; they do not establish a complete Windows Docker Desktop installation.

## Controller releases

An intentional dispatch publishes a Linux amd64 Native AOT distribution to GitHub Releases. The Ubuntu 24.04 build requires glibc 2.39 or newer and normal .NET native dependencies, including ICU/OpenSSL. Codex and GitHub CLI are separate image/runtime dependencies. Windows and ARM executables are not supplied.

Before publication, create the `controller-release` GitHub environment and restrict it to `main`. Protect main and release tags. Required human release approval was removed by the maintainer on 2026-10-22; new versions can publish without waiting for a person. This environment has no Azure trust or credentials; only the release job receives `contents: write`.

Create an immutable version tag on a reviewed main commit containing the delivery scripts, then dispatch from main:

```sh
gh workflow run release-controller.yml --ref main -f tag=v1.2.3
```

The job validates the tag, checks its commit belongs to main, checks out that source, verifies it and packages it. Pull requests publish nothing, and ordinary pushes to main publish only [baseline image releases](#baseline-releases), never a native distribution. An existing release, including a partial upload, is refused to preserve assets; inspect and resolve a partial release explicitly.

Local packaging does not publish:

```sh
bash scripts/delivery/package-controller.sh 1.2.3 /tmp/controller-release
```

```text
repoagent-controller-1.2.3-linux-amd64.tar.gz
repoagent-controller-1.2.3-linux-amd64.tar.gz.sha256
  repoagent-controller-1.2.3-linux-amd64/
    RepoAgentController
    github-discussion/SKILL.md
    repoagent-orientation/SKILL.md
    VERSION                  # version, full source commit, runtime
    SHA256SUMS               # executable, skills and VERSION
```

Verify the archive checksum before extraction, then run `sha256sum -c SHA256SUMS` inside it. Keep the skills beside the executable; the controller registers that directory for native skill discovery. Local builds record HEAD and can include edits; release only from the workflow's clean tagged source. This archive interface supports later image composition without developer credentials.

## Public controller container image

`Publish controller image` builds and smokes the real default Linux amd64 image on relevant pull requests with read-only permissions, then uploads and downloads the exact artifact ID into a second job to verify its checksum and loaded image identity. It records the image's own `versions` output and embedded `image-inventory.json` in the checksummed artifact and checks that the reported gh and Codex versions match the inventory. It uses the existing explicit source inventory; no enrollment, Codex sign-in or host GitHub credential enters the build. Pull requests never publish.

A push to `main` that changes the same image inputs (controller source, container, image manifest, bundled skill, composition or release scripts, or this workflow) publishes automatically. Main runs also execute the full `scripts/delivery/verify.sh` gate, because `main` has no required status checks; publication waits for it and for the artifact verification. One `main` publication runs at a time; a newer pending `main` run replaces an older pending one, so a burst of merges publishes the newest commit. Documentation-only and auth-service changes do not publish a controller image.

The build uploads its Docker archive, build metadata, versions output, inventory and checksums for seven days before publication proceeds through the main-only `controller-release` environment. The publish job loads that same artifact, checks its checksum/platform/UID/controller role/source revision, and receives `packages: write` only in that job. It publishes `ghcr.io/repoagent/controller:sha-FULL_SOURCE_SHA-RUN_ID-ATTEMPT`; reruns use fresh coordinates, and no `latest` tag is changed. A separate release job with only `contents: write` then creates the [baseline release](#baseline-releases).

Manual recovery stays available: `gh workflow run publish-controller-image.yml --ref main` rebuilds, verifies and publishes current `main` with a new release. If only the release job failed, re-run that failed job in the same workflow run; it reuses the already published image coordinates.

Initial GHCR packages are private by default. An account with package administration rights must make `repoagent/controller` public under package **Settings > Danger Zone > Change visibility**, and grant this repository **Write** under **Manage Actions access** if access was not inherited. The workflow cannot report success until a clean anonymous registry client downloads the immutable image and all its layers into empty storage and confirms that its image config matches the approved build. A visibility failure can therefore leave a published private image; inspect the package and rerun deliberately after fixing access. No personal registry token or ACR is required. Use the public `ghcr.io/repoagent/controller@sha256:...` reference from the release or run summary for Azure; publication does not deploy or prove storage/ownership compatibility.

Action versions are pinned to current release commits. See [GitHub's image publication guidance](https://docs.github.com/en/actions/tutorials/publish-packages/publish-docker-images), [artifact upload](https://github.com/actions/upload-artifact) and [download](https://github.com/actions/download-artifact) documentation.

### Baseline releases

Each successful publication creates one GitHub Release for the anonymously verified image. Publication never updates a running controller; operators adopt a release deliberately through [local image update](local-runtime.md#update-an-immutable-image-or-restore-the-prior-image) or the [Azure controller image update](../infra/controller/README.md).

- Tag `baseline-RUN_ID.ATTEMPT`, created on the published source commit. It corresponds one-to-one with the GHCR tag `sha-FULL_SOURCE_SHA-RUN_ID-ATTEMPT`. Run IDs increase across the repository, so a larger `RUN_ID` (then `ATTEMPT`) is a later publication.
- Title `Controller baseline SHORT_SHA (gh GH_VERSION, Codex CODEX_VERSION)`, and a body with the `docker pull` immutable reference and a version table.
- Machine-readable metadata: the body line `<!-- repoagent.baseline-release/v1 -->` followed by one fenced `json` block:

| Field | Meaning |
| --- | --- |
| `schema` | `repoagent.baseline-release/v1`; incompatible changes use a new schema name |
| `release` | The release tag |
| `image` | Immutable `ghcr.io/repoagent/controller@sha256:...` manifest reference that was pulled anonymously |
| `imageTag` | The GHCR tag pushed for that run; registry tags can move, so pull by `image` |
| `imageConfigDigest` | Image config digest from the build; Docker's classic image store reports it as the image ID |
| `platform` | `linux/amd64` |
| `versions.controller` | Full source commit |
| `versions.gh`, `versions.codex` | Versions reported by the image's own `gh --version`/`codex --version`, which must equal the inventory |
| `inventorySha256` | SHA-256 of the image's `/opt/repoagent/image-inventory.json` bytes; a running container can recompute it to find its own release. No match means a derived, local or otherwise unreleased image |
| `run` | The verification and publication workflow run |

Consumers should select releases by the `baseline-` tag prefix and this marker rather than GitHub's **Latest** badge, which manual `v*` controller releases can also hold. An existing release with the same tag is never replaced. Baseline tags are not covered by the `v*` tag ruleset; add `refs/tags/baseline-*` to the tag ruleset to block their update or deletion.

## Automatic auth-service delivery

Changes merged to `main` under `service/auth`, `infra/auth`, the auth delivery scripts/workflows or shared MSBuild/NuGet files trigger `Auth service CI`. Controller-only changes do not deploy auth. Pull requests run verification without publication or Azure credentials.

Delivery verifies the .NET service against pinned Azurite and compiles Bicep, then builds one Linux amd64 image with the auth role, numeric nonroot user and full source-revision label. It publishes to the existing public GHCR package and anonymously verifies the resolved immutable digest. Publication has `packages: write` and no Azure token; deployment has `id-token: write` and no package write.

The Azure job applies `main.bicep` in the existing resource group. Public deployment includes `cleanup.bicep`, so the API and scheduled cleanup Job receive the **same digest**. The script inspects both images, waits for the ready API revision and requests public `/health/live` and `/health/ready`. Cleanup runs independently so it can process expired records while HTTP scales to zero; it retains its separate Table-only identity.

Hosted lifecycle acceptance remains tracked in #35/#28. Manual READY variables do not block source publication or deployment, and a successful deployment does not establish the full GitHub enrollment/issuance journey.

## Azure configuration

Reuse the subscription, resource group, identity and exact main-only OIDC subject in the [Azure deployment runbook](azure-auth-deployment.md). Auth jobs use no GitHub environment or Azure client secret. Deployment Contributor cannot grant roles or import keys. Existing keys, grants, Table quota initialization, restore epoch and monitoring are operator prerequisites and remain unchanged by ordinary code delivery.

| Repository variable | Required value |
| --- | --- |
| `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | Existing nonsecret deployment identity/target UUIDs from the runbook |
| `AZURE_RESOURCE_GROUP`, `AZURE_LOCATION` | `rg-repoagent-controller-auth`, `swedencentral` |
| `AUTH_APP_NAME` | `repoagent-auth` |
| `AUTH_STORAGE_ACCOUNT_NAME`, `AUTH_KEY_VAULT_NAME` | Existing owned storage account and vault |
| `AUTH_IMAGE_REPOSITORY` | Exactly `ghcr.io/repoagent/controller-auth` |
| `AUTH_SIGNING_KEY_URI`, `AUTH_DATA_PROTECTION_KEY_URI` | Separate pinned `/keys/name/version` references |
| `AUTH_TABLE_NAME` | Dedicated broker Table; defaults to `brokerstate` |
| `AUTH_DEPLOYMENT_EPOCH` | Existing external restore-fencing epoch; preserve outside Table backups |
| `AUTH_REQUEST_LIMIT_PER_MINUTE` | Optional: default 120, allowed 1–10000 |
| `AUTH_CONCURRENT_REQUEST_LIMIT` | Optional: default 8, allowed 1–128 |
| `AUTH_REQUEST_TIMEOUT_SECONDS` | Optional: default 15, allowed 1–60 |

The dedicated GHCR package must be public and allow repository Actions publication. Initial package visibility/access setup remains separate; each actual digest is anonymously verified before deployment. No personal registry token is required. No GitHub App private key, host GitHub credential or Codex credential enters the image or Bicep.

For infrastructure changes, read current resource/module documentation, use latest registry module versions, compile locally, then deploy to Azure and inspect actual resources. These templates use direct resource declarations and a repository-local cleanup module. Before an environment becomes mission critical, use a separate staging/test environment in a real Azure region.

## Manual retry and rollback

```sh
# Retry normal verification, publication and deployment from main.
gh workflow run ci-auth-service.yml --ref main -f operation=deploy
# Deploy or roll back both modes to a previously approved immutable image.
gh workflow run deploy-auth.yml --ref main -f image=ghcr.io/repoagent/controller-auth@sha256:APPROVED_DIGEST
# Existing outputs-only OIDC access check.
gh workflow run ci-auth-service.yml --ref main -f operation=access-check
```

Automatic deployment and manual rollback share noncanceling delivery concurrency and the same Bicep/app health path. Deployment across the two resources is not atomic. On failure, inspect Azure's deployment and actual resource state before retrying; do not infer that a failed workflow made no changes. Rollback is explicit and re-applies current Bicep configuration with the selected older image.
