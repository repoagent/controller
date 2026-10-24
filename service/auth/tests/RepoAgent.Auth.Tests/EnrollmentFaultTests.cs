using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class EnrollmentFaultTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lost_global_admissions_remain_bounded_and_duplicate_restart_resumes_one_reservation()
    {
        var lost = new ObservedTransport { After = message =>
        {
            if (ObservedTransport.IsBatch(message)) throw new HttpRequestException("Lost committed admission");
            return Task.CompletedTask;
        }};
        await using var f = await EnrollmentFixture.CreateAsync(lost);
        var original = f.Request();
        for (var i = 0; i < 64; i++)
        {
            var request = i == 0 ? original : f.Request() with { RepositoryId = 123456 + i / 4 };
            using var response = await f.Send("/v1/enrollments", request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        using var full = await f.Send("/v1/enrollments", f.Request() with { RepositoryId = 999999 });
        Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);
        var rows = await Rows(f);
        Assert.Equal(82, rows.Count); // 64 ledgers, 16 directories, two initialized quotas.
        Assert.Single(rows, row => row.PartitionKey == "global:issuance-v1" && row.RowKey == "quota");
        Assert.All(rows.Where(row => row.PartitionKey != "global:issuance-v1"),
            row => Assert.Equal("global:enrollment-v1", row.PartitionKey));
        Assert.Equal(64, rows.Single(r => r.PartitionKey == "global:enrollment-v1" && r.RowKey == "quota").GetInt64("Count"));
        Assert.Equal(0, f.GitHub.Reads);
        Assert.Equal(0, f.GitHub.Mints);
        var deadline = rows.Single(r => r.RowKey == "pending:" + original.IdempotencyId).GetDateTimeOffset("Deadline");
        await f.RestartAsync();
        var duplicates = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.Send("/v1/enrollments", original)));
        foreach (var response in duplicates) response.Dispose();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var recovered = await f.Send("/v1/enrollments", original);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(deadline, (await Json(recovered)).GetProperty("deadline").GetDateTimeOffset());
        Assert.Equal(1, f.GitHub.Mints);
        rows = await Rows(f);
        Assert.Equal(64, rows.Count(r => r.PartitionKey == "global:enrollment-v1" && r.RowKey.StartsWith("pending:", StringComparison.Ordinal)));
        Assert.Single(rows, r => r.PartitionKey == RevocationFixture.Partition && r.RowKey.StartsWith("pending:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_completion_commit_or_locator_response_recovers_one_encrypted_device_after_restart(bool locator)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var pending = await Start(f);
        var batches = 0;
        var injected = false;
        var transport = new ObservedTransport { After = message =>
        {
            var target = locator
                ? message.Request.Method.ToString() == "POST" && !ObservedTransport.IsBatch(message)
                : ObservedTransport.IsBatch(message) && ++batches == 4;
            if (target && !injected)
            {
                injected = true;
                throw new HttpRequestException("Lost completion persistence response");
            }
            return Task.CompletedTask;
        }};
        await f.RestartAsync(transport);
        using var lost = await f.Send($"/v1/enrollments/{pending}/completion");
        Assert.True(injected);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, lost.StatusCode);
        var device = Assert.Single(await Rows(f), r => r.RowKey.StartsWith("device:", StringComparison.Ordinal));
        var hash = device.GetString("SecretHash");
        Assert.False(device.GetBoolean("Acknowledged"));
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var recovered = await f.Send($"/v1/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var delivered = await Json(recovered);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var repeated = await f.Send($"/v1/enrollments/{pending}/completion");
        Assert.Equal(delivered.ToString(), (await Json(repeated)).ToString());
        device = Assert.Single(await Rows(f), r => r.RowKey.StartsWith("device:", StringComparison.Ordinal));
        Assert.Equal(hash, device.GetString("SecretHash"));
        Assert.Equal(1, f.GitHub.Mints);
    }

    // Which answers prove a withdrawal or a binding change is the core's rule (ApprovalObserverTests);
    // this is the setup v1 completion route's outcome for one of each.
    [Theory]
    [InlineData("absent", true)]
    [InlineData("404", false)]
    [InlineData("environment", true)]
    public async Task Only_verified_withdrawal_or_binding_change_closes_observed_approval(string observation, bool closed)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var pending = await Start(f);
        using var completion = await f.Send($"/v1/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var delivered = await Json(completion);
        var marker = f.GitHub.Marker;
        var environment = f.GitHub.Environment;
        if (observation == "absent") f.GitHub.Marker = "";
        if (observation == "404") f.GitHub.PageAnswer = (_, _) => new GitHubRead<GitHubVariablePage>.NotFound();
        if (observation == "environment") f.GitHub.Environment = GitHubRead.Found(new ApprovalEnvironment(988, "approval/environment"));
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var observed = await f.Send($"/v1/enrollments/{pending}/completion");
        Assert.DoesNotContain("deviceSecret", await observed.Content.ReadAsStringAsync(Token));
        f.GitHub.Marker = marker;
        f.GitHub.PageAnswer = null;
        f.GitHub.Environment = environment;
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var retry = await f.Send($"/v1/enrollments/{pending}/completion");
        Assert.Equal(closed ? HttpStatusCode.Gone : HttpStatusCode.OK, retry.StatusCode);
        if (!closed) Assert.Equal(delivered.ToString(), (await Json(retry)).ToString());
        else Assert.Equal("closed", (await Pending(f, pending)).GetString("State"));
    }

    [Fact]
    public async Task Repeated_observations_share_durable_floor_and_do_not_grow_rows_or_extend_deadline()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var pending = await Start(f, approve: false);
        var initial = await Pending(f, pending);
        var rows = (await Rows(f)).Count;
        for (var i = 0; i < 4; i++)
        {
            using var allowed = await f.Send($"/v1/enrollments/{pending}/completion");
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
            var reads = f.GitHub.Reads;
            for (var j = 0; j < 3; j++)
            {
                if (j == 1) await f.RestartAsync();
                using var blocked = await f.Send($"/v1/enrollments/{pending}/completion");
                Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
            }
            Assert.Equal(reads, f.GitHub.Reads);
            Assert.Equal(rows, (await Rows(f)).Count);
            await f.RestartAsync();
            f.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.Equal(initial.GetDateTimeOffset("Deadline"), (await Pending(f, pending)).GetDateTimeOffset("Deadline"));
        Assert.Equal(1, f.GitHub.Mints);
        f.Clock.Advance(TimeSpan.FromMinutes(15));
        using var expired = await f.Send($"/v1/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
    }

    [Fact]
    public async Task Revoke_during_ack_observation_wins_ETag_conflict_and_never_reactivates()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var pending = await Start(f);
        using var completed = await f.Send($"/v1/enrollments/{pending}/completion");
        var delivered = await Json(completed);
        var envelope = delivered.GetProperty("enrollment");
        var device = envelope.GetProperty("deviceId").GetString()!;
        var secret = envelope.GetProperty("deviceSecret").GetString()!;
        var revoked = false;
        f.GitHub.BeforeRead = async request =>
        {
            if (!revoked && request.StartsWith("variables ", StringComparison.Ordinal))
            {
                revoked = true;
                using var result = await f.Send($"/v1/devices/{device}", secret: secret, method: HttpMethod.Delete);
                Assert.Equal(HttpStatusCode.NoContent, result.StatusCode);
            }
        };
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var body = new { completionId = delivered.GetProperty("completionId").GetString() };
        using var ack = await f.Send($"/v1/devices/{device}/acknowledgment", body, secret);
        Assert.True(revoked);
        Assert.NotEqual(HttpStatusCode.OK, ack.StatusCode);
        f.GitHub.BeforeRead = null;
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var retry = await f.Send($"/v1/devices/{device}/acknowledgment", body, secret);
        Assert.NotEqual(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal("revoked", (await f.Storage.Table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "device:" + device, cancellationToken: Token)).Value.GetString("State"));
    }

    [Fact]
    public async Task Lost_repository_admissions_count_toward_four_pending_limit_before_any_provider_work()
    {
        var batches = 0;
        var transport = new ObservedTransport { After = message =>
        {
            if (ObservedTransport.IsBatch(message) && ++batches % 2 == 0)
                throw new HttpRequestException("Lost committed repository admission");
            return Task.CompletedTask;
        }};
        await using var f = await EnrollmentFixture.CreateAsync(transport);
        var original = f.Request();
        for (var i = 0; i < 4; i++)
        {
            using var admitted = await f.Send("/v1/enrollments", i == 0 ? original : f.Request());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, admitted.StatusCode);
        }
        var before = await Rows(f);
        using var rejected = await f.Send("/v1/enrollments", f.Request());
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(before.Count, (await Rows(f)).Count);
        Assert.Equal(4, before.Count(r => r.PartitionKey == RevocationFixture.Partition && r.RowKey.StartsWith("pending:", StringComparison.Ordinal)));
        Assert.Equal(4, before.Single(r => r.RowKey == "gate").GetInt64("PendingCount"));
        Assert.Equal(0, f.GitHub.Reads);
        Assert.Equal(0, f.GitHub.Mints);
        await f.RestartAsync();
        using var resumed = await f.Send("/v1/enrollments", original);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.Equal(1, f.GitHub.Mints);
    }

    [Fact]
    public async Task Unknown_other_pending_authority_is_never_replayed_or_borrowed_for_completion()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var original = f.Request();
        var normal = f.GitHub.Before;
        f.GitHub.Before = request => request.Method == HttpMethod.Post
            ? throw new HttpRequestException("Unknown GitHub mint outcome") : normal!(request);
        using var unknown = await f.Send("/v1/enrollments", original);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unknown.StatusCode);
        var attempt = Assert.Single(await Rows(f), r => r.RowKey.StartsWith("attempt:", StringComparison.Ordinal));
        Assert.Equal("sending", attempt.GetString("State"));
        f.GitHub.Before = normal;
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var replay = await f.Send("/v1/enrollments", original);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, replay.StatusCode);
        Assert.Equal(1, f.GitHub.Mints);
        var next = await Start(f);
        var reads = f.GitHub.Reads;
        using var denied = await f.Send($"/v1/enrollments/{next}/completion");
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
        Assert.DoesNotContain(await Rows(f), r => r.RowKey.StartsWith("device:", StringComparison.Ordinal));
        Assert.Equal(2, (await Rows(f)).Single(r => r.RowKey == "gate").GetInt64("OutstandingTokenCount"));
        Assert.Equal(attempt.ETag, (await f.Storage.Table.GetEntityAsync<TableEntity>(attempt.PartitionKey, attempt.RowKey, cancellationToken: Token)).Value.ETag);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Original_deadline_or_changed_epoch_prevents_recovery_and_acknowledgment(bool epoch)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var pending = await Start(f);
        using var completion = await f.Send($"/v1/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var delivered = await Json(completion);
        var device = delivered.GetProperty("enrollment").GetProperty("deviceId").GetString()!;
        var secret = delivered.GetProperty("enrollment").GetProperty("deviceSecret").GetString()!;
        var row = await Pending(f, pending);
        if (epoch) await f.RestartAsync(epoch: "restored-epoch");
        else f.Clock.Advance(row.GetDateTimeOffset("Deadline")!.Value - f.Clock.GetUtcNow());
        var reads = f.GitHub.Reads;
        using var recovery = await f.Send($"/v1/enrollments/{pending}/completion");
        Assert.Equal(epoch ? HttpStatusCode.Unauthorized : HttpStatusCode.Gone, recovery.StatusCode);
        using var ack = await f.Send($"/v1/devices/{device}/acknowledgment", new { completionId = delivered.GetProperty("completionId").GetString() }, secret);
        Assert.Equal(epoch ? HttpStatusCode.Unauthorized : HttpStatusCode.Gone, ack.StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
        Assert.False((await f.Storage.Table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "device:" + device, cancellationToken: Token)).Value.GetBoolean("Acknowledged"));
    }

    [Fact]
    public async Task Duplicate_active_ack_after_restart_does_not_extend_inactivity_or_retain_recovery_authority()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var pending = await Start(f);
        using var completion = await f.Send($"/v1/enrollments/{pending}/completion");
        var delivered = await Json(completion);
        var device = delivered.GetProperty("enrollment").GetProperty("deviceId").GetString()!;
        var secret = delivered.GetProperty("enrollment").GetProperty("deviceSecret").GetString()!;
        var body = new { completionId = delivered.GetProperty("completionId").GetString() };
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var ack = await f.Send($"/v1/devices/{device}/acknowledgment", body, secret);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        var initial = (await f.Storage.Table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "device:" + device, cancellationToken: Token)).Value;
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromDays(1));
        var reads = f.GitHub.Reads;
        using var duplicate = await f.Send($"/v1/devices/{device}/acknowledgment", body, secret);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        var current = (await f.Storage.Table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "device:" + device, cancellationToken: Token)).Value;
        Assert.Equal(initial.ETag, current.ETag);
        Assert.Equal(initial.GetDateTimeOffset("LastActivityAt"), current.GetDateTimeOffset("LastActivityAt"));
        Assert.Equal(initial.GetDateTimeOffset("ExpiresAt"), current.GetDateTimeOffset("ExpiresAt"));
        Assert.Equal(reads, f.GitHub.Reads);
        using var retired = await f.Send($"/v1/enrollments/{pending}/completion");
        Assert.Equal(HttpStatusCode.Unauthorized, retired.StatusCode);
        var stored = await Pending(f, pending);
        Assert.False(stored.ContainsKey("ProtectedCompletion"));
        Assert.False(stored.ContainsKey("RecoveryHash"));
    }

    [Fact]
    public async Task New_discovery_preserves_incumbent_and_cannot_transfer_its_verifier_authority()
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var first = await Start(f);
        using var completion = await f.Send($"/v1/enrollments/{first}/completion");
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var incumbent = (await f.Storage.Table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "gate", cancellationToken: Token)).Value;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await Start(f);
        var gate = (await f.Storage.Table.GetEntityAsync<TableEntity>(RevocationFixture.Partition, "gate", cancellationToken: Token)).Value;
        Assert.Equal(incumbent.GetString("OwnerDeviceId"), gate.GetString("OwnerDeviceId"));
        Assert.Equal(incumbent.GetString("VerifierAttempt"), gate.GetString("VerifierAttempt"));
        Assert.Equal("unacknowledged", gate.GetString("State"));
        var reads = f.GitHub.Reads;
        using var denied = await f.Send($"/v1/enrollments/{second}/completion");
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Equal(reads, f.GitHub.Reads);
        Assert.Single(await Rows(f), r => r.RowKey.StartsWith("device:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    public async Task Resumed_unsent_verifier_uses_original_JWT_claims_and_cannot_refresh_original_expiry(int delaySeconds)
    {
        var batches = 0;
        var interrupted = false;
        var transport = new ObservedTransport { After = message =>
        {
            // Admission, repository state, work claim, global work, then the persisted mint attempt.
            // Lose that last response before signing/discovery/send claim can run.
            if (ObservedTransport.IsBatch(message) && ++batches == 5)
            {
                interrupted = true;
                throw new HttpRequestException("Lost reserved verifier response before signing");
            }
            return Task.CompletedTask;
        }};
        await using var f = await EnrollmentFixture.CreateAsync(transport);
        var request = f.Request();
        using var initial = await f.Send("/v1/enrollments", request);
        Assert.True(interrupted);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, initial.StatusCode);
        var original = Assert.Single(await Rows(f), row => row.RowKey.StartsWith("attempt:", StringComparison.Ordinal));
        Assert.Equal("reserved", original.GetString("State"));
        Assert.Equal(0, f.GitHub.Reads);
        Assert.Equal(0, f.GitHub.Mints);
        Assert.Equal(0, f.GitHub.Authorizations);
        string? sentAuthorization = null;
        f.GitHub.Before = message =>
        {
            if (message.Method == HttpMethod.Post) sentAuthorization = message.Headers.Authorization!.Parameter;
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await f.RestartAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(delaySeconds));
        using var resumed = await f.Send("/v1/enrollments", request);
        var persisted = Assert.Single(await Rows(f), row => row.RowKey.StartsWith("attempt:", StringComparison.Ordinal));
        Assert.Equal(original.GetDateTimeOffset("ReservedAt"), persisted.GetDateTimeOffset("ReservedAt"));
        Assert.Equal(original.GetDateTimeOffset("ReservedAt")!.Value.AddHours(24), persisted.GetDateTimeOffset("PayloadPurgeAt"));
        var ledger = Assert.Single(await Rows(f), row => row.RowKey == "operation:" + original.GetString("OperationId"));
        Assert.Equal(original.GetString("Generation"), ledger.GetString("Generation"));
        Assert.Equal(persisted.GetDateTimeOffset("PayloadPurgeAt"), ledger.GetDateTimeOffset("PayloadPurgeAt"));
        Assert.Equal(original.GetDateTimeOffset("JwtIssuedAt"), persisted.GetDateTimeOffset("JwtIssuedAt"));
        Assert.Equal(original.GetDateTimeOffset("JwtExpiresAt"), persisted.GetDateTimeOffset("JwtExpiresAt"));
        if (delaySeconds == 120)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resumed.StatusCode);
            Assert.Empty(f.GitHub.Windows);
            Assert.Null(sentAuthorization);
            Assert.Equal(0, f.GitHub.Reads);
            Assert.Equal(0, f.GitHub.Mints);
            Assert.Equal("reserved", persisted.GetString("State"));
        }
        else
        {
            Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
            Assert.Equal(1, f.GitHub.Mints);
            // One authorization, over the original reserved window, signs the resumed mint.
            var window = Assert.Single(f.GitHub.Windows);
            Assert.Equal(original.GetDateTimeOffset("JwtIssuedAt"), window.IssuedAt);
            Assert.Equal(original.GetDateTimeOffset("JwtExpiresAt"), window.ExpiresAt);
            Assert.Equal("app-jwt-1", sentAuthorization);
        }
    }

    private static async Task<string> Start(EnrollmentFixture f, bool approve = true)
    {
        using var start = await f.Send("/v1/enrollments", f.Request());
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var result = await Json(start);
        if (approve) f.GitHub.Marker = result.GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        return result.GetProperty("pendingId").GetString()!;
    }
    private static Task<JsonElement> Json(HttpResponseMessage response) => response.Content.ReadFromJsonAsync<JsonElement>(Token);
    private static async Task<TableEntity> Pending(EnrollmentFixture f, string id) =>
        (await f.Storage.Table.GetEntityAsync<TableEntity>("repository:" + id.Split('_')[0], "pending:" + id.Split('_')[1], cancellationToken: Token)).Value;
    private static async Task<List<TableEntity>> Rows(EnrollmentFixture f)
    {
        var rows = new List<TableEntity>();
        await foreach (var row in f.Storage.Table.QueryAsync<TableEntity>(cancellationToken: Token)) rows.Add(row);
        return rows;
    }
}
