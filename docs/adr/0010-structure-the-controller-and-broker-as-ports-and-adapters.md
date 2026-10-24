# Structure the controller and the credential broker as ports and adapters

Accepted on 2026-10-24. Builds on [ADR 0009](0009-keep-hosting-lifecycle-out-of-the-controller.md).

The controller's and the credential broker's decisions live in a core that does no I/O. Every edge is a port that the core defines in its own terms, and an adapter implements each port as an anti-corruption layer over one external system. Each core is unit tested with in-memory fakes of its ports and `TimeProvider`, so all logic is tested in-process, in milliseconds.

## Controller

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

## Credential broker

```
 driving adapters                         core                     driven ports         adapters
 ----------------                         ----                     ------------         --------
 setup routes (setup secret) --+                              +--> AppInstallations     GitHub REST, App JWT
 controller routes ------------+--> controller enrollment, ---+--> ControllerApprovals  GitHub REST approval variable
   (controller secret)         |    approval checks, GitHub   +--> GitHubTokens         GitHub REST access_tokens
 /health ----------------------+    token issuance, limits,   +--> BrokerState          Azure Table
 cleanup schedule -------------+    revocation, retained      +--> SecretProtection     Data Protection, Blob + Key Vault
                                    state, cleanup, redaction
```

The App JWT and its Key Vault signer belong to the GitHub adapters. The core never sees how GitHub authenticates the App.

## Rules

Two rules keep the layer honest in both components:

- **Domain types at the ports.** Ports take and return domain types such as `Discussion`, `AgentTurn`, `ControllerEnrollment` and `ControllerApproval`, never `JsonNode` or `TableEntity`. GitHub's, Codex's and Azure's shapes stop at the adapter.
- **Ports are named for what the core needs, not for the provider.** The port is `Discussions`, not `GitHubGraphQL`, `AgentRuntime`, not `Codex`, and `BrokerState`, not `TableClient`. Broker mode and direct App mode are two adapters of one controller `GitHubTokenSource` port.

## Testing

- Each core gets unit tests against fakes of every port. All component tests are unit tests.
- Adapters get no separate contract tests. We trust GitHub not to break its API or webhook payloads. An adapter is exercised by the end-to-end environment. A fake of a stateful port such as `BrokerState` must keep the guarantees the core relies on, such as optimistic concurrency.
- End-to-end is one shared local Aspire AppHost that runs the credential broker with its own local storage, one or more controllers and a real repository. Humans and coding agents use it to spin up the whole system locally, debug it, and run the end-to-end tests, including the installer pointed at the local broker. The only shared resource is the Key Vault that holds the GitHub App's signing key. A locally generated controller secret can be kept in .NET user secrets and passed to a controller at startup.

## Considered options

- **Keep concrete edges and fake at the process or emulator level** (a fake `gh` and `codex` on `PATH` for the controller, Azurite and real HTTP for the broker, as today). Rejected. Every test pays for process startup, emulator round trips and real timers, which is why the suites take minutes to tens of minutes, and provider shapes spread through the cores.
- **Contract test each adapter against the real provider.** Rejected for GitHub, because its API is stable and the cost is not worth it. The agent runtime is pinned in the image, so a protocol change arrives with a deliberate image update and shows up in the end-to-end environment. Optional startup tool updates bypass that pin and carry this risk. We accept it and revisit if updates break the adapter.

Implementation: [#203](https://github.com/repoagent/controller/issues/203) (controller), [#205](https://github.com/repoagent/controller/issues/205) (credential broker), [#204](https://github.com/repoagent/controller/issues/204) (Aspire end-to-end environment).
