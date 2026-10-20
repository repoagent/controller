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

**There is currently no implemented auth service/provider or published service image.** This workflow cannot create a working service today. It intentionally has no automatic source build or implicit infrastructure provisioning. Issue #35 owns initial Azure provisioning, identity federation, environment protections and operational acceptance. Missing setup fails; it is not a successful skipped deployment.

The concrete entry point updates one existing Azure Container App with an explicitly supplied immutable, publicly readable image. Before enabling it, service owners must supply and accept an auth-service image with OCI label `io.repoagent.role=auth-service` and Linux amd64 support, published to a dedicated repository. Anonymous `skopeo inspect` must be permitted. Private registry authentication is not implemented. The exact repository allowlist plus role label prevents accidental controller selection; labels are not publisher signatures. Repository publishing rights and the operator's approved digest are the trust boundary.

Create the GitHub environment **`auth-production`**, with required reviewers and deployment branches restricted to `main`. In that environment configure:

| Kind | Name | Required value |
| --- | --- | --- |
| Secret | `AZURE_CLIENT_ID` | Deployment identity application/client UUID |
| Secret | `AZURE_TENANT_ID` | Entra tenant UUID |
| Secret | `AZURE_SUBSCRIPTION_ID` | Explicit target subscription UUID |
| Variable | `AUTH_RESOURCE_GROUP` | Existing resource group |
| Variable | `AUTH_APP_NAME` | Existing Container App name |
| Variable | `AUTH_IMAGE_REPOSITORY` | Exact dedicated repository, e.g. `ghcr.io/example/repoagent-auth` (no tag/digest) |
| Variable | `AUTH_DEPLOYMENT_READY` | `true` only after service/infrastructure readiness acceptance |

These UUIDs are identifiers, stored as environment secrets to follow Azure Login's supported configuration guidance; there is **no client secret**. Configure the Entra federated credential with issuer `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, and subject `repo:repoagent/controller:environment:auth-production`. Only this deployment job requests `id-token: write`. Give its identity only the needed Container App read/write and revision-read permissions at the existing app scope; do not grant subscription Contributor, role-assignment, Key Vault key signing, or runtime data access. Validate the custom role against the actual tenant before enabling deployment. The service's runtime managed identity and its separate key/store permissions belong to service provisioning, not deployment login.

The existing app must carry Azure tag `repoagent-role=auth-service`, contain exactly one container, use `Single` active revision mode, and already have its ingress, probes, secrets, environment variables, registry access and runtime identity configured. The script validates the exact returned resource ID and role before updating only the named container image. It never creates an app or modifies secrets, ingress, identity, roles or resource groups.

Once those gates are satisfied, select a reviewed immutable image:

```sh
gh workflow run deploy-auth.yml --ref main \
  -f image=ghcr.io/example/repoagent-auth@sha256:REPLACE_WITH_64_LOWERCASE_HEX_DIGITS
```

Preflight validates configuration, repository/digest, image role and platform before OIDC login. After login, every Azure command explicitly selects the subscription/resource group/app. Inputs are passed as process arguments, never evaluated as shell code. Provider response bodies are captured and not printed. The script verifies the app template contains the selected digest and waits up to ten minutes for `latestRevisionName == latestReadyRevisionName`. Single revision mode lets Azure switch traffic after readiness. This does not prove end-to-end GitHub authorization or key/store behavior: production probes and service acceptance must cover those. An app may already have been updated if readiness verification fails; failure does not automatically roll back.

Rollback is another reviewed dispatch with the previous approved immutable auth-service digest, subject to the same environment gates and metadata checks. Retain those digests and images. Never reuse a controller digest. No live release, registry write, Azure login/deployment, environment mutation or identity provisioning is part of local workflow verification.

## Evidence and maintenance

Validate changed YAML with `actionlint` and scripts with Bash syntax checks plus the real local verification command. Action references are full commit SHAs verified against official release tags: checkout v6.1.0, setup-dotnet v5.4.0, Azure Login v3.1.0. Update these intentionally after reviewing upstream releases. Hosted Actions, GitHub Release publication, OIDC federation and Azure rollout still require hosted acceptance after setup; files and fake CLI tests are not deployed-service evidence.

Primary guidance: [GitHub secure workflow use](https://docs.github.com/en/actions/reference/security/secure-use), [setup-dotnet global.json support](https://github.com/actions/setup-dotnet), [Azure Login OIDC and supported versions](https://github.com/Azure/login), and [Container App update CLI](https://learn.microsoft.com/en-us/cli/azure/containerapp#az-containerapp-update).
