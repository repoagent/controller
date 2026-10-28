# Controller core and adapters

The controller follows [ADR 0010](adr/0010-structure-the-controller-and-broker-as-ports-and-adapters.md). `RepoAgentController.Core` owns decisions over domain types and injected ports; `RepoAgentController` owns files, processes, provider protocols, HTTP and command-line composition. The core's banned-API analyzer rejects common I/O, wall-clock and `JsonNode` APIs.

| Core responsibility | Adapter responsibility |
| --- | --- |
| `GitHubTokenLifetime` owns renewal, expiry, rejection and its `IAgentToolCredentials` binding. Tools can read only the currently usable token; a tool read never renews it. | `CredentialHandoff` serves the private Unix socket and its nonsecret pointer. Its wire messages and filesystem permissions stay outside Core. |
| `ControllerPolling` observes immediately, waits after each completed pass, separates intake stop from entered activity, and flushes durable output for `--once`. | The command line selects polling mode and supplies the interval and lifetime tokens. |
| `ControllerWebhooks` admits typed authenticated observations, excludes the dashboard and App comments, coalesces pending Discussion reads, reconciles periodically and registers/deactivates the webhook. A bound default-ref push persists a checkout hint before acknowledgement. | `DiscussionWebhooks` authenticates signatures, bounds HTTP body reads, translates provider payloads and maps dispositions to HTTP responses. `ControllerHttpServer` owns Kestrel, routing and listener isolation. |
| `Bridge.ReplaceAgentCredentials` excludes dispatch and requires both settled agent activity and proven checkout idle before replacing credentials. An admission that cannot prove global idle refuses replacement. | The credential-file watcher detects and validates a changed supply; the command line wires it to the core operation. |
| `ControllerStatus` and `StatusSnapshot` project stored work and current activity into operator and busy/idle status. | HTTP and CLI adapters serialize those domain snapshots. |

`BridgeCommand` remains the composition root: it parses options, validates external paths and coordinates, acquires leases, constructs adapters and translates process stop/authority loss into lifetime cancellation. It does not decide webhook admission or the ordering of credential replacement.

## Verification ownership

Core characterizations use the real bridge, sessions, status and outbox with in-memory ports and controlled time. `ControllerWebhooksTests` covers observation, duplicate coalescing, a change arriving during a read, failed-read recovery, periodic reconciliation, normal stop and durable push hints. `GitHubTokenLifetimeTests` includes agent-tool publication ownership; `AgentCredentialReplacementTests` covers runtime and checkout idle exclusion. Polling and status projection retain their existing core suites.

The former process-only Discussion-dispatch and busy/idle decision cases are replaced by these core tests. HTTP serialization, listener/signature/body-deadline safety, OS-signal shutdown, descendant termination and proof that bearer tokens do not reach persistent outputs retain their existing host/process checks. Those tests protect process and transport facts, rather than substituting executable stand-ins for core decisions. This change adds no separate provider adapter contract suite.

Actual pinned Codex/GitHub translation and installer compatibility remain with the [shared Aspire journey](../service/auth/local-development.md) and [hosted smoke gate](ci-cd.md). In-process characterizations and host fixtures do not establish real-provider acceptance.
