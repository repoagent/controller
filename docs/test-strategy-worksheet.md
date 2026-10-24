# Test strategy worksheet

Fill in the **Test kind** column. Suggested values:

| Kind | Meaning |
|---|---|
| unit | In-process, fakes for every external system, fake clock. Milliseconds. |
| integration | In-process plus one real managed dependency (file system, Azurite, Table storage, git). |
| contract | Our adapter against a provider-owned artifact or faithful sandbox (broker API, Codex app-server protocol, GitHub API shape). |
| process | A real script or executable started as a process, collaborators faked. |
| live | Deployed or real external service (GitHub, OpenAI, Azure, Cloudflare). Scheduled or manual. |
| none | Covered elsewhere or not worth a test. |

```
            operator's machine (Windows/Linux)           Azure
 operator -> install / repoagent CLI ----enroll----> credential broker
                 |                                      ^        |
                 | docker run              GitHub token  |        | GitHub App key
                 v                                      |        v
           container image  ->  controller  ------------+     GitHub API
                                   |   ^
                         agent     |   | Discussions, webhooks, outbox
                                   v   |
                                 Codex app-server  -> OpenAI
```

## 1. Controller (`src/RepoAgentController`)

### Credentials

| # | Flow | Test kind | Notes |
|---|---|---|---|
| 1.1 | Get a GitHub token from the broker, using the controller secret | unit | Fake the broker's behavior. |
| 1.2 | Renew the GitHub token before expiry, and reuse an authorized lease after a controller restart | | |
| 1.3 | Broker denies, revokes or is unreachable: the controller stops or backs off, and never runs with a stale token | | |
| 1.4 | Direct App mode: sign the App JWT from the private key and get a GitHub token | | |
| 1.5 | The agent's gh and Git calls get the GitHub token through the controller's handoff, never through arguments or files | | |
| 1.6 | Codex credentials: use the imported auth, notice a refresh, and accept a replacement while running | | |

### Discussion intake

| # | Flow | Test kind | Notes |
|---|---|---|---|
| 1.7 | Polling finds new Discussions and comments since the last poll | | |
| 1.8 | Webhook mode: accept a signed delivery, reject an unsigned or wrong one, and reconcile on an interval | | |
| 1.9 | Status endpoint: `GET /status` reports busy during an agent turn and idle after it, in polling and webhook mode | | |
| 1.10 | Discussion enrollment by App mention, mention user, or explicit URL | | |
| 1.11 | Category enrollment: a new Discussion in a selected category enrolls, a preexisting one never does | | |
| 1.12 | A deleted Discussion is forgotten without stopping other live work | | |

### Agent sessions and delivery

| # | Flow | Test kind | Notes |
|---|---|---|---|
| 1.13 | Start one agent session per enrolled Discussion, and resume it after a restart | | |
| 1.14 | Deliver input as an agent turn. Input that arrives while busy waits, in order | | |
| 1.15 | Active-session limit: only running agent turns count, and extra input waits | | |
| 1.16 | Unacknowledged delivery: redeliver once idle, marked as redelivery | | |
| 1.17 | EYES receipt only after the agent session acknowledges | | |
| 1.18 | The agent's reply is posted as the App bot | | |
| 1.19 | A model label selects the model and reasoning for the next agent turn, and never starts one | | |
| 1.20 | Compatibility with the real Codex app-server protocol (start, turn, events, shutdown) | | |

### Repository settings

| # | Flow | Test kind | Notes |
|---|---|---|---|
| 1.21 | Read `REPOAGENT_*` variables. Absent, invalid and failed reads give distinct states | | |
| 1.22 | Settings are read only with the repository-settings profile | | |

### Managed checkout

| # | Flow | Test kind | Notes |
|---|---|---|---|
| 1.23 | Clone the repository into the workspace on first start | | |
| 1.24 | Refresh to origin at an idle boundary, including diverged and force-pushed cases | | |
| 1.25 | Never refresh during an active or uncertain agent turn | | |
| 1.26 | A conflict or failed refresh blocks with a manual-recovery state | | |

### Outbox, dashboard, operations

| # | Flow | Test kind | Notes |
|---|---|---|---|
| 1.27 | Outbox: retry GitHub writes, honor rate limits, drop permanent failures, survive a restart | | |
| 1.28 | Dashboard: create it in the chosen category, heartbeat, status updates | | |
| 1.29 | Baseline release notice when a newer verified release exists | | |
| 1.30 | `status` command and rotation of the retained log | | |
| 1.31 | State store persistence and an exclusive lease (one controller per state directory) | | |
| 1.32 | CLI options parse into the right runtime mode (`--once`, `--check-auth`, `--clone`, webhook flags) | | |

## 2. Credential broker (`service/auth`)

| # | Flow | Test kind | Notes |
|---|---|---|---|
| 2.1 | `POST /enrollments`: validate the request, run discovery, return the marker. A replay is idempotent | | |
| 2.2 | Approval check: the repository-held approval exists and matches the marker | | |
| 2.3 | `POST /enrollments/{id}/completion`: release the controller secret once, and replay the same completion | | |
| 2.4 | `POST /devices/{id}/acknowledgment` | | |
| 2.5 | `POST /devices/{id}/installation-token`: authenticate the controller secret, recheck the controller approval, mint a GitHub token with the profile's runtime authority | | |
| 2.6 | Lease reuse and published lease, plus issuance limits | | |
| 2.7 | Approval removed or changed: renewal is denied | | |
| 2.8 | `DELETE /devices/{id}`: self-revocation | | |
| 2.9 | Request protection and rate limits, 429 with Retry-After | | |
| 2.10 | Expired and unsent enrollment recovery, retirement of retained state | | |
| 2.11 | Cleanup job and capacity reclamation | | |
| 2.12 | Payload redaction and telemetry privacy (no secrets in logs or traces) | | |
| 2.13 | Table storage persistence and concurrency | | |
| 2.14 | `/health/live` and `/health/ready` | | |
| 2.15 | Deployed: Key Vault signing, managed identity, OIDC deploy | | |

## 3. Host CLI (`install.*`, `repoagent*`, `scripts/*.ps1`)

| # | Flow | Test kind | Notes |
|---|---|---|---|
| 3.1 | Guided install preview is read-only and shows the selected profile and permissions | | |
| 3.2 | Guided install apply: enroll, import credentials, configure, start | | |
| 3.3 | A failure at any step resumes with the same identity, and a saved enrollment cannot widen its profile | | |
| 3.4 | Controller enrollment: create the pending enrollment with the setup secret, write the approval variable with admin authority, poll completion, acknowledge | | |
| 3.5 | Controller enrollment recovery: lost responses, rate limits, deadline, no new controller approval | | |
| 3.6 | Private files: created with private permissions, unsafe ancestors rejected (Windows ACL, Unix mode) | | |
| 3.7 | Import Codex auth or log in. An unattended run never starts device login | | |
| 3.8 | Local lifecycle: start, stop, restart, status, logs | | |
| 3.9 | Local update and rollback of the image, keeping volumes and saved choices | | |
| 3.10 | Rerun keeps saved model, hosting and update choices | | |
| 3.11 | Write repository settings (`repository-settings.ps1`) | | |
| 3.12 | Revoke approval: delete only a matching marker, and report unresolved results honestly | | |
| 3.13 | Azure plan: discovery and read-only preview | | |
| 3.14 | Azure update and rollback of the hosted controller image | | |
| 3.15 | Build the image locally, and publish it | | |
| 3.16 | No secret appears in any CLI output | | |
| 3.17 | Same behavior on Windows and Linux operator machines | | |

## 4. Container image and runtime (`container/`, `image/`, `scripts/compose-image.py`)

| # | Flow | Test kind | Notes |
|---|---|---|---|
| 4.1 | Image composition: pinned artifacts and checksums, and the version catalog matches `image/default.json` | | |
| 4.2 | `runtime.py configure`: saved settings become controller arguments | | |
| 4.3 | Install skills; optional skills cannot shadow required ones | | |
| 4.4 | Prepare the Codex home and supply auth to the controller only | | |
| 4.5 | Optional tool updates at startup | | |
| 4.6 | Quick tunnel: acquire a cloudflared URL and recover after it is lost | | |
| 4.7 | Runs as non-root with read-only mounts where required | | |
| 4.8 | The built image actually starts and reaches `ready` | | |

## 5. Azure hosting (`infra/`)

| # | Flow | Test kind | Notes |
|---|---|---|---|
| 5.1 | Controller Bicep deploys the lean profile with queue and credential access | | |
| 5.2 | `controller-runtime.py` initializes private state on the Azure file share | | |
| 5.3 | Broker Bicep, RBAC and monitoring deploy | | |
| 5.4 | Bicep templates compile and their parameters validate | | |

## 6. Release pipeline (`scripts/delivery`, workflows)

| # | Flow | Test kind | Notes |
|---|---|---|---|
| 6.1 | Publish the controller image and verify an anonymous pull | | |
| 6.2 | Create a baseline release that records the image reference and tool versions | | |
| 6.3 | Tool refresh proposes pinned version bumps | | |
| 6.4 | Package the controller | | |

## 7. Cross-component journeys

| # | Flow | Test kind | Notes |
|---|---|---|---|
| 7.1 | Fresh install, enroll, mention in a Discussion, agent replies | | |
| 7.2 | Removing the controller approval stops the controller's token renewal | | |
| 7.3 | Image update keeps state, agent sessions and settings | | |
| 7.4 | Signed push and webhook delivery through a real tunnel | | |
