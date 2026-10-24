# Durable webhook work for an idle Azure controller

The native controller supports an opt-in `--azure-work-queue` mode. It retains
work after the webhook HTTP response ends and releases it after native turns,
observed-input checkpoints and requested checkout refreshes finish. Polling
keeps its behavior. Ordinary webhook mode shares the deleted-Discussion
handling described under failures below, without the queue retry.

This is the native implementation, not an activated Azure deployment profile.
A published image, compatible storage, managed-identity authorization and real
scaling acceptance are still required under issues #37 and #38. The working
local StoryBeam controller can continue running during those checks.

## Configuration

Use webhook broker clone mode with explicit `--repository-id` and
`--installation-id`, the protected enrollment file, and a queue endpoint:

```text
--azure-work-queue https://ACCOUNT.queue.core.windows.net/controller-work
```

The endpoint accepts only the public Azure HTTPS queue format without
credentials, SAS, query, fragment or alternate port. Authentication uses the
app's explicit system-assigned managed identity. It never discovers local
Azure developer credentials or accepts a connection string. Give that identity
queue data access at the selected queue scope; the controller sends, receives,
updates and deletes messages. Azure's [queue authorization documentation](https://learn.microsoft.com/en-us/azure/storage/queues/authorize-access-azure-active-directory)
explains the separate identity and RBAC requirements.

The queue can be a child of the existing Standard StorageV2 account. It does
not require another storage account, user-assigned identity, registry, network
or continuously running service. Storage bytes and operations still have a
cost; no idle-cost or monthly-spend guarantee follows from this source change.

## Work lifetime

Startup creates an invisible, nonexpiring reconciliation hint before broker,
Git or native startup can outlive the initial HTTP request. Validated incoming
deliveries are persisted before HTTP202. Messages contain only a version,
immutable repository/App/installation/broker binding hash and a reconciliation
number; Discussion bodies and credentials are not stored in the queue.

The receiver holds at most 32 leases, renews their 90-second visibility every
15 seconds using the latest pop receipt, and continues observing new inputs
while an earlier native turn runs. Native acknowledgment does not complete
work. The existing checkout admission/turn journal coordinates the idle
boundary; only known completed work with saved checkpoints and completed
refresh can be acknowledged. Discussion input still waiting in the controller
for an idle session also retains every observed item until it is dispatched.
The acknowledgment batch is frozen before checking
idle, so a push observed during that check retains its own queue item. A failed
or cancelled enqueue also stops the runtime because the service may already
have committed the hint. Duplicate hints reread observed state rather
than replaying an accepted input. Autonomous full-poll and checkout timers are
disabled in this mode; startup and authenticated delivery hints reconcile.
Safe deferred refreshes retry the retained generation at idle with a five-second
backoff. Temporary lookup failures need no new delivery; uncertain updates still
require manual recovery and retain their work.

Lease loss, lost broker credentials, unknown native activity, failed checkpoint
and failed queue send, receive, retry or completion stop the runtime while
retaining recovery evidence. A failed observation does not: its hint returns to
the queue, invisible for 5 seconds doubling per delivery up to 10 minutes,
while later hints continue (`durable-work-retry`). A native delivery that fails
after its input was observed is not retried through the queue: the controller
retries it itself (`delivery-retry`), and the observed hint stays held until
the input is dispatched. Only GitHub's typed `NOT_FOUND` for the Discussion
itself in the resolved configured repository confirms deletion. The controller
then forgets that Discussion's state, including its session mapping,
checkpoints, waiting input, delivery retry and pending markers, and completes
its work (`discussion-forgotten`); a turn it already runs continues until the
native ends it, and native conversation history is retained. Timeouts,
incomplete reads, access loss, rate limits and provider errors are never
treated as deletion. Within a running lifetime, failed or unacknowledged native
input is retried and redelivered with a marker; native work left uncertain when
a runtime stops is not automatically replayed. Backlog, retried failures or
manual recovery can therefore keep compute active; that condition is not idle.

## Scaling and stopping

The intended Container Apps profile combines HTTP wake-up with an `azure-queue`
rule, minimum zero and maximum one. Its queue rule must count **all** messages,
including invisible received messages, with target one and activation zero.
[KEDA's queue scaler](https://keda.sh/docs/2.21/scalers/azure-storage-queue/)
documents `queueLengthStrategy: all`. [Container Apps scaling](https://learn.microsoft.com/en-us/azure/container-apps/scale-app)
documents managed-identity custom rules. Platform readback and a real turn held
beyond the cooldown must confirm this behavior before enabling idle scaling.
An HTTP-only rule cannot account for a turn that continues after HTTP202.

Normal queue-mode replica shutdown leaves the signed GitHub webhook active so
the next delivery can wake the app. For deliberate removal, first drain and
stop this controller, then invoke the same native binding/credentials with
`--disable-webhook`, retaining `--webhook-url` and omitting
`--azure-work-queue`. The controller takes its normal exclusive state lease,
authenticates and confirms the saved hook is inactive without starting native
work. Do this before revoking its broker authority. A failed confirmation
returns failure and preserves the saved registration for inspection. Any broker
lease minted for cleanup participates in normal outstanding-token settlement;
disabling a hook does not settle or transfer controller ownership.

Do not remove queued messages to force an idle result or bypass manual
recovery, and do not disable byte-range locks or relax credential permissions.
Real Azure queue authorization, invisible-message scaling, zero/cold-start,
signed delivery, native history and restart acceptance remain deployment gates.
