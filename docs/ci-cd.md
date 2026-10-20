# Controller verification, releases, and auth-service deployment

`Verify controller` runs on pull requests and pushes to `main` on GitHub-hosted Ubuntu 24.04 amd64, with read-only repository permission and no persisted checkout credentials. It uses `global.json` (currently .NET 11 RC); it does not change the planned auth service's .NET 10 LTS choice. `dotnet run --project tests/RepoAgentController.Tests/RepoAgentController.Tests.csproj --configuration Release` invokes the real xUnit runner. `dotnet test` is not an equivalent command for this project. The workflow also runs the installer process suite with real Bash and Linux PowerShell, Native AOT publication, native `--help`, and NativeSmoke. Installer Docker calls use the existing executable fixture; native Windows Docker Desktop acceptance remains separate.

Run the same checks locally from the repository root:

```sh
bash scripts/delivery/verify.sh /tmp/controller-verification
```

Install the SDK selected by `global.json`, Python 3, PowerShell, clang and zlib development headers first. These build prerequisites are present on the selected GitHub runner. No live credentials are needed. The deployment guard tests replace only CLI responses; they cannot prove Azure readiness.

## Controller binary release

The release is a Linux amd64 Native AOT executable targeting the Ubuntu 24.04 build baseline (glibc 2.39 or newer; normal .NET native dependencies including ICU/OpenSSL may be needed). It is not a static musl binary and is not a Windows or ARM release. Codex and other integrations remain separately installed runtime dependencies.

An operator must create the `controller-release` GitHub environment, restrict it to `main`, and configure required reviewers before using this workflow. Protect `main` and release tags from unauthorized edits. No environment credentials are required; only the release job has `contents: write`. A maintainer creates an existing version tag (`v1.2.3` or `v1.2.3-rc.1`) pointing to a reviewed commit on `main` that includes these workflows/scripts, then intentionally dispatches:

```sh
gh workflow run release-controller.yml --ref main -f tag=v1.2.3
```

The workflow validates the tag, verifies its commit is in `main`, checks out that commit, runs verification, and publishes its distribution to GitHub Releases. Ordinary pushes and PRs never publish. Tags must remain immutable. Existing releases are refused, including partial uploads, so retries cannot overwrite artifacts; inspect a partial release and make an explicit operator recovery decision.

Local packaging without publication:

```sh
bash scripts/delivery/package-controller.sh 1.2.3 /tmp/controller-release
```

Artifacts:

```text
repoagent-controller-1.2.3-linux-amd64.tar.gz
repoagent-controller-1.2.3-linux-amd64.tar.gz.sha256
  archive: repoagent-controller-1.2.3-linux-amd64/
    RepoAgentController
    github-discussion/SKILL.md
    VERSION                  # version, full source commit, runtime
    SHA256SUMS               # executable, skill, VERSION
```

Verify the external checksum before extraction, then `sha256sum -c SHA256SUMS` in the extracted directory. Preserve the skill directory next to the executable. The source commit is also compiled into informational version metadata. Local builds record HEAD and can include local edits; publish only from the workflow's clean tagged checkout. This stable archive contract enables a future image builder to consume a reviewed binary without copying developer state. It does not publish an image or change the existing image builder.

## Auth service target and manual rollback

**There is currently no implemented auth service/provider or published service image.** This workflow cannot create a working service today. The automatic pipeline below is explicitly gated on real service source and infrastructure; it cannot yet build or provision a working service. The existing [Azure deployment runbook](azure-auth-deployment.md) records the verified resource-group OIDC bootstrap. Issue #35 retains service resource provisioning, runtime permissions and operational acceptance. Missing setup fails; it is not a successful skipped deployment.

The concrete entry point updates one existing Azure Container App with an explicitly supplied immutable, publicly readable image. Before enabling it, service owners must supply and accept an auth-service image with OCI label `io.repoagent.role=auth-service` and Linux amd64 support, published to a dedicated repository. Anonymous `skopeo inspect` must be permitted. Private registry authentication is not implemented. The exact repository allowlist plus role label prevents accidental controller selection; labels are not publisher signatures. Repository publishing rights and the operator's approved digest are the trust boundary.

Reuse the existing main-branch OIDC configuration in the [Azure deployment runbook](azure-auth-deployment.md). The deployment job intentionally has **no GitHub environment**: adding one would change the token subject and invalidate the current trust. Cloud and publication jobs run only on `main`; the rollback entrypoint is manually dispatched. Protect `main` and restrict who can dispatch workflows; the existing OIDC bootstrap does not establish branch protections or an approval gate. An environment approval gate would require a separately authorized trust change first.

These repository variables select the existing identity and target; the target variables below and the automatic-delivery prerequisites described later must be configured after service acceptance:

| Kind | Name | Required value |
| --- | --- | --- |
| Variable | `AZURE_CLIENT_ID` | Existing deployment identity client UUID from the runbook |
| Variable | `AZURE_TENANT_ID` | Existing Entra tenant UUID |
| Variable | `AZURE_SUBSCRIPTION_ID` | Existing explicit target subscription UUID |
| Variable | `AZURE_RESOURCE_GROUP` | `rg-repoagent-controller-auth` (mapped to the script's `AUTH_RESOURCE_GROUP`) |
| Variable | `AZURE_LOCATION` | `swedencentral`; used by the existing bootstrap access check |
| Variable | `AUTH_APP_NAME` | Existing Container App name |
| Variable | `AUTH_IMAGE_REPOSITORY` | Exact dedicated repository, `ghcr.io/repoagent/controller-auth` (no tag/digest) |
| Variable | `AUTH_DEPLOYMENT_READY` | `true` only after service/infrastructure readiness acceptance |

The UUIDs are non-secret repository variables, matching the verified bootstrap. Azure Login uses OIDC; there is **no client secret**. The existing federated credential has issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, and exact immutable subject `repo:repoagent@345029828/controller@1426154477:ref:refs/heads/main`. Do not replace it with a name-only or environment subject. This deployment job requests `id-token: write`; ordinary controller verification does not.

The existing deployment identity has Contributor scoped to this resource group. It cannot create runtime RBAC assignments, create other resource groups or register subscription providers. This workflow only updates a pre-provisioned app and does not broaden that role. Operators must provision the app's runtime identity, image-pull/data/signing permissions and required resources separately under #35. Deployment access does not establish runtime key/store access. The existing `ci-auth-service.yml` and `infra/auth/verify-access.bicep` remain the independent bootstrap access check; no service deployment or modification to those files is included here.

The existing app must carry Azure tag `repoagent-role=auth-service`, contain exactly one container, use `Single` active revision mode, and already have its ingress, probes, secrets, environment variables, registry access and runtime identity configured. The script validates the exact returned resource ID and role before updating only the named container image. It never creates an app or modifies secrets, ingress, identity, roles or resource groups.

Once those gates are satisfied, select a reviewed immutable image:

```sh
gh workflow run deploy-auth.yml --ref main \
  -f image=ghcr.io/repoagent/controller-auth@sha256:REPLACE_WITH_64_LOWERCASE_HEX_DIGITS
```

Preflight validates configuration, repository/digest, image role and platform before OIDC login. After login, every Azure command explicitly selects the subscription/resource group/app. Inputs are passed as process arguments, never evaluated as shell code. Provider response bodies are captured and not printed. The script verifies the app template contains the selected digest and waits up to ten minutes for `latestRevisionName == latestReadyRevisionName`. Single revision mode lets Azure switch traffic after readiness. This does not prove end-to-end GitHub authorization or key/store behavior: production probes and service acceptance must cover those. An app may already have been updated if readiness verification fails; failure does not automatically roll back.

Rollback is another reviewed dispatch with the previous approved immutable auth-service digest, subject to the same main-branch, readiness and metadata checks. Retain those digests and images. Never reuse a controller digest. No live release, registry write, Azure login/deployment, environment mutation or identity provisioning is part of local workflow verification.

## Evidence and maintenance

Validate changed YAML with `actionlint` and scripts with Bash syntax checks plus the real local verification command. Action references are full commit SHAs verified against official release tags: checkout v6.1.0, setup-dotnet v5.4.0, Azure Login v3.1.0. Update these intentionally after reviewing upstream releases. The existing access-check run proves bootstrap OIDC access. The new workflows, GitHub Release publication and actual Azure service rollout still require hosted acceptance after setup; files and fake CLI tests are not deployed-service evidence.

Primary guidance: [GitHub secure workflow use](https://docs.github.com/en/actions/reference/security/secure-use), [setup-dotnet global.json support](https://github.com/actions/setup-dotnet), [Azure Login OIDC and supported versions](https://github.com/Azure/login), and [Container App update CLI](https://learn.microsoft.com/en-us/cli/azure/containerapp#az-containerapp-update).

## Automatic auth-service delivery

`Auth service CI` now extends the existing access check. Auth-related pull requests run read-only verification; matching changes merged into `main` automatically verify, build, publish an immutable image, apply guarded Bicep and check the app's ready revision. Paths include `service/auth/**`, `infra/auth/**`, the auth workflow/scripts/tests and root shared MSBuild/NuGet configuration. Controller-only changes do not trigger auth deployment. The independent controller CI remains active. Automatic delivery and manual digest rollback share one concurrency group so they cannot update the app simultaneously.

**This pipeline is currently blocked by real dependencies, and auth-path runs fail with an actionable error.** It never substitutes a healthy placeholder or turns missing prerequisites into a skipped-success result. The provider must deliver this isolated source contract under #30/#31:

- `service/auth/global.json` pins a stable .NET 10 SDK independently of the controller SDK.
- `service/auth/verify.sh` runs the real service tests from that directory, fails on test failure or zero discovery, and verifies provider/store/cleanup behavior. This is a reviewed source-owner entrypoint, not generated fake test evidence.
- `service/auth/Dockerfile` builds only the service from the `service/auth` context. It declares OCI label `io.repoagent.role=auth-service`, an explicit nonzero numeric `USER` (optionally with a numeric group), Linux amd64 support, port 8080, liveness `/health/live` and readiness `/health/ready`. It must not copy controller/Codex/user credentials or the App private key. Runtime secrets arrive through provisioned references and runtime identity.

Infrastructure slice #47 owns `infra/auth/main.bicep`, `validate.sh` and `deploy.sh`. The pipeline calls credential-free `validate.sh` during verification, local `deploy.sh check` before publication, and `deploy.sh apply` only after image inspection and Azure OIDC login. Apply performs ownership checks, what-if and Incremental deployment inside the existing resource group. It rejects conflicting resources rather than adopting them. Foundation-only provisioning is a separate explicit operator step; automatic delivery never runs foundation modes. The foundation identity/vault/private Blob resources and operator-created runtime role assignments must already exist before runtime apply. See the [infrastructure runbook](auth-infrastructure.md) delivered with #47 for provisioning details. The Ubuntu 24.04 hosted runner supplies the standalone Bicep compiler; local verification requires `bicep` on PATH or `BICEP_BIN`. The infrastructure scripts do not install tools or authenticate automatically.

In addition to the existing variables above, runtime application requires repository variables:

| Variable | Contract |
| --- | --- |
| `AUTH_IMAGE_REPOSITORY` | Exactly `ghcr.io/repoagent/controller-auth`, dedicated to the service |
| `AUTH_PROVIDER_READY` | `true` after actual provider acceptance |
| `AUTH_STORAGE_READY` | `true` after the chosen durable store is implemented and accepted |
| `AUTH_CLEANUP_READY` | `true` after expiry/cleanup behavior is implemented and accepted |
| `AUTH_RUNTIME_ACCESS_READY` | `true` after operator-created runtime role assignments are verified |
| `AUTH_SIGNING_KEY_URI` | Versioned `/keys/name/version` URI in the foundation Key Vault |
| `AUTH_DATA_PROTECTION_KEY_URI` | Versioned `/keys/name/version` URI in that vault |
| `AUTH_OAUTH_CLIENT_SECRET_URI` | Versioned `/secrets/name/version` URI in that vault |
| `AUTH_STORE_RESOURCE_ID` | Non-secret same-subscription ARM resource ID of the accepted provider store |

`AUTH_DEPLOYMENT_READY=true` remains the overall acceptance gate. These flags are operator attestations, not automated proof of provider readiness. The pipeline does not choose a database, import keys/secrets, or assign runtime RBAC. The existing Contributor identity cannot make those role assignments.

The dedicated GHCR package must **already exist, be public, and grant this repository's Actions token access** before automatic publication. Initial package bootstrap and visibility/access configuration are separate explicit operator tasks after the real service exists; the workflow does not change package visibility or request a personal token. It checks visibility through the user-owned package endpoint `users/repoagent/packages/container/controller-auth` before any Docker push; `repoagent` is a user account, not an organization. GitHub's first package publication defaults to private, so setting a repository variable alone is insufficient. [GitHub Container registry guidance](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry) describes package visibility and Actions access.

Only the trusted-main publish job grants `packages: write`; it logs in with its short-lived `GITHUB_TOKEN`. The image gets a source-commit tag for traceability, but deployment consumes only the resolved SHA256 digest. Before pushing, the built image's role, platform and numeric user are checked. After publishing and again before provisioning, `skopeo inspect --no-creds --config` verifies the exact digest anonymously. Only then are the infrastructure inputs `AUTH_IMAGE_ROLE=auth-service`, `AUTH_IMAGE_NON_ROOT=true`, and `AUTH_IMAGE_PUBLIC=true` supplied. These attestations are never accepted as user workflow inputs. The Azure job has no package write permission and the publish job has no Azure OIDC permission.

Manual operations remain available:

```sh
# Existing outputs-only access check (no service deployment).
gh workflow run ci-auth-service.yml --ref main -f operation=access-check
# Re-run complete verified source delivery after prerequisites exist.
gh workflow run ci-auth-service.yml --ref main -f operation=deploy
# Existing-app rollback/redeploy; does not reapply infrastructure.
gh workflow run deploy-auth.yml --ref main -f image=ghcr.io/repoagent/controller-auth@sha256:APPROVED_DIGEST
```

An image may have been published if a subsequent Azure check fails. Infrastructure apply may have changed resources if revision readiness fails. Inspect the failed stage and use an approved prior digest for explicit rollback; no automatic rollback or cleanup is implied. Workflow source and process doubles establish orchestration, not a published image, successful Bicep apply or functioning auth provider.
