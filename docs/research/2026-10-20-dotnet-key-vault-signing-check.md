# .NET JWT + Azure Key Vault RSA composition: verified locally

Date: 2026-10-20. Result: **PASS**. The real Azure Key Vault RSA adapter works with Microsoft's JWT library on .NET 10 without a custom signature provider, custom JWT encoder, PEM export, or handwritten signer adapter. This is an offline library-composition check; it is not Azure or GitHub acceptance.

## Exact versions and primary package evidence

- SDK: **10.0.402**, pinned by scratch `global.json` with roll-forward disabled.
- Runtime: **Microsoft.NETCore.App 10.0.13**; target framework `net10.0`.
- `Azure.Security.KeyVault.Keys` **4.10.2**.
- `Microsoft.IdentityModel.JsonWebTokens` **8.23.0**.
- Resolved transitives include `Azure.Core` **1.54.0** and `Microsoft.IdentityModel.Tokens` **8.23.0**.

Versions were selected as the latest non-prerelease entries returned directly by NuGet's [Azure Key Vault Keys flat-container feed](https://api.nuget.org/v3-flatcontainer/azure.security.keyvault.keys/index.json) and [Microsoft JWT flat-container feed](https://api.nuget.org/v3-flatcontainer/microsoft.identitymodel.jsonwebtokens/index.json). Exact feed snapshots, explicit package references and generated dependency lock file are retained in `/tmp/repoagent-auth-signing-check/`. Product/package pages: [Azure.Security.KeyVault.Keys 4.10.2](https://www.nuget.org/packages/Azure.Security.KeyVault.Keys/4.10.2), [Microsoft.IdentityModel.JsonWebTokens 8.23.0](https://www.nuget.org/packages/Microsoft.IdentityModel.JsonWebTokens/8.23.0).

The .NET 10 SDK/runtime were already installed. No SDK or system software was installed. Initial restricted-network restore failed NU1301; authorized escalation allowed NuGet restore. Execution itself made no network calls because the complete Key Vault transport is supplied locally.

## Composition exercised

```csharp
using RSA remoteRsa = await cryptographyClient.CreateRSAAsync(cancellationToken);
var signing = new SigningCredentials(
    new RsaSecurityKey(remoteRsa), SecurityAlgorithms.RsaSha256);
var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
var jwt = handler.CreateToken(new SecurityTokenDescriptor
{
    Issuer = "Iv-fixture-client",
    IssuedAt = now.AddSeconds(-60),
    Expires = now.AddMinutes(9),
    SigningCredentials = signing
});
```

The scratch program uses the actual `CryptographyClient`, `CreateRSAAsync`, `RSAKeyVault`, `RsaSecurityKey`, `SigningCredentials`, `JsonWebTokenHandler.CreateToken` and `ValidateTokenAsync` implementations. There is no custom IdentityModel signature provider. A dummy `TokenCredential` returns a clearly synthetic value; neither `DefaultAzureCredential` nor ambient Azure credentials are used.

Only the outbound Key Vault HTTP transport is replaced via `HttpClientTransport` and a fixture `HttpMessageHandler`. The fixture alone owns a freshly generated RSA private key. It returns a public JWK from GET key and uses the platform `RSA.SignHash` for the POST sign operation, after checking RS256 and a 32-byte SHA-256 digest. This models the external service boundary; it is not a production signing implementation. No private key material crosses that boundary. The fixture also performs a synthetic bearer challenge, exercising the dummy credential path.

## Commands and observed results

Working directory: `/tmp/repoagent-auth-signing-check`.

```sh
dotnet --version
dotnet run --project SigningCheck.csproj -c Release
dotnet list SigningCheck.csproj package --include-transitive
dotnet build SigningCheck.csproj -c Release --no-restore
timeout 30s dotnet run --project SigningCheck.csproj -c Release --no-build --no-restore
```

The project treats warnings as errors. Final build: **0 warnings, 0 errors**, exit 0. Final execution: exit 0, completed within the external 30-second watchdog. Logs: `build.log`, `result.log`.

Observed safe output:

```text
PASS runtime=10.0.13 remote-rsa=RSAKeyVault private-export=CryptographicException
PASS RS256 JWTs=2 public-key-reads=1 remote-signs=2 challenges=1 dummy-credential-calls=1
PASS two JWTs validated locally with public RSA; no PEM export or custom JWT/signature provider.
```

Assertions establish that:

- `remoteRsa.ExportParameters(true)` throws `CryptographicException`; public-only export succeeds.
- Both tokens have `alg=RS256`, the exact fixture issuer, issued-at backdated by 60 seconds, and expiry nine minutes ahead.
- Microsoft's validation library accepts each signature using a separate RSA instance imported from public-only parameters, with issuer/signature/algorithm/lifetime validation enabled.
- Reusing the same SDK RSA adapter and signing credentials generates **one public-key read and two remote sign operations** for two tokens. A public-key object is reused; JWT creation still incurs a remote signing operation per token.
- The simulated authentication handshake produced one challenge and one dummy credential call.

No JWT, private key, real credential or actual Azure resource identifier was printed. All code/artifacts are confined to the assigned scratch paths. No repository, branch, index, issue, deployed resource or production implementation was changed.

## Practical verdict and limits

This exact stable package combination removes the need to invent a Key Vault-backed JWT `SignatureProvider` for the proposed service. Use the existing SDK adapter and IdentityModel pipeline.

`CreateRSAAsync` initializes asynchronously, but `JsonWebTokenHandler.CreateToken` and the adapter's signing path are synchronous. The sample configures Azure retry network timeout to five seconds, disables retries and configures HttpClient timeout to ten seconds, plus initialization cancellation and an external process watchdog. It does **not** demonstrate production latency, transport timeout semantics or cancellation of synchronous signing under a real stalled service.

Adapter/public-RSA/HTTP fixture objects are disposed with normal `using` scopes. Repeated-token call counts demonstrate reuse within that scope; comprehensive IdentityModel cache eviction, key-version rotation and cross-scope provider disposal behavior were not tested. Keep a deliberate long-lived client/key lifetime when implementing and validate rotation/disposal behavior then.

Unproven live gates: Azure managed identity, least-privilege get/sign permissions, network/TLS, actual vault policy and key import/version rotation, transient failures, real Key Vault signatures and GitHub acceptance of the resulting App JWT. No cloud access was attempted. This check does not implement or approve the enrollment/broker service.
