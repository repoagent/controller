# Structure the controller as ports and adapters

Accepted on 2026-10-24. Builds on [ADR 0009](0009-keep-hosting-lifecycle-out-of-the-controller.md).

The controller's decisions live in a core that does no I/O. Every edge is a port that the core defines in its own terms, and an adapter implements each port as an anti-corruption layer over one external system. The core is unit tested with in-memory fakes of its ports and `TimeProvider`, so the controller's logic is tested in-process, in milliseconds.

```
 driving adapters                       core                    driven ports        adapters
 ----------------                       ----                    ------------        --------
 POST /github/webhook --+                                  +--> GitHubTokenSource     broker HTTPS | direct App JWT
 GET  /status ----------+                                  +--> Discussions           GitHub GraphQL
 poll timer ------------+--> enrollment, waiting input, ---+--> RepositorySettings    GitHub REST variables
 CLI commands ----------+    active-session limit,         +--> BaselineReleases      GitHub REST releases
                             redelivery, EYES receipts,    +--> WebhookRegistration   GitHub REST hooks
                             model selection, refresh at   +--> AgentRuntime          codex app-server, stdio JSON-RPC
                             idle, outbox policy, status   +--> RepositoryCheckout    git
                                                           +--> ControllerState       files
                                                           +--> AgentToolCredentials  Unix socket handoff
```

Two rules keep the layer honest:

- **Domain types at the ports.** Ports take and return domain types such as `Discussion`, `Comment` and `AgentTurn`, never `JsonNode`. GitHub's and Codex's shapes stop at the adapter.
- **Ports are named for what the core needs, not for the provider.** The port is `Discussions`, not `GitHubGraphQL`, and `AgentRuntime`, not `Codex`. Broker mode and direct App mode are two adapters of one `GitHubTokenSource` port.

## Testing

- The core gets unit tests against fakes of every port. Most cases live here.
- Adapters get no separate contract tests. We trust GitHub not to break its API or webhook payloads. An adapter is exercised by the end-to-end environment.
- End-to-end is a local Aspire AppHost that runs the credential broker with its own local storage, one or more controllers and a real RepoAgent repository, so the whole system can be debugged and tested manually, including the installer pointed at the local broker. The only shared resource is the Key Vault that holds the GitHub App's private key. A locally generated controller secret can be kept in .NET user secrets and passed to a controller at startup.

## Considered options

- **Keep concrete edges and fake at the process level** (a fake `gh` and `codex` on `PATH`, as today). Rejected. Every test pays for process startup and real timers, which is why the suite takes tens of minutes, and GitHub's response shapes spread through the core.
- **Contract test each adapter against the real provider.** Rejected for GitHub, because its API is stable and the cost is not worth it. The agent runtime is pinned in the image, so a protocol change arrives with a deliberate image update and shows up in the end-to-end environment. Optional startup tool updates bypass that pin and carry this risk. We accept it and revisit if updates break the adapter.

Implementation: [#203](https://github.com/repoagent/controller/issues/203) (ports and adapters), [#204](https://github.com/repoagent/controller/issues/204) (Aspire end-to-end environment).
