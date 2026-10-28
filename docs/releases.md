# Component releases

Release Please 17.11.2 is the single authority for controller and credential broker semantic versions. Each starts at 1.0.0. Reviewed Conventional Commit merge messages classify compatible fixes and maintenance as patch, supported features as minor, and breaking supported changes (`!` with an explanation or `BREAKING CHANGE`) as major; the largest applicable change wins. Protocol and persisted-state schema versions remain separate contracts.

Controller compatibility covers CLI/configuration, supported operator interfaces, broker interaction, persisted state, and base-image extension/runtime interfaces. Broker compatibility covers enrollment, renewal and revocation HTTP behavior, controller compatibility and persisted-state upgrades. Required upgrade steps and known limitations belong in editable release-PR notes before approval.

The pinned Node wrapper in `scripts/releases` supplies actual runtime-file ownership to Release Please before its component split. Conventional Commit scopes communicate intent but never decide ownership. `components.json` lists controller, broker and shared build inputs; shared runtime changes feed both independent histories. Documentation, tests and development-only changes do not automatically release either component. A component's own previous semantic tag determines its cutoff; historical baseline/SHA tags never become semantic versions.

Logical controller release files are `releases/controller/version.txt` and its changelog. Broker release files are `service/auth/version.txt` and its changelog. Both use independent component-prefixed tags, `controller-vMAJOR.MINOR.PATCH` and `broker-vMAJOR.MINOR.PATCH`. Never move a released tag or overwrite assets. Protect these tag patterns and main through repository rules; those external protections are not created by this change.

The main-only Release Please workflow creates reviewable release PRs and tagged release notes. GitHub's workflow token suppresses normal PR/tag events, so a separate least-privilege dispatch job explicitly starts read-only verification for release PRs and the component publication workflows after approval. A semantic GitHub Release may temporarily lack verified artifact metadata while its publication workflow runs; a tag or release record alone is not successful image publication or deployment. Inspect the verified immutable metadata before adoption or retrying partial publication.

Local proposal tests use the actual pinned Release Please strategy and manifest without network access:

```sh
npm ci --prefix scripts/releases --ignore-scripts --no-audit --no-fund
npm test --prefix scripts/releases
```
