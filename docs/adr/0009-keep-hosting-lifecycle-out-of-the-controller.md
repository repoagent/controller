# Keep hosting lifecycle out of the controller

Accepted on 2026-10-24. Retires the opt-in `--azure-work-queue` durable webhook work that `docs/azure-durable-work.md` described; #201 removed both.

The controller is a standalone component. It depends only on what it needs to do its job: the credential broker for its GitHub token, the agent runtime for agent sessions and turns, and the GitHub CLI and Git for repository work. It does not know where it runs or whether its host may stop it. Whether a hosting environment such as Azure Container Apps may scale to zero is that environment's concern.

The controller reports its own activity instead. It serves a status endpoint that says whether it is busy (an agent turn is running, or input is pending, retrying or waiting) or idle, with the same work counts that `RepoAgentController status` already reports. A hosting environment that needs to keep a busy controller alive adds its own component that reads this endpoint. That component is designed when hosted operation is revisited, not now.

We follow the CUPID properties: the controller stays composable (a small dependency set, and a status endpoint any host can use), does one thing (connects Discussions to agent sessions), is predictable (it behaves the same in every environment), and is domain-based (its code speaks of Discussions, agent turns and GitHub tokens, not queue leases and visibility timeouts).

## Considered options

- **Keep durable webhook work in the controller.** Rejected. It couples the controller to Azure Queue, managed identity and Container Apps scaling, adds a lease protocol to every agent turn, and makes the controller's correctness depend on one host's scaling behavior.
- **Serve the status endpoint from the existing `HttpListener`.** Rejected. The controller would then have two hand-written HTTP surfaces, and a listener in polling mode too.
- **Host HTTP in ASP.NET Core.** Chosen. `WebApplication.CreateSlimBuilder` supports Native AOT, and one Kestrel host serves both `/github/webhook` and the status endpoint, with graceful shutdown and health checks. The cost is a framework dependency and a larger AOT binary.

## Consequences

- `--azure-work-queue`, `DurableWebhookWork`, the managed-identity queue client and the `Azure.Storage.Queues` dependency are removed.
- In webhook mode, the controller no longer keeps an Azure replica alive after the webhook response. Until a hosting component exists, hosted deployments must not scale a controller to zero while it is busy.
