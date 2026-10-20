# Local development with a Quick Tunnel

Quick Tunnel mode gives a local container a temporary public HTTPS origin without a Cloudflare account, domain, credentials, host-installed `cloudflared` or inbound router configuration. It is for development/testing: hostnames change on replacement and Cloudflare provides no uptime guarantee. Use a fixed URL or the planned [named tunnel](https://github.com/egil/repoagent/issues/22) for stable hosting.

## Setup

The normal image includes checksum-pinned `cloudflared` 2026.10.1 for Linux amd64/arm64. Rebuild with a reviewed version/checksum change to update it; its self-updater is disabled. Normal gh/Codex startup updates remain configurable as before. The image remains non-root and the bridge is Native AOT, with no required .NET runtime or project language.

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

Stdout reports `tunnel-starting`, `tunnel-connected` with the public origin, and the existing hook/receiver readiness and delivery logs. Connection/URL discovery has a 90-second deadline. A tunnel startup failure, absent URL, or exited tunnel stops startup with an actionable `tunnel-error`; it does not claim readiness. The bridge and tunnel are supervised together: an exited process stops its companion, while `stop` signals the bridge first, allowing it to disable the saved hook before cloudflared is stopped. Bridge cleanup gets a bounded grace period, with process-group termination as a fallback. Temporary provider disconnections may be retried by cloudflared; startup readiness does not promise continuing provider availability.

Cloudflare documents that a Quick Tunnel URL stops working when its cloudflared process stops. We do not assume the hostname remains permanently reserved; disable the GitHub hook rather than leaving it pointed at an old temporary endpoint.

## Replace the tunnel or switch hosting

```sh
./repoagent restart
```

Replacement creates a new temporary hostname and updates the same saved repository hook using the same private signing secret. Keep the instance's normal volumes: they contain the hook identity, source checkpoints, Codex credentials/history and workspace. Accepted input is not intentionally replayed, and Discussion sessions retain their context. Do not run another instance against the same state concurrently.

To switch to an operator-provided origin, stop, repeat configuration with the same repository/App and `--mode fixed-url --public-url https://agent.example.com`, then start. Preserve the same instance and volumes. The fixed-URL mode uses its own external ingress and starts no tunnel. Switching to `--mode polling` starts neither a tunnel nor a listener. Controlled stop attempts to disable the upstream hook and retains it for restart; forced kill or failed remote cleanup can leave it active. The hook remains until explicitly decommissioned; see the [webhook guide](webhooks.md) for private storage, permissions, receipt semantics, recovery limits and hook cleanup.

## Manual verification

Use a disposable Discussion and the separate human test participant. After startup reconciliation, add a mention/reply; retain its real GitHub delivery status, response URL, source EYES, hook ID and native session ID. Stop/restart the container, verify a different temporary hostname on that same hook and submit a continuation that recalls prior context through the same native session. Confirm the saved hook is inactive after controlled stop, its ID/secret are retained for restart, and the stopped container owns no remaining tunnel process. Also verify fixed-URL SIGTERM deactivation and a network-unavailable shutdown that logs failure and finishes within the stop budget.

Also check a real network-isolated startup fails without readiness, and terminating cloudflared in an idle test container stops the companion bridge with a visible error. These are separate bounded checks; do not automatically retry the entire provider journey. Keep secrets, auth caches and private keys out of evidence. Record results and any provider limitations in [issue #21](https://github.com/egil/repoagent/issues/21).

Reference: [Cloudflare Quick Tunnels](https://developers.cloudflare.com/tunnel/get-started/quick-tunnels/).
