# Credential broker releases

The broker has its own stable semantic version, beginning at **1.0.0**, and tags such as `broker-v1.0.0`. Controller versions, enrollment protocol versions, approval policies, deployment epochs and retained-state schemas remain separate contracts. Historical `sha-...` images are provenance, not inferred semantic releases.

## Compatibility and review

The supported broker contract includes enrollment, GitHub-token renewal and self-revocation HTTP behavior; compatibility with supported controller profiles; and the persisted state needed to retain grants, revocation, accounting and safe cleanup. Removing a supported route or response, changing authorization or settlement semantics incompatibly, or requiring an incompatible state migration is a **major** change. Compatible features are **minor** changes; compatible fixes and maintenance are **patch** changes. The highest required bump wins. A service version never silently widens an enrollment or changes a protocol/schema number.

Release Please is the version authority. Reviewed Conventional Commit PR titles/merge messages use `fix(broker): ...` for patches, `feat(broker): ...` for features and `!` or a `BREAKING CHANGE:` explanation for breaking changes. Scope text describes the change; actual affected runtime/build/deployment paths determine component attribution, including shared inputs. Controller-only changes do not release the broker. Internal-only documentation, tests and refactors do not automatically require a release.

The release PR proposes the broker version and editable changelog. Before approval, review user-visible features and fixes, breaking changes, required upgrade or state-migration steps, and known limitations. Explain rollback restrictions when an older binary cannot read newly written state. Publication or a successful readiness check does not prove that every older controller or historical state is compatible.

## Identity and publication

`service/auth/version.txt` is the broker component's version file. The compiled `--version --json` diagnostic, HTTP/cleanup telemetry's service version, OCI version label, `broker-v...` image tag and GitHub release mapping agree. Keep the full source revision and immutable image digest separately; identical version labels are not proof that two images have identical content.

The main-only Broker workflow first verifies the exact approved release source. It builds a source-SHA image, checks Linux/amd64, explicit nonroot identity, broker role, source/version labels and the compiled diagnostic, then anonymously verifies the immutable digest. Only then may it create the semantic image tag. Existing published mappings and tags must match; the workflow never clobbers an asset or replaces a released version with different content.

The release asset `broker-release.json` records the component, version, tag, source, digest, semantic image tag and publication run. Approved Release Please notes remain unchanged. Registry publication has only its existing registry-write authority; a separate metadata job has release-write authority and no registry-write or Azure identity. Deployment waits for both verified image publication and confirmed metadata. Failed or uncertain uploads are read back before retry succeeds. An existing verified semantic tag or source image can resume a partial publication without rebuilding it.

One noncanceling Broker delivery lock serializes workflow publishers and deployments. Repository administrators must protect `broker-v*` Git tags from force updates/deletion and reserve semantic package tags for this workflow; direct registry or portal writes bypass its checks. GitHub workflow-token-created releases do not trigger ordinary downstream events, so the release orchestrator explicitly dispatches the Broker workflow after creating an approved release.

## Deploy or roll back deliberately

Normal source pushes verify code and propose release work; they do not redeploy an already released version under changed source. Approved broker release publication and deployment use the existing main-branch workflow and OIDC trust. Both the HTTP API and cleanup Job receive **one verified immutable digest**. Successful deployment requires both images to match, the latest API revision to be ready, and public live/ready HTTPS checks to pass.

To retry an approved release:

```sh
gh workflow run broker.yml --repo repoagent/controller --ref main \
  -f operation=release -f release-tag=broker-v1.0.0
```

To deliberately deploy or roll back to a recorded broker version:

```sh
gh workflow run broker.yml --repo repoagent/controller --ref main \
  -f operation=deploy-release -f release-tag=broker-v1.0.0
```

The workflow resolves the release asset, checks its tag/source/version binding, anonymously attests that digest and deploys the pair. It does not rebuild the selected release. Historical digest-only images remain available through `operation=deploy-image -f image=ghcr.io/repoagent/controller-auth@sha256:...`; their source/role/platform/nonroot checks remain, while no semantic version is invented. Guarded foundation/staging/recovery operations retain their current-main source checks.

Read [Azure deployment and recovery](azure-auth-deployment.md), release notes and retained-state compatibility guidance before choosing a rollback. Version selection is not credential recovery, capability settlement or a storage restore. The flow adds no GitHub environment, OIDC subject, Azure role, controller credential migration or automatic controller upgrade.

## Verification limits

Public helper tests exercise release selection, version/source/image mismatches, retry and partial-publication decisions using external command transports. Actual compiled CLI and locally built image checks establish matching packaged diagnostics and labels. Hosted GitHub/GHCR publication, Azure paired deployment, readiness and rollback still require observations bound to the actual source, version, digest and workflow run; source tests alone do not establish those outcomes.
