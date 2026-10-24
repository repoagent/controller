# Repository webhooks with a fixed HTTPS URL

Polling remains the default. Fixed-URL mode adds an HTTP listener and registers a repository-specific webhook; it does not change the GitHub App's shared callback URL. Each deployment owns its endpoint and private signing secret.

## Configure and run

Install the App with **Discussions: read and write** and **Webhooks: read and write**. The installation owner must approve the additional permission after it is added to the App. Polling-only deployments do not need the Webhooks permission. Webhook-mode GitHub tokens retain both approved permissions when renewed.

Provide external HTTPS ingress that forwards `/github/webhook` to the container's HTTP listener. The public URL must be an HTTPS origin without a path, query, fragment or embedded credentials. TLS terminates at the ingress; keep certificate verification enabled for GitHub deliveries.

```sh
./repoagent build
./repoagent configure --repository OWNER/REPO --app-id APP_ID \
  --key-path /absolute/path/key.pem --mode fixed-url \
  --public-url https://agent.example.com --listen-port 8080 \
  --reconcile-interval 300
./repoagent login
./repoagent check
./repoagent start
./repoagent logs --follow
```

The setup wizard offers the same deployment choice. Existing account configuration, optional skills and the CLI update choice still apply. For dedicated live verification, use an isolated instance, empty `--mention-user ''` and explicit `--tool-updates pinned` as described in the acceptance catalog.

The local Docker wrapper publishes the selected listener port on **127.0.0.1 only**, for an external HTTPS reverse proxy. It does not create that proxy or a public endpoint. For a proxy in another container, connect both containers to an appropriate private Docker network and forward to the agent's container port. Polling mode creates no listener or published port.

The native invocation inside an already configured container is:

```text
RepoAgentController --repo OWNER/REPO --app-id APP_ID \
  --private-key /run/secrets/github-app.pem --workspace /workspace \
  --state /data/state/bridge.json --webhook-url https://agent.example.com \
  --listen-port 8080 --reconcile-interval 300
```

`check` verifies credentials and the approved installation grant without registering a hook or starting agent work. `start` binds the listener and creates or updates the saved hook before reporting readiness. Missing permission or a failed registration stops startup with an error.

## Delivery and persistence

The bridge subscribes to `discussion` and `discussion_comment`. It verifies the HMAC-SHA256 over the original request bytes, checks the configured repository, handles ping, and queues an observation of the affected Discussion. HTTP acknowledgement is asynchronous and is **not** a native acceptance receipt. EYES still follows actual native acceptance.

One worker serializes targeted observations and reconciliation through the existing JSON checkpoint and native-session path. Redelivery can schedule another read, but unchanged accepted source versions are not deliberately sent again. Routine comments from this App are ignored by the receiver; existing source checkpoints also suppress own output and receipts. Delivery IDs, event/actions, scheduled Discussions, forwarding and errors appear in timestamped stdout without the secret or token.

The generated secret and returned hook ID live in private persistent state, separate from public settings. Keep the state, native history, credentials and workspace volumes when replacing the container. Restart reconfigures the same hook with the same secret. To change the public hostname, stop, repeat configuration with the new origin, then start; the saved hook is updated rather than another being created. Run one worker/replica for each state file.

Webhook mode reconciles once at startup and then at the configured interval, default five minutes. Reconciliation covers missed notifications and emoji reactions, for which GitHub documents no Discussion reaction webhook. Activity removed between observations can still be missed. The in-memory scheduling queue is not a durable event history. Failed or unacknowledged native delivery is retried with backoff and redelivered with a marker, as in polling mode (see the [README](../README.md#checkpoints-and-manual-recovery)).

If the first registration is interrupted after GitHub creates the hook but before its ID is saved locally, inspect repository Webhooks before retrying. Remove only that deployment’s orphan hook (verify its endpoint and creation time), then retry with the retained private state. Registration is not an atomic transaction across GitHub and the local filesystem.

Controlled shutdown attempts to disable only this deployment’s saved repository hook before closing the listener. It uses an independent five-second cleanup budget; success logs `webhook-disabled`, while an unconfirmed result logs `webhook-disable-error` with manual deactivation guidance. The hook ID, URL, secret and events are retained; restart re-enables and updates the same hook. No webhook is deleted.

This is best-effort cleanup: forced termination, network loss, insufficient permissions or a stop grace period that is too short can leave the hook active. Inspect repository **Settings → Webhooks** and deactivate only the recorded hook if shutdown cannot confirm success. GitHub does not automatically redeliver failed deliveries; startup reconciliation covers observable activity that remains available. Disabling a hook avoids sending future repository payloads to a stale endpoint. Signatures authenticate payloads; they do not encrypt their contents or reserve a temporary hostname.

Deleting local storage does not disable or delete the remote hook. For permanent decommissioning, remove only this deployment’s recorded hook in repository settings after shutdown. Keep its private state if you intend to restart or switch endpoint modes; deleting it loses the saved hook identity and secret.

## Hosting choices

| Choice | Status | Public endpoint |
| --- | --- | --- |
| Polling | Available | None; no inbound listener |
| Fixed URL | Available | Operator-provided HTTPS ingress, independent of Cloudflare |
| [Quick Tunnel](quick-tunnel.md) | Available | Temporary hostname for local testing, no Cloudflare account/domain |
| Named tunnel | Planned in [#22](https://github.com/repoagent/controller/issues/22) | Stable hostname with operator-managed Cloudflare configuration |

The same image can run behind external HTTP ingress in Azure Container Apps or another host. Set the public origin to that deployment's HTTPS hostname and the ingress target port to the configured listener port. Run image command `start`; provide the selected key at `/run/secrets/github-app.pem`, container-owned Codex authentication, public configuration, and persistent storage at the paths in the container guide. Keep configuration read-only during agent work. Use one replica and persistent storage for the JSON state/native history; do not rely on an ephemeral container filesystem for conversation continuity. Provisioning the cloud resources and HTTPS ingress is operator-owned.

## Manual verification catalog

Use authorized disposable Discussions and separate human participant credentials. Observe conditions with bounded deadlines; retain failures for diagnosis rather than automatically retrying the journey. Record image/tool versions, source/reply URLs, delivery IDs, agent session IDs, hook ID/URL and redacted logs in issue #20.

| Case | Action | Observable result |
| --- | --- | --- |
| W01 | After startup reconciliation, create a canonical App mention while periodic reconciliation is not due. | Real GitHub delivery gets a prompt 2xx; targeted observation, agent session, App reply and source EYES precede the next reconciliation. |
| W02 | Add a comment and a nested reply. | Each delivery targets the existing Discussion/session and the App replies under the correct root, with source EYES. |
| W03 | Redeliver a handled delivery using GitHub's delivery API/UI. | Delivery is acknowledged/observed without another forwarded accepted source or duplicate reply. |
| W04 | Send an invalid signature; separately sign a wrong-repository payload using the test deployment's secret. | HTTP401/403 respectively; neither schedules agent work. Never put the secret in a command argument or log. |
| W05 | Restart; then change the externally supplied origin and start again. | One saved hook ID/secret is reused, URL changes in GitHub, and a real continuation keeps the same agent session/context. |
| W06 | Add a human reaction on App output. | A scheduled reconciliation observes it and native feedback is accepted, without requiring a reaction webhook. |
| W07 | Select polling in a separate clean instance. | Authentication and ordinary polling still work without hook permission, listener or published port. |

Successful disposable fixtures can be deleted after snapshots are retained. Failed fixtures stay until diagnosis is complete. Preserve only redacted conversation/log evidence; do not copy App credential caches, hook secrets, Codex auth files or the PEM into artifacts.

References: [GitHub repository hooks](https://docs.github.com/en/rest/repos/webhooks), [failed deliveries](https://docs.github.com/en/webhooks/using-webhooks/handling-failed-webhook-deliveries), [delivery practices](https://docs.github.com/en/webhooks/using-webhooks/best-practices-for-using-webhooks), [signature validation](https://docs.github.com/en/webhooks/using-webhooks/validating-webhook-deliveries), [Discussion events](https://docs.github.com/en/webhooks/webhook-events-and-payloads#discussion), [Azure external ingress](https://learn.microsoft.com/en-us/azure/container-apps/ingress-overview).
