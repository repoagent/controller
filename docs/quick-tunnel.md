# Local development with a Quick Tunnel

Quick Tunnel mode gives a local container a temporary public HTTPS origin without a Cloudflare account, domain, credentials, host-installed `cloudflared` or inbound router configuration. It is for development/testing: hostnames change on replacement and Cloudflare provides no uptime guarantee. For stable hosting, use a fixed URL, for example through a [Cloudflare named tunnel](webhooks.md#stable-hostname-through-a-cloudflare-named-tunnel).

## Setup

The normal image includes checksum-pinned `cloudflared` 2026.10.1 for Linux amd64/arm64. Rebuild with a reviewed version/checksum change to update it; its self-updater is disabled. gh and Codex also run the image's pinned versions, including with older saved settings; startup never updates tools. The image remains non-root and the bridge is Native AOT, with no required .NET runtime or project language.

Install the GitHub App with Discussions and Webhooks read/write, with the installation owner's approval. Then select Quick Tunnel in `./repoagent setup`, or configure it explicitly:

```sh
./repoagent build
./repoagent configure --repository OWNER/REPO --app-id APP_ID \
  --key-path /absolute/path/key.pem --mode quick-tunnel \
  --listen-port 8080 --reconcile-interval 300
./repoagent login
./repoagent check
./repoagent start
./repoagent logs --follow
```

`check` verifies authentication and the webhook grant without creating a tunnel or registering a hook. `start` launches the pinned tunnel in the container, discovers its actual HTTPS origin and waits for an established tunnel connection. It then starts the existing receiver with that origin, forwarding to the listener on container loopback. The saved hook is created/updated before the bridge reports `ready`. Quick Tunnel mode publishes no host port. No Cloudflare credentials are read or requested.

Stdout reports `tunnel-starting`, `tunnel-connected` with the public origin, and the existing hook/receiver readiness and delivery logs. Connection/URL discovery has a 90-second deadline. A failure, absent URL or exit before the first tunnel connects stops startup with an actionable `tunnel-error`; it does not claim readiness. If cloudflared cannot be started at all, that error includes the operating-system error. `stop` signals the bridge first, allowing it to disable the saved hook before cloudflared is stopped. Bridge cleanup gets a bounded grace period, with process-group termination as a fallback. Temporary provider disconnections may be retried by cloudflared; startup readiness does not promise continuing provider availability.

## Automatic tunnel recovery

After the first tunnel connects, an unexpected cloudflared exit is recovered in the same container:

1. `tunnel-lost` reports the exit. The supervisor keeps the existing controller and requests a replacement tunnel without waiting for work to finish. `tunnel-recovering` announces each attempt; delays start at 5 seconds and double up to 300 seconds, then stay capped.
2. Once the replacement connects, the supervisor reads the controller's private loopback `GET /status` once a second, with a two-second total request deadline and interruptible I/O. Only an explicit `idle` response permits relaunch. `busy`, an unreachable or failing endpoint, and malformed status all keep the existing controller alive. `tunnel-waiting` logs the reason at the start of the wait and at most once every 30 seconds.
3. On idle, the supervisor requests normal controller shutdown and waits for its actual exit. A turn admitted between that status snapshot and shutdown also finishes before the controller exits. This recovery wait has no forced-shutdown deadline. The controller stops admitting new work; waiting input stays saved for the next lifetime. Explicit operator stop still uses bounded cleanup and may force termination; loss of broker authority still stops owned work immediately.
4. The supervisor starts one new controller with the replacement URL against the same volumes. It updates the **same saved hook** (same ID and signing secret) before reporting `ready`. Webhook delivery through the old URL is unavailable while recovery waits; the old hook remains registered until controller shutdown. Normal reconciliation recovers missed events.
5. A replacement that cannot connect, a new controller that cannot start or exits before `ready`, or a replacement lost before readiness is a failed attempt. `tunnel-recovery-failed` records the reason and the next attempt uses the existing capped backoff. A failed tunnel attempt preserves an existing busy or draining controller. Failed newly launched controllers are stopped before retry. Only the new controller's readiness with a live tunnel permits `tunnel-recovered`; backoff resets after 300 seconds of recovered service.

At most one tunnel and one controller are owned at a time. Sessions, retained history, source checkpoints and durable waiting input survive recovery; accepted input is not intentionally replayed. A controller that exits on its own before recovery requests its retirement, or exits unsuccessfully while retiring, still stops the tunnel and container with `tunnel-error`. Inspect its preceding error. `stop` cancels connection, status and retirement waits, starts no replacement process, and cleans up the owned controller before the tunnel. Fixed-URL and polling modes never launch or recover cloudflared.

Cloudflare documents that a Quick Tunnel URL stops working when its cloudflared process stops. Recovery does not assume the old hostname remains reserved. The saved hook is deactivated during the retiring controller's shutdown, then updated to the replacement origin on relaunch.

## Replace the tunnel or switch hosting

```sh
./repoagent restart
```

Replacement creates a new temporary hostname and updates the same saved repository hook using the same private signing secret. Keep the instance's normal volumes: they contain the hook identity, source checkpoints, Codex credentials/history and workspace. Accepted input is not intentionally replayed, and Discussion sessions retain their context. Do not run another instance against the same state concurrently.

To switch to an operator-provided origin, stop, repeat configuration with the same repository/App and `--mode fixed-url --public-url https://agent.example.com`, then start. Preserve the same instance and volumes. The fixed-URL mode uses its own external ingress and starts no tunnel. Switching to `--mode polling` starts neither a tunnel nor a listener. Controlled stop attempts to disable the upstream hook and retains it for restart; forced kill or failed remote cleanup can leave it active. The hook remains until explicitly decommissioned; see the [webhook guide](webhooks.md) for private storage, permissions, receipt semantics, recovery limits and hook cleanup.

## Manual verification

Use a disposable Discussion and the separate human test participant. After startup reconciliation, add a mention/reply; retain its real GitHub delivery status, response URL, source EYES, hook ID and agent session ID. Stop/restart the container, verify a different temporary hostname on that same hook and submit a continuation that recalls prior context through the same agent session. Confirm the saved hook is inactive after controlled stop, its ID/secret are retained for restart, and the stopped container owns no remaining tunnel process. Also verify fixed-URL SIGTERM deactivation and a network-unavailable shutdown that logs failure and finishes within the stop budget.

Also check a real network-isolated startup fails without readiness. For recovery, terminate only the owned cloudflared process (`docker exec repoagent-INSTANCE pkill -x cloudflared`) and verify `tunnel-lost`, a different hostname on the same hook ID, `ready`, then `tunnel-recovered`, followed by a continuation through the same agent session. During a long turn, kill cloudflared and verify a replacement connects while the same controller and turn stay alive; allow the turn to finish before controller relaunch. Also hold a turn admitted just after an idle snapshot for more than 12 seconds during retirement. With status unreachable or malformed, verify no relaunch and a bounded reason log. Block outbound access to Cloudflare for several retry periods to check that backoff grows, the existing controller survives and no `tunnel-recovered` appears; restore access and confirm a later retry recovers. Stop during a recovery wait and confirm no new cloudflared starts. These are separate bounded checks; do not automatically retry the entire provider journey. Keep secrets, auth caches and private keys out of evidence. Record results and any provider limitations in [issue #21](https://github.com/repoagent/controller/issues/21), and recovery results in [issue #192](https://github.com/repoagent/controller/issues/192).

Reference: [Cloudflare Quick Tunnels](https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/).
