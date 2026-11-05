# Pictures in Discussions

Image publishing is optional. The agent publishes PNG/JPEG images and links to editable SVG/Mermaid sources through the configured GitHub App. Files stay in the same repository on the dedicated `repoagent-assets` branch. Returned Markdown can be embedded in Discussion bodies, comments and replies.

GitHub's native attachment endpoint does not generally accept ordinary App installation tokens, including tokens with write access ([upstream limitation](https://github.com/cli/cli/issues/14309)). This feature uses the supported [repository file APIs](https://docs.github.com/en/rest/repos/contents#create-or-update-file-contents).

## Permission and setup

The App registration must declare **Contents: read and write**, and the selected installation must accept it. This permission covers the whole repository. The asset branch and helper are conventions, not a restriction on token authority. Branch rules still apply; the publisher never forces a branch update or bypasses a rule. Asset commits can trigger workflows that select that branch.

Existing enrollments keep their exact permissions. Adopting this authority requires revoking the old enrollment through the supported lifecycle and creating a fresh instance and enrollment with an image profile. Preserve the old instance's state and history; a fresh instance does not automatically migrate its sessions. Deploy a compatible broker before enrolling, then start a compatible controller. Older brokers and controllers refuse the new profile.

For a fresh guided installation, add `-ImagePublishing` to your normal `install.ps1` invocation. Combine it with `-Clone` and `-RepositorySettings` when needed. Review the permission preview before `-Apply`. Standalone enrollment selects a profile ending in `-images`:

```powershell
./scripts/enroll-controller.ps1 -Repository OWNER/REPO `
  -BrokerOrigin https://YOUR-TRUSTED-BROKER `
  -Profile polling-clone-settings-images -StateDirectory /PRIVATE/NEW-INSTANCE
```

Fresh image enrollments automatically select setup v5 and runtime protocol v4, with policy `repository-agent-images-approval-v5`. Profile names are `polling` or `webhook`, optionally `-clone`, optionally `-settings`, then `-images`. Tokens have Discussions write and Contents write, plus repository hooks write for webhook mode and Agent variables read for settings mode. Verifier authority remains separate and read-only.

Native startup uses `--image-publishing` and the enrolled clone/settings selections. Container configuration accepts `configure --image-publishing`; external configuration accepts `REPOAGENT_IMAGE_PUBLISHING=true`. Changing image authority on an existing configured instance is refused. Own-App operators can use the native flag with their private-key configuration and matching installed Contents-write permission.

## Agent command and storage

Enabled controllers install `repoagent-assets` beside their authenticated `gh` wrapper and supply its absolute path in the agent's instructions. The helper requests current credentials from the running controller for each operation, without stored tokens or personal-account fallback.

```sh
/PATH/FROM/AGENT/INSTRUCTIONS/repoagent-assets --discussion 47 /absolute/path/sketch.png
```

Paste the returned Markdown into the intended post or reply. SVG and `.mmd` files return editable-source links. Errors go to stderr with a nonzero exit status. Files must contain 1 byte to 10 MiB, with a supported extension and matching PNG/JPEG header or valid UTF-8 source. Retain local files in a persistent task directory outside the managed mainline checkout until publication succeeds.

The helper verifies the Discussion belongs to the approved repository. It creates an orphan asset branch containing only an ownership marker and assets, refuses a foreign branch or an asset branch used as the default branch, and never changes the managed checkout. Content-hash paths deduplicate retries; concurrent updates use bounded optimistic retries. Commit-pinned links preserve earlier images after revisions. Deleting a comment does not delete assets; coordinate any storage cleanup with existing links.

## Private repositories and acceptance

Links name repository files at a commit, without credentials or expiring API download URLs. GitHub documents [repository image embeds and private-image access](https://docs.github.com/en/get-started/writing-on-github/getting-started-with-writing-and-formatting-on-github/basic-writing-and-formatting-syntax#images). Repository visibility and GitHub's image delivery/cache behavior govern access; making a repository public exposes retained assets too.

Live private-Discussion rendering remains a release acceptance requirement. Before rollout, use a harmless image in a disposable private repository with an explicitly authorized App token. Check actual image loading in a Discussion body, top-level comment and reply from a second authorized account; reload after token renewal, and check that an unauthenticated viewer cannot fetch the private image. Markdown or `bodyHTML` alone is insufficient evidence. Do not claim instantaneous cache revocation. Automated storage/profile tests establish local behavior, not this provider/browser result.
