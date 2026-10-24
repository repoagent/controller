# Local auth development

The Aspire AppHost runs the HTTP API and cleanup directly as .NET processes, with Azurite for Table/Blob storage and the community [Azure Key Vault Emulator](https://github.com/james-gould/azure-keyvault-emulator) in Docker. You can attach a C# debugger to either .NET process. No Azure subscription or cloud credentials are needed for this stack.

From `service/auth`, with Docker running and the SDK from `global.json` installed:

```sh
dotnet dev-certs https --trust
# Linux/WSL: include the .NET development certificate in OpenSSL's trust paths.
export SSL_CERT_DIR="$HOME/.aspnet/dev-certs/trust:/usr/lib/ssl/certs"
dotnet run --project dev/RepoAgent.Auth.AppHost
```

On WSL the trust command can report partial success when Windows PowerShell or browser certificate tools are unavailable. The OpenSSL trust directory above is used by the native .NET services. Browser trust is configured separately if needed; do not disable TLS validation.

Open the dashboard URL printed by Aspire. The API is `https://localhost:7443`, with `/health/live` and `/health/ready`. Cleanup runs the same application's `cleanup --watch` Development-only mode once per minute; Azure retains the scheduled `cleanup --once` Job. Azurite uses ports 27000–27002; Key Vault uses 4997 so persisted key URIs stay stable. The dashboard uses 15888 and its local resource/telemetry endpoints use 18889–18890.

The `initialize` process creates a dedicated **local** `brokerstate` Table, seeds its two Int64 quota rows only when creation succeeds, creates the key-ring Blob container, and creates or reuses signing/wrapping keys. An existing Table with missing metadata fails initialization. It never resets accounting. API and cleanup wait for successful initialization. Local state persists between AppHost runs. Certificates, the emulator database and generated settings stay in the ignored `.local` directory; Azurite data uses an Aspire-owned volume. Stop the AppHost before deliberately deleting local state, and reset both stores together if you need a fresh development environment. Hosted state is independent.

## Signing and real GitHub access

The default generated signing key supports local cryptographic/storage debugging. It cannot authenticate as the published GitHub App, so it does not establish real enrollment or token issuance. The application's App ID and permission profiles remain fixed.

A maintainer who needs the real GitHub path can explicitly set `SigningKeyPemPath` in the AppHost's user secrets to a private local file holding that App's key. Initialization imports it into the local emulator. Never commit the file or put its contents in command arguments, logs, controller enrollment files or controller images. The AppHost user-secrets ID is `repoagent-auth-apphost`; the default stack does not discover or copy any private key.

Controller developers can instead use the hosted broker, `https://repoagent-auth.livelydune-309701b4.swedencentral.azurecontainerapps.io`, without receiving an App key. Controller enrollment runs natively through `scripts/enroll-controller.ps1`; an enrolled controller runs directly with `dotnet run --project src/RepoAgentController -- ... --enrollment-file <private-file> --check-auth` from the repository root. `--check-auth` checks broker/GitHub access without starting Codex. An expired or unacknowledged enrollment is not usable; owner handover remains tracked in #34.

## Debug the API outside the AppHost

Keep the emulators running and stop the `auth` resource in the dashboard to free port 7443. In a terminal or C# launch profile, set `ASPNETCORE_ENVIRONMENT=Development` and `Broker__DevelopmentSettingsFile` to the absolute path of `dev/RepoAgent.Auth.AppHost/.local/broker.json`. Set the Azure emulator credential environment values shown in the `auth` resource's configuration, including its loopback authority, then run/debug `src/RepoAgent.Auth`. These are emulator credentials, not your Azure login.

Production rejects local settings files and development storage/vault options. Development storage must match the configured loopback endpoint, and development signing/wrapping keys and credential authority must point to the same loopback HTTPS vault. No default Azure credential fallback is added to hosted operation. `dev` is excluded from the auth Docker build context.

## Verification

`bash verify.sh` compiles the AppHost and runs the service suite against isolated pinned Azurite. Start the AppHost to verify the real local composition and inspect its resources in the dashboard. Aspire 13.6.2 and the Key Vault emulator integration 3.1.5 are pinned from current stable releases; [Aspire's storage documentation](https://aspire.dev/integrations/cloud/azure/azure-storage-tables/azure-storage-tables-host/) describes the emulator integration.

Azure infrastructure remains Bicep: compile locally, deploy against the actual resource group, and inspect Azure. This AppHost does not publish or emulate ARM resources.
