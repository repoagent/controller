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

## Auth service: gated manual deployment

**There is currently no implemented auth service/provider or published service image.** This workflow cannot create a working service today. It intentionally has no automatic source build or implicit infrastructure provisioning. The existing [Azure deployment runbook](azure-auth-deployment.md) records the verified resource-group OIDC bootstrap. Issue #35 retains service resource provisioning, runtime permissions and operational acceptance. Missing setup fails; it is not a successful skipped deployment.

The concrete entry point updates one existing Azure Container App with an explicitly supplied immutable, publicly readable image. Before enabling it, service owners must supply and accept an auth-service image with OCI label `io.repoagent.role=auth-service` and Linux amd64 support, published to a dedicated repository. Anonymous `skopeo inspect` must be permitted. Private registry authentication is not implemented. The exact repository allowlist plus role label prevents accidental controller selection; labels are not publisher signatures. Repository publishing rights and the operator's approved digest are the trust boundary.

Reuse the existing main-branch OIDC configuration in the [Azure deployment runbook](azure-auth-deployment.md). The deployment job intentionally has **no GitHub environment**: adding one would change the token subject and invalidate the current trust. It runs only on `main`, by explicit manual dispatch. Protect `main` and restrict who can dispatch workflows; the existing OIDC bootstrap does not establish branch protections or an approval gate. An environment approval gate would require a separately authorized trust change first.

These repository variables select the existing identity and target; only the three `AUTH_*` variables below remain to be configured after service acceptance:

| Kind | Name | Required value |
| --- | --- | --- |
| Variable | `AZURE_CLIENT_ID` | Existing deployment identity client UUID from the runbook |
| Variable | `AZURE_TENANT_ID` | Existing Entra tenant UUID |
| Variable | `AZURE_SUBSCRIPTION_ID` | Existing explicit target subscription UUID |
| Variable | `AZURE_RESOURCE_GROUP` | `rg-repoagent-controller-auth` (mapped to the script's `AUTH_RESOURCE_GROUP`) |
| Variable | `AZURE_LOCATION` | `swedencentral`; used by the existing bootstrap access check |
| Variable | `AUTH_APP_NAME` | Existing Container App name |
| Variable | `AUTH_IMAGE_REPOSITORY` | Exact dedicated repository, e.g. `ghcr.io/example/repoagent-auth` (no tag/digest) |
| Variable | `AUTH_DEPLOYMENT_READY` | `true` only after service/infrastructure readiness acceptance |

The UUIDs are non-secret repository variables, matching the verified bootstrap. Azure Login uses OIDC; there is **no client secret**. The existing federated credential has issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, and exact immutable subject `repo:repoagent@345029828/controller@1426154477:ref:refs/heads/main`. Do not replace it with a name-only or environment subject. This deployment job requests `id-token: write`; ordinary controller verification does not.

The existing deployment identity has Contributor scoped to this resource group. It cannot create runtime RBAC assignments, create other resource groups or register subscription providers. This workflow only updates a pre-provisioned app and does not broaden that role. Operators must provision the app's runtime identity, image-pull/data/signing permissions and required resources separately under #35. Deployment access does not establish runtime key/store access. The existing `ci-auth-service.yml` and `infra/auth/verify-access.bicep` remain the independent bootstrap access check; no service deployment or modification to those files is included here.

The existing app must carry Azure tag `repoagent-role=auth-service`, contain exactly one container, use `Single` active revision mode, and already have its ingress, probes, secrets, environment variables, registry access and runtime identity configured. The script validates the exact returned resource ID and role before updating only the named container image. It never creates an app or modifies secrets, ingress, identity, roles or resource groups.

Once those gates are satisfied, select a reviewed immutable image:

```sh
gh workflow run deploy-auth.yml --ref main \
  -f image=ghcr.io/example/repoagent-auth@sha256:REPLACE_WITH_64_LOWERCASE_HEX_DIGITS
```

Preflight validates configuration, repository/digest, image role and platform before OIDC login. After login, every Azure command explicitly selects the subscription/resource group/app. Inputs are passed as process arguments, never evaluated as shell code. Provider response bodies are captured and not printed. The script verifies the app template contains the selected digest and waits up to ten minutes for `latestRevisionName == latestReadyRevisionName`. Single revision mode lets Azure switch traffic after readiness. This does not prove end-to-end GitHub authorization or key/store behavior: production probes and service acceptance must cover those. An app may already have been updated if readiness verification fails; failure does not automatically roll back.

Rollback is another reviewed dispatch with the previous approved immutable auth-service digest, subject to the same main-branch, readiness and metadata checks. Retain those digests and images. Never reuse a controller digest. No live release, registry write, Azure login/deployment, environment mutation or identity provisioning is part of local workflow verification.

## Evidence and maintenance

Validate changed YAML with `actionlint` and scripts with Bash syntax checks plus the real local verification command. Action references are full commit SHAs verified against official release tags: checkout v6.1.0, setup-dotnet v5.4.0, Azure Login v3.1.0. Update these intentionally after reviewing upstream releases. The existing access-check run proves bootstrap OIDC access. The new workflows, GitHub Release publication and actual Azure service rollout still require hosted acceptance after setup; files and fake CLI tests are not deployed-service evidence.

Primary guidance: [GitHub secure workflow use](https://docs.github.com/en/actions/reference/security/secure-use), [setup-dotnet global.json support](https://github.com/actions/setup-dotnet), [Azure Login OIDC and supported versions](https://github.com/Azure/login), and [Container App update CLI](https://learn.microsoft.com/en-us/cli/azure/containerapp#az-containerapp-update).
