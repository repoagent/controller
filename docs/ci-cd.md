# Controller and broker delivery workflows

The **Controller** workflow (`controller.yml`) verifies controller changes on pull requests and pushes to `main`, with read-only permissions and no persisted checkout credentials. It uses the root `global.json` (.NET 11 RC), executes the actual xUnit runner, checks nonzero discovery, runs the controller process tests in `tests/installers` and the delivery tests, then builds Native AOT and executes native help plus NativeSmoke. The controller project runs the trim and AOT analyzers on every build and treats all warnings as errors, and the Native AOT packaging uses the same warning policy as the image build, so code that would fail image publication (for example IL2026/IL3050) fails this gate first. The controller core (`src/RepoAgentController.Core`, [ADR 0010](adr/0010-structure-the-controller-and-broker-as-ports-and-adapters.md)) also runs the banned-API analyzer: its `BannedSymbols.txt` rejects the common file, network, process, environment and wall-clock APIs and `JsonNode`, and review catches the rest. Both verification scripts compile and verify formatting of every project through `RepoAgent.slnx`, including live tests without running credential-dependent scenarios. The broker and controller share the exact .NET 11 RC3 SDK and [analyzer policy](code-quality.md).

```sh
bash scripts/delivery/verify.sh /tmp/controller-verification
```

Local prerequisites are the pinned SDK, Python 3 with its `venv` module, clang and zlib development headers. The shared `scripts/delivery/test-python.sh` launcher creates a temporary isolated environment and downloads checksum-pinned JMESPath 1.0.1 for delivery tests; it requires HTTPS access to files.pythonhosted.org and does not require pip or global package installation. Both controller verification and Broker CI use that launcher. To run a focused suite locally, use `bash scripts/delivery/test-python.sh scripts.delivery.test_broker_diagnostics -v`.

The installer (`install.*`, `repoagent*`, `scripts/*.ps1`) and the container runtime (`container/`) have no automated tests ([ADR 0010](adr/0010-structure-the-controller-and-broker-as-ports-and-adapters.md)). Test changes manually against the local AppHost using the [run steps](local-apphost.md); credential-dependent live acceptance remains tracked in [#204](https://github.com/repoagent/controller/issues/204).

The repository has four workflow entry points: **Controller**, **Broker**, the scheduled **Baseline tool refresh** (with a reusable per-tool helper), and **Hosted installer smoke**. Controller owns verification, image publication and native tagged releases. Broker owns verification, deployment and recovery operations. Superseded read-only checks are cancelled; publication and deployment writes finish once started.

## Controller releases

Controller and credential broker versions are independent. Pinned Release Please proposes a version and editable notes in a release PR, starting each component at 1.0.0. See the [component release policy](releases.md) for compatibility, reviewed Conventional Commits, shared source attribution, tag protection and workflow-token event suppression. Enable GitHub Actions to create pull requests before enabling the proposal workflow.

The ordinary **Controller** workflow verifies relevant PRs and main changes with read-only authority. It also accepts explicit branch `verify=true` dispatches for Release Please PRs, because a PR created with `GITHUB_TOKEN` does not start normal PR workflows. It does not publish on an ordinary push. Hosted branch smoke remains a separate `smoke=true` path with no publication authority.

After an approved release PR is merged, Release Please creates `controller-vMAJOR.MINOR.PATCH` on its exact main commit. A separate **Controller release** workflow selects that immutable tag, verifies main ancestry and version/notes agreement, runs the full gate, builds both native archive and image, and checks that compiled diagnostics, inventory and exact offline notes agree. A semantic GitHub Release record can exist while artifacts are still pending; only verified `controller-release.json` metadata and its recorded assets establish completed publication.

The native Linux amd64 Native AOT archive requires glibc 2.39 or newer and normal ICU/OpenSSL dependencies. Windows and ARM executables are not supplied. It includes the executable, packaged skills, `VERSION`, `RELEASE_NOTES.md` and `SHA256SUMS`. Image `/opt/repoagent/controller/RELEASE_NOTES.md` contains the same bytes; `versions` reports the compiled controller version separately from tool versions. Image inventory records the source version and notes hash, while source commit, full bundled-tool inventory and immutable image digest remain separate provenance.

Publication uses the existing main-only `controller-release` environment. Its registry job has package write but no release write; finalization has release write but no package write. One global noncanceling semantic-release lock serializes candidates. The registry version alias `ghcr.io/repoagent/controller:controller-vMAJOR.MINOR.PATCH` is written only after positive absence and anonymous verification of the candidate immutable image. The release publishes that digest with native-asset hashes. A released alias or conflicting asset is never overwritten.

Manual recovery dispatch selects the approved semantic tag, not current main:

```sh
gh workflow run controller-release.yml --ref main -f tag=controller-v1.2.3
```

A completed retry verifies recorded assets and anonymous immutable-image identity, then leaves them unchanged. If an alias push succeeds but its observation fails, the failed publish job retains a `controller-publication-RUN-ATTEMPT` artifact. Its `publication.json` was recorded before the alias write and binds the verified digest/config, source/version, notes, inventory and native asset hashes. Dispatch recovery with the original run ID and that publication artifact ID:

```sh
gh workflow run controller-release.yml --ref main -f tag=controller-v1.2.3 -f recovery-run=123 -f recovery-artifact=456
```

Recovery accepts only artifacts from this repository's main Controller release workflow, compares their plan with the approved tag, and skips rebuilding. It anonymously verifies the exact retained immutable image/config and existing alias, then resumes finalization without pushing any image. A missing receipt, uncertain registry observation, absent recovery alias or mismatching identity fails closed for inspection. Never delete or move a released tag/alias or use `--clobber` to conceal a mismatch. Failed visibility checks may leave an unpublished/private candidate; make GHCR public and grant repository package write, then inspect before retrying. No `latest` tag or running controller is changed.

### Baseline releases

The dashboard reads completed `controller-vMAJOR.MINOR.PATCH` releases in numerical SemVer order, excludes previews and broker releases, and identifies its running image by inventory digest. Its v2 machine-readable body marker is `<!-- repoagent.controller-release/v2 -->`; `controller-release.json` records version, source, image/config digests, inventory/notes hashes and native-asset hashes. Missing metadata is not a completed baseline.

Historical `baseline-RUN_ID.ATTEMPT` releases retain their v1 metadata and remain identifiable. They are not semantic versions. When a legacy running image meets semantic releases, the dashboard preserves its inventory identity and explicitly requires deliberate migration; it does not claim semantic currentness or downgrade. Older controllers only understand legacy tags, so operators must deliberately adopt a verified semantic image once before its dashboard can discover later semantic versions. The controller never updates itself; preserve external configuration, credentials and session data during [image adoption](local-runtime.md#update-an-immutable-image-or-restore-the-prior-image).

### Tool refresh

`Baseline tool refresh` checks for newer GitHub CLI and Codex CLI versions daily at 05:41 UTC, and on demand with `gh workflow run refresh-baseline-tools.yml --ref main`. It feeds a verified update through the normal baseline publication; it never changes a running controller.

Each tool has its own refresh (`refresh-baseline-tool.yml`, called once for gh and once for Codex). The two refreshes run one after the other, and a failure in one does not cancel the other. Each refresh selects from the current `main`, so the second one builds on a pin the first one made in the same run. Every refresh runs these steps for its own tool:

1. **Select.** `scripts/delivery/tool-refresh.py select TOOL` reads the latest stable GitHub release of `cli/cli` (gh) or `openai/codex` (Codex, `rust-v` tags) with the workflow token. A version is selected only if it is newer than the pin in `image/default.json` and the release publishes the exact Linux amd64 assets the image installs, with SHA-256 digests: `gh_VERSION_linux_amd64.tar.gz` for gh, or `codex-x86_64-unknown-linux-musl.tar.gz` and `codex-code-mode-host-x86_64-unknown-linux-musl.tar.gz` for Codex. An older upstream release never downgrades a pin. A newer release that is a draft, prerelease or other tag form, or that lacks one of these assets or digests, fails that tool's refresh visibly. Nothing newer ends the refresh successfully without changes. The selection records the `main` commit it was made from.
2. **Verify.** A read-only job checks out exactly that commit, pins the selected version in it and then runs the full `scripts/delivery/verify.sh` gate, whose native tests use the selected Codex. It then builds the default image with the selected tools, runs `versions` and NativeSmoke inside it and requires the versions the image reports to equal its inventory.
3. **Hosted smoke.** The reusable `e2e-smoke.yml` gate installs the exact selected candidate through the operator installer and observes a real Quick Tunnel webhook, EYES receipt and App reply. Cleanup is part of success. It uses repository secrets, not the publication identity; see below.
4. **Pin.** Only after verification and hosted smoke succeed, a job in the `controller-release` environment with `contents: write` and `actions: write` commits the new catalog lines for that tool in `scripts/compose-image.py` and `image/default.json` to `main`. Every other pin stays as reviewed. The commit is one GitHub API call that applies only if `main` is still the verified commit. It is signed by GitHub and its message names the tool, both versions and the verification run.
5. **Propose.** A workflow-token commit starts no push workflow, so the same job explicitly dispatches **Release Please** on main. Verified tool-only changes use `fix(controller)` with previous/new tool versions and propose a controller patch release. Upstream tool major versions do not redefine controller compatibility. Approved release notes and source version then drive the semantic artifact workflow.

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

The same gate discovers an existing Discussion category and enables it with `-DashboardCategory`. It records the category's pre-install Discussion IDs, refuses to adopt an existing App dashboard, observes the new App-authored dashboard title/body and a second heartbeat, and records the pinned runtime's actual usage outcome before the mention starts a turn. Available values, unsupported/unavailable collection and failed reads remain distinct; no quota exhaustion is induced. Cleanup additionally requires the exact new dashboard's creation log from this run's container before deleting it. Missing ownership evidence fails closed and retains it for inspection. API Markdown evidence does not prove GitHub's light/dark or narrow-screen rendering.

Cleanup runs with `if: always()`, deletes owned fixtures even after an assertion failure, stops the owned controller, and calls `install.ps1 -Action Revoke`. It inspects the structured `githubMarker` and `brokerClosure` fields, not just exit zero. Only verified owned Docker storage is removed. Unconfirmed closure, a remaining approval, failed fixture removal or uncertain local shutdown fails the gate and prevents pinning. Private setup/enrollment, API-key supplies and raw controller/installer logs are never uploaded. The seven-day artifact contains only controlled fixture snapshots and nonsecret install/sweep/cleanup outcomes. A failed installation also records its exit code and at most 4,000 characters of diagnostic text after credential and opaque-token redaction; the complete installer log stays private.

An installer command timeout records the same bounded redacted diagnostic with `state: timed-out` and no invented exit code. The outer 960-second wait allows the broker's bounded 900-second operation to finish its bookkeeping. Same-request recheck messages are flushed to the live job log without publishing private installer output.

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
| `AUTH_REQUEST_LIMIT_PER_MINUTE` | Optional: default 120, allowed 1-10000 |
| `AUTH_CONCURRENT_REQUEST_LIMIT` | Optional: default 8, allowed 1-128 |
| `AUTH_REQUEST_TIMEOUT_SECONDS` | Optional: default 15, allowed 1-60 |

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
