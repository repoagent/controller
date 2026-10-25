# Evaluating ACP for local agent runtimes

Research date: **2026-10-25**. Reviewed controller commit `e6cb503d4eb79f6f2af1d1d1f4e43e23ad454e77`, official ACP specifications, registry entries, adapter implementations and .NET library provenance. This is a source-based assessment; agent/runtime behavior was not exercised. Context: [glossary](../../GLOSSARY.md), [ADR 0010](../adr/0010-structure-the-controller-and-broker-as-ports-and-adapters.md) and [ADR 0011](../adr/0011-pin-tools-and-update-them-through-a-smoke-tested-image.md).

## Recommendation

**ACP makes sense for supporting several local agent backends. Introduce it alongside the direct Codex adapter and prove parity with two backends before choosing it as the default.** For a Codex-only controller, the immediate benefit is smaller: the existing connection already uses a subprocess and stdio JSON-RPC, and the maintained Codex ACP adapter still starts App Server. The value is a shared protocol and upstream provider integration, with another packaged dependency to operate. [Current adapter](../../src/RepoAgentController/Codex.cs), [Codex adapter README][codex-readme]

The controller would act as a headless ACP client. Its agent-runtime adapter would launch the selected ACP agent locally. Discussion enrollment, waiting input, global admission, redelivery, EYES receipts, the GitHub outbox and reply routing remain controller policy. The agent currently publishes through an authenticated CLI wrapper; streaming ACP assistant text alone would not preserve a batch's several possible reply targets. [Bridge](../../src/RepoAgentController/Bridge.cs), [agent instructions](../../src/RepoAgentController/Codex.cs), [ACP architecture](https://agentclientprotocol.com/get-started/architecture)

This fits ADR 0010's existing `IAgentRuntime` seam. Keep ACP wire types in its adapter. Its current domain interface still reflects Codex semantics, so adoption includes revisiting the interface rather than synthesizing unsupported native guarantees. [Runtime interface](../../src/RepoAgentController.Core/IAgentRuntime.cs)

## What needs to change in this controller

| Existing requirement | Consequence for ACP adoption |
| --- | --- |
| Start/load one persistent session per Discussion | Map new/load/resume according to negotiated capabilities; persist backend identity with the session ID. |
| Acknowledge input separately from completion | Select an explicit version/adapter contract that supplies acceptance evidence. V1 completion cannot serve as an early receipt. |
| Correlate accepted work with completion | Preserve the refresh journal and admission policy using genuine correlation or a revised operation/activity model. |
| Select model and reasoning before session creation | ACP options are session-scoped; move discovery/selection to the appropriate point and reject unsupported choices. |
| Inject developer instructions and packaged skills; reload project context | Implement equivalent authority, discovery and reload through each backend's configuration. |
| Replace model-account credentials and preserve GitHub tool authority | Verify backend-specific credential handling, wrapper inheritance, descendant termination and restart behavior. |

Sources: [runtime interface](../../src/RepoAgentController.Core/IAgentRuntime.cs), [session policy](../../src/RepoAgentController.Core/AgentSessions.cs), [dispatch and selection](../../src/RepoAgentController/Bridge.cs), [Codex setup](../../src/RepoAgentController/Codex.cs), [credential replacement](../../src/RepoAgentController/CodexCredentials.cs).

Three implementation details make a direct replacement risky:

- **Dispatch awaits acceptance while holding the bridge gate and checkout admission.** Waiting for an entire ACP v1 prompt here would block other dispatches and postpone receipts. Reusing the current transport's universal 60-second request timeout would also turn long prompts into uncertain retries. A successful pipe write is not evidence of agent acceptance. Revisit the mapping to `AgentRequestRejected` too: the current type promises no work was performed, which a failed long-running ACP request must not be assumed to establish. [Dispatch](../../src/RepoAgentController/Bridge.cs), [transport](../../src/RepoAgentController/Codex.cs), [rejection contract](../../src/RepoAgentController.Core/IAgentRuntime.cs)
- **Refresh requires both a recorded admission and correlated completion.** The journal rejects missing/unmatched turn identities and blocks after transport loss. Sessions stay active even when a native idle notification arrives before completion. ACP activity needs an explicit mapping, including a policy for detached background work; an aggregate state is not automatically a native turn identity. [Refresh journal](../../src/RepoAgentController/CheckoutRefresh.cs), [session regression cases](../../tests/RepoAgentController.Tests/AgentSessionsTests.cs)
- **Provider portability includes instruction authority and process ownership.** The existing adapter uses higher-priority developer instructions, extra skill roots, explicit skill inputs, trust configuration and skill-reload checks. Putting those instructions into a user prompt would change their authority. It also isolates the CLI environment, installs authenticated wrappers and terminates descendants on credential loss. Several backends need backend-scoped authentication health and persisted routing instead of one shared Codex account failure. [Codex setup](../../src/RepoAgentController/Codex.cs), [environment isolation](../../src/RepoAgentController/AppGitHubCommand.cs), [runtime composition](../../src/RepoAgentController/BridgeCommand.cs)

The smallest useful experiment is a headless ACP adapter that demonstrates the same controller workflow with Codex and a second agent, then exercises the recovery and concurrency gates below. Keep the direct adapter available until that evidence passes. Pin the adapters **and the runtime actually launched** through ADR 0011's smoke-tested image process; a bundled dependency can otherwise bypass the image's existing Codex pin. This recommendation follows the current ADR direction and does not record a new accepted architecture decision.

## Evidence snapshots and provenance

Documentation/schema source inspected: ACP commit [`41c6a98`](https://github.com/agentclientprotocol/agent-client-protocol/tree/41c6a98caa3dea7d8e05a8013348e266dd4286e1). Registry snapshot: [`cedfdb9`](https://github.com/agentclientprotocol/registry/tree/cedfdb9fe2c3ed52045e95ea9af0388eae69530a). Published versions and development heads differ:

| Adapter | Published release / registry package | Additional source inspected |
| --- | --- | --- |
| Codex | [2.2.2, released October 10][codex-release]; registry installs `@agentclientprotocol/codex-acp@2.2.2`. Released source [`202e66e`](https://github.com/agentclientprotocol/codex-acp/tree/202e66e63343ec4f5a5e35ab590251d813d129ca). | Development head [`a77829f`, October 23](https://github.com/agentclientprotocol/codex-acp/tree/a77829fecc87cebbb2e53782ce6795aee072adc8), including draft-v2 handlers absent from the release. |
| Claude | [0.95.0, released October 23][claude-release]; registry installs `@agentclientprotocol/claude-agent-acp@0.95.0`. Released source [`ebfbfbd`](https://github.com/agentclientprotocol/claude-agent-acp/tree/ebfbfbdaa9c2a6a8bd9eb0a4965b521009152350). | Development head [`739dde3`, October 24](https://github.com/agentclientprotocol/claude-agent-acp/tree/739dde3ae5bb848bdb90e2c9150e2cc4687d480c). Lifecycle findings below also apply to the release; later subagent presentation refinements were not assumed shipped. |

The [historical Zed Codex README](https://github.com/zed-industries/codex-acp/blob/296069e841634cd4bb9bc4515602d836e49231ec/README.md) explicitly directs new installs to `agentclientprotocol/codex-acp`. The old Claude repository redirects to `agentclientprotocol/claude-agent-acp`. Registry authors credit OpenAI/JetBrains/Zed for Codex and Anthropic/Zed/JetBrains for Claude. These are primary adapter projects using vendor runtimes; registry authorship is not a guarantee that every agent feature is standardized by ACP. [Codex registry entry][codex-registry], [Claude registry entry][claude-registry]

## Stable v1 contract versus optional and draft behavior

| Concern | What the reviewed specification establishes |
| --- | --- |
| Local transport — **core** | UTF-8 JSON-RPC 2.0 over stdin/stdout, one message per newline, protocol-only stdout and separate stderr. The client launches the agent subprocess. Existing JSON-RPC machinery may be reusable; method and lifecycle semantics differ. [Transports](https://agentclientprotocol.com/protocol/v1/transports) |
| Remote transport — **draft/custom** | The introduction mentions HTTP/WebSocket scenarios, but the normative v1 transport page still calls Streamable HTTP a draft proposal. Custom bidirectional transports are allowed. Local stdio interoperability does not prove a portable remote deployment. [Introduction](https://agentclientprotocol.com/get-started/introduction), [Transports](https://agentclientprotocol.com/protocol/v1/transports) |
| Negotiation — **core** | `initialize` agrees a major protocol version and exchanges agent/client capabilities and auth methods. Treat absent capabilities as unsupported. An SDK/npm major version is not the negotiated protocol version. [Initialization](https://agentclientprotocol.com/protocol/v1/initialization) |
| Session creation — **core** | `session/new` accepts absolute `cwd` and MCP configuration and returns a unique `sessionId`. Sessions have independent contexts. No persistence duration or concurrency limit is prescribed. [Session setup](https://agentclientprotocol.com/protocol/v1/session-setup) |
| Recovery — **optional, stable** | `loadSession: true` enables `session/load`, which replays history before returning. `sessionCapabilities.resume` enables `session/resume`, restoring context without replay. `list`, `close` and `delete` are separately optional. `close` cancels work and releases active resources. [Session setup](https://agentclientprotocol.com/protocol/v1/session-setup), [Session list](https://agentclientprotocol.com/protocol/v1/session-list), [Session delete](https://agentclientprotocol.com/protocol/v1/session-delete) |
| Prompt acceptance/completion — **v1 core** | `session/prompt` remains outstanding while output streams through `session/update`. Its response contains `stopReason`: `end_turn`, `max_tokens`, `max_turn_requests`, `refusal` or `cancelled`. There is no separate core acceptance response. [Prompt turn](https://agentclientprotocol.com/protocol/v1/prompt-turn) |
| Live status — **limited in v1** | Message chunks, tool status, plans and configuration updates provide activity. Tool status describes a tool, not the whole session. The stable schema has no `state_update`; `session/list` metadata lacks an active-turn/status field. Do not mistake silence for idle. [Tool calls](https://agentclientprotocol.com/protocol/v1/tool-calls), [stable schema][schema-v1] |
| Cancellation — **core, with asynchronous completion** | Send `session/cancel` notification, answer outstanding permission requests as cancelled, and continue receiving updates until the original prompt returns `cancelled`. General `$/cancel_request` support and cascading cancellation are optional. Killing every detached process is not guaranteed. [Prompt cancellation](https://agentclientprotocol.com/protocol/v1/prompt-turn#cancellation), [Request cancellation](https://agentclientprotocol.com/protocol/v1/cancellation) |
| Steering/queueing — **not stable-v1 baseline** | Sending input into an active turn is not a portable substitute for ordinary sequential prompting. Current adapters advertise extra steering/queueing surfaces; negotiate and validate each instead of assuming native Codex steering semantics. Extensions use `_meta` and `_`-prefixed methods. [stable schema][schema-v1], [Extensibility](https://agentclientprotocol.com/protocol/v1/extensibility) |

**Draft v2 changes the integration contract materially.** `session/prompt` returns the inserted user message's `messageId`; `state_update` reports `running`, `requires_action` and `idle`. Foreground state is session-scoped, not a provider turn identifier, and idle can coexist with background activity. Acceptance means insertion into the ACP conversation, **not persistence or model consumption**. Lost responses remain uncertain; retries can create another submission. These limits are explicit in the draft. [v2 prompt lifecycle](https://agentclientprotocol.com/protocol/v2/prompt-lifecycle)

V2 also requires list/resume/close, replaces `session/load` with resume plus `replayFrom`, removes client filesystem/terminal execution methods in favor of client-provided MCP tools, and restructures capabilities/updates. Even the migration guide's wording “stable v2 baseline” refers to a schema within an overall draft protocol. [v2 migration](https://agentclientprotocol.com/protocol/v2/migration)

## Session durability, multiplexing and delivery uncertainty

**Conversation continuation is different from recovery of an interrupted agent turn.** V1's prompt request has session ID/content/metadata; its response has a stop reason, not a durable submission token. Optional message IDs are neither client-selected idempotency keys nor provider turn IDs. [stable schema][schema-v1]

Integration inference: restored history may leave the original RPC, approval, turn status or input-consumption outcome unresolved.

ACP allows independent sessions on one connection, and both adapters keep per-session state/query structures. This supports multiplexing structurally; it does not establish maximum parallelism, crash isolation, thread safety under overlapping callbacks or separate OS authority per session. Provider history directories, accounts and working directories still matter. Neither cross-agent migration of stored sessions nor continuation under a different account/provider is promised. [Session setup](https://agentclientprotocol.com/protocol/v1/session-setup), [Codex session implementation][codex-server-main], [Claude session implementation][claude-agent]

## Configuration, permissions and tool environment

- **Models/reasoning:** session `configOptions` are optional, preferred over legacy modes, and set through `session/set_config_option`. Categories include `model`, `thought_level`, `model_config` and `mode`; option IDs/values are agent-defined. Responses and `config_option_update` carry the full state, including dependent changes when the model changes. There is no universal model catalog or portable mapping of every agent's effort levels. Boolean options require a client capability. [Session config options](https://agentclientprotocol.com/protocol/v1/session-config-options)
- **Headless permissions:** `session/request_permission` is a client callback, carrying explicit available choices. The specification permits automatic allow/reject according to user settings; it does not define one universal unattended approval or sandbox policy. A controller needs an explicit response policy and cancellation handling even when it has no IDE UI. [Tool permissions](https://agentclientprotocol.com/protocol/v1/tool-calls#requesting-permission)
- **Client tools:** v1 `fs/read_text_file`/`write_text_file` and the five terminal methods are negotiated capabilities, not mandatory client implementations. Terminals include cwd/env, output, wait, kill and release lifecycles. Declining them prevents those ACP callbacks, **not the agent's own local file/shell access**. Local agent tools still operate under the runtime's environment and sandbox. [Filesystem](https://agentclientprotocol.com/protocol/v1/file-system), [Terminals](https://agentclientprotocol.com/protocol/v1/terminals), [tool execution](https://agentclientprotocol.com/protocol/v1/tool-calls)
- **MCP:** v1 requires agent support for stdio MCP servers; HTTP/SSE are optional. Session requests carry server command/args/env or HTTP headers. This configures MCP processes/connections, not arbitrary environment injection into every native shell tool. Existing agent configuration can also affect server selection. [MCP setup](https://agentclientprotocol.com/protocol/v1/session-setup#mcp-servers), [Codex MCP translation][codex-mcp]
- **Authentication:** initialization advertises methods; protocol-driven methods use `authenticate`, while terminal login requires advertised client support. ACP does not supply a model account or GitHub authority, and successful agent login does not prove agent tools receive the controller's GitHub token. Provider login and tool-credential handoff are separate integration checks. [Authentication](https://agentclientprotocol.com/protocol/v1/authentication)

## Actual adapter behavior

### Codex

Published **2.2.2** is a stdio ACP adapter around App Server. Its release manifest specifies ACP SDK `^1.5.0` and Codex `^0.160.1` dependencies; these are dependency ranges, not a verified installed resolution. Release initialization advertises load, resume/list/close, embedded context/images, HTTP MCP and no SSE MCP. It also advertises additional experimental surfaces. [released README][codex-readme], [released package manifest][codex-package], [released initialization][codex-server-release]

Its ACP session ID is the Codex thread ID. Resume/load call the provider's resume APIs; history comes from Codex storage. An important edge case is documented in source: a newly created, never-prompted thread may lack a rollout on disk, and the fallback works only while that thread remains live in App Server. Simultaneous ownership by another Codex client can prevent resume. Do not claim every successful `session/new` is immediately restart-durable. [released session client][codex-client-release]

The release uses ACP v1. The **development head** adds version-specific routing and draft-v2 prompt insertion/status handling. Its internal App Server client retains native turn IDs while awaiting `turn/completed`; ordinary ACP v1 responses do not expose that full lifecycle. [development router][codex-router], [v2 handler][codex-server-main], [internal turn handling][codex-appclient]

Other concrete behaviors worth verifying against a chosen package:

- Model, `reasoning_effort` and `fast-mode` configuration are implemented; fast mode maps to a provider service tier when the model supports it. These selectors are adapter-specific. [model options][codex-model], [fast mode][codex-fast]
- Approval/sandbox presets include read-only, workspace-write, automatic review and full access. The inspected source defaults to automatic review; assuming a particular unattended security policy from ACP alone would be wrong. [mode implementation][codex-mode]
- API keys and ChatGPT authentication are supported. `NO_BROWSER` hides browser login; it does not create credentials. `CODEX_PATH` selects another binary, and `CODEX_CONFIG` supplies adapter-specific configuration. [released README][codex-readme]
- The child App Server inherits the adapter process environment unless an explicit environment is supplied. The source includes lazy App Server restart and session restoration with crash-loop limits; that is adapter recovery, not an ACP-wide guarantee or resurrection of a lost in-flight turn. [process launch][codex-rpc], [recovery implementation][codex-recovery]
- AIR status/index, typed failures and other `_meta.jetbrains.air` features are opt-in experimental contracts. They must not be counted as ordinary ACP v1 capabilities. [extension contract][codex-extensions]
- The release's typed-failure response helper returns `end_turn` with failure metadata. A stop reason alone must not be interpreted as proof of provider success. [released failure mapping](https://github.com/agentclientprotocol/codex-acp/blob/202e66e63343ec4f5a5e35ab590251d813d129ca/src/CodexAcpServer.ts#L3904)

### Claude

Published **0.95.0** uses official Claude Agent SDK `0.3.309`, ACP SDK `2.0.1`, and Node ≥22. Unlike published Codex 2.2.2, it **already serves v1 and experimental v2**: default `auto` selects v2 for a request for version ≥2; request version 1 explicitly for stable semantics. [released package manifest][claude-package], [protocol selection][claude-serve]

V1 advertises load/resume/list/close. Records hold separate Query/input/settings; missing records use SDK resume/history. Disposal tears down queries, and SDK persistence options affect storage. This establishes no in-flight-turn reattachment guarantee. [session/query implementation][claude-agent], [history/transcript reader][claude-resume]

V1 prompting waits for completion. Draft-v2 code acknowledges SDK insertion and emits foreground states. Steering, raw SDK notifications and AIR session-index status are extra surfaces, without portable native-turn recovery guarantees. [v1 implementation][claude-agent], [released v2 prompting][claude-v2-prompt], [AIR extension contract][claude-extensions]

Reasoning choices derive from the selected model's supported effort levels. Permission bypass is restricted by user/root/sandbox checks and managed settings; native Claude tools are the default. ACP MCP definitions and process/caller/provider environments are translated into SDK options. Validate effective credentials and permissions rather than assuming an inherited variable reaches every tool. [effort implementation][claude-effort], [bypass eligibility][claude-permissions], [query configuration][claude-agent]

Cancellation needs special attention: documented background commands and some background subagent activity can outlive cancellation of the parent foreground turn. Runtime idleness and foreground idleness are therefore different observations. [Claude README][claude-readme]

## C#/.NET and Native AOT

No first-party ACP .NET SDK was identified. The official SDK catalog lists Kotlin, Java, Python, Rust and TypeScript; its separate **community** page lists three .NET projects. Being listed is not an ACP endorsement or conformance/AOT result. [official SDK list](https://github.com/agentclientprotocol/agent-client-protocol#integrations), [community libraries](https://agentclientprotocol.com/libraries/community)

| Community project | Evidence and limitation |
| --- | --- |
| [nuskey8/acp-csharp](https://github.com/nuskey8/acp-csharp) | Explicitly unofficial; provides client/agent protocol connections. Its [project file](https://github.com/nuskey8/acp-csharp/blob/main/src/AgentClientProtocol/AgentClientProtocol.csproj) targets .NET 8/9/10 and netstandard2.1, but spells the property `IsAotCompitable`. That is not Microsoft's `IsAotCompatible`, so it does not establish AOT compatibility. |
| [MertBasar0/acp-net](https://github.com/MertBasar0/acp-net) | Explicitly says it is **not a full ACP protocol SDK**; it supplies process/runtime/WSL/diagnostic/testing infrastructure and expects another protocol implementation. |
| [AgenticDesktop/ACPLibrary](https://github.com/AgenticDesktop/ACPLibrary) | Client/agent stdio library with v1-style examples and client callbacks. Its [project file](https://github.com/AgenticDesktop/ACPLibrary/blob/main/Agentic.ACPLibrary.csproj) targets .NET 10. Protocol completeness and Native AOT compatibility were not established. |

A small ACP adapter in the existing .NET controller could reuse JSON-RPC framing and implement an explicitly bounded v1 surface with source-generated `System.Text.Json` metadata. That is an engineering option, **not an implemented or measured conclusion**. It still needs bidirectional request dispatch while a prompt is outstanding, capability/version checks, callback cancellation, stderr/process supervision and preservation of delivery uncertainty. Community-library evaluation should include trimming/AOT publication, polymorphic union handling and the selected protocol snapshot. Microsoft's guidance supports source generation and AOT analysis, not a compatibility claim about any ACP package. [Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/index), [JSON source generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)

## Unverified concerns and decision gates

These are proposed follow-up checks, **not completed tests**:

1. Choose released adapter/runtime versions and record their actual initialization/configuration output. Pinning adapter source/package alone may not pin a caret-ranged runtime dependency.
2. Establish the required acceptance boundary. Test failure before submission, after provider acceptance but before acknowledgment, after output starts and before completion; inspect durable history before redelivery. Neither v1 nor draft v2 supplies exactly-once delivery.
3. Restart the controller, adapter and underlying runtime separately. Verify stored-session continuation, session IDs, model/effort restoration, pending permission behavior and lost-turn/status reconciliation; include a session created before its first prompt.
4. Run two independent sessions with overlapping tool/permission callbacks. Verify routing, cancellation isolation, active-session accounting and shared process failure behavior. Check detached tools before interpreting idle as safe to stop the runtime.
5. Verify the intended headless permission policy, existing account authentication, MCP startup and refreshed GitHub tool credentials in the packaged environment. Decline unsupported client capabilities deliberately.
6. Publish the chosen .NET implementation with Native AOT and exercise the real protocol path in the existing end-to-end environment, following ADR 0010 and ADR 0011. No community SDK or handwritten implementation was compiled in this research.

[schema-v1]: https://github.com/agentclientprotocol/agent-client-protocol/blob/41c6a98caa3dea7d8e05a8013348e266dd4286e1/schema/v1/schema.json
[registry-tree]: https://github.com/agentclientprotocol/registry/tree/cedfdb9fe2c3ed52045e95ea9af0388eae69530a
[codex-registry]: https://github.com/agentclientprotocol/registry/blob/cedfdb9fe2c3ed52045e95ea9af0388eae69530a/codex-acp/agent.json
[claude-registry]: https://github.com/agentclientprotocol/registry/blob/cedfdb9fe2c3ed52045e95ea9af0388eae69530a/claude-acp/agent.json
[codex-release]: https://github.com/agentclientprotocol/codex-acp/releases/tag/v2.2.2
[claude-release]: https://github.com/agentclientprotocol/claude-agent-acp/releases/tag/v0.95.0
[codex-readme]: https://github.com/agentclientprotocol/codex-acp/blob/202e66e63343ec4f5a5e35ab590251d813d129ca/README.md
[codex-package]: https://github.com/agentclientprotocol/codex-acp/blob/202e66e63343ec4f5a5e35ab590251d813d129ca/package.json
[codex-server-release]: https://github.com/agentclientprotocol/codex-acp/blob/202e66e63343ec4f5a5e35ab590251d813d129ca/src/CodexAcpServer.ts#L453
[codex-client-release]: https://github.com/agentclientprotocol/codex-acp/blob/202e66e63343ec4f5a5e35ab590251d813d129ca/src/CodexAcpClient.ts#L618
[codex-server-main]: https://github.com/agentclientprotocol/codex-acp/blob/a77829fecc87cebbb2e53782ce6795aee072adc8/src/CodexAcpServer.ts
[codex-router]: https://github.com/agentclientprotocol/codex-acp/blob/a77829fecc87cebbb2e53782ce6795aee072adc8/src/AcpAgentRouter.ts#L62
[codex-appclient]: https://github.com/agentclientprotocol/codex-acp/blob/a77829fecc87cebbb2e53782ce6795aee072adc8/src/CodexAppServerClient.ts#L315
[codex-model]: https://github.com/agentclientprotocol/codex-acp/blob/a77829fecc87cebbb2e53782ce6795aee072adc8/src/ModelConfigOption.ts
[codex-fast]: https://github.com/agentclientprotocol/codex-acp/blob/a77829fecc87cebbb2e53782ce6795aee072adc8/src/FastModeConfig.ts
[codex-mode]: https://github.com/agentclientprotocol/codex-acp/blob/a77829fecc87cebbb2e53782ce6795aee072adc8/src/AgentMode.ts
[codex-mcp]: https://github.com/agentclientprotocol/codex-acp/blob/a77829fecc87cebbb2e53782ce6795aee072adc8/src/McpServerConfig.ts
[codex-rpc]: https://github.com/agentclientprotocol/codex-acp/blob/a77829fecc87cebbb2e53782ce6795aee072adc8/src/CodexJsonRpcConnection.ts#L33
[codex-recovery]: https://github.com/agentclientprotocol/codex-acp/blob/a77829fecc87cebbb2e53782ce6795aee072adc8/src/app-server-recovery/AppServerRecovery.ts#L100
[codex-extensions]: https://github.com/agentclientprotocol/codex-acp/blob/a77829fecc87cebbb2e53782ce6795aee072adc8/docs/air-extensions.md
[claude-readme]: https://github.com/agentclientprotocol/claude-agent-acp/blob/739dde3ae5bb848bdb90e2c9150e2cc4687d480c/README.md
[claude-package]: https://github.com/agentclientprotocol/claude-agent-acp/blob/ebfbfbdaa9c2a6a8bd9eb0a4965b521009152350/package.json
[claude-serve]: https://github.com/agentclientprotocol/claude-agent-acp/blob/ebfbfbdaa9c2a6a8bd9eb0a4965b521009152350/src/serve.ts#L12
[claude-agent]: https://github.com/agentclientprotocol/claude-agent-acp/blob/739dde3ae5bb848bdb90e2c9150e2cc4687d480c/src/acp-agent.ts
[claude-resume]: https://github.com/agentclientprotocol/claude-agent-acp/blob/739dde3ae5bb848bdb90e2c9150e2cc4687d480c/src/resumed-session.ts#L49
[claude-v2-prompt]: https://github.com/agentclientprotocol/claude-agent-acp/blob/ebfbfbdaa9c2a6a8bd9eb0a4965b521009152350/src/v2/prompt.ts#L30
[claude-extensions]: https://github.com/agentclientprotocol/claude-agent-acp/blob/739dde3ae5bb848bdb90e2c9150e2cc4687d480c/docs/air-extensions.md
[claude-effort]: https://github.com/agentclientprotocol/claude-agent-acp/blob/ebfbfbdaa9c2a6a8bd9eb0a4965b521009152350/src/session-effort.ts#L89
[claude-permissions]: https://github.com/agentclientprotocol/claude-agent-acp/blob/ebfbfbdaa9c2a6a8bd9eb0a4965b521009152350/src/permissions/modes.ts#L7
