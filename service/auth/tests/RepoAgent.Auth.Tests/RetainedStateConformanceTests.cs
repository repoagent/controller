using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class RetainedStateConformanceTests
{
    [Fact]
    public async Task Enrollment_verifier_limit_keeps_second_enrollment_charged_without_an_uncharged_attempt()
    {
        var f = new InMemoryBroker { Retained = new(128, 256, 1) };
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        Assert.Equal(HttpStatusCode.OK, (await f.Start(Request(), 2)).StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Retained.HistoricalDiscovery(f, () => f.Start(Request("APPROVAL_SECOND"), 2))).StatusCode);
        var rows = f.Rows();
        Assert.Equal(2, rows.Count(r => r.Scope == "global:enrollment-v1" && r.Name.StartsWith("pending:", StringComparison.Ordinal)));
        Assert.Equal(1, f.GitHub.VerifierMints);
        AssertAllocation(rows, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Active_controller_enrollment_runtime_and_verifier_renewal_cannot_bypass_retained_issuance_limit(bool renewVerifier)
    {
        var f = new InMemoryBroker { Retained = new(128, 256, 2) };
        AgentVariableEnrollmentTests.ConfigureGitHub(f.GitHub);
        f.GitHub.RuntimeLifetime = TimeSpan.FromMinutes(6);
        var start = await f.Start(Request(), 2);
        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        f.GitHub.Marker = start.Json.GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var completion = await f.Complete(start.Json.GetProperty("pendingId").GetString()!, 2);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var receipt = completion.Json;
        var enrollmentId = receipt.GetProperty("enrollment").GetProperty("deviceId").GetString()!;
        var secret = receipt.GetProperty("enrollment").GetProperty("deviceSecret").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await f.Acknowledge(enrollmentId, receipt.GetProperty("completionId").GetString()!, secret, 2)).StatusCode);
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(enrollmentId, secret)).StatusCode);
        var before = f.Rows();
        AssertAllocation(before, 2);
        f.Clock.Advance(TimeSpan.FromMinutes(renewVerifier ? 56 : 1));
        if (!renewVerifier) f.Retained = new(128, 256, 1);
        // Lowering below the existing count must not reset accounting or remove ledgers.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Issue(enrollmentId, secret)).StatusCode);
        var after = f.Rows();
        Assert.Equal(before.Select(r => r.Key), after.Select(r => r.Key));
        AssertAllocation(after, 2);
        Assert.Equal(1, f.GitHub.VerifierMints);
        Assert.Equal(2, f.GitHub.Mints);
    }

    [Fact]
    public async Task Anonymous_global_only_false_hint_consumes_candidate_capacity_even_after_unknown_response()
    {
        var f = new InMemoryBroker { Retained = new(1, 256, 4096) };
        f.State.AfterCommit = _ => throw new BrokerStateUnavailable("Unknown committed admission");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(Request(repository: 999998), 2)).StatusCode);
        var before = f.Rows();
        Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Start(Request(repository: 999999), 2)).StatusCode);
        Assert.Equal(before.Select(r => r.Key), f.Rows().Select(r => r.Key));
        Assert.Single(before, r => r.Name == "repository:999998");
        Assert.Single(before, r => r.Name.StartsWith("pending:", StringComparison.Ordinal));
        Assert.DoesNotContain(before, r => r.Scope.StartsWith("repository:", StringComparison.Ordinal));
        Assert.Equal(1, f.State.Get(EnrollmentQuota.Key)!.CandidateCount);
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
        var f = new InMemoryBroker();
        StateKey quota = corruption == "missing-enrollment" ? EnrollmentQuota.Key : IssuanceQuota.Key;
        if (corruption.StartsWith("missing", StringComparison.Ordinal)) Assert.True(f.State.Remove(quota));
        else f.State.Change(quota, row =>
        {
            if (corruption == "incomplete") row.Remove("RetainedIssuanceCount");
            if (corruption == "wrong-version") row["RetentionSchemaVersion"] = 2L;
            if (corruption == "inconsistent") row["Slots"] = 1L;
        });
        var before = f.Rows();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(Request(), 2)).StatusCode);
        var after = f.Rows();
        Assert.Equal(before.Select(r => (r.Key, r.Version)), after.Select(r => (r.Key, r.Version)));
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
                ["Broker:TableName"] = "tests",
                ["Broker:DeploymentEpoch"] = "test"
            };
            if (value is not null) settings["Broker:" + limit] = value;
            return BrokerOptions.Read(name => settings.GetValueOrDefault(name));
        }
        int Selected(BrokerOptions o) => limit switch
        {
            "CandidateRepositoryLimit" => o.CandidateRepositoryLimit,
            "RetainedEnrollmentLimit" => o.RetainedEnrollmentLimit,
            _ => o.RetainedIssuanceLimit
        };
        Assert.Equal(maximum, Selected(Read(null)));
        Assert.Equal(1, Selected(Read("1")));
        Assert.Equal(maximum, Selected(Read(maximum.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        foreach (var invalid in new[] { "garbage", "0", "-1", (maximum + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) })
            Assert.Throws<InvalidOperationException>(() => Read(invalid));
    }

    private static RepositoryVariableEnrollmentRequest Request(string variable = "APPROVAL", long repository = 123456) =>
        new(Guid.NewGuid().ToString("N"), repository, "owner/project", variable, "polling");

    private static void AssertAllocation(IReadOnlyList<StoredRow> rows, int expected)
    {
        var ledgers = rows.Where(r => r.Scope == "global:issuance-v1" && r.Name.StartsWith("operation:", StringComparison.Ordinal))
            .Select(r => r.Read<IssuanceLedger>()).ToList();
        Assert.Equal(expected, ledgers.Count);
        Assert.Equal(expected, rows.Count(r => r.Name.StartsWith("slot:", StringComparison.Ordinal)));
        Assert.Equal(expected, rows.Single(r => r.Key == "global:issuance-v1/quota").Read<IssuanceQuota>().RetainedIssuanceCount);
        foreach (var ledger in ledgers)
        {
            var attempt = Assert.Single(rows, r => r.Scope == "repository:" + ledger.RepositoryId && r.Name == ledger.AttemptRowKey).Read<IssuanceAttempt>();
            Assert.Equal(ledger.Generation, attempt.Generation);
            Assert.Equal(ledger.ReservedAt, attempt.ReservedAt);
            Assert.Equal(ledger.ReservedAt!.Value.AddHours(24), ledger.PayloadPurgeAt);
            Assert.Equal(ledger.PayloadPurgeAt, attempt.PayloadPurgeAt);
            Assert.Equal(4, ledger.RowCredits);
        }
    }
}
