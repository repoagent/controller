# Controller and broker delivery workflows

The **Controller** workflow (`controller.yml`) verifies controller changes on pull requests and pushes to `main`, with read-only permissions and no persisted checkout credentials. It uses the root `global.json` (.NET 11 RC), executes the actual xUnit runner, checks nonzero discovery, runs the controller process tests in `tests/installers` and the delivery tests, then builds Native AOT and executes native help plus NativeSmoke. The controller project runs the trim and AOT analyzers on every build and treats all warnings as errors, and the Native AOT packaging uses the same warning policy as the image build, so code that would fail image publication (for example IL2026/IL3050) fails this gate first. The controller core (`src/RepoAgentController.Core`, [ADR 0010](adr/0010-structure-the-controller-and-broker-as-ports-and-adapters.md)) also runs the banned-API analyzer: its `BannedSymbols.txt` rejects the common file, network, process, environment and wall-clock APIs and `JsonNode`, and review catches the rest. Both verification scripts compile and verify formatting of every project through `RepoAgent.slnx`, including live tests without running credential-dependent scenarios. The broker and controller share the exact .NET 11 RC3 SDK and [analyzer policy](code-quality.md).

```sh
bash scripts/delivery/verify.sh /tmp/controller-verification
```

Local prerequisites are the pinned SDK, Python 3, clang and zlib development headers.

The installer (`install.*`, `repoagent*`, `scripts/*.ps1`) and the container runtime (`container/`) have no automated tests ([ADR 0010](adr/0010-structure-the-controller-and-broker-as-ports-and-adapters.md)). They are tested manually against the local AppHost ([#204](https://github.com/repoagent/controller/issues/204), not built yet) when they change.

The repository has four workflow entry points: **Controller**, **Broker**, the scheduled **Baseline tool refresh** (with a reusable per-tool helper), and **Hosted installer smoke**. Controller owns verification, image publication and native tagged releases. Broker owns verification, deployment and recovery operations. Superseded read-only checks are cancelled; publication and deployment writes finish once started.

## Controller releases

An intentional dispatch publishes a Linux amd64 Native AOT distribution to GitHub Releases. The Ubuntu 24.04 build requires glibc 2.39 or newer and normal .NET native dependencies, including ICU/OpenSSL. Codex and GitHub CLI are separate image/runtime dependencies. Windows and ARM executables are not supplied.

Before publication, create the `controller-release` GitHub environment and restrict it to `main`. Protect main and release tags. Required human release approval was removed by the maintainer on 2026-10-22; new versions can publish without waiting for a person. This environment has no Azure trust or credentials; only the release job receives `contents: write`.

Create an immutable version tag on a reviewed main commit containing the delivery scripts, then dispatch from main:

```sh
gh workflow run controller.yml --ref main -f tag=v1.2.3
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

The same **Controller** workflow builds and smokes the real default Linux amd64 image on relevant pull requests with read-only permissions, then uploads and downloads the exact artifact ID into a second job to verify its checksum and loaded image identity. It records the image's own `versions` output and embedded `image-inventory.json` in the checksummed artifact and checks that the reported gh and Codex versions match the inventory. It uses the existing explicit source inventory; no enrollment, Codex sign-in or host GitHub credential enters the build. Pull requests never publish.

A push to `main` that changes the same image inputs (controller source, container, image manifest, bundled skill, composition or release scripts, or this workflow) publishes automatically. Both PR and main runs execute the full `scripts/delivery/verify.sh` gate, because `main` has no required status checks; publication waits for it and for the artifact verification. Read-only build and verification jobs cancel superseded runs on the same ref. Publication and release jobs are serialized independently and never cancel an active write. Documentation-only changes and service code changes under `service/auth` do not publish a controller image. A verified [tool refresh](#tool-refresh) pins newer gh/Codex versions on `main` and dispatches this publication.

The build uploads its Docker archive, build metadata, versions output, inventory and checksums for seven days before publication proceeds through the main-only `controller-release` environment. The publish job loads that same artifact, checks its checksum/platform/UID/controller role/source revision, and receives `packages: write` only in that job. It publishes `ghcr.io/repoagent/controller:sha-FULL_SOURCE_SHA-RUN_ID-ATTEMPT`; reruns use fresh coordinates, and no `latest` tag is changed. A separate release job with only `contents: write` then creates the [baseline release](#baseline-releases).

Manual recovery stays available: `gh workflow run controller.yml --ref main` rebuilds, verifies and publishes current `main` with a new release. If only the release job failed, re-run that failed job in the same workflow run; it reuses the already published image coordinates.

Initial GHCR packages are private by default. An account with package administration rights must make `repoagent/controller` public under package **Settings > Danger Zone > Change visibility**, and grant this repository **Write** under **Manage Actions access** if access was not inherited. The workflow cannot report success until a clean anonymous registry client downloads the immutable image and all its layers into empty storage and confirms that its image config matches the approved build. A visibility failure can therefore leave a published private image; inspect the package and rerun deliberately after fixing access. No personal registry token or ACR is required. Use the public `ghcr.io/repoagent/controller@sha256:...` reference from the release or run summary; publication does not deploy a controller.

Action versions are pinned to current release commits. See [GitHub's image publication guidance](https://docs.github.com/en/actions/tutorials/publish-packages/publish-docker-images), [artifact upload](https://github.com/actions/upload-artifact) and [download](https://github.com/actions/download-artifact) documentation.

### Baseline releases

Each successful publication creates one GitHub Release for the anonymously verified image. Publication never updates a running controller; operators adopt a release deliberately through [local image update](local-runtime.md#update-an-immutable-image-or-restore-the-prior-image). Hosting lifecycle is outside the controller ([ADR 0009](adr/0009-keep-hosting-lifecycle-out-of-the-controller.md)); hosted operation will be redesigned around the status endpoint.

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

The controller's [Discussion dashboard](controller-operations.md#discussion-status-dashboard) is one such consumer: it reports a newer release as an operator update notice and never deploys it. Consumers should select releases by the `baseline-` tag prefix and this marker rather than GitHub's **Latest** badge, which manual `v*` controller releases can also hold. An existing release with the same tag is never replaced. Baseline tags are not covered by the `v*` tag ruleset; add `refs/tags/baseline-*` to the tag ruleset to block their update or deletion.

### Tool refresh

`Baseline tool refresh` checks for newer GitHub CLI and Codex CLI versions daily at 05:41 UTC, and on demand with `gh workflow run refresh-baseline-tools.yml --ref main`. It feeds a verified update through the normal baseline publication; it never changes a running controller.

Each tool has its own refresh (`refresh-baseline-tool.yml`, called once for gh and once for Codex). The two refreshes run one after the other, and a failure in one does not cancel the other. Each refresh selects from the current `main`, so the second one builds on a pin the first one made in the same run. Every refresh runs these steps for its own tool:

1. **Select.** `scripts/delivery/tool-refresh.py select TOOL` reads the latest stable GitHub release of `cli/cli` (gh) or `openai/codex` (Codex, `rust-v` tags) with the workflow token. A version is selected only if it is newer than the pin in `image/default.json` and the release publishes the exact Linux amd64 assets the image installs, with SHA-256 digests: `gh_VERSION_linux_amd64.tar.gz` for gh, or `codex-x86_64-unknown-linux-musl.tar.gz` and `codex-code-mode-host-x86_64-unknown-linux-musl.tar.gz` for Codex. An older upstream release never downgrades a pin. A newer release that is a draft, prerelease or other tag form, or that lacks one of these assets or digests, fails that tool's refresh visibly. Nothing newer ends the refresh successfully without changes. The selection records the `main` commit it was made from.
2. **Verify.** A read-only job checks out exactly that commit, pins the selected version in it and then runs the full `scripts/delivery/verify.sh` gate, whose native tests use the selected Codex. It then builds the default image with the selected tools, runs `versions` and NativeSmoke inside it and requires the versions the image reports to equal its inventory.
3. **Hosted smoke.** The reusable `e2e-smoke.yml` gate installs the exact selected candidate through the operator installer and observes a real Quick Tunnel webhook, EYES receipt and App reply. Cleanup is part of success. It uses repository secrets, not the publication identity; see below.
4. **Pin.** Only after verification and hosted smoke succeed, a job in the `controller-release` environment with `contents: write` and `actions: write` commits the new catalog lines for that tool in `scripts/compose-image.py` and `image/default.json` to `main`. Every other pin stays as reviewed. The commit is one GitHub API call that applies only if `main` is still the verified commit. It is signed by GitHub and its message names the tool, both versions and the verification run.
5. **Publish.** A commit made with the workflow token starts no push workflow, so the same job dispatches **Controller** on `main`. That run verifies again and creates the baseline release, which records the exact gh and Codex versions. When both tools are pinned in one run, each pin dispatches its own publication.

An incompatible update fails at selection or verification. It changes nothing on `main`, publishes nothing, and leaves existing baseline releases and images usable. Only that tool is held back: the other tool's refresh still selects, verifies, pins and publishes a supported update on its own. The run then shows as failed, and the daily run retries the same latest version until upstream publishes a fix or a maintainer intervenes, for example with a reviewed pull request that pins another version. If `main` moved after verification, the pin is refused and the next run selects and verifies again on the new `main`. If dispatch fails after the pin, the job fails and names the manual recovery: `gh workflow run controller.yml --ref main`.

Cloudflared, base images and the Ubuntu snapshot still change only through review. Containers always use the image's pinned tools, including with older saved settings; operators adopt refreshed tools by updating to the new baseline image. Startup never updates tools.

### Hosted installer smoke

Run the same hosted gate on a branch without publishing an image or changing pins:

```sh
gh workflow run controller.yml --ref BRANCH -f smoke=true
```

The branch must contain this implementation. Once `e2e-smoke.yml` is on the default branch, it can also be dispatched directly with `gh workflow run e2e-smoke.yml --ref BRANCH`. A workflow-token push does not trigger another workflow; the existing Controller entry point provides explicit branch dispatch. Ordinary pull-request verification remains read-only and does not receive these secrets.

The reusable smoke job holds one global `e2e-smoke` concurrency lock, across every branch and caller, with `cancel-in-progress: false`. A newer queued run can replace an older queued run under GitHub's concurrency semantics; it cannot cancel the running cleanup. Both the refresh caller and the Controller smoke entry point pass repository secrets with `secrets: inherit`. There is no `e2e` environment or new Azure/GitHub OIDC authority.

Before enrollment, the helper allows up to 110 seconds for an anonymous broker readiness check, including bounded GET retries for a scaled-to-zero service. A readiness 200 proves availability only; it does not establish enrollment, token issuance or settlement. An unconfirmed check stops before writing the Codex supply or starting enrollment.

Required repository secrets are `REPOAGENT_E2E_GH_TOKEN` (the `repoagent` participant's authority limited to `repoagent/ControllerTestRepo`, including Discussions and Agent-variable management) and `OPENAI_API_KEY`. The shared App must be installed on that private repository with the selected clone/webhook permissions. PowerShell 7.4+, Docker Linux containers and the pinned SDK are required on the runner. The image is built without credentials; the host GitHub CLI is downloaded from the same checksummed candidate catalog as the image.

The job sweeps only Discussions created by `repoagent` with the exact `[RepoAgent hosted smoke ID] mention` title and `repoagent.hosted-smoke/v1` body marker. `SMOKE_CONTROLLER_APPROVAL` in that test repository's Agent variables is reserved exclusively for this gate. Unexpected binding at this coordinate stops the sweep. It never sweeps arbitrary Discussions, variables, repositories or production controller state.

A fresh `install.ps1 -Action Install -NonInteractive -Apply` uses setup v4, `webhook-clone`, a Quick Tunnel, an empty mention-account override, an isolated instance and private state. The minimal live case in `DiscussionJourney` creates one App-mention Discussion and observes its actual creation webhook, successful observation, EYES and an App-authored marker reply. Reconciliation is set to one hour; a polling reply alone cannot pass the webhook assertions. It waits for the turn to complete before closing credentials.

Cleanup runs with `if: always()`, deletes owned fixtures even after an assertion failure, stops the owned controller, and calls `install.ps1 -Action Revoke`. It inspects the structured `githubMarker` and `brokerClosure` fields, not just exit zero. Only verified owned Docker storage is removed. Unconfirmed closure, a remaining approval, failed fixture removal or uncertain local shutdown fails the gate and prevents pinning. Private setup/enrollment, API-key supplies and raw controller/installer logs are never uploaded. The seven-day artifact contains only controlled fixture snapshots and nonsecret install/sweep/cleanup outcomes. A failed installation also records its exit code and at most 4,000 characters of diagnostic text after credential and opaque-token redaction; the complete installer log stays private.

A lost runner may leave an approval whose controller secret is no longer available. The next globally locked run withdraws that exact validated reserved approval and asks the broker to admit a fresh request. Only the exact supported `enrollment_not_ready` guidance triggers a recheck of the **same private request** after at least 60 seconds, bounded to 80 minutes overall. That interval is not a readiness promise. Unknown/malformed diagnostics and `enrollment_settlement_unconfirmed` stop for inspection. Pending discovery likewise resumes its same request after the installer's five-second guidance. No retry replaces an enrollment identity or rewrites private state.

Broker DELETE 204 confirms enrollment closure, never token settlement. Outcomes continue to say `outstandingTokens: unsettled`; a following tool's smoke can wait for the broker's safe settlement/preflight and can fail if readiness remains unconfirmed. This deliberately favors retaining old pins over bypassing capability settlement. A failing hosted run records an issue and leaves publication blocked.

Implementation and local workflow checks do not establish hosted provider acceptance. Keep #207 open until an actual branch run proves its real secrets, App scope, tunnel, reply and cleanup. Cloudflared selection exists, but its daily matrix entry stays disabled until that acceptance; adding a YAML gate alone does not authorize activating new cloudflared publication.

## Automatic broker delivery

Changes under `service/auth`, `infra/auth`, the broker delivery scripts/workflows or shared MSBuild/NuGet files trigger **Broker** verification. [Independent broker releases](broker-releases.md) begin at 1.0.0 and are proposed by Release Please; controller-only changes do not release or deploy the broker. A source push with no new approved release is verified without rebuilding or redeploying the previous version. Pull requests and explicit `operation=verify` branch runs use no publication or Azure authority. The explicit branch route covers release PRs created with `GITHUB_TOKEN`, whose normal PR events are suppressed.

After Release Please creates an approved `broker-v...` release, the release orchestrator explicitly dispatches delivery from `main`. The Broker workflow verifies the exact selected source and compiles Bicep, then checks one Linux amd64 image's role, numeric nonroot user, compiled version/source diagnostic and OCI labels. It anonymously verifies the immutable GHCR digest before creating a semantic tag. A separate release-metadata job confirms the non-clobbered `broker-release.json` version/source/digest mapping without modifying approved notes. Publication has `packages: write` and no Azure token; the metadata writer has only `contents: write`; deployment has `id-token: write` and no package write. Retries reuse recorded verified content and reject conflicting version tags or assets.

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
# Retry the current approved release from main (unreleased source is not published).
gh workflow run broker.yml --ref main -f operation=deploy
# Publish/deploy an exact approved semantic release, or resolve one for rollback.
gh workflow run broker.yml --ref main -f operation=release -f release-tag=broker-v1.0.0
gh workflow run broker.yml --ref main -f operation=deploy-release -f release-tag=broker-v1.0.0
# Deploy or roll back both modes to a previously approved immutable image.
gh workflow run broker.yml --ref main -f operation=deploy-image -f image=ghcr.io/repoagent/controller-auth@sha256:APPROVED_DIGEST
# Existing outputs-only OIDC access check.
gh workflow run broker.yml --ref main -f operation=access-check
```

Automatic deployment and manual rollback share noncanceling delivery concurrency and the same Bicep/app health path. Deployment across the two resources is not atomic. On failure, inspect Azure's deployment and actual resource state before retrying; do not infer that a failed workflow made no changes. Rollback is explicit and re-applies current Bicep configuration with the selected older image.
