# Component releases

Release Please 17.11.2 is the single authority for controller and credential broker semantic versions. Each starts at 1.0.0. Reviewed Conventional Commit merge messages classify compatible fixes and maintenance as patch, supported features as minor, and breaking supported changes (`!` with an explanation or `BREAKING CHANGE`) as major; the largest applicable change wins. Protocol and persisted-state schema versions remain separate contracts.

The next controller release must classify the executable rename as a major compatibility change. Release Please remains the version authority: retain the released version files until its release PR proposes the next version, and preserve `!` or `BREAKING CHANGE` in the reviewed merge message.

The native controller command is now `repoagent`. This is a breaking rename: `RepoAgentController` is no longer installed and has no compatibility alias. Adopt matching native clients, host wrappers and controller images, update any executable invocations, and restart the selected controller. Preserve its existing state, journals and Codex storage; this upgrade requires neither controller re-enrollment nor a session/state reset. Native archives contain only the new executable name; retain the same persistent data mounts when replacing an image. Runtime/private transport compatibility must be qualified separately before adoption.

Controller compatibility covers CLI/configuration, supported operator interfaces, broker interaction, persisted state, and base-image extension/runtime interfaces. Broker compatibility covers enrollment, renewal and revocation HTTP behavior, controller compatibility and persisted-state upgrades. Required upgrade steps and known limitations belong in editable release-PR notes before approval.

The pinned Node wrapper in `scripts/releases` supplies actual runtime-file ownership to Release Please before its component split. Conventional Commit scopes communicate intent but never decide ownership. `components.json` lists controller, broker and shared build inputs; shared runtime changes feed both independent histories. Controller ownership includes the shipped installer/runtime dependencies and the controller verification and hosted smoke governance helpers; those helpers gate controller adoption and tool refresh, rather than changing the broker artifact. Documentation, tests and development-only changes do not automatically release either component. A component's own previous semantic tag determines its cutoff; historical baseline/SHA tags never become semantic versions.

The ownership map itself is classification metadata, not an automatic shared release input. Editing `scripts/releases/components.json` alone proposes no component release; when it accompanies runtime changes, those actual changed inputs determine ownership. Adding a broker-only entry therefore does not release the controller, and the reverse also holds. Shared SDK/build settings, release tooling and workflow/configuration changes remain shared release inputs. A mixed commit can still affect both components, and compatible controller verification maintenance remains a controller patch even when broker-only features in the same history require a broker minor release.

Logical controller release files are `releases/controller/version.txt` and its changelog. Broker release files are `service/auth/version.txt` and its changelog. Both use independent component-prefixed tags, `controller-vMAJOR.MINOR.PATCH` and `broker-vMAJOR.MINOR.PATCH`. Never move a released tag or overwrite assets. Protect these tag patterns and main through repository rules; those external protections are not created by this change.

The main-only Release Please workflow creates reviewable release PRs and tagged release notes. GitHub's workflow token suppresses normal PR/tag events, so a separate least-privilege dispatch job explicitly starts read-only verification for release PRs and the component publication workflows after approval. A semantic GitHub Release may temporarily lack verified artifact metadata while its publication workflow runs; a tag or release record alone is not successful image publication or deployment. Inspect the verified immutable metadata before adoption or retrying partial publication.

Local proposal tests use the actual pinned Release Please strategy and manifest without network access:

```sh
npm ci --prefix scripts/releases --ignore-scripts --no-audit --no-fund
npm test --prefix scripts/releases
```
