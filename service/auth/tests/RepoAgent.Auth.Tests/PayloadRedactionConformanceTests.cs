using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed partial class PayloadRedactionTests
{
    [Fact]
    public async Task Enrollment_deadline_is_inclusive_and_repeated_cleanup_cannot_rebase_it()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var started = await StartAgent(f);
        var ledger = await EnrollmentLedger(f, started.Id);
        var due = ledger.GetDateTimeOffset("PayloadPurgeAt")!.Value;
        f.Clock.Advance(due.AddTicks(-1) - f.Clock.GetUtcNow());
        await RedactEnrollment(f, ledger);
        Assert.True((await Read(f, ledger.PartitionKey, ledger.RowKey)).ContainsKey("RecoveryHash"));
        Assert.True((await Read(f, RevocationFixture.Partition, ledger.RowKey)).ContainsKey("RecoveryHash"));
        var before = await Inventory(f);
        f.Clock.Advance(TimeSpan.FromTicks(1));
        await RedactEnrollment(f, ledger);
        var first = await Read(f, RevocationFixture.Partition, ledger.RowKey);
        Assert.False(first.ContainsKey("RecoveryHash"));
        Assert.Equal(ledger.GetDateTimeOffset("Deadline"), first.GetDateTimeOffset("FirstTerminalAt"));
        f.Clock.Advance(TimeSpan.FromDays(31));
        await RedactEnrollment(f, ledger);
        var later = await Read(f, RevocationFixture.Partition, ledger.RowKey);
        Assert.Equal(first.GetDateTimeOffset("FirstTerminalAt"), later.GetDateTimeOffset("FirstTerminalAt"));
        Assert.Equal(due, later.GetDateTimeOffset("PayloadPurgeAt"));
        Assert.False(later.ContainsKey("NextWorkAt"));
        Assert.False((await Read(f, ledger.PartitionKey, ledger.RowKey)).ContainsKey("BodyHash"));
        AssertCharges(before, await Inventory(f));
    }

    [Fact]
    public async Task Active_acknowledgment_survives_audit_compaction_without_activity_extension()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var j = await ActiveAgent(f);
        var ledger = await EnrollmentLedger(f, j.Id);
        var initial = await Read(f, RevocationFixture.Partition, "device:" + j.Device);
        var before = await Inventory(f);
        f.Clock.Advance(TimeSpan.FromDays(31));
        await RedactEnrollment(f, ledger);
        using var duplicate = await f.Send($"/v2/devices/{j.Device}/acknowledgment", new { completionId = j.Completion }, j.Secret);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        var current = await Read(f, initial.PartitionKey, initial.RowKey);
        foreach (var field in new[] { "SecretHash", "Marker", "Challenge", "CompletionId", "PendingId", "LastActivityAt", "ExpiresAt" })
            Assert.Equal(initial[field], current[field]);
        var pending = await Read(f, RevocationFixture.Partition, ledger.RowKey);
        Assert.False(pending.ContainsKey("ProtectedCompletion"));
        Assert.False(pending.ContainsKey("RecoveryHash"));
        Assert.False(pending.ContainsKey("NextWorkAt"));
        Assert.Equal(j.Completion, pending.GetString("CompletionId"));
        AssertCharges(before, await Inventory(f));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoked_or_inactive_secret_uses_original_terminal_clock(bool revoke)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var j = await ActiveAgent(f);
        var ledger = await EnrollmentLedger(f, j.Id);
        var original = await Read(f, RevocationFixture.Partition, "device:" + j.Device);
        DateTimeOffset terminal;
        if (revoke)
        {
            using var response = await f.Send($"/v1/devices/{j.Device}", secret: j.Secret, method: HttpMethod.Delete);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            terminal = (await Read(f, original.PartitionKey, original.RowKey)).GetDateTimeOffset("FirstTerminalAt")!.Value;
            f.Clock.Advance(TimeSpan.FromHours(1));
            using var retry = await f.Send($"/v1/devices/{j.Device}", secret: j.Secret, method: HttpMethod.Delete);
            Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        }
        else terminal = original.GetDateTimeOffset("ExpiresAt")!.Value;
        var before = await Inventory(f);
        f.Clock.Advance(terminal.AddHours(24).AddTicks(-1) - f.Clock.GetUtcNow());
        using var denied = await f.Send($"/v1/devices/{j.Device}/installation-token", secret: j.Secret);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        await RedactEnrollment(f, ledger);
        Assert.True((await Read(f, original.PartitionKey, original.RowKey)).ContainsKey("SecretHash"));
        f.Clock.Advance(TimeSpan.FromTicks(1));
        await RedactEnrollment(f, ledger);
        var redacted = await Read(f, original.PartitionKey, original.RowKey);
        Assert.False(redacted.ContainsKey("SecretHash"));
        Assert.Equal(terminal, redacted.GetDateTimeOffset("FirstTerminalAt"));
        f.Clock.Advance(TimeSpan.FromDays(2));
        await RedactEnrollment(f, ledger);
        Assert.Equal(terminal, (await Read(f, original.PartitionKey, original.RowKey)).GetDateTimeOffset("FirstTerminalAt"));
        AssertCharges(before, await Inventory(f));
    }

    [Theory]
    [InlineData("invalid-expiry")]
    [InlineData("missing-fallback")]
    [InlineData("unknown")]
    public async Task Issuance_fallback_redacts_without_settling_known_or_unknown_authority(string fault)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        var normal = f.GitHub.Before!;
        f.GitHub.Before = request =>
        {
            if (request.Method == HttpMethod.Delete) throw new HttpRequestException("Revocation unavailable");
            if (request.Method == HttpMethod.Post)
            {
                if (fault == "unknown") throw new HttpRequestException("Unknown provider outcome");
                return Task.FromResult<HttpResponseMessage?>(InMemoryGitHub.Json(new { token = "known-invalid-expiry", expires_at = "invalid" }, HttpStatusCode.Created));
            }
            return normal(request);
        };
        var id = Guid.NewGuid().ToString("N");
        using var start = await f.Send("/v2/enrollments", AgentRequest(id));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, start.StatusCode);
        var rows = await Inventory(f);
        var attempt = Assert.Single(rows, r => r.RowKey.StartsWith("attempt:", StringComparison.Ordinal));
        var ledger = Assert.Single(rows, r => r.PartitionKey == "global:issuance-v1" && r.RowKey.StartsWith("operation:", StringComparison.Ordinal));
        Assert.Equal(fault == "unknown" ? "sending" : "known", attempt.GetString("State"));
        if (fault != "unknown")
        {
            Assert.True(attempt.ContainsKey("ProtectedToken"));
            Assert.Equal(DateTimeOffset.MaxValue, attempt.GetDateTimeOffset("ExpiresAt"));
            Assert.False(attempt.GetBoolean("ScopeValid"));
        }
        if (fault == "missing-fallback")
        {
            attempt.Remove("ReservedAt"); attempt.Remove("PayloadPurgeAt");
            await f.Storage.Table.UpdateEntityAsync(attempt, attempt.ETag, TableUpdateMode.Replace, Token);
            f.Clock.Advance(TimeSpan.FromMinutes(1));
            using var corruptRetry = await f.Send("/v2/enrollments", AgentRequest(id));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, corruptRetry.StatusCode);
            Assert.Equal(1, f.GitHub.Mints);
        }
        else
        {
            var due = ledger.GetDateTimeOffset("ReservedAt")!.Value.AddHours(24);
            f.Clock.Advance(due.AddTicks(-1) - f.Clock.GetUtcNow());
            await Cleanup(f).RedactIssuanceAsync(ledger.GetString("OperationId")!, ledger.GetString("Generation")!, Token);
            var notDue = await Read(f, attempt.PartitionKey, attempt.RowKey);
            Assert.Equal(attempt.ContainsKey("ProtectedToken"), notDue.ContainsKey("ProtectedToken"));
            Assert.False(notDue.ContainsKey("PayloadRedacted"));
            f.Clock.Advance(TimeSpan.FromTicks(1));
        }
        var before = await Inventory(f);
        await Cleanup(f).RedactIssuanceAsync(ledger.GetString("OperationId")!, ledger.GetString("Generation")!, Token);
        var after = await Read(f, attempt.PartitionKey, attempt.RowKey);
        Assert.False(after.ContainsKey("ProtectedToken"));
        Assert.True(after.GetBoolean("PayloadRedacted"));
        foreach (var field in attempt.Keys.Except(new[] { "ProtectedToken", "Timestamp", "odata.etag" }))
            Assert.Equal(attempt[field], after[field]);
        Assert.False(after.ContainsKey("SettledAt"));
        AssertCharges(before, await Inventory(f));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var retry = await f.Send("/v2/enrollments", AgentRequest(id));
        Assert.NotEqual(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(1, f.GitHub.Mints);
    }

    [Fact]
    public async Task Due_old_epoch_issuance_is_redacted_behind_current_owner_without_resetting_gate()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var j = await ActiveAgent(f);
        var rows = await Inventory(f);
        var ledger = Assert.Single(rows, r => r.PartitionKey == "global:issuance-v1" && r.RowKey.StartsWith("operation:", StringComparison.Ordinal));
        var attempt = await Read(f, RevocationFixture.Partition, ledger.GetString("AttemptRowKey")!);
        Assert.True(attempt.ContainsKey("ProtectedToken"));
        var gate = await Read(f, RevocationFixture.Partition, "gate");
        Assert.Equal(j.Device, gate.GetString("OwnerDeviceId"));
        gate["Epoch"] = "new-deployment";
        await f.Storage.Table.UpdateEntityAsync(gate, gate.ETag, TableUpdateMode.Replace, Token);
        f.Clock.Advance(ledger.GetDateTimeOffset("PayloadPurgeAt")!.Value - f.Clock.GetUtcNow());
        var before = await Inventory(f);
        await Cleanup(f, "new-deployment").RedactIssuanceAsync(ledger.GetString("OperationId")!, ledger.GetString("Generation")!, Token);
        var redacted = await Read(f, attempt.PartitionKey, attempt.RowKey);
        Assert.False(redacted.ContainsKey("ProtectedToken"));
        Assert.Equal(attempt.GetString("State"), redacted.GetString("State"));
        var current = await Read(f, gate.PartitionKey, gate.RowKey);
        foreach (var field in new[] { "Epoch", "OwnerDeviceId", "State", "OutstandingTokenCount", "PendingCount" })
            Assert.Equal(gate[field], current[field]);
        AssertCharges(before, await Inventory(f));
    }

    private static object AgentRequest(string id) => new { idempotencyId = id, repositoryId = 123456, repository = "owner/project", variable = "APPROVAL", profile = "polling" };
    private static async Task<(string Id, string Pending)> StartAgent(EnrollmentFixture f)
    {
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        var id = Guid.NewGuid().ToString("N");
        using var response = await f.Send("/v2/enrollments", AgentRequest(id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        f.GitHub.Marker = json.GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        return (id, json.GetProperty("pendingId").GetString()!);
    }
    private sealed record Journey(string Id, string Pending, string Device, string Secret, string Completion);
    private static async Task<Journey> ActiveAgent(EnrollmentFixture f)
    {
        var start = await StartAgent(f);
        using var response = await f.Send($"/v2/enrollments/{start.Pending}/completion");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var receipt = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        var envelope = receipt.GetProperty("enrollment");
        var j = new Journey(start.Id, start.Pending, envelope.GetProperty("deviceId").GetString()!, envelope.GetProperty("deviceSecret").GetString()!, receipt.GetProperty("completionId").GetString()!);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var ack = await f.Send($"/v2/devices/{j.Device}/acknowledgment", new { completionId = j.Completion }, j.Secret);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        return j;
    }
    private static BrokerPayloadRedaction Cleanup(EnrollmentFixture f, string epoch = RevocationFixture.Epoch, TableClient? table = null) =>
        new(table ?? f.Storage.CreateClient(), new(new Uri("https://example.table.core.windows.net"), f.Storage.TableName, epoch, null, 120, 8, 15), f.Clock);
    private static Task<TableEntity> EnrollmentLedger(EnrollmentFixture f, string id) => Read(f, "global:enrollment-v1", "pending:" + id);
    private static Task RedactEnrollment(EnrollmentFixture f, TableEntity ledger, string epoch = RevocationFixture.Epoch) =>
        Cleanup(f, epoch).RedactEnrollmentAsync(ledger.RowKey["pending:".Length..], ledger.GetString("Generation")!, Token);
    private static async Task<TableEntity> Read(EnrollmentFixture f, string partition, string key) =>
        (await f.Storage.CreateClient().GetEntityAsync<TableEntity>(partition, key, cancellationToken: Token)).Value;
    private static async Task<List<TableEntity>> Inventory(EnrollmentFixture f)
    {
        var result = new List<TableEntity>();
        await foreach (var row in f.Storage.CreateClient().QueryAsync<TableEntity>(cancellationToken: Token)) result.Add(row);
        return result;
    }
    private static void AssertCharges(List<TableEntity> before, List<TableEntity> after)
    {
        foreach (var row in before)
        {
            var current = Assert.Single(after, r => r.PartitionKey == row.PartitionKey && r.RowKey == row.RowKey);
            foreach (var field in row.Keys.Where(k => k.EndsWith("Count", StringComparison.Ordinal) || k is "RowCredits" or "GlobalSlot" or "Slots" or "Admissions" or "Reads" or "Signs" or "Mints"))
                Assert.Equal(row[field], current[field]);
        }
        Assert.Equal(before.Count, after.Count);
    }
}
