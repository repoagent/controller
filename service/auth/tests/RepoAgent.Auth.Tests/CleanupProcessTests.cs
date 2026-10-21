using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed partial class CleanupProcessTests(ITestOutputHelper output)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Independent_cleanup_removes_due_enrollment_payload_without_HTTP_or_keys()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        // Real admission writes original clocks already overdue for the child wall clock.
        // No persisted purge clock is rewritten and no cleanup facade runs during setup.
        f.Clock.Advance(TimeSpan.FromDays(-2));
        var id = Guid.NewGuid().ToString("N");
        using var response = await f.Send("/v2/enrollments", new
        {
            idempotencyId = id, repositoryId = 123456, repository = "owner/project",
            variable = "APPROVAL", profile = "polling"
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var key = "pending:" + id;
        var before = (await f.Storage.Table.GetEntityAsync<TableEntity>("global:enrollment-v1", key, cancellationToken: Token)).Value;
        var pendingBefore = (await f.Storage.Table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, key, cancellationToken: Token)).Value;
        Assert.True(before.ContainsKey("RecoveryHash"));
        Assert.True(pendingBefore.ContainsKey("RecoveryHash"));
        Assert.True(before.GetDateTimeOffset("PayloadPurgeAt") < DateTimeOffset.UtcNow);
        await f.StopHttpAsync();

        var home = Directory.CreateTempSubdirectory("cleanup-process-");
        try
        {
            // A web host attempting to listen here would fail. The setup host is stopped.
            using var occupied = new TcpListener(IPAddress.Loopback, 0);
            occupied.Start();
            var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
            var endpoint = f.Storage.Table.Uri.AbsoluteUri[..^(f.Storage.TableName.Length + 1)];
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = home.FullName,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
            start.ArgumentList.Add("cleanup");
            start.ArgumentList.Add("--once");
            // No inherited signing, App, provider, identity or Data Protection inputs.
            start.Environment.Clear();
            start.Environment["HOME"] = home.FullName;
            start.Environment["DOTNET_ENVIRONMENT"] = "Development";
            start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
            start.Environment["Broker__TableEndpoint"] = endpoint;
            start.Environment["Broker__TableName"] = f.Storage.TableName;
            start.Environment["Broker__DeploymentEpoch"] = RevocationFixture.Epoch;
            start.Environment["Broker__Cleanup__DevelopmentConnectionString"] =
                "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
                "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
                "TableEndpoint=" + endpoint;
            using var child = Process.Start(start)!;
            var stdout = child.StandardOutput.ReadToEndAsync(Token);
            var stderr = child.StandardError.ReadToEndAsync(Token);
            try
            {
                await child.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(75), Token);
            }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
            }
            Assert.True((await stderr).Length == 0, "Cleanup must not emit raw errors or host logs.");
            using var summary = JsonDocument.Parse(await stdout);
            Assert.Equal(0, summary.RootElement.GetProperty("failures").GetInt32());
            Assert.Equal(0, child.ExitCode);
            Assert.Empty(Directory.EnumerateFileSystemEntries(home.FullName, "*", SearchOption.AllDirectories));
            output.WriteLine("Independent DLL exited {0}; summary processed={1}, failures={2}, incomplete={3}. No HTTP bind or HOME/DP files.",
                child.ExitCode, summary.RootElement.GetProperty("processedAllocations").GetInt32(),
                summary.RootElement.GetProperty("failures").GetInt32(), summary.RootElement.GetProperty("incompleteCoverage").GetBoolean());

            var fresh = f.Storage.CreateClient();
            var ledger = (await fresh.GetEntityAsync<TableEntity>(before.PartitionKey, key, cancellationToken: Token)).Value;
            var pending = (await fresh.GetEntityAsync<TableEntity>(pendingBefore.PartitionKey, key, cancellationToken: Token)).Value;
            Assert.Equal(before.GetDateTimeOffset("Deadline"), ledger.GetDateTimeOffset("Deadline"));
            Assert.Equal(before.GetDateTimeOffset("PayloadPurgeAt"), ledger.GetDateTimeOffset("PayloadPurgeAt"));
            output.WriteLine("Fresh persisted overdue RecoveryHash presence: global={0}, repository={1}.",
                ledger.ContainsKey("RecoveryHash"), pending.ContainsKey("RecoveryHash"));
            Assert.Multiple(
                () => Assert.False(ledger.ContainsKey("RecoveryHash"), "Independent cleanup must physically remove the overdue global recovery hash."),
                () => Assert.False(pending.ContainsKey("RecoveryHash"), "Independent cleanup must physically remove the overdue repository recovery hash."));
        }
        finally
        {
            home.Delete(recursive: true);
        }
    }
    [Fact]
    public async Task Bodyless_Table_GET_replay_preserves_SDK_counts_and_fits_the_reviewed_send_envelope()
    {
        await using var storage = new RevocationFixture();
        await storage.InitializeAsync();
        await using var proxy = new CleanupTableProxy(storage);
        await using var eof = new CleanupGetEofShim(proxy.Endpoint);
        // Qualified connectivity evidence only: loopback may bypass proxies even
        // with a default handler. Source review establishes the direct-only policy.
        var result = await CleanupProcessFixture.RunAsync(storage, eof.Endpoint, environment: new Dictionary<string, string>
        {
            ["HTTP_PROXY"] = "http://127.0.0.1:1", ["HTTPS_PROXY"] = "http://127.0.0.1:1",
            ["ALL_PROXY"] = "http://127.0.0.1:1", ["http_proxy"] = "http://127.0.0.1:1",
            ["https_proxy"] = "http://127.0.0.1:1", ["all_proxy"] = "http://127.0.0.1:1",
            ["NO_PROXY"] = "", ["no_proxy"] = ""
        });
        var dropped = eof.Dropped;
        var dispatches = result.Summary.GetProperty("sdkTableDispatches").GetInt32();
        var repeated = proxy.Requests.Where(r => r.Method == "GET" && r.ClientRequestId is not null)
            .GroupBy(r => r.ClientRequestId).Count(g => g.Count() > 1);
        output.WriteLine("Forwarded bodyless GET response dropped: {0}; incoming sends={1}; SDK dispatches={2}; repeated SDK request identities={3}; exit={4}.",
            dropped, proxy.Requests.Count, dispatches, repeated, result.Exit);
        Assert.True(dropped);
        Assert.Contains(".NET 10.0.13", proxy.Requests[0].UserAgent ?? "");
        Assert.True(repeated > 0, "The genuine bodyless-GET replay must be observed before evaluating the old count claim.");
        Assert.True(proxy.Requests.Count > dispatches);
        // Explicit approved schema/oracle migration:47f preserves the authentic old
        // equality failure. We retain replay, and now assert an honest scoped bound.
        Assert.Equal(0, result.Exit);
        AssertSdkAccounting(result.Summary);
        Assert.InRange(proxy.Requests.Count, dispatches + 1,
            result.Summary.GetProperty("reviewedHttp11Envelope").GetProperty("tableRequestUpperBound").GetInt32());
    }

    [Fact]
    public async Task Deadline_cancelled_cycle_checkpoint_reports_unknown_selected_lane_ages()
    {
        await using var storage = new RevocationFixture();
        await storage.InitializeAsync();
        Assert.Equal(0, (await CleanupProcessFixture.RunAsync(storage)).Exit);
        var old = DateTimeOffset.UtcNow.AddDays(-2);
        foreach (var key in new[] { "cleanup:payload-v1", "cleanup:audit-v1" })
        {
            var row = await Read(storage, "global:enrollment-v1", key);
            row["NextFamily"] = 0L;
            if (key == "cleanup:payload-v1") row["NextLane"] = 0L;
            foreach (var family in new[] { "Enrollment", "Issuance" })
            {
                row[family + "StartedAt"] = old;
                row[family + "ProgressAt"] = old;
            }
            await storage.Table.UpdateEntityAsync(row, row.ETag, TableUpdateMode.Replace, Token);
        }
        var durableBefore = await Read(storage, "global:enrollment-v1", "cleanup:audit-v1");
        var cycle = durableBefore.GetInt64("IssuanceCycle")!.Value;
        await using var proxy = new CleanupTableProxy(storage);
        var pace = new Stopwatch();
        var held = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        double? checkpointArrivalSeconds = null;
        CleanupTableProxy.Exchange? checkpoint = null;
        proxy.Before = async request =>
        {
            if (!pace.IsRunning) pace.Start();
            if (request.Path.EndsWith("/$batch", StringComparison.Ordinal)
                && request.Body.Contains("cleanup:audit-v1", StringComparison.Ordinal)
                && System.Text.RegularExpressions.Regex.IsMatch(request.Body,
                    "\"IssuanceCycle\"\\s*:\\s*\"?" + (cycle + 1) + "\"?(?=\\s*[,}])"))
            {
                checkpointArrivalSeconds = pace.Elapsed.TotalSeconds;
                checkpoint = request;
                // Hold the actual terminal checkpoint without forwarding it. The child must
                // cancel at its own60s deadline, before its3s individual request timeout.
                return await held.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            }
            // An absolute schedule compensates for prior real response latency rather
            // than accumulating it; each request is still delayed by at most2.3s.
            var delay = proxy.Requests.Count * 2300L - pace.ElapsedMilliseconds;
            if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), Token);
            return true;
        };
        (int Exit, JsonElement Summary) result;
        try { result = await CleanupProcessFixture.RunAsync(storage, proxy.Endpoint); }
        finally { held.TrySetResult(false); }
        var durableAfter = await Read(storage, "global:enrollment-v1", "cleanup:audit-v1");
        output.WriteLine($"Selected audit/issuance cycle checkpoint arrived at {checkpointArrivalSeconds:F3}s; command reason={result.Summary.GetProperty("stopReason").GetString()}, elapsed={result.Summary.GetProperty("elapsedMilliseconds").GetInt64()}ms; durable cycle before={cycle}, after={durableAfter.GetInt64("IssuanceCycle")}.");
        Assert.NotNull(checkpointArrivalSeconds);
        Assert.Same(checkpoint, proxy.Requests.Last());
        Assert.InRange(checkpointArrivalSeconds.Value, 57, 60);
        Assert.Equal(0, result.Exit);
        Assert.Equal("time-budget", result.Summary.GetProperty("stopReason").GetString());
        Assert.Equal(0, result.Summary.GetProperty("failures").GetInt32());
        Assert.Equal(cycle, durableAfter.GetInt64("IssuanceCycle"));
        Assert.Equal(durableBefore.GetDateTimeOffset("IssuanceStartedAt"), durableAfter.GetDateTimeOffset("IssuanceStartedAt"));
        Assert.Equal(durableBefore.GetDateTimeOffset("IssuanceProgressAt"), durableAfter.GetDateTimeOffset("IssuanceProgressAt"));
        var selected = Assert.Single(result.Summary.GetProperty("cursorAndCycleAges").EnumerateArray(),
            row => row.GetProperty("lane").GetString() == "audit" && row.GetProperty("family").GetString() == "issuance");
        output.WriteLine("Unconfirmed selected cursor age={0}; cycle age={1}; durable age remains about2days.",
            selected.GetProperty("cursorAgeSeconds"), selected.GetProperty("cycleAgeSeconds"));
        Assert.Multiple(
            () => Assert.Equal(JsonValueKind.Null, selected.GetProperty("cursorAgeSeconds").ValueKind),
            () => Assert.Equal(JsonValueKind.Null, selected.GetProperty("cycleAgeSeconds").ValueKind));
    }


    [Fact]
    public async Task Optional_monitoring_preserves_one_summary_authority_and_all_exit_codes()
    {
        await using var identity = new TelemetryServer("500", true);
        var telemetry = new Dictionary<string, string>
        {
            ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = TelemetryCapture.ConnectionForCli,
            ["Broker__ManagedIdentityClientId"] = "11111111-1111-1111-1111-111111111111",
            ["IDENTITY_ENDPOINT"] = $"http://127.0.0.1:{identity.Port}/token", ["IDENTITY_HEADER"] = "synthetic-cli"
        };
        var schema = new[] { "processedAllocations", "deletedRowsObserved", "payloadFieldsRemovedObserved", "unsettledAllocationsObserved", "failures", "conflicts",
            "incompleteCoverage", "stopReason", "logicalQueryPages", "sdkTableDispatches", "sdkBatchDispatches", "reviewedHttp11Envelope", "elapsedMilliseconds",
            "oldestObservedOverduePayloadAt", "oldestObservedOverduePayloadAgeSeconds", "unknownDeadlineCount", "cursorAndCycleAges" }.Order().ToArray();
        JsonElement? baseline = null;
        foreach (var mode in new[] { "disabled", "malformed", "unavailable", "table-failure" })
        {
            await using var storage = new RevocationFixture(); await storage.InitializeAsync();
            await using var proxy = new CleanupTableProxy(storage);
            if (mode == "table-failure") proxy.Before = _ => Task.FromResult(false);
            var environment = new Dictionary<string, string>(telemetry);
            if (mode == "disabled") environment.Remove("APPLICATIONINSIGHTS_CONNECTION_STRING");
            if (mode == "malformed") environment["APPLICATIONINSIGHTS_CONNECTION_STRING"] = "private-invalid-connection";
            var result = await CleanupProcessFixture.RunAsync(storage, proxy.Endpoint, environment: environment);
            Assert.Equal(mode == "table-failure" ? 1 : 0, result.Exit);
            var properties = result.Summary.EnumerateObject().Select(p => p.Name).Order().ToArray();
            Assert.Equal(schema, properties);
            if (mode != "table-failure")
            {
                baseline ??= result.Summary;
                foreach (var field in new[] { "processedAllocations", "deletedRowsObserved", "payloadFieldsRemovedObserved", "unsettledAllocationsObserved", "failures", "conflicts", "logicalQueryPages", "sdkTableDispatches", "sdkBatchDispatches" })
                    Assert.Equal(baseline.Value.GetProperty(field).GetInt32(), result.Summary.GetProperty(field).GetInt32());
            }
            AssertSdkAccounting(result.Summary);
            Assert.InRange(proxy.Requests.Count, 1, result.Summary.GetProperty("reviewedHttp11Envelope").GetProperty("tableRequestUpperBound").GetInt32());
            Assert.DoesNotContain(proxy.Requests, r => r.Path.Contains("blob", StringComparison.OrdinalIgnoreCase) || r.Path.Contains("keys", StringComparison.OrdinalIgnoreCase));
            output.WriteLine($"{mode}: exit={result.Exit}, Table sends={proxy.Requests.Count}, identity={identity.Count}");
        }
        await using var invalidStorage = new RevocationFixture();
        var before = identity.Count;
        var args = await CleanupProcessFixture.RunRawAsync(invalidStorage, environment: telemetry, arguments: ["cleanup", "--invalid"]);
        Assert.Equal((2, "", "Usage: cleanup --once" + Environment.NewLine), args);
        telemetry["Broker__TableName"] = "!private-invalid-table";
        var config = await CleanupProcessFixture.RunRawAsync(invalidStorage, environment: telemetry);
        Assert.Equal((2, "", "Invalid cleanup configuration." + Environment.NewLine), config);
        Assert.Equal(before, identity.Count);
    }

}
