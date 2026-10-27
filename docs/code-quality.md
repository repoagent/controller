# Development and code quality

All 11 projects target `net11.0` and use SDK `11.0.100-rc.3.26522.148`. Root and broker `global.json` are identical, allow prereleases and disable roll-forward. This is a release candidate, not a stable SDK. Microsoft's [release metadata](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/11.0/releases.json) identifies RC3 (October 25, 2026) as a go-live security release. The SDK includes runtime and ASP.NET Core `11.0.0-rc.3.26522.148`. Updating the SDK requires reviewing runtime/container pins together and rerunning the gates below; separate broker runtime deployment still needs operator authorization.

## Shared policy

`Directory.Build.props` enables nullable, SDK analyzers, `AnalysisLevel=11-recommended`, build code style and warnings-as-errors for every project. No independent `AnalysisMode` or NetAnalyzers package overrides the SDK. `.editorconfig` adds cancellation, disposal, ValueTask, stream, rethrow, ordinal-comparison, cryptography/TLS and logging rules. Both cores retain their own banned symbols and analyzer 4.14.0; RS0030 is an error. Framework ASP.NET analyzers remain enabled in both hosts. The controller, controller core and NativeSmoke run trim/AOT/single-file checks on ordinary builds; broker, Aspire and tests do not claim AOT compatibility.

The three xUnit projects retain `xunit.v3.mtp-v2` 4.0.2, which resolves `xunit.analyzers` 2.2.0 and Microsoft.Testing.Platform 2.5.1. Aspire AppHost/SDK and Azure Storage hosting stay at 13.6.2. Package-supplied analyzers remain active. Unused private members (IDE0051) and assignments (IDE0059) are build warnings. Whitespace uses four-space indentation, UTF-8, LF and a final newline; imports put System first. `dotnet format` verifies the complete explicit solution.

**IDE0079 limitation:** it is configured as a warning for editors. Microsoft [documents that it does not run during builds](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/style-rules/ide0079). A bounded RC3 probe with an unnecessary CS0168 pragma produced no diagnostic in either `dotnet build` or `dotnet format analyzers --diagnostics IDE0079 --verify-no-changes`. Consequently, CLI enforcement of unnecessary suppressions is unavailable with this toolchain; review must check them. A successful formatter invocation does not establish IDE0079 enforcement.

SDK/framework generators and generated files retain their supplied generated-code treatment. Do not label handwritten files as generated to avoid analysis. Native worktrees and review artifacts are excluded from project globs; an ancestor checkout's `.globalconfig` is removed before compilation while SDK/package configs are retained. Root `.editorconfig` stops ordinary editor-config inheritance.

## Verification

Install the exact SDK from `global.json`, then run from the repository root:

```sh
bash scripts/delivery/verify-dotnet.sh
bash scripts/delivery/verify.sh /tmp/repoagent-packages
bash service/auth/verify.sh
```

The shared gate checks equal SDK policies and solution coverage of every C# project under src, tests and service/auth (also in Git archives), builds all projects in Release with analyzers, and runs `dotnet format RepoAgent.slnx --no-restore --verify-no-changes --severity warn`. Both existing Controller and Broker verification scripts invoke it. Live tests compile without executing credential-dependent scenarios. The controller gate executes its xUnit/process/delivery suites and Linux AOT/native smoke; the broker gate executes actual service tests and rejects zero execution.

Container build inputs matter: the controller's source inventory and explicit `COPY` statements include shared policy before build. The test Containerfile does the same. Broker Docker builds use the repository root with `-f service/auth/Dockerfile`; its Dockerfile-specific allowlist sends only shared policy, SDK selections and broker production sources. Shared policy stays above `service/auth`, including during restore; banned symbols arrive with production sources. The publication and archived-source bootstrap paths use this same context. Neither context includes worktrees, artifacts or development credentials.

Temporary migration probes insert actual-project violations and require CA2016, RS0030 in both cores, xUnit1031 and ASP0024 in both hosts. Each file is removed afterwards. The composed-context delivery test also inserts a cancellation violation and requires CA2016, proving that composition preserves enforcement.

## Separate All-profile audit

An RC3 `AnalysisLevel=11-all` inventory was run with warnings reported rather than relaxed in the committed policy: 913 unique diagnostics, including 546 CA2007, 185 CA1307 and 114 CA1849 findings. The recommended policy remains the acceptance profile. Blanket ConfigureAwait requirements would conflict with xUnit scheduling; synchronous process/file fixture work does not justify a repository-wide async rewrite. Explicit ordinal rules already protect protocol comparisons. The remaining security audit findings were three CA5392 native-library search-path findings and one CA5399 certificate-revocation option finding. They require platform/network compatibility decisions beyond the existing behavior-preserving rollout; no new TLS callback, certificate acceptance or native-library search policy is introduced here. The selected weak-crypto, deprecated-TLS and certificate-callback rules apply now, alongside code review. Zero diagnostics under this policy does not prove credential safety.

## Narrow exceptions

Keep suppressions on the smallest member/type or declaration with an ownership or compatibility reason. Do not add blanket `NoWarn`, category disables or package/framework analyzer switches. New exceptions need review; remove obsolete ones when their reason ends.

- CA1707 applies to internal/private test APIs; public test names intentionally remain underscore-separated executable stories. Production public API naming retains the recommended policy.
- Four broker storage-record files suppress CA1051 only: `RecordMap` passes their fields by reference for typed, lossless storage mapping. Replacing fields with properties would redesign that contract. Golden row tests continue to verify stored names, types and values.
- Existing core exception names retain individual CA1710 compatibility exceptions. Existing positional signatures retain individual CA1068 exceptions where cancellation precedes optional configuration.
- Managed-only SemaphoreSlim fields whose callbacks/waits may unwind after shutdown retain local CA1001/CA2213 exceptions; no native wait handles are created. The synchronous credential handoff likewise cannot dispose its cancellation source while detached handlers still access it. Borrowed Console.Out writers remain owned by the runner.
- CA2000 exceptions identify concrete transfers the analyzer cannot follow: the meter provider owns its reader/exporter, ControllerDashboard owns BaselineReleases, fixture Lifetime owns its retained resources, and the controller task owns its HTTP client/handler. Other missing reader/activity/outbox disposals are fixed explicitly. The broker's HTTP client is DI-created and DI-disposed; webhook resources are disposed only after the HTTP server and workers stop.
- The Linux test `open` P/Invoke explicitly marshals UTF-8, with a local CA2101 explanation; UTF-16 would violate libc's contract. One existing global test class keeps a local CA1050 compatibility exception.

## Cleanup HTTP request envelope

The runtime-specific summary scope is now `Azure.Core1.62.0/.NET11.0.0-rc.3.26522.148/direct HTTP1.x/no handler authentication`. The four-sends-per-dispatch bound was rechecked against exact upstream sources, rather than inferred from targeting `net11.0`:

1. Azure.Core 1.62.0 [BuildRequestMessage](https://github.com/Azure/azure-sdk-for-net/blob/Azure.Core_1.62.0/sdk/core/Azure.Core/src/Pipeline/HttpClientTransport.Request.cs#L109) creates an HttpRequestMessage without overriding its version/policy. Its [transport](https://github.com/Azure/azure-sdk-for-net/blob/Azure.Core_1.62.0/sdk/core/Azure.Core/src/Pipeline/HttpClientTransport.cs#L243) passes that message to HttpClient.SendAsync.
2. RC3 [HttpRequestMessage](https://github.com/dotnet/runtime/blob/v11.0.0-rc.3.26522.148/src/libraries/System.Net.Http/src/System/Net/Http/HttpRequestMessage.cs#L14) defaults to HTTP/1.1 and RequestVersionOrLower.
3. RC3 [HttpConnectionPool](https://github.com/dotnet/runtime/blob/v11.0.0-rc.3.26522.148/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/ConnectionPool/HttpConnectionPool.cs#L28) limits connection-failure retries to three, for one initial send plus three retries. The [send loop](https://github.com/dotnet/runtime/blob/v11.0.0-rc.3.26522.148/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/ConnectionPool/HttpConnectionPool.cs#L405) selects HTTP/1.x for these defaults. Its separate HTTP/2 downgrade/session-auth paths do not apply.
4. CleanupRequestBudget still disables proxies, redirects, cookies and handler credentials. Azure bearer-auth reissues cross the counted SDK-dispatch boundary. Thus the existing 128-dispatch/32-batch limits retain conservative bounds of 512 Table sends/128 batch sends in this exact reviewed scope.

This is a source-based upper bound, not measured wire traffic, a bound on DNS/TCP/TLS work, or proof of a deployed runtime. Hosted image/runtime acceptance remains separate. Package, runtime or transport changes require revalidation.
