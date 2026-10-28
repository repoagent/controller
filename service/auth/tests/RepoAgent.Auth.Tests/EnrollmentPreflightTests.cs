using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

// Replacement admission is decided before it creates another GitHub capability or pending deadline.
public sealed class EnrollmentPreflightTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Revoked_owner_blocks_a_fresh_start_without_minting_or_charging_another_enrollment(int setupVersion)
    {
        var f = new InMemoryBroker();
        var owner = await Retained.ActiveAgent(f);
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(owner.EnrollmentId, owner.Secret)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await f.Revoke(owner.EnrollmentId, owner.Secret)).StatusCode);
        var before = f.Rows().Select(row => (row.Key, row.Version)).ToList();
        var mints = f.GitHub.VerifierMints;
        var request = Retained.AgentRequest(Guid.NewGuid().ToString("N"));

        var blocked = setupVersion == 1 ? await f.Start(InMemoryBroker.Request()) : await f.Start(request, setupVersion);

        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("enrollment_not_ready", blocked.Json.GetProperty("code").GetString());
        Assert.Equal(60, blocked.Json.GetProperty("retryAfterSeconds").GetInt32());
        Assert.Equal(["code", "retryAfterSeconds"], blocked.Json.EnumerateObject().Select(field => field.Name));
        Assert.Equal(mints, f.GitHub.VerifierMints);
        Assert.Equal(before, f.Rows().Select(row => (row.Key, row.Version)));
        Assert.Null(f.State.Get(EnrollmentLedger.Key(request.IdempotencyId)));
    }

    [Fact]
    public async Task Rechecking_until_durable_owner_release_preserves_clocks_and_then_admits_one_fresh_enrollment()
    {
        var f = new InMemoryBroker();
        var owner = await Retained.ActiveAgent(f);
        Assert.Equal(HttpStatusCode.NoContent, (await f.Revoke(owner.EnrollmentId, owner.Secret)).StatusCode);
        var request = Retained.AgentRequest(Guid.NewGuid().ToString("N"));
        var oldDeadline = f.Pending(owner.Pending).Deadline;
        for (var retry = 0; retry < 2; retry++)
        {
            Assert.Equal(HttpStatusCode.Conflict, (await f.Start(request, 4)).StatusCode);
            f.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        await Retained.ReclaimEnrollment(f, owner.Id);
        f.Clock.Advance(TimeSpan.FromMinutes(66));
        foreach (var ledger in Retained.IssuanceLedgers(f)) await Retained.ReclaimIssuance(f, ledger.Value);
        Assert.Equal(0, f.Gate.OutstandingTokenCount);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Start(request, 4)).StatusCode);
        Assert.Equal(owner.EnrollmentId, f.Gate.OwnerEnrollmentId);
        await Retained.ReclaimEnrollment(f, owner.Id);
        var admittedAt = f.Clock.GetUtcNow();

        var start = await f.Start(request, 4);

        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        Assert.Equal(admittedAt.AddMinutes(15), start.Json.GetProperty("deadline").GetDateTimeOffset());
        Assert.Equal(oldDeadline, f.Pending(owner.Pending).Deadline);
        Assert.Equal(start.Text, (await f.Start(request, 4)).Text);
        Assert.Equal(2, f.GitHub.VerifierMints);
        f.GitHub.Marker = start.Json.GetProperty("marker").GetString()!;
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        var completion = await f.Complete(start.Json.GetProperty("pendingId").GetString()!, 4);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        var enrollment = completion.Json.GetProperty("enrollment");
        f.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await f.Acknowledge(enrollment.GetProperty("deviceId").GetString()!,
            completion.Json.GetProperty("completionId").GetString()!, enrollment.GetProperty("deviceSecret").GetString()!, 4)).StatusCode);
    }

    [Theory]
    [InlineData("global-admission")]
    [InlineData("publication")]
    [InlineData("reservation")]
    [InlineData("repository-reservation")]
    public async Task Concurrent_fresh_starts_that_pass_early_readiness_mint_at_most_one_verifier(string stage)
    {
        var f = new InMemoryBroker();
        CloneEnrollmentContractTests.ConfigureGitHub(f.GitHub, "polling");
        var pause = new CommitPause(f.State, transaction => stage switch
        {
            "global-admission" => transaction.InScope("global:enrollment-v1"),
            "publication" => transaction.Writes("State", "discovering"),
            "repository-reservation" => transaction.Writes("State", "reserved"),
            _ => transaction.Writes("MintAdmissionStarted", true)
        });
        var firstRequest = Retained.AgentRequest(Guid.NewGuid().ToString("N"));
        var first = f.Start(firstRequest, 4);
        try
        {
            await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
            var winner = await f.Start(Retained.AgentRequest(Guid.NewGuid().ToString("N")), 4);
            Assert.Equal(HttpStatusCode.OK, winner.StatusCode);
            pause.Release.TrySetResult();
            var loser = await first;
            Assert.Contains(loser.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.ServiceUnavailable });
            Assert.Equal(1, f.GitHub.VerifierMints);
            Assert.Equal(1, f.Gate.OutstandingTokenCount);
            Assert.Single(f.Rows(), row => row.Name.StartsWith("attempt:", StringComparison.Ordinal));
            Assert.Equal(stage == "repository-reservation" ? 2 : 1, Retained.IssuanceLedgers(f).Count);
            // Any global reservation committed before the conflict remains conservatively charged.
            Assert.Equal(stage == "global-admission" ? 1 : 2, f.State.Get(EnrollmentQuota.Key)!.Count);
            if (stage != "global-admission")
            {
                var original = f.State.Get(EnrollmentLedger.Key(firstRequest.IdempotencyId))!;
                Assert.Equal(original.CreatedAt!.Value.AddMinutes(15), original.Deadline);
            }
        }
        finally
        {
            pause.Release.TrySetResult();
            await first;
        }
    }

    [Theory]
    [InlineData("read")]
    [InlineData("epoch")]
    [InlineData("state")]
    [InlineData("count")]
    public async Task Unconfirmed_readiness_has_no_timing_or_private_evidence_and_creates_no_authority(string fault)
    {
        var f = new InMemoryBroker();
        var gate = RepositoryGate.Key(InMemoryBroker.RepositoryId);
        f.State.Seed(gate, RepositoryGate.Vacant(InMemoryBroker.RepositoryId, InMemoryBroker.Epoch));
        if (fault == "read") f.State.BeforeRead = key => key.Name == "gate"
            ? throw new BrokerStateUnavailable("Unknown read") : Task.CompletedTask;
        if (fault == "epoch") f.DeploymentEpoch = "restored-epoch";
        if (fault == "state") f.State.Change(gate, row => row["State"] = "closed");
        if (fault == "count") f.State.Change(gate, row => row.Remove("OutstandingTokenCount"));
        var before = f.Rows().Select(row => (row.Key, row.Version)).ToList();

        var answer = await f.Start(Retained.AgentRequest(Guid.NewGuid().ToString("N")), 4);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, answer.StatusCode);
        Assert.Equal("enrollment_settlement_unconfirmed", answer.Json.GetProperty("code").GetString());
        Assert.Equal(["code"], answer.Json.EnumerateObject().Select(field => field.Name));
        Assert.Equal(before, f.Rows().Select(row => (row.Key, row.Version)));
        Assert.Equal(0, f.GitHub.Mints);
    }

    [Fact]
    public async Task Unknown_same_request_send_is_never_replayed_and_original_clocks_and_charges_survive_restart()
    {
        var f = new InMemoryBroker();
        var request = Retained.AgentRequest(Guid.NewGuid().ToString("N"));
        CloneEnrollmentContractTests.ConfigureGitHub(f.GitHub, "polling");
        f.GitHub.BeforeMint = _ => throw new GitHubUnavailable("Unconfirmed send");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Start(request, 4)).StatusCode);
        f.GitHub.BeforeMint = null;
        var before = f.Rows().Select(row => (row.Key, row.Version)).ToList();
        f.Clock.Advance(TimeSpan.FromMinutes(3));

        var retry = await f.Start(request, 4);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, retry.StatusCode);
        Assert.Equal("enrollment_settlement_unconfirmed", retry.Json.GetProperty("code").GetString());
        Assert.Equal(["code"], retry.Json.EnumerateObject().Select(field => field.Name));
        Assert.Equal(1, f.GitHub.VerifierMints);
        Assert.Equal(before, f.Rows().Select(row => (row.Key, row.Version)));
        Assert.Equal(HttpStatusCode.Conflict, (await f.Start(request with { Variable = "OTHER" }, 4)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Start(request, 4, new string('b', 43))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Start(request, 2)).StatusCode);
        Assert.Equal(before, f.Rows().Select(row => (row.Key, row.Version)));
        f.Clock.Advance(TimeSpan.FromMinutes(66));
        Assert.Equal(HttpStatusCode.Gone, (await f.Start(request, 4)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Start(Retained.AgentRequest(Guid.NewGuid().ToString("N")), 4)).StatusCode);
        Assert.Equal(1, f.GitHub.VerifierMints);
    }
}
