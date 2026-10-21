using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class CleanupProcessTests(ITestOutputHelper output)
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
}
