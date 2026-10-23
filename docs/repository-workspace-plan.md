# Repository workspace and native session context

The maintainer requested [#42](https://github.com/repoagent/controller/issues/42): a managed clone of the configured repository's default branch, push-webhook updates, and Codex sessions started at the repository root with trust enabled for that folder. This is the intended behavior to implement; the current controller accepts an existing directory and does not clone or refresh repository contents.

## Product contract

Prepare the configured repository after GitHub App identity is verified and before starting native agent work. Use the authenticated `gh` command to clone over HTTPS into persistent runtime storage. Resolve and bind the immutable repository identity and actual default branch; do not assume `main`, clone a webhook-selected arbitrary URL, or replace an unrelated existing checkout. The clone belongs to runtime data, not image layers.

Set the native process working directory, `thread/start.cwd` and `thread/resume.cwd` to the same canonical checkout root. Enable trust for that exact verified folder in the controller's private Codex configuration, preserving other settings. Repository `AGENTS.md`, repository-local skills and trusted project configuration then participate through Codex's native mechanisms. Keep the existing controller transport instructions, authenticated `gh` wrapper and persistent session history.

Receive signed push webhooks for the configured repository and reconcile changes to its actual default branch. Duplicate, delayed and reordered events should converge on current GitHub state. Also reconcile on startup and periodically so missed deliveries do not leave the checkout permanently stale. A push payload is a trigger for an authoritative fetch, not authority to select a repository or reset local work.

## Credentials and compatibility

Private Git access requires an explicitly approved `contents:read` grant. The current frozen v1 broker profiles permit only Discussions write, optional repository-hooks write and implicit metadata read; the existing consumer deliberately rejects Contents. Introduce an explicit clone-capable profile/version and approval preview without silently broadening existing v1 enrollments. Re-enrollment is required when the grant changes. No Contents write, code-push or PR-creation authority follows merely from cloning.

Use the renewable App credential for both `gh repo clone` and later Git fetches. Git needs a credential helper that obtains the current token through the isolated App wrapper; placing `gh` on PATH alone is insufficient. Never persist a token-bearing remote URL, plaintext token in Git config, or a static token that outlives cache revocation. Scope credentials to the enrolled GitHub repository and retain fail-closed behavior.

## Updating without damaging agent work

Track actual native turn execution, not just acceptance of `turn/start`. Current observation serialization ends at acceptance while native execution continues. Coordinate all active turns sharing a checkout with applying updates; a single-reader webhook queue does not itself make filesystem mutation safe. Native disconnect and uncertain completion must not be interpreted as idle.

Initially use a conservative shared-checkout policy: defer checkout mutation while native work is active; when idle, update only a clean controller-managed checkout at the expected previously applied commit. Preserve staged, unstaged and untracked files, user branches, local commits and merge/rebase state. Leave refresh pending with actionable status when those conditions are not met; do not auto-stash, clean or reset agent work. A default-branch rename, deletion or force-push must either be reconciled safely under these conditions or report a recoverable blocked state.

Superseded for the owned checkout by [#180](https://github.com/repoagent/controller/issues/180) (spec #171): at idle, the controller now forces the managed checkout to the verified origin commit, including divergence and force-push. It still never resets paths outside that checkout, other branches or separate worktrees. [Local installation](local-installation.md) describes the current behavior.

If later work requires concurrent writable Discussions, assess separate Git worktrees per session rather than allowing competing writers or moving one session's checkout underneath another. That extension does not change the requirement that each native session starts at its own repository root with trust scoped to it.

## Code evidence and delivery slices

At merged main `6f1b446`, `BridgeCommand` verifies identity and installs the App wrapper before constructing `Codex`; this is the bootstrap seam. `Codex` already uses its workspace for process/start/resume, but only logs completion notifications. Runtime configuration does not create a project trust entry. `DiscussionWebhooks` subscribes to Discussions events and ignores pushes. No checkout owner/default-branch state exists.

Deliver bounded slices, preserving independent verification:

1. Versioned clone-capable permission/enrollment profile with explicit Contents read and unchanged legacy v1 fail-closed behavior.
2. Idempotent authenticated default-branch checkout and exact-root Codex trust/start/resume integration.
3. Native turn-activity coordination and safe, persistent push/startup/periodic reconciliation.

Use real disposable local Git repositories for checkout/dirty/index/untracked/default-branch/force-push/restart cases; replace outbound GitHub at the owned transport or process seam. Add a sparse native acceptance journey proving repository-root instructions and a repository-local skill are available on start and resume. Local fixtures cannot prove real GitHub App clone permissions or hosted signed push delivery.

## Primary documentation

Codex's `projects.<path>.trust_level` marks a specific project/worktree trusted; untrusted projects skip project-scoped configuration, hooks and rules. Trust is distinct from sandbox and approval policy. [Configuration reference](https://learn.chatgpt.com/docs/config-file/config-reference)

App-server starts a thread with `cwd` and supports cwd-scoped skill discovery/reload. Existing sessions may cache instructions or skill metadata; prove refresh behavior rather than assuming changed files immediately rewrite a running conversation. [App-server documentation](https://learn.chatgpt.com/docs/app-server)

Codex discovers repository instructions from the repository root toward the working directory and discovers repository skills in `.agents/skills`. Starting at the checkout root supplies the intended project context; the controller should preserve native discovery rather than flattening files into global skills. [AGENTS.md guide](https://learn.chatgpt.com/docs/agent-configuration/agents-md), [Skills guide](https://learn.chatgpt.com/docs/build-skills)

GitHub documents Contents permission for HTTP Git access with App installation tokens; GitHub CLI clones through Git and supports explicit HTTPS. Push webhooks require an appropriate App subscription/permission. [Installation authentication](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation), [App permissions](https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/choosing-permissions-for-a-github-app), [gh clone](https://cli.github.com/manual/gh_repo_clone), [Push payload](https://docs.github.com/en/webhooks/webhook-events-and-payloads#push)
