using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

// TEMPORARY (#205 PR 7): the Azurite reclamation cases' shared helpers, moved here unchanged so each file's cases can
// move to the in-memory core on their own. Deleted with the last Azurite case.
public sealed partial class AzuriteReclamationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // From CapacityReclamationConformanceTests.cs.
    private static object Request(string id) => new { idempotencyId = id, repositoryId = 123456, repository = "owner/project", variable = "APPROVAL", profile = "polling" };
    private sealed record ActiveJourney(string Id, string Device, string Secret, string Completion);
    private static async Task<ActiveJourney> Active(EnrollmentFixture f)
    {
        var start = await Start(f);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var completion = await f.Send($"/v2/enrollments/{start.Pending}/completion");
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var receipt = await completion.Content.ReadFromJsonAsync<JsonElement>(Token);
        var envelope = receipt.GetProperty("enrollment");
        var j = new ActiveJourney(start.Id, envelope.GetProperty("deviceId").GetString()!, envelope.GetProperty("deviceSecret").GetString()!, receipt.GetProperty("completionId").GetString()!);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var ack = await f.Send($"/v2/devices/{j.Device}/acknowledgment", new { completionId = j.Completion }, j.Secret);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        return j;
    }
    private static async Task<(string Id, string Pending)> Start(EnrollmentFixture f)
    {
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        var id = Guid.NewGuid().ToString("N");
        using var response = await f.Send("/v2/enrollments", Request(id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        f.GitHub.Marker = json.GetProperty("marker").GetString()!;
        return (id, json.GetProperty("pendingId").GetString()!);
    }
    private static BrokerOptions Options(EnrollmentFixture f) => new(new Uri("https://example.table.core.windows.net"), f.Storage.TableName, RevocationFixture.Epoch, null, 120, 8, 15);
    private static CapacityReclamation Cleanup(EnrollmentFixture f, TableClient? table = null) => new(new TableBrokerState(table ?? f.Storage.CreateClient()), Options(f).DeploymentEpoch, f.Clock);
    private static Task Reclaim(EnrollmentFixture f, TableEntity ledger, TableClient? table = null) => Cleanup(f, table).ReclaimIssuanceAsync(ledger.GetString("OperationId")!, ledger.GetString("Generation")!, Token);
    private static async Task<TableEntity> Read(EnrollmentFixture f, string partition, string key) => (await f.Storage.CreateClient().GetEntityAsync<TableEntity>(partition, key, cancellationToken: Token)).Value;
    private static async Task<List<TableEntity>> Rows(EnrollmentFixture f)
    {
        var rows = new List<TableEntity>();
        await foreach (var row in f.Storage.CreateClient().QueryAsync<TableEntity>(cancellationToken: Token)) rows.Add(row);
        return rows;
    }
    private static async Task<List<TableEntity>> IssuanceLedgers(EnrollmentFixture f) => (await Rows(f)).Where(x => x.PartitionKey == "global:issuance-v1" && x.RowKey.StartsWith("operation:", StringComparison.Ordinal)).ToList();
    private sealed class CapacityPause
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ObservedTransport Transport { get; }
        public CapacityPause(Func<string, bool> matches)
        {
            var paused = 0;
            Transport = new() { Before = async message =>
            {
                if (!ObservedTransport.IsBatch(message) || message.Request.Content is null) return;
                using var stream = new MemoryStream();
                await message.Request.Content.WriteToAsync(stream, Token);
                if (!matches(Encoding.UTF8.GetString(stream.ToArray())) || Interlocked.CompareExchange(ref paused, 1, 0) != 0) return;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(12), Token);
            }};
        }
    }

    // From ExpiredEnrollmentRecoveryTests.cs.
    private static async Task<(string Id, string Device, string Secret, string Completion)> Unacknowledged(EnrollmentFixture f)
    {
        var start = await Start(f);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var response = await f.Send($"/v2/enrollments/{start.Pending}/completion");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        var enrollment = receipt.GetProperty("enrollment");
        return (start.Id, enrollment.GetProperty("deviceId").GetString()!, enrollment.GetProperty("deviceSecret").GetString()!,
            receipt.GetProperty("completionId").GetString()!);
    }

    private static async Task SettleVerifiers(EnrollmentFixture f)
    {
        var ledgers = await IssuanceLedgers(f);
        var expiry = DateTimeOffset.MinValue;
        foreach (var ledger in ledgers)
        {
            var attempt = await Read(f, RevocationFixture.Partition, ledger.GetString("AttemptRowKey")!);
            expiry = new[] { expiry, attempt.GetDateTimeOffset("ExpiresAt")!.Value.AddMinutes(5) }.Max();
        }
        if (expiry > f.Clock.GetUtcNow()) f.Clock.Advance(expiry - f.Clock.GetUtcNow());
        foreach (var ledger in ledgers) await Reclaim(f, ledger);
    }

    private static async Task ReclaimPending(EnrollmentFixture f, string id, TableClient? table = null)
    {
        var ledger = await Read(f, "global:enrollment-v1", "pending:" + id);
        await Cleanup(f, table).ReclaimEnrollmentAsync(id, ledger.GetString("Generation")!, Token);
    }

    // From RetainedRetirementConformanceTests.cs.
    private static string RetainedCounter(bool enrollment) => enrollment ? "RetainedEnrollmentCount" : "RetainedIssuanceCount";
    private static string Target(TableEntity ledger, bool enrollment) => enrollment ? ledger.RowKey : ledger.GetString("AttemptRowKey")!;
    private static string Release(TableEntity ledger, bool enrollment) => "release:" + (enrollment ? ledger.RowKey : ledger.GetString("OperationId"));
    private static Task Retirement(EnrollmentFixture f, TableEntity ledger, TableClient? client = null) => ledger.PartitionKey == "global:enrollment-v1"
        ? Cleanup(f, client).ReclaimEnrollmentAsync(ledger.RowKey[8..], ledger.GetString("Generation")!, Token) : Reclaim(f, ledger, client);
    private static async Task RetirementGone(EnrollmentFixture f, TableEntity ledger, bool enrollment)
    {
        await AssertAbsent(f, ledger.PartitionKey, ledger.RowKey);
        await AssertAbsent(f, RevocationFixture.Partition, Target(ledger, enrollment));
        await AssertAbsent(f, RevocationFixture.Partition, Release(ledger, enrollment));
        Assert.Equal(0L, (await Read(f, ledger.PartitionKey, "quota")).GetInt64(RetainedCounter(enrollment)));
    }
    private static async Task<string> Body(HttpMessage message)
    {
        using var stream = new MemoryStream();
        if (message.Request.Content is not null) await message.Request.Content.WriteToAsync(stream, Token);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
    private static async Task<TableEntity> UnpublishedEnrollment(EnrollmentFixture f, string? id = null)
    {
        var interrupted = false;
        await f.RestartAsync(new ObservedTransport { After = message =>
        {
            if (ObservedTransport.IsBatch(message) && !interrupted) { interrupted = true; throw new HttpRequestException("Lost new allocation response"); }
            return Task.CompletedTask;
        }});
        id ??= Guid.NewGuid().ToString("N");
        using var response = await f.Send("/v2/enrollments", Request(id));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(interrupted);
        await f.RestartAsync();
        return await Read(f, "global:enrollment-v1", "pending:" + id);
    }
    private static async Task<TableEntity> RetirementEligible(EnrollmentFixture f, bool enrollment, bool lateSettlement = false)
    {
        TableEntity ledger;
        DateTimeOffset resolved;
        if (enrollment)
        {
            ledger = await UnpublishedEnrollment(f);
            Assert.Equal(1L, ledger.GetInt64("ReclamationVersion"));
            resolved = ledger.GetDateTimeOffset("Deadline")!.Value;
        }
        else
        {
            _ = await Start(f);
            ledger = Assert.Single(await IssuanceLedgers(f));
            resolved = (await Read(f, RevocationFixture.Partition, Target(ledger, false))).GetDateTimeOffset("ExpiresAt")!.Value.AddMinutes(5);
        }
        f.Clock.Advance(resolved.AddDays(lateSettlement ? 31 : 0) - f.Clock.GetUtcNow());
        await Retirement(f, ledger);
        if (!lateSettlement) f.Clock.Advance(TimeSpan.FromDays(30));
        return ledger;
    }

    // From RetainedRetirementTests.cs.
    private static async Task AssertAbsent(EnrollmentFixture f, string partition, string rowKey) =>
        Assert.False((await f.Storage.CreateClient().GetEntityIfExistsAsync<TableEntity>(partition, rowKey,
            cancellationToken: Token)).HasValue, $"Retirement must physically delete {partition}/{rowKey}");

    // From RevokedOwnerRecoveryTests.cs.
    private static async Task<ActiveJourney> AcknowledgedOwner(EnrollmentFixture f, string profile = "polling")
    {
        CloneEnrollmentContractTests.ConfigureGitHub(f, profile);
        var id = Guid.NewGuid().ToString("N");
        using var start = await f.Send("/v2/enrollments", new { idempotencyId = id, repositoryId = 123456,
            repository = "owner/project", variable = "APPROVAL", profile });
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var status = await start.Content.ReadFromJsonAsync<JsonElement>(Token);
        f.GitHub.Marker = status.GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var complete = await f.Send($"/v2/enrollments/{status.GetProperty("pendingId").GetString()}/completion");
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var receipt = await complete.Content.ReadFromJsonAsync<JsonElement>(Token);
        var device = receipt.GetProperty("enrollment");
        var owner = new ActiveJourney(id, device.GetProperty("deviceId").GetString()!, device.GetProperty("deviceSecret").GetString()!,
            receipt.GetProperty("completionId").GetString()!);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var ack = await f.Send($"/v2/devices/{owner.Device}/acknowledgment", new { completionId = owner.Completion }, owner.Secret);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var runtime = await f.Send($"/v{(profile == "polling-clone" ? 2 : 1)}/devices/{owner.Device}/installation-token", secret: owner.Secret);
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        return owner;
    }

    private static async Task SettleOwnerCapabilities(EnrollmentFixture f)
    {
        f.Clock.Advance(TimeSpan.FromMinutes(66));
        foreach (var ledger in await IssuanceLedgers(f)) await Reclaim(f, ledger);
    }

    // From UnsentEnrollmentRecoveryTests.cs.
    private static async Task<(string Id, object Request, TableEntity Ledger)> FencedEnrollment(EnrollmentFixture f, string profile = "polling")
    {
        CloneEnrollmentContractTests.ConfigureGitHub(f, profile);
        var id = Guid.NewGuid().ToString("N");
        var request = new { idempotencyId = id, repositoryId = 123456,
            repository = "owner/project", variable = "APPROVAL", profile };
        var pause = new CapacityPause(body => body.Contains("\"State\":\"sending\"", StringComparison.Ordinal));
        await f.RestartAsync(pause.Transport);
        var writer = f.Send("/v2/enrollments", request);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), Token);
            var ledger = Assert.Single(await IssuanceLedgers(f));
            await Reclaim(f, ledger);
            pause.Release.TrySetResult();
            using var interrupted = await writer;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, interrupted.StatusCode);
            Assert.Equal(0, f.GitHub.Mints);
            return (id, request, ledger);
        }
        finally { pause.Release.TrySetResult(); using var response = await writer; }
    }
}
