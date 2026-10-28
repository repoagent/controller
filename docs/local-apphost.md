# Shared local controller AppHost

The existing [broker AppHost](../service/auth/local-development.md) can run the broker, cleanup, local storage/key-ring wrapping and one native controller together. Configure it once, then start that same AppHost for manual debugging or the automated journey. Production state and the App's private signing key remain outside this environment.

## Prerequisites

- Linux or macOS for the native enrolled controller. Windows supports broker-only debugging; use a complete Linux environment for this journey, including its SDK, Docker, Azure CLI, GitHub CLI and trusted certificates. WSL must have the SDK from `global.json`; a Windows SDK installation does not supply its Linux SDK.
- The pinned SDK and image tool versions (`image/default.json`), Docker, PowerShell 7.4+, and a Codex sign-in or API-key supply. This path runs native tools; it does not install or update them.
- An Azure CLI profile authorized to sign with the shared App key, and `SharedSigningKeyUri` configured as described in [broker development](../service/auth/local-development.md#signing-and-real-github-access). Wrapping and state stay local; the generated-key offline mode cannot authenticate a real controller.
- The `repoagent` GitHub account signed into `gh`, a disposable `repoagent/e2e-<run>` repository with Discussions and an answerable category enabled, and the App installed on it. Use a fresh test repository and delete it after revoking/settling the local enrollment. The automated test itself creates and deletes only its own Discussion; it never deletes a preexisting repository.
- A trusted .NET development certificate on the **same host** for broker and controller. On Linux, include the development certificate trust directory in `SSL_CERT_DIR` as described in the broker guide. No TLS bypass is supported.

## Enroll and configure

First start broker-only mode on its stable `https://localhost:7443` origin. Omit `--isolated` for this dedicated enrolled stack; isolated broker-only debugging is still supported. Do not run another stack on its fixed ports.

```sh
aspire start --apphost service/auth/dev/RepoAgent.Auth.AppHost/RepoAgent.Auth.AppHost.csproj
aspire wait auth --apphost service/auth/dev/RepoAgent.Auth.AppHost/RepoAgent.Auth.AppHost.csproj
```

Create private directories outside the source checkout for the enrollment, controller state and agent workspace. Keep controller state and Codex storage private (0700), and enrollment/supply files owner-only. Enroll against that exact local origin through the existing operator path:

```powershell
pwsh scripts/enroll-controller.ps1 -Repository repoagent/e2e-RUN `
  -BrokerOrigin https://localhost:7443 -Profile polling `
  -StateDirectory /absolute/private/enrollment -NonInteractive -Apply
```

The enrollment uses the operator's GitHub authority only during setup. Its `enrollment.json` includes the controller secret; do not paste it into terminal output, command arguments, repository files or dashboard captures. Stop the broker-only AppHost before enabling its controller. Import the saved document into the AppHost's user secrets through standard input:

```powershell
$project = 'service/auth/dev/RepoAgent.Auth.AppHost/RepoAgent.Auth.AppHost.csproj'
$enrollment = Get-Content /absolute/private/enrollment/enrollment.json -Raw
$binding = $enrollment | ConvertFrom-Json
@{
  Controller = @{
    Repository = $binding.repository
    AppId = [string]$binding.appId
    BrokerOrigin = $binding.brokerOrigin
    Workspace = '/absolute/private/workspace'
    StateDirectory = '/absolute/private/controller'
    CodexAuthFile = '/absolute/private/codex-supply/auth.json'
  }
  Parameters = @{ 'controller-enrollment' = $enrollment }
} | ConvertTo-Json -Depth 5 | dotnet user-secrets set --project $project
```

`controller-enrollment` is an Aspire **secret parameter**, injected into the controller's `REPOAGENT_LOCAL_ENROLLMENT` environment variable. The existing controller removes that variable before starting tools. The AppHost never writes it into its `.local` files or passes it as a process argument. User secrets are private local configuration, not encrypted credential storage; do not dump AppHost environments or user-secrets values into evidence.

Optional `Controller` settings:

| Setting | Meaning |
| --- | --- |
| `CodexHome` | Private absolute agent storage; defaults to `StateDirectory/codex`. |
| `StatusPort` | Loopback status port, default 8081; distinct from the broker port. |
| `MentionUser` | Optional mention account; replies still come from the App. |
| `DashboardCategory` | Existing category for the optional owned dashboard. |
| `Clone` | Set true only for the enrollment's explicit clone profile. |
| `RepositorySettings` | Set true only for its explicit repository-settings profile. |

The `polling` enrollment in the example uses neither profile flag. Repository, App ID, origin and flags must match the saved binding; the controller performs the normal validation. The AppHost requires an HTTPS `localhost` origin belonging to its own local broker and fixes the broker's public/process endpoint to that port. State and Codex storage must be outside the source checkout. It does not copy existing production enrollments or change their origin.

## Run and inspect

Start the configured AppHost with the same command. Broker readiness gates controller startup; `/status` is shown as its `status` endpoint. Restarting the AppHost retains the broker's local stores/wrapping key and the controller's private enrollment and session storage.

**Randomized ports are refused when a controller is configured.** `aspire start --isolated` sets `DcpPublisher:RandomizePorts=true`, which would change the saved enrollment origin. Use isolated mode for broker-only worktrees, or a dedicated stable-port stack for the enrolled controller; never repair an enrollment by editing its origin. Removing `Controller:Repository` returns to broker-only mode. Keep local broker stores and private controller configuration together when deliberately resetting this environment.

Agent tools can inspect the same resources through the Aspire CLI:

```sh
aspire describe --apphost service/auth/dev/RepoAgent.Auth.AppHost/RepoAgent.Auth.AppHost.csproj
aspire logs controller --apphost service/auth/dev/RepoAgent.Auth.AppHost/RepoAgent.Auth.AppHost.csproj
aspire otel logs --apphost service/auth/dev/RepoAgent.Auth.AppHost/RepoAgent.Auth.AppHost.csproj
```

The repository's Aspire skill supplies resource/log/trace workflows. Inspect only relevant nonsecret fields; raw JSON state can include resource environments. Check the controller's `/status` is idle before intentionally stopping the stack. Aspire remains an external process supervisor, so an interrupted/forced stop is not evidence that active agent work drained safely.

## Automated journey

Stop a manually running stack first. The test owns the AppHost lifecycle and uses the same private AppHost configuration. Port randomization is disabled deliberately for its saved enrollment; do not run this fixture concurrently with another copy using those stores or ports.

```sh
REPOAGENT_APPHOST_LIVE=1 dotnet run --project tests/RepoAgent.AppHostTests \
  --configuration Release -- -class RepoAgent.AppHostTests.SharedDiscussionJourney
```

The opt-in test requires the `repoagent` account and an `e2e-` repository, starts the actual AppHost through `DistributedApplicationTestingBuilder`, checks normal HTTPS broker readiness and controller status, posts a uniquely marked mention, observes an App-authored reply plus its EYES receipt, waits for idle and deletes its Discussion in bounded cleanup. It uses the actual broker, GitHub CLI and Codex runtime; no fake provider or permissive TLS handler is installed. Disposing the AppHost stops its resources. If cleanup fails, the test fails; inspect that run's uniquely marked fixture rather than claiming cleanup succeeded.

Without the flag, the provider test is explicitly skipped. These local checks are separate:

```sh
dotnet run --project tests/RepoAgent.AppHostTests --configuration Release \
  -- -class RepoAgent.AppHostTests.SharedAppHostTests
```

They prove configured resource presence and refusal of randomized saved origins. They do not start the provider journey, prove an enrollment, measure live usage, establish credential rotation or validate dashboard Markdown rendering. The full native/container installer, Quick Tunnel and tool-refresh hosted smoke remain with [#207](https://github.com/repoagent/controller/issues/207); the developer can point a manual installer at this broker only when that exact HTTPS origin is reachable and trusted from the installed runtime.

For a complete run, record the nonsecret source/tool versions, origin, start/reply/receipt/idle/cleanup outcomes and any unexercised acceptance items. Revoke and settle the disposable local enrollment through the documented [revocation procedure](host-enrollment.md), then delete the run-owned repository. Never restore or mutate production broker state to make a development test pass.
