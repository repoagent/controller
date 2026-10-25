# RepoAgent

RepoAgent lets your team work with a coding agent through GitHub Discussions in your repository. Participants ask questions, plan work and follow up in comment threads; the agent replies as your installed GitHub App bot.

The controller currently supports **GitHub Discussions with Codex**, with one controller per repository and one persistent agent session per Discussion.

## Supported features

- **Shared conversations.** Everyone in a Discussion works with the same agent session across its comment threads. Follow-up messages retain the conversation's context, including after controller restarts when its storage is preserved.
- **Discussion activity.** The agent receives new and edited messages, reactions and who added them, answer and state changes, and confirmed comment or reply deletions.
- **Flexible conversation starts.** Mention the App bot or a configured mention account such as `@repoagent`, select a Discussion by URL, or automatically start sessions for new Discussions in chosen categories.
- **Model selection.** Set repository defaults for the Codex model and reasoning effort, or override them per Discussion with a label such as `repoagent:sol6.1:xhigh`. Changes apply to the next turn.
- **Queued work and concurrency limits.** Follow-ups wait while the agent is busy. A configurable limit controls how many Discussions run at once; the default is four.
- **Delivery recovery.** Waiting input survives restarts, uncertain deliveries are retried, and controller receipts and dashboard updates recover from temporary GitHub failures and rate limits.
- **Optional repository context.** Give the agent a checkout of your repository's default branch, including its instructions and skills. The controller refreshes it between completed turns.
- **Repository settings.** Manage model defaults, concurrency and automatic category enrollment through nonsecret `REPOAGENT_*` Actions variables.
- **GitHub App identity.** Controller and agent GitHub access use the installed App's repository-scoped permissions. Shared-App setup uses revocable repository approval without distributing the App's signing key; using your own App is also supported.
- **Separate Codex credentials.** Supply Codex credentials independently of GitHub authentication and replace them while the controller stays running. The change waits for active work to finish and preserves session history.
- **Status and troubleshooting.** Inspect active and queued sessions, authentication health and recent failures through status commands and retained logs. An optional Discussion dashboard adds a heartbeat and image update notices.
- **Container distribution.** Run locally with Docker or behind your own HTTPS hosting. Public GHCR baseline images record their bundled tool versions; you can extend an image with your own tools and adopt updates deliberately.

Category enrollment, repository model defaults and concurrency changes require [repository settings](docs/repository-settings.md) to be enabled. Broker installations opt in with `-RepositorySettings` during a fresh installation. Per-Discussion model labels work without repository settings; model availability depends on the signed-in Codex account.

## Get started

You need a repository with Discussions enabled, a compatible GitHub App installed, Codex credentials, and Docker with Linux containers. The supported image targets Linux amd64.

- **Local installation:** follow the [guided PowerShell installer](docs/local-installation.md) for Windows, Linux or WSL. It previews changes before `-Apply`, supports shared-App enrollment through a trusted credential broker, and can reuse an existing Codex sign-in.
- **Your own GitHub App:** use the [App setup guide](docs/github-app-auth.md) and [container setup](docs/container-setup.md). Bash installation is available for this mode.
- **Existing controller credentials:** use the [supplied-enrollment runtime](docs/local-runtime.md) or [external credential startup](docs/container-setup.md#external-controller-credentials) for an already-issued enrollment.

Choose how the controller receives activity:

- **Polling:** no public endpoint required.
- **Fixed HTTPS URL:** [webhook mode](docs/webhooks.md) uses your existing ingress and automatically registers the repository hook.
- **Temporary public URL:** [Cloudflare Quick Tunnel](docs/quick-tunnel.md) supports local development without an account or domain and automatically recovers a lost tunnel.

## Use RepoAgent

1. Open a Discussion and mention the configured account or the App bot's actual login, such as `@repo-agent-connector[bot]`. With category enrollment enabled, creating a Discussion in a selected category starts it automatically.
2. A 👀 reaction means the agent accepted the input. It does not mean the work has finished; the agent posts its reply in the Discussion.
3. Continue in comments and replies. Follow-ups wait for the current turn to finish and then reach the same session.

Keep the controller and its host running to receive activity. Preserve its state and Codex storage across restarts. If you enable repository context, the managed checkout is disposable; the agent uses separate worktrees for edits. The supplied GitHub permission profiles support Discussions and optional code reading; they do not grant code pushes or pull request creation. Issues and pull requests are not conversation entry points.

## Configuration and operations

- [Repository settings](docs/repository-settings.md): model defaults, model labels, concurrency and category enrollment.
- [Container setup](docs/container-setup.md): skills, tools, storage and runtime commands.
- [Controller operations](docs/controller-operations.md): status, dashboard, logs, credential replacement and recovery.
- [Enrollment and revocation](docs/host-enrollment.md): repository approval and controller access.
- [Image updates and rollback](docs/local-runtime.md#update-an-immutable-image-or-restore-the-prior-image): replace the selected image while retaining your data.
- [Azure hosting](infra/controller/README.md): deployment and persistent storage. The host must keep the controller alive while it is busy; automatic scale-to-zero protection is not built in.

When repository settings are enabled, the controller and its agent can read **all direct repository Actions variables**. Keep secrets in GitHub Secrets or private runtime storage. See the [settings permission details](docs/repository-settings.md#broker-enrollments).

For implementation and design, see the [architecture decisions](docs/adr/), [domain glossary](GLOSSARY.md), [credential broker documentation](service/auth/README.md) and [image release process](docs/ci-cd.md).
