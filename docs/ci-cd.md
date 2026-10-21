# Verification, controller releases and auth-service delivery

`Verify controller` runs on pull requests and pushes to `main`, with read-only permissions and no persisted checkout credentials. It uses the root `global.json` (.NET 11 RC), executes the actual xUnit runner, checks nonzero discovery, runs installer and delivery process tests, then builds Native AOT and executes native help plus NativeSmoke. The auth service independently pins .NET 10 LTS.

```sh
bash scripts/delivery/verify.sh /tmp/controller-verification
```

Local prerequisites are the pinned SDK, Python 3, PowerShell 7, clang and zlib development headers. The separate installer workflow runs the expanded Linux suite and native Windows PowerShell revocation checks. These process tests use controlled GitHub/Docker boundaries; they do not establish a complete Windows Docker Desktop installation.

## Controller releases

An intentional dispatch publishes a Linux amd64 Native AOT distribution to GitHub Releases. The Ubuntu 24.04 build requires glibc 2.39 or newer and normal .NET native dependencies, including ICU/OpenSSL. Codex and GitHub CLI are separate image/runtime dependencies. Windows and ARM executables are not supplied.

Before publication, create the `controller-release` GitHub environment, restrict it to `main`, and configure its required reviewers. Protect main and release tags. This environment has no Azure trust or credentials; only the release job receives `contents: write`.

Create an immutable version tag on a reviewed main commit containing the delivery scripts, then dispatch from main:

```sh
gh workflow run release-controller.yml --ref main -f tag=v1.2.3
```

The job validates the tag, checks its commit belongs to main, checks out that source, verifies it and packages it. Ordinary PRs/pushes never publish. An existing release, including a partial upload, is refused to preserve assets; inspect and resolve a partial release explicitly.

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
    VERSION                  # version, full source commit, runtime
    SHA256SUMS               # executable, skill and VERSION
```

Verify the archive checksum before extraction, then run `sha256sum -c SHA256SUMS` inside it. Keep the skill beside the executable. Local builds record HEAD and can include edits; release only from the workflow's clean tagged source. This archive interface supports later image composition without developer credentials.

## Automatic auth-service delivery

`Auth service CI` retains the existing manual access check and adds read-only PR verification plus automatic main delivery. Its paths cover `service/auth`, `infra/auth`, auth delivery scripts/tests/workflows and shared root MSBuild/NuGet files. Controller-only changes do not trigger auth delivery. The actual service tests, Bicep compiler and infrastructure process/compiled-template tests run before publication. PR jobs receive no publication or Azure credentials.

The service implements repository-approved enrollment, installation-token issuance and renewal, self-revocation, and the independent `cleanup --once` command. `/health/ready` checks required service registrations without exercising GitHub, storage or signing. Source verification and a healthy response do not establish production acceptance. Missing acceptance or configuration still blocks normal publication/provisioning; the workflow does not substitute a healthy placeholder.

After all prerequisites are accepted, trusted main jobs build a Linux amd64 service image, verify its role and numeric nonroot user, publish it and resolve an immutable digest. The Azure job anonymously verifies that digest, runs guarded resource ownership/what-if checks, applies Incremental Bicep and waits for the intended ready revision. Automatic delivery and manual rollback share a concurrency group. Publication has `packages: write` and no Azure token; deployment has `id-token: write` and no package write.

The source interface is `service/auth/global.json`, real nonzero-discovery `verify.sh` and `Dockerfile`. The build context contains only the auth service. The image declares `io.repoagent.role=auth-service`, port 8080, numeric nonzero USER, `/health/live` and `/health/ready`; it contains no App private key, host GitHub credential or Codex credential.

## Azure setup and acceptance gates

Reuse the exact resource group, subscription, identity and main-only OIDC subject in the [Azure deployment runbook](azure-auth-deployment.md). The auth jobs use **no GitHub environment**. Adding one changes the subject and requires matching federation before use. Azure login uses official OIDC exchange with repository variables, never a deployment client secret. Protect main and control workflow dispatch rights; the OIDC bootstrap does not establish branch protection.

| Repository variable | Required value |
| --- | --- |
| `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` | Existing nonsecret deployment identity/target UUIDs from the runbook |
| `AZURE_RESOURCE_GROUP`, `AZURE_LOCATION` | `rg-repoagent-controller-auth`, `swedencentral` |
| `AUTH_APP_NAME` | Intended owned Container App |
| `AUTH_IMAGE_REPOSITORY` | Exactly `ghcr.io/repoagent/controller-auth` |
| `AUTH_DEPLOYMENT_READY` | `true` only after complete service/platform acceptance |
| `AUTH_PROVIDER_READY`, `AUTH_STORAGE_READY`, `AUTH_CLEANUP_READY`, `AUTH_RUNTIME_ACCESS_READY` | Each `true` only after the corresponding real acceptance |
| `AUTH_SIGNING_KEY_URI`, `AUTH_DATA_PROTECTION_KEY_URI` | Separate versioned `/keys/name/version` references in the owned vault |
| `AUTH_TABLE_NAME` | Dedicated broker Table; defaults to `brokerstate` |
| `AUTH_DEPLOYMENT_EPOCH` | Required external restore-fencing epoch, 1–128 ASCII letters/digits, hyphen or underscore |
| `AUTH_REQUEST_LIMIT_PER_MINUTE` | Optional: 120 by default, allowed 1–10000 |
| `AUTH_CONCURRENT_REQUEST_LIMIT` | Optional: 8 by default, allowed 1–128 |
| `AUTH_REQUEST_TIMEOUT_SECONDS` | Optional: 15 by default, allowed 1–60 |

The Table/runtime configuration above is implemented in the merged #55 infrastructure changes. The endpoint and managed-identity client ID derive from owned resources. There is no human GitHub OAuth client secret in the UI-free protocol. The approving operator's host GitHub CLI manages a direct repository Agent variable; controller runtime tokens cannot change it. No GitHub Environment or inherited organization variable supplies approval.

Readiness flags are operator attestations, not automated proof. Keep them unset while requirements are incomplete. Real Azure managed-identity Table transactions and scoped denial, signing, GitHub authorization, cleanup, restore and the staged user journey remain required before enabling production delivery.

The [infrastructure runbook](auth-infrastructure.md) owns foundation provisioning, guarded `deploy.sh check/apply`, and operator-only runtime roles. The existing deployment Contributor cannot assign RBAC. Automatic delivery never runs foundation modes, imports keys, grants roles or selects alert recipients. Preserve the external epoch outside Table backup. Ordinary main OIDC provisioning stays within the existing group and refuses conflicting ownership.

The dedicated GHCR package must already exist, be public and grant this repository's Actions token access before automatic publication. Initial bootstrap/visibility configuration is separate. The publication preflight validates the client, tenant and subscription UUIDs before any infrastructure, registry or image provider calls. It checks `users/repoagent/packages/container/controller-auth`; repoagent is a user account. A variable alone cannot make a newly published private package public. See [GitHub Container registry access](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry).

Anonymous digest inspection verifies Linux amd64, the auth role and numeric nonroot USER. Role labels prevent accidental controller selection; registry publishing authority and the approved digest establish trust. Private registry authentication is not implemented. No personal token is requested. Image role/nonroot/public attestations passed to Bicep are generated after these checks, not accepted as workflow inputs.

## Manual access check, retry and rollback

```sh
# Existing outputs-only OIDC access check.
gh workflow run ci-auth-service.yml --ref main -f operation=access-check
# Full delivery retry after every prerequisite is accepted.
gh workflow run ci-auth-service.yml --ref main -f operation=deploy
# Existing-app rollback/redeploy; does not reapply infrastructure.
gh workflow run deploy-auth.yml --ref main -f image=ghcr.io/repoagent/controller-auth@sha256:APPROVED_DIGEST
```

The manual existing-app path runs the same dedicated-repository, anonymous OCI-config and numeric nonroot-user attestation as automatic delivery before any Azure update. It requires one container, Single revision mode, the exact resource ID and `repoagent-role=auth-service` tag. It updates only the named container image, leaving secrets, ingress and runtime identity alone. Inputs are process arguments, never shell code. Provider response bodies are captured rather than printed. It checks the configured digest and waits up to ten minutes for the latest ready revision.

A failure may occur after publication or Azure changes. Inspect that stage and dispatch a previously accepted digest for rollback; rollback and cleanup are not automatic. Revision readiness does not prove the entire GitHub authorization journey.

Validate YAML with actionlint, shell syntax with Bash, and execute the real local verification command. Actions are pinned to official commit references. Local packaging and process doubles prove build/orchestration behavior, not a GitHub Release, registry publication or deployed provider. Hosted release and Azure acceptance remain explicit until executed; local verification performs none of those writes.
