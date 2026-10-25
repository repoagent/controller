using System.Net;
using System.Text;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed partial class PayloadRedactionTests
{
    [Theory]
    [InlineData("pending")]
    [InlineData("enrollment-attempt")]
    [InlineData("runtime-attempt")]
    public async Task Paused_add_cannot_replace_original_charged_key_tombstone(string stage)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        Journey? active = stage == "runtime-attempt" ? await ActiveAgent(f) : null;
        if (active is null) AgentVariableEnrollmentTests.ConfigureGitHub(f);
        var id = Guid.NewGuid().ToString("N");
        var fence = new TablePause(body => body.Contains(stage == "pending" ? "\"State\":\"discovering\"" : "\"State\":\"reserved\"", StringComparison.Ordinal));
        await f.RestartAsync(fence.Transport);
        var writer = active is null ? f.Send("/v2/enrollments", AgentRequest(id))
            : f.Send($"/v1/devices/{active.Device}/installation-token", secret: active.Secret);
        try
        {
            await fence.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var before = await Inventory(f);
            var enrollment = stage == "pending";
            var ledger = enrollment ? await EnrollmentLedger(f, id)
                : Assert.Single(before, r => r.PartitionKey == "global:issuance-v1"
                    && r.RowKey.StartsWith("operation:", StringComparison.Ordinal)
                    && !before.Any(a => a.PartitionKey == RevocationFixture.Partition && a.RowKey == r.GetString("AttemptRowKey")));
            var target = enrollment ? ledger.RowKey : ledger.GetString("AttemptRowKey")!;
            Assert.DoesNotContain(before, r => r.PartitionKey == RevocationFixture.Partition && r.RowKey == target);
            f.Clock.Advance(ledger.GetDateTimeOffset("PayloadPurgeAt")!.Value - f.Clock.GetUtcNow());
            if (enrollment) await RedactEnrollment(f, ledger);
            else await Cleanup(f).RedactIssuanceAsync(ledger.GetString("OperationId")!, ledger.GetString("Generation")!, Token);
            var tombstone = await Read(f, RevocationFixture.Partition, target);
            Assert.Equal("redacted", tombstone.GetString("State"));
            Assert.Equal(ledger.GetString("Generation"), tombstone.GetString("Generation"));
            fence.Release.TrySetResult();
            using var response = await writer;
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            var retained = await Read(f, tombstone.PartitionKey, tombstone.RowKey);
            Assert.Equal(tombstone.ETag, retained.ETag);
            Assert.False(retained.ContainsKey("RecoveryHash"));
            Assert.False(retained.ContainsKey("ProtectedToken"));
            Assert.False(retained.ContainsKey("ProtectedCompletion"));
            var after = await Inventory(f);
            foreach (var row in before)
            {
                var current = Assert.Single(after, r => r.PartitionKey == row.PartitionKey && r.RowKey == row.RowKey);
                foreach (var field in row.Keys.Where(k => k.EndsWith("Count", StringComparison.Ordinal) || k is "RowCredits" or "Slots" or "GlobalSlot"))
                    Assert.Equal(row[field], current[field]);
            }
            Assert.DoesNotContain(after, r => r.RowKey.StartsWith("device:", StringComparison.Ordinal) && (active is null || r.RowKey != "device:" + active.Device));
            Assert.Equal(active is null ? 0 : 1, f.GitHub.Mints);
        }
        finally
        {
            fence.Release.TrySetResult();
            using var completed = await writer;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Paused_known_ciphertext_or_device_completion_cannot_rehydrate_after_cleanup(bool completion)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        string id;
        string path;
        if (completion)
        {
            var start = await StartAgent(f);
            id = start.Id; path = $"/v2/enrollments/{start.Pending}/completion";
        }
        else
        {
            AgentVariableEnrollmentTests.ConfigureGitHub(f);
            id = Guid.NewGuid().ToString("N"); path = "/v2/enrollments";
        }
        var fence = new TablePause(body => body.Contains(completion ? "\"ProtectedCompletion\"" : "\"ProtectedToken\"", StringComparison.Ordinal));
        await f.RestartAsync(fence.Transport);
        var writer = f.Send(path, completion ? null : AgentRequest(id));
        try
        {
            await fence.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var before = await Inventory(f);
            var ledger = completion ? await EnrollmentLedger(f, id)
                : Assert.Single(before, r => r.PartitionKey == "global:issuance-v1" && r.RowKey.StartsWith("operation:", StringComparison.Ordinal));
            f.Clock.Advance(ledger.GetDateTimeOffset("PayloadPurgeAt")!.Value - f.Clock.GetUtcNow());
            if (completion) await RedactEnrollment(f, ledger);
            else await Cleanup(f).RedactIssuanceAsync(ledger.GetString("OperationId")!, ledger.GetString("Generation")!, Token);
            fence.Release.TrySetResult();
            using var response = await writer;
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            var target = await Read(f, RevocationFixture.Partition, completion ? ledger.RowKey : ledger.GetString("AttemptRowKey")!);
            Assert.False(target.ContainsKey(completion ? "ProtectedCompletion" : "ProtectedToken"));
            Assert.True(target.GetBoolean("PayloadRedacted"));
            var after = await Inventory(f);
            Assert.DoesNotContain(after, r => r.RowKey.StartsWith("device:", StringComparison.Ordinal));
            AssertCharges(before, after);
        }
        finally
        {
            fence.Release.TrySetResult();
            using var completed = await writer;
        }
    }

    [Fact]
    public async Task Late_locator_publication_remains_routing_only_and_charged_after_redaction()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var start = await StartAgent(f);
        var fence = new TablePause(body => body.Contains("\"PartitionKey\":\"locator:", StringComparison.Ordinal), batchesOnly: false);
        await f.RestartAsync(fence.Transport);
        var writer = f.Send($"/v2/enrollments/{start.Pending}/completion");
        try
        {
            await fence.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var ledger = await EnrollmentLedger(f, start.Id);
            var before = await Inventory(f);
            Assert.DoesNotContain(before, r => r.PartitionKey.StartsWith("locator:", StringComparison.Ordinal));
            Assert.Single(before, r => r.RowKey.StartsWith("device:", StringComparison.Ordinal));
            f.Clock.Advance(ledger.GetDateTimeOffset("PayloadPurgeAt")!.Value - f.Clock.GetUtcNow());
            await RedactEnrollment(f, ledger);
            fence.Release.TrySetResult();
            using var response = await writer;
            Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
            var after = await Inventory(f);
            var locator = Assert.Single(after, r => r.PartitionKey.StartsWith("locator:", StringComparison.Ordinal));
            Assert.Equal(ledger.GetString("DeviceId"), locator.GetString("DeviceId"));
            Assert.Equal(ledger.GetString("PendingId"), locator.GetString("PendingId"));
            Assert.Equal(ledger.GetString("RepositoryId"), locator.GetString("RepositoryId"));
            Assert.All(locator.Keys, key => Assert.Contains(key, new[] { "PartitionKey", "RowKey", "Timestamp", "odata.etag", "RepositoryId", "DeviceId", "PendingId" }));
            AssertCharges(before, after.Where(r => r.PartitionKey != locator.PartitionKey).ToList());
            Assert.False((await Read(f, RevocationFixture.Partition, ledger.RowKey)).ContainsKey("ProtectedCompletion"));
            Assert.False((await Read(f, RevocationFixture.Partition, "device:" + ledger.GetString("DeviceId"))).ContainsKey("SecretHash"));
        }
        finally
        {
            fence.Release.TrySetResult();
            using var completed = await writer;
        }
    }

    [Fact]
    public async Task Release_winning_cleanup_race_preserves_renewed_live_secret()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var j = await ActiveAgent(f);
        var ledger = await EnrollmentLedger(f, j.Id);
        var device = await Read(f, RevocationFixture.Partition, "device:" + j.Device);
        var originalExpiry = device.GetDateTimeOffset("ExpiresAt")!.Value;
        f.Clock.Advance(originalExpiry.AddMinutes(-1) - f.Clock.GetUtcNow());
        var releaseFence = new TablePause(body => body.Contains("\"State\":\"released\"", StringComparison.Ordinal));
        await f.RestartAsync(releaseFence.Transport);
        var writer = f.Send($"/v1/devices/{j.Device}/installation-token", secret: j.Secret);
        Task? cleanup = null;
        var cleanupFence = new TablePause(body => body.Contains("\"PayloadRedacted\":true", StringComparison.Ordinal));
        try
        {
            await releaseFence.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var before = await Inventory(f);
            f.Clock.Advance(originalExpiry.AddHours(24) - f.Clock.GetUtcNow());
            cleanup = Cleanup(f, table: f.Storage.CreateClient(cleanupFence.Transport))
                .RedactEnrollmentAsync(j.Id, ledger.GetString("Generation")!, Token);
            await cleanupFence.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            releaseFence.Release.TrySetResult();
            using var result = await writer;
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            cleanupFence.Release.TrySetResult();
            await Assert.ThrowsAsync<StateConflict>(() => cleanup);
            var renewed = await Read(f, device.PartitionKey, device.RowKey);
            Assert.True(renewed.GetDateTimeOffset("ExpiresAt") > f.Clock.GetUtcNow());
            Assert.Equal(device["SecretHash"], renewed["SecretHash"]);
            Assert.False(renewed.ContainsKey("PayloadRedacted"));
            AssertCharges(before, await Inventory(f));
        }
        finally
        {
            releaseFence.Release.TrySetResult(); cleanupFence.Release.TrySetResult();
            using var result = await writer;
            if (cleanup is not null)
            {
                try { await cleanup; }
                catch (Exception ex) when (ex is StateConflict or BrokerStateUnavailable) { }
            }
        }
    }

    [Fact]
    public async Task Stale_runtime_token_replacement_cannot_restore_redacted_ciphertext()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var j = await ActiveAgent(f);
        var fence = new TablePause(body => body.Split('\n').Any(line =>
            line.Contains("\"ProtectedToken\"", StringComparison.Ordinal)
            && line.Contains("\"Purpose\":\"runtime\"", StringComparison.Ordinal)
            && line.Contains("\"State\":\"known\"", StringComparison.Ordinal)));
        await f.RestartAsync(fence.Transport);
        var writer = f.Send($"/v1/devices/{j.Device}/installation-token", secret: j.Secret);
        try
        {
            await fence.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            var before = await Inventory(f);
            var ledger = Assert.Single(before, r => r.PartitionKey == "global:issuance-v1" && r.GetString("Purpose") == "runtime" && r.RowKey.StartsWith("operation:", StringComparison.Ordinal));
            var originalAttempt = await Read(f, RevocationFixture.Partition, ledger.GetString("AttemptRowKey")!);
            Assert.Equal("sending", originalAttempt.GetString("State"));
            Assert.False(originalAttempt.ContainsKey("ProtectedToken"));
            f.Clock.Advance(ledger.GetDateTimeOffset("PayloadPurgeAt")!.Value - f.Clock.GetUtcNow());
            await Cleanup(f).RedactIssuanceAsync(ledger.GetString("OperationId")!, ledger.GetString("Generation")!, Token);
            fence.Release.TrySetResult();
            using var response = await writer;
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            var attempt = await Read(f, RevocationFixture.Partition, ledger.GetString("AttemptRowKey")!);
            Assert.False(attempt.ContainsKey("ProtectedToken"));
            Assert.True(attempt.GetBoolean("PayloadRedacted"));
            Assert.Equal("sending", attempt.GetString("State"));
            Assert.False(attempt.ContainsKey("SettledAt"));
            AssertCharges(before, await Inventory(f));
        }
        finally
        {
            fence.Release.TrySetResult();
            using var completed = await writer;
        }
    }

    private sealed class TablePause
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ObservedTransport Transport { get; }
        public TablePause(Func<string, bool> matches, bool batchesOnly = true)
        {
            var paused = 0;
            Transport = new() { Before = async message =>
            {
                if (batchesOnly && !ObservedTransport.IsBatch(message) || message.Request.Content is null) return;
                using var stream = new MemoryStream();
                await message.Request.Content.WriteToAsync(stream, Token);
                if (!matches(Encoding.UTF8.GetString(stream.ToArray())) || Interlocked.CompareExchange(ref paused, 1, 0) != 0) return;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), Token);
            }};
        }
    }
}
