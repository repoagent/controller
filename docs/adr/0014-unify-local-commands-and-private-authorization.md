# Unify local commands and private authorization

> Authorization/isolation and desktop-admission restrictions below were superseded on 2026-10-31 by [ADR 0016](0016-trust-container-owner-for-native-remote-access.md). Shared transport, naming and privacy decisions remain in force.

Accepted on 2026-10-30. Implementation and migration are tracked in [#332](https://github.com/repoagent/controller/issues/332); this decision does not describe functionality already shipped in controller 1.1.0.

The shipped activity viewer and proposed native remote-access management both address a running controller's existing agent runtime. We will use one `repoagent` CLI and one private HTTP transport over a per-instance Unix-domain socket, with server-enforced permissions for each operation. Sharing connection infrastructure reduces divergent instance selection and lifecycle behavior; it must not turn inspection permission into administration authority.

## Command and transport boundary

Commands that inspect or administer a running controller use the same CLI client, protected endpoint descriptor, instance/state/repository binding, protocol-version checks and runtime-lifetime checks. This includes `repoagent activity` and `repoagent remote`. Purely offline commands such as help or version reporting do not need a running controller.

The native executable and host/container wrappers expose `repoagent`. The wrapper resolves the selected existing instance and invokes that instance's CLI. The old `RepoAgentController` command spelling is removed, with no alias or duplicate executable. Internal project/type names need not change solely for this naming decision.

Both command families use one ASP.NET Core HTTP listener on a private Unix socket. The existing custom activity framing is migrated rather than maintained as a second protocol. All these routes are exclusive to the Unix listener: sharing a Kestrel application with status or webhook listeners must not expose them on TCP or through a tunnel. No TCP fallback, arbitrary runtime-protocol forwarding or public transcript API is introduced. An unsupported protocol, stale descriptor or wrong instance fails explicitly rather than falling back to a weaker endpoint.

CLI and HTTP handlers are thin driving adapters over the existing controller application/core, consistent with [ADR 0010](0010-structure-the-controller-and-broker-as-ports-and-adapters.md). Activity reuses the existing inspector/activity ports; remote management extends the existing runtime/state ports. Both use the same owned runtime connection. Host/container invocation stays in the wrapper under [ADR 0009](0009-keep-hosting-lifecycle-out-of-the-controller.md).

## Permissions are per operation

| Permission | Allowed operations |
| --- | --- |
| Inspection | List unambiguous mapped Discussions, read bounded retained activity, follow bounded activity updates. |
| Administration | Inspection plus all remote-management operations: enable, pair, status, clients, revoke and disable. |

Remote status and client enumeration require administration even though they read data. HTTP verbs, command names and socket reachability do not determine permission. New operations are denied unless they have an explicit authorization policy; neither new routes nor future command families inherit broad access by default.

The server authenticates local callers using OS-provided Unix peer identity or an equivalently reviewed OS-enforced mechanism. Request headers, claimed user/group IDs, environment variables and GitHub/controller credentials cannot establish operator authority. Authorization runs before runtime or protected-state access and also applies to requests made directly to the socket without the CLI. The socket directory and filesystem permissions restrict initial reachability; they do not replace this operation-level check.

Remote administration and native remote access require an OS-enforced separation between agent-executed tools and administration authority. The deployment design must protect administrative configuration, runtime control channels, credentials, process access and inherited handles as well as the socket. Omitting an environment variable is insufficient. Agent tools must not acquire equivalent authority through an alternate runtime, a wrapper invocation or shared writable configuration. Preserve non-root agent execution and the bounded GitHub credential handoff. Host root and equivalent container-administration authority remain trusted.

If authenticated caller identity or the required separation cannot be established, remote administration remains unavailable and remote access disabled. Do not treat every same-UID client as an administrator. Existing local inspection remains independent of remote enablement, pairing or provider/account availability, including its supported Linux/macOS use; Linux remote-administration acceptance does not establish support on other platforms.

## Preserve inspection and controller invariants

Activity is read-only through its application surface: it does not start or resume sessions, create turns, change remote settings or authorize clients. Preserve mapped Discussion selection, pagination, JSON/text output, limits, redaction, explicit omissions/gaps and viewer cancellation that leaves agent work running. Follow uses bounded buffers and disconnect handling rather than a blanket timeout that breaks a live viewer. Old cursors/subscriptions are invalidated by runtime replacement. Activity output must not disclose pairing codes, paired-client inventory or administration state.

Administration retains persistent desired intent, truthful observed state, generation-bound responses, bounded execution and sanitized audit outcomes. Pairing secrets are returned only to the authorized pairing caller. A shared transport does not make native desktop control read-only: allowed desktop actions must participate in admission limits, checkout exclusion, completion tracking and authority-loss shutdown. Unsupported actions must be prevented before execution, not ignored after an invariant fails.

## Rule for future commands

Every new command targeting a running controller must reuse this transport and declare:

- Its required permission and how the server denies other callers before side effects.
- Its instance/lifetime binding, allowed state/runtime effects and concurrency behavior.
- Its request, response, time and streaming limits, cancellation behavior and treatment of uncertain outcomes.
- Its sensitive output and audit policy, including which information is forbidden in ordinary logs, status and Discussions.
- Core behavioral coverage and real end-to-end evidence appropriate to its boundary.

Authorization tests must include an inspection-only caller attempting administration directly over the shared socket. Real Linux/container acceptance must also cover an administrator, an unrelated identity, agent tools and attempts to bypass the boundary through files/processes/handles. Verify route exclusion from every TCP listener. Use the shared Aspire environment for adapters and manual wrapper qualification; do not substitute a second fake-provider adapter suite for the approach in ADR 0010.

## Consequences and migration

We accept the cost of migrating the existing activity transport and establishing genuine operator isolation instead of maintaining two local protocols or treating a private socket as sufficient administration protection. Generic RPC or shell/file access is not an extension mechanism.

Removing a supported executable name is an intentional breaking CLI change under [ADR 0013](0013-use-independent-semantic-component-releases.md). Release notes and packaging must update executable invocations, native/container paths, wrappers and help together. Operators adopt matching clients/images and restart the selected controller for the new endpoint; incompatible old clients receive clear upgrade guidance. The rename and transport change do not themselves require re-enrollment or a session/state reset. Preserve existing state, journals and runtime ownership. Update the README with supported behavior when implementation ships, and qualify the pinned runtime through [ADR 0011](0011-pin-tools-and-update-them-through-a-smoke-tested-image.md).

