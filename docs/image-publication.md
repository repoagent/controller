# Publish a controller image

Use PowerShell 7 and the image ID emitted by [image composition](image-composition.md). Publication is a separate host operation; the builder never publishes images. The publisher accepts only a full immutable `sha256:` image ID already present in the selected local Docker daemon. It never resolves a mutable source tag on your behalf and requires Linux amd64.

```powershell
# Copy the full ID of the image you explicitly built, not a registry digest.
$imageId = 'sha256:REPLACE_WITH_64_LOWERCASE_HEX_DIGITS'
.\scripts\publish-image.ps1 -ImageId $imageId -Destination Local
```

Local mode verifies the image identity and returns `LocalVerified`. It never tags or pushes anything. A local-only image cannot deploy to remote Azure.

## Choose the exact publication destination

A registry plan requires a fully qualified repository and a separate explicit tag. There are no destination, repository, tag or credential defaults:

| Destination | Repository example | Prerequisite |
| --- | --- | --- |
| `DockerHub` | `docker.io/my-org/controller` | Existing Docker Hub repository/namespace and host Docker push plus manifest-read authority |
| `GHCR` | `ghcr.io/my-org/controller` | Host Docker authority to publish/read the chosen GitHub package; organization/package policies must permit it |
| `ACR` | `myregistry.azurecr.io/controller` | Existing standard public-Azure registry/repository and host Docker push plus manifest-read authority |

Docker Hub and GHCR require an explicit namespace. ACR permits a single repository name or nested repository paths. Azure sovereign-cloud endpoints, custom registry hosts, image indexes and multiple platforms are outside this initial publisher. The tool does not create registries, repositories, credentials or role assignments, change package visibility, or enable registry admin. Visibility and access follow the chosen registry's existing policy; choose a destination whose access policy suits your image before applying.

The host needs Docker, a Linux amd64 image and Docker Buildx. Authenticate the Docker client separately through your existing approved host workflow. The publisher invokes Docker using its existing identity, including configured credential helpers. It never extracts tokens, reads credential caches, runs login, passes credentials as command arguments or copies host authentication into the image. Successful push alone does not prove that the eventual controller host can pull the image.

## Review the plan, then apply

```powershell
$publication = @{
    ImageId = $imageId
    Destination = 'GHCR'
    Repository = 'ghcr.io/my-org/controller'
    Tag = 'reviewed-2026-10-20'
}

# Read-only local inspection and JSON explanation; no tag, push or registry query.
.\scripts\publish-image.ps1 @publication

# Explicitly permit tagging and publication to precisely this target.
.\scripts\publish-image.ps1 @publication -Apply
```

Apply can overwrite both the local target tag and the remote target tag. It reinspects the supplied immutable local ID, tags that ID, verifies the local target still matches, and pushes only the selected tag. It then resolves the remote descriptor and fetches its manifest again using the immutable digest. The manifest's config digest must equal the supplied local image ID. It never selects an arbitrary cached `RepoDigests` value or treats Docker's progress text as verification.

Docker's single-image Buildx `{{json .Manifest}}` output is a descriptor containing `mediaType`, `digest` and `size`; the subsequent `--raw` output is the actual schema-version-2 image manifest containing `config.digest`. Both Docker schema2 and OCI image manifests are supported. Image indexes and manifest lists fail explicitly.

Successful Apply returns JSON with `status: PublishedVerified`, `remoteVerified: true` and `immutableReference: registry/repository@sha256:...`. That immutable reference is the publication output eligible for the separate hosting workflow. The publisher always leaves `runtimePullAuthorized` and `azureDeploymentReady` false: publication is not a deployment or runtime authorization check.

## Failures and concurrent writers

A failure exits nonzero and returns `Unverified`, a safe stage name and no immutable reference. `publicationMayHaveChanged` becomes true before push begins, including partial/failed pushes. Docker output and credential-helper errors are captured with bounded buffers and are not echoed; errors identify the failed stage without printing potentially sensitive provider text. Commands time out after fifteen minutes, and their combined captured stdout/stderr is limited to 1,048,576 decoded characters. Inspect your host Docker setup separately when troubleshooting authentication failures.

Docker does not provide an atomic tag-and-push operation by local image ID. A concurrent local writer can replace the target tag between verification and push; a remote writer can replace the remote tag before it is resolved. The final immutable-manifest check detects an unrelated image and refuses a verified result, but a wrong or partial target publication may already have happened. There is no automatic repair, retry, tag deletion, lock or staging tag. Avoid concurrent publication to the same target. A tag changed after successful verification does not change the verified immutable reference.

## Image secrets and hosting gate

Publish only credential-free images. The publisher does not add secrets or execute image contents, and it is not an image-secret scanner. Composition's allowlisted source/context exclusions still apply. An explicitly supplied base image or derived Dockerfile remains trusted user content: credentials already in its layers would be published too. Public visibility must never be used to distribute image-embedded runtime credentials or the shared App signing key.

[Hosting issue #38](https://github.com/repoagent/controller/issues/38) owns runtime pull identity and its explicit deployment gate: Docker Hub/GHCR runtime credential references, and ACR managed pull identity with the narrow role appropriate to the registry's RBAC or ABAC authorization mode. This publisher changes none of those resources. Runtime pull acceptance and the shared-App broker/enrollment release gates must pass separately before claiming a usable Azure controller.

Process fixtures exercise our argument, plan, failure and identity decisions through actual PowerShell and an executable Docker boundary. They do not establish registry compatibility. Credential-free local-registry push/inspection/pull evidence covers only that Docker boundary; native Windows execution and each selected private Docker Hub/GHCR/ACR destination need their own acceptance evidence. No live registry credentials or publication destination are bundled with this tool.

Command/specification references: [Docker push](https://docs.docker.com/reference/cli/docker/image/push/), [Docker Buildx inspection](https://docs.docker.com/reference/cli/docker/buildx/imagetools/inspect/), [Buildx descriptor formatting implementation](https://github.com/docker/buildx/blob/master/util/imagetools/printers.go), and [OCI image configuration identity](https://github.com/opencontainers/image-spec/blob/main/config.md).
