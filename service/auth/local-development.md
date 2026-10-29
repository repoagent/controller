# Local auth development

The Aspire AppHost runs the HTTP API and cleanup directly as .NET processes, with Azurite for Table/Blob storage and the community [Azure Key Vault Emulator](https://github.com/james-gould/azure-keyvault-emulator) in Docker. You can attach a C# debugger to either .NET process. The default generated-key stack needs no Azure subscription or cloud credentials. Real GitHub authentication uses the shared App signing key through the developer's Azure CLI sign-in; wrapping and storage remain local.

From `service/auth`, with Docker running and the SDK from `global.json` installed:

```sh
dotnet dev-certs https --trust
# Linux/WSL: include the .NET development certificate in OpenSSL's trust paths.
export SSL_CERT_DIR="$HOME/.aspnet/dev-certs/trust:/usr/lib/ssl/certs"
dotnet run --project dev/RepoAgent.Auth.AppHost
```

For an already-built Release host with the pinned .NET 11 RC3 SDK, launch directly through the .NET SDK:

```sh
ASPIRE_SUPPRESS_CLI_RUN_HOOK=true dotnet run --project dev/RepoAgent.Auth.AppHost --configuration Release --no-build --launch-profile http
```

Aspire 13.6.2's [AppHost package targets](https://github.com/microsoft/aspire/blob/v13.6.2/src/Aspire.Hosting.AppHost/build/Aspire.Hosting.AppHost.targets) explicitly support `ASPIRE_SUPPRESS_CLI_RUN_HOOK`. This keeps the CLI bundle's DCP/dashboard assets but bypasses its launch wrapper. In the verified environment, the wrapper delegated a Release `--no-build` invocation to a missing Debug output and attempted certificate trust setup. The direct command uses the selected Release output and performs no CLI certificate-trust setup; it does not disable TLS verification or alter the project's analyzer configuration.

On .NET 11.0.0-rc.3.26522.148, the direct command started the distributed application, served the dashboard with HTTP 200 and shut down with exit 0 on Ctrl+C. That observation establishes AppHost startup under the pinned SDK/runtime. It does not establish completed emulator initialization, broker readiness, real GitHub authentication or the broader local-adapter acceptance tracked in #204. Existing local certificate prerequisites below still apply; no trust-store changes were made for this direct-start check.


On WSL the trust command can report partial success when Windows PowerShell or browser certificate tools are unavailable. The OpenSSL trust directory above is used by the native .NET services. Browser trust is configured separately if needed; do not disable TLS validation.

Open the dashboard URL printed by Aspire. The API is `https://localhost:7443`, with `/health/live` and `/health/ready`. Cleanup runs the same application's `cleanup --watch` Development-only mode once per minute; Azure retains the scheduled `cleanup --once` Job. Azurite uses ports 27000–27002; Key Vault uses 4997 so persisted key URIs stay stable. The dashboard uses 15888 and its local resource/telemetry endpoints use 18889–18890.

The `initialize` process creates a dedicated **local** `brokerstate` Table, seeds its two Int64 quota rows only when creation succeeds, creates the key-ring Blob container, and creates or reuses the local wrapping key (and a generated signing key in the default offline mode). An existing Table with missing metadata fails initialization. It never resets accounting. API and cleanup wait for successful initialization. Local state persists between AppHost runs. Certificates, the emulator database and generated settings stay in the ignored `.local` directory; Azurite data uses an Aspire-owned volume. Stop the AppHost before deliberately deleting local state, and reset both stores together if you need a fresh development environment. Hosted state is independent.

Broker-only isolated runs can reuse persisted emulator keys after Aspire changes the published loopback port. Initialization preserves each local key's name and version while binding its URI to the current local vault endpoint. Shared signing key URIs remain exactly as configured. Configured controller enrollments still require their stable saved broker origin.

## Signing and real GitHub access

The default generated signing key supports local cryptographic/storage debugging. It cannot authenticate as the published GitHub App, so it does not establish real enrollment or token issuance. The application's App ID and permission profiles remain fixed.

For the real GitHub path, use an existing Azure CLI sign-in with **Key Vault Crypto User** access to the shared App signing key. The App must already be installed on the selected repository. Set the pinned key URI in the AppHost's user secrets, then start the same AppHost:

```sh
# Run from service/auth. Sign in as the authorized developer if needed.
az login
dotnet user-secrets set SharedSigningKeyUri \
  "https://VAULT.vault.azure.net/keys/APP_SIGNING_KEY/KEY_VERSION" \
  --project dev/RepoAgent.Auth.AppHost
dotnet run --project dev/RepoAgent.Auth.AppHost
```

`SharedSigningKeyUri` names a versioned HTTPS key on the shared vault's DNS origin at port 443, without credentials, a query or a fragment. The initializer writes this nonsecret URI and the explicit `Auth:DevelopmentSigningCredential=AzureCli` choice into the ignored local broker settings. It only creates or reuses keys in the local emulator; it never reads, creates, imports, exports or changes the shared key. The broker signs through Key Vault using `AzureCliCredential`, with a three-second CLI process timeout and normal challenge-resource verification. There is no credential-chain fallback. The emulator's credential remains separate and only its local crypto client relaxes challenge-resource verification; TLS certificate validation stays enabled for both.

The AppHost user-secrets ID is `repoagent-auth-apphost`. Importing an App PEM is no longer supported. Remove a previous `SigningKeyPemPath` setting with `dotnet user-secrets remove SigningKeyPemPath --project dev/RepoAgent.Auth.AppHost`; the AppHost refuses to start while it is set. No private App key is needed on disk. Remove `SharedSigningKeyUri` to return to generated-key debugging. Keep controller enrollment and Codex supplies in private storage, and never put their contents in source, arguments, dashboard captures or logs.

For a real local-broker run, follow [controller enrollment](../../docs/controller-enrollment.md) using `-BrokerOrigin https://localhost:7443` and a fresh private state directory. Use a disposable `repoagent/e2e-<run>` repository with Discussions enabled and the App installed; delete that repository and settle its local enrollment when finished. A readiness 200 checks composition only. Observe successful enrollment and token renewal before claiming shared signing works. Keep the same persisted local stores and wrapping key when restarting an enrolled controller.

The same AppHost can also start one configured **native controller on Linux or macOS**, using the local broker and a real repository. Follow [the shared controller journey](../../docs/local-apphost.md) to enroll it, supply private configuration and run the opt-in AppHost tests. Without `Controller:Repository`, the existing broker-only mode remains available on Windows too. Shared signing, a controller reply, credential rotation and rendered dashboard behavior each require their own observed acceptance; compilation or broker readiness does not establish them.

An installed Docker controller needs the same saved HTTPS broker origin to be reachable and trusted inside the container; its `localhost` is not the developer machine. The native shared AppHost path does not establish that installed container path. Never rewrite an enrollment's origin or disable certificate validation to bridge it. The complete installer/container journey remains with [#207](https://github.com/repoagent/controller/issues/207).

Controller developers can instead use the hosted broker, `https://repoagent-auth.livelydune-309701b4.swedencentral.azurecontainerapps.io`, without receiving an App key. Controller enrollment runs natively through `scripts/enroll-controller.ps1`; an enrolled controller runs directly with `dotnet run --project src/RepoAgentController -- ... --enrollment-file <private-file> --check-auth` from the repository root. `--check-auth` checks broker/GitHub access without starting Codex. An expired or unacknowledged enrollment is not usable; owner handover remains tracked in #34.

## Debug the API outside the AppHost

Keep the emulators running and stop the `auth` resource in the dashboard to free port 7443. In a terminal or C# launch profile, set `ASPNETCORE_ENVIRONMENT=Development` and `Broker__DevelopmentSettingsFile` to the absolute path of `dev/RepoAgent.Auth.AppHost/.local/broker.json`. Set the Azure emulator credential environment values shown in the `auth` resource's configuration, including its loopback authority, then run/debug `src/RepoAgent.Auth`. These are emulator credentials, not your Azure login.

Production rejects local settings files, development storage/vault options and `Auth:DevelopmentSigningCredential`, even when no signing key is configured. Production continues to use managed identity for both keys and storage. Development storage must match its configured loopback endpoints; the wrapping key and emulator credential authority must use the same loopback HTTPS vault. Without the explicit `AzureCli` signing choice, signing must use that local vault too. Shared signing requires the local wrapping vault and local Table/Blob storage; it cannot opt into hosted state. `dev` is excluded from the auth Docker build context.

## Verification

`bash verify.sh` compiles the AppHost and runs the service suite, which needs neither Docker nor Azurite. Start the AppHost to verify the real local composition and inspect its resources in the dashboard. Aspire 13.6.2 and the Key Vault emulator integration 3.1.5 are pinned from current stable releases; [Aspire's storage documentation](https://aspire.dev/integrations/cloud/azure/azure-storage-tables/azure-storage-tables-host/) describes the emulator integration.

Azure infrastructure remains Bicep: compile locally, deploy against the actual resource group, and inspect Azure. This AppHost does not publish or emulate ARM resources.
