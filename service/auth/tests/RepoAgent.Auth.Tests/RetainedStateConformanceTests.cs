using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class RetainedStateConformanceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Enrollment_verifier_limit_keeps_second_enrollment_charged_without_an_uncharged_attempt()
    {
        await using var f = await EnrollmentFixture.CreateAsync(settings: new() { ["Broker:RetainedIssuanceLimit"] = "1" });
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        using var first = await f.Send("/v2/enrollments", Request());
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var second = await f.Send("/v2/enrollments", Request("APPROVAL_SECOND"));
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        var rows = await Rows(f);
        Assert.Equal(2, rows.Count(r => r.PartitionKey == "global:enrollment-v1" && r.RowKey.StartsWith("pending:", StringComparison.Ordinal)));
        Assert.Equal(1, f.GitHub.VerifierMints);
        AssertAllocation(rows, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Active_device_runtime_and_verifier_renewal_cannot_bypass_retained_issuance_limit(bool renewVerifier)
    {
        await using var f = await EnrollmentFixture.CreateAsync(settings: new() { ["Broker:RetainedIssuanceLimit"] = "2" });
        AgentVariableEnrollmentTests.ConfigureGitHub(f);
        using var start = await f.Send("/v2/enrollments", Request());
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var pending = await start.Content.ReadFromJsonAsync<JsonElement>(Token);
        f.GitHub.Marker = pending.GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var completion = await f.Send($"/v2/enrollments/{pending.GetProperty("pendingId").GetString()}/completion");
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var receipt = await completion.Content.ReadFromJsonAsync<JsonElement>(Token);
        var device = receipt.GetProperty("enrollment").GetProperty("deviceId").GetString()!;
        var secret = receipt.GetProperty("enrollment").GetProperty("deviceSecret").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        using var ack = await f.Send($"/v2/devices/{device}/acknowledgment", new { completionId = receipt.GetProperty("completionId").GetString() }, secret);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var path = $"/v1/devices/{device}/installation-token";
        using var runtime = await f.Send(path, secret: secret);
        Assert.Equal(HttpStatusCode.OK, runtime.StatusCode);
        var before = await Rows(f);
        AssertAllocation(before, 2);
        f.Clock.Advance(TimeSpan.FromMinutes(renewVerifier ? 56 : 1));
        await f.RestartAsync(settings: renewVerifier ? null : new() { ["Broker:RetainedIssuanceLimit"] = "1" });
        // Lowering below the existing count must not reset accounting or remove ledgers.
        using var denied = await f.Send(path, secret: secret);
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        var after = await Rows(f);
        Assert.Equal(before.Select(Key).Order(), after.Select(Key).Order());
        AssertAllocation(after, 2);
        Assert.Equal(1, f.GitHub.VerifierMints);
        Assert.Equal(2, f.GitHub.Mints);
    }

    [Fact]
    public async Task Anonymous_global_only_false_hint_consumes_candidate_capacity_even_after_unknown_response()
    {
        var lost = new ObservedTransport { After = message =>
        {
            if (ObservedTransport.IsBatch(message)) throw new HttpRequestException("Unknown committed admission");
            return Task.CompletedTask;
        }};
        await using var f = await EnrollmentFixture.CreateAsync(lost, new() { ["Broker:CandidateRepositoryLimit"] = "1" });
        using var first = await f.Send("/v2/enrollments", Request(repository: 999998));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        var before = await Rows(f);
        using var second = await f.Send("/v2/enrollments", Request(repository: 999999));
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(before.Select(Key).Order(), (await Rows(f)).Select(Key).Order());
        Assert.Single(before, r => r.RowKey == "repository:999998");
        Assert.Single(before, r => r.RowKey.StartsWith("pending:", StringComparison.Ordinal));
        Assert.DoesNotContain(before, r => r.PartitionKey.StartsWith("repository:", StringComparison.Ordinal));
        Assert.Equal(1, before.Single(r => r.PartitionKey == "global:enrollment-v1" && r.RowKey == "quota").GetInt64("CandidateCount"));
        Assert.Equal(0, f.GitHub.Mints);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("missing-enrollment")]
    [InlineData("incomplete")]
    [InlineData("wrong-version")]
    [InlineData("inconsistent")]
    public async Task Unactivated_or_inconsistent_schema_denies_without_bootstrap(string corruption)
    {
        await using var f = await EnrollmentFixture.CreateAsync();
        var partition = corruption == "missing-enrollment" ? "global:enrollment-v1" : "global:issuance-v1";
        var quota = (await f.Storage.Table.GetEntityAsync<TableEntity>(partition, "quota", cancellationToken: Token)).Value;
        if (corruption.StartsWith("missing", StringComparison.Ordinal)) await f.Storage.Table.DeleteEntityAsync(quota.PartitionKey, quota.RowKey, quota.ETag, Token);
        else
        {
            if (corruption == "incomplete") quota.Remove("RetainedIssuanceCount");
            if (corruption == "wrong-version") quota["RetentionSchemaVersion"] = 2L;
            if (corruption == "inconsistent") quota["Slots"] = 1L;
            await f.Storage.Table.UpdateEntityAsync(quota, quota.ETag, TableUpdateMode.Replace, Token);
        }
        var before = await Rows(f);
        using var denied = await f.Send("/v2/enrollments", Request());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, denied.StatusCode);
        var after = await Rows(f);
        Assert.Equal(before.Select(Key).Order(), after.Select(Key).Order());
        Assert.Equal(before.Select(r => r.ETag).OrderBy(e => e.ToString()), after.Select(r => r.ETag).OrderBy(e => e.ToString()));
        Assert.Equal(0, f.GitHub.Mints);
    }

    [Theory]
    [InlineData("CandidateRepositoryLimit", 128)]
    [InlineData("RetainedEnrollmentLimit", 256)]
    [InlineData("RetainedIssuanceLimit", 4096)]
    public void Limits_accept_only_downward_positive_configuration(string limit, int maximum)
    {
        BrokerOptions Read(string? value)
        {
            var settings = new Dictionary<string, string?>
            {
                ["Broker:TableEndpoint"] = "https://example.table.core.windows.net",
                ["Broker:TableName"] = "tests", ["Broker:DeploymentEpoch"] = "test"
            };
            if (value is not null) settings["Broker:" + limit] = value;
            return BrokerOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        }
        int Selected(BrokerOptions o) => limit switch
        {
            "CandidateRepositoryLimit" => o.CandidateRepositoryLimit,
            "RetainedEnrollmentLimit" => o.RetainedEnrollmentLimit,
            _ => o.RetainedIssuanceLimit
        };
        Assert.Equal(maximum, Selected(Read(null)));
        Assert.Equal(1, Selected(Read("1")));
        Assert.Equal(maximum, Selected(Read(maximum.ToString())));
        foreach (var invalid in new[] { "garbage", "0", "-1", (maximum + 1).ToString() })
            Assert.Throws<InvalidOperationException>(() => Read(invalid));
    }

    private static object Request(string variable = "APPROVAL", long repository = 123456) => new
    {
        idempotencyId = Guid.NewGuid().ToString("N"), repositoryId = repository,
        repository = "owner/project", variable, profile = "polling"
    };
    private static string Key(TableEntity r) => r.PartitionKey + "/" + r.RowKey;
    private static async Task<List<TableEntity>> Rows(EnrollmentFixture f)
    {
        var rows = new List<TableEntity>();
        await foreach (var row in f.Storage.Table.QueryAsync<TableEntity>(cancellationToken: Token)) rows.Add(row);
        return rows;
    }
    private static void AssertAllocation(List<TableEntity> rows, int expected)
    {
        var ledgers = rows.Where(r => r.PartitionKey == "global:issuance-v1" && r.RowKey.StartsWith("operation:", StringComparison.Ordinal)).ToList();
        Assert.Equal(expected, ledgers.Count);
        Assert.Equal(expected, rows.Count(r => r.RowKey.StartsWith("slot:", StringComparison.Ordinal)));
        Assert.Equal(expected, rows.Single(r => r.PartitionKey == "global:issuance-v1" && r.RowKey == "quota").GetInt64("RetainedIssuanceCount"));
        foreach (var ledger in ledgers)
        {
            var attempt = Assert.Single(rows, r => r.PartitionKey == "repository:" + ledger.GetString("RepositoryId") && r.RowKey == ledger.GetString("AttemptRowKey"));
            Assert.Equal(ledger.GetString("Generation"), attempt.GetString("Generation"));
            Assert.Equal(ledger.GetDateTimeOffset("ReservedAt"), attempt.GetDateTimeOffset("ReservedAt"));
            Assert.Equal(ledger.GetDateTimeOffset("ReservedAt")!.Value.AddHours(24), ledger.GetDateTimeOffset("PayloadPurgeAt"));
            Assert.Equal(ledger.GetDateTimeOffset("PayloadPurgeAt"), attempt.GetDateTimeOffset("PayloadPurgeAt"));
            Assert.Equal(4, ledger.GetInt64("RowCredits"));
        }
    }
}
