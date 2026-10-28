using System.Net;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class RenewalCapacityTests
{
    [Fact]
    public async Task Ordinary_fifty_five_minute_renewals_do_not_exhaust_capacity_when_scheduled_cleanup_is_delayed()
    {
        var f = new InMemoryBroker();
        var controller = await Retained.ActiveAgent(f);
        for (var renewal = 0; renewal < 9; renewal++)
        {
            Assert.Equal(HttpStatusCode.OK, (await f.Issue(controller.Device, controller.Secret)).StatusCode);
            Assert.InRange(f.Gate.OutstandingTokenCount!.Value, 1, 8);
            f.Clock.Advance(TimeSpan.FromMinutes(55));
        }
        Assert.Contains(Retained.IssuanceLedgers(f), ledger => ledger.Value.CapacityState == "released");
    }

    [Fact]
    public async Task Pressure_relief_does_not_release_an_unknown_old_attempt()
    {
        var f = new InMemoryBroker();
        var controller = await Retained.ActiveAgent(f);
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(controller.Device, controller.Secret)).StatusCode);
        var unknown = IssuanceAttempt.Key(Retained.Repository, f.Enrollment(controller.Device).RuntimeAttempt!);
        f.Clock.Advance(TimeSpan.FromMinutes(55));
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(controller.Device, controller.Secret)).StatusCode);
        f.State.Change(unknown, row => row["State"] = "outstanding");
        var version = f.State.Version(unknown);

        for (var renewal = 0; renewal < 8; renewal++)
        {
            f.Clock.Advance(TimeSpan.FromMinutes(55));
            Assert.Equal(HttpStatusCode.OK, (await f.Issue(controller.Device, controller.Secret)).StatusCode);
        }

        Assert.Equal(version, f.State.Version(unknown));
        Assert.NotEqual(true, f.State.Get(unknown)!.CapacityReleased);
        Assert.Contains(Retained.IssuanceLedgers(f), ledger => ledger.Value.CapacityState == "released");
    }

    [Fact]
    public async Task Pressure_relief_waits_for_the_full_five_minute_expiry_margin()
    {
        var f = new InMemoryBroker { Limits = new(RepositoryAttempts: 2) };
        var controller = await Retained.ActiveAgent(f);
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(controller.Device, controller.Secret)).StatusCode);
        var runtime = IssuanceAttempt.Key(Retained.Repository, f.Enrollment(controller.Device).RuntimeAttempt!);
        f.Clock.Advance(TimeSpan.FromMinutes(55));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Issue(controller.Device, controller.Secret)).StatusCode);
        Assert.NotEqual(true, f.State.Get(runtime)!.CapacityReleased);
        f.Clock.Advance(TimeSpan.FromMinutes(7)); // The older verifier is safe, but the runtime's expiry margin is not.
        Assert.Equal(HttpStatusCode.TooManyRequests, (await f.Issue(controller.Device, controller.Secret)).StatusCode);
        Assert.NotEqual(true, f.State.Get(runtime)!.CapacityReleased);
        f.Clock.Advance(TimeSpan.FromMinutes(3));

        Assert.Equal(HttpStatusCode.OK, (await f.Issue(controller.Device, controller.Secret)).StatusCode);
        Assert.True(f.State.Get(runtime)!.CapacityReleased);
    }

    [Fact]
    public async Task A_failed_global_release_keeps_its_charge_and_a_later_pressure_visit_finishes_the_same_proof()
    {
        var f = new InMemoryBroker();
        var controller = await Retained.ActiveAgent(f);
        for (var renewal = 0; renewal < 3; renewal++)
        {
            Assert.Equal(HttpStatusCode.OK, (await f.Issue(controller.Device, controller.Secret)).StatusCode);
            f.Clock.Advance(TimeSpan.FromMinutes(55));
        }
        string? interrupted = null;
        f.State.BeforeCommit = transaction =>
        {
            if (transaction.Writes.FirstOrDefault(write => write.Kind == StateWriteKind.Delete && write.Key is StateKey<IssuanceSlot>)?.Key is StateKey<IssuanceSlot> key)
            {
                interrupted = f.State.Get(key)!.OperationId;
                throw new BrokerStateUnavailable("controlled global release failure");
            }
            return Task.CompletedTask;
        };
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Issue(controller.Device, controller.Secret)).StatusCode);
        Assert.NotNull(interrupted);
        Assert.True(f.State.Get(IssuanceAttempt.Key(Retained.Repository, interrupted))!.CapacityReleased);
        Assert.NotEqual("released", f.State.Get(IssuanceLedger.Key(interrupted))!.CapacityState);
        f.State.BeforeCommit = null;

        for (var renewal = 0; renewal < 2; renewal++)
        {
            f.Clock.Advance(TimeSpan.FromMinutes(55));
            Assert.Equal(HttpStatusCode.OK, (await f.Issue(controller.Device, controller.Secret)).StatusCode);
        }
        Assert.Equal("released", f.State.Get(IssuanceLedger.Key(interrupted))!.CapacityState);
        Assert.InRange(f.Gate.OutstandingTokenCount!.Value, 1, 8);
    }

    [Fact]
    public async Task Empty_continuation_pages_cannot_turn_pressure_relief_into_an_unbounded_scan()
    {
        var f = new InMemoryBroker();
        var controller = await Retained.ActiveAgent(f);
        for (var renewal = 0; renewal < 3; renewal++)
        {
            Assert.Equal(HttpStatusCode.OK, (await f.Issue(controller.Device, controller.Secret)).StatusCode);
            f.Clock.Advance(TimeSpan.FromMinutes(55));
        }
        var lists = f.State.Lists;
        var commits = f.State.Commits;
        f.State.EmptyPages = 100;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Issue(controller.Device, controller.Secret)).StatusCode);

        Assert.InRange(f.State.Lists - lists, 1, 8);
        Assert.Equal(commits, f.State.Commits);
    }

    [Fact]
    public async Task Concurrent_cleanup_wins_its_versions_without_a_second_capacity_release()
    {
        var f = new InMemoryBroker();
        var controller = await Retained.ActiveAgent(f);
        for (var renewal = 0; renewal < 3; renewal++)
        {
            Assert.Equal(HttpStatusCode.OK, (await f.Issue(controller.Device, controller.Secret)).StatusCode);
            f.Clock.Advance(TimeSpan.FromMinutes(55));
        }
        var competed = false;
        f.State.BeforeCommit = async transaction =>
        {
            if (competed) return;
            var write = transaction.Writes.FirstOrDefault(item => item.Key is StateKey<IssuanceAttempt>
                && item.Row is { } row && row.TryGetValue("State", out var state) && state is "settled");
            if (write?.Key is not StateKey<IssuanceAttempt> key) return;
            competed = true;
            var attempt = f.State.Get(key)!;
            await f.Reclamation().ReclaimIssuanceAsync(attempt.OperationId!, attempt.Generation!, TestContext.Current.CancellationToken);
        };

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Issue(controller.Device, controller.Secret)).StatusCode);
        Assert.True(competed);
        Assert.True(f.State.Conflicts > 0);
        f.State.BeforeCommit = null;
        Assert.Equal(HttpStatusCode.OK, (await f.Issue(controller.Device, controller.Secret)).StatusCode);
        Assert.Equal(f.Gate.OutstandingTokenCount, f.State.Get(IssuanceQuota.Key)!.Slots);
        Assert.InRange(f.Gate.OutstandingTokenCount!.Value, 1, 8);
    }

    [Theory]
    [InlineData("repository", BrokerRejectionReason.RepositoryAttempts)]
    [InlineData("global", BrokerRejectionReason.GlobalAttempts)]
    [InlineData("retained", BrokerRejectionReason.RetainedIssuance)]
    [InlineData("repository-minute", BrokerRejectionReason.RepositoryMinuteBudget)]
    [InlineData("global-minute", BrokerRejectionReason.GlobalMinuteBudget)]
    public async Task Issuance_denials_preserve_a_bounded_nonsecret_reason_at_the_route(string limit, BrokerRejectionReason reason)
    {
        var f = IssuanceFixture.Create();
        var limits = limit switch
        {
            "repository" => new IssuanceLimits(RepositoryAttempts: 1),
            "global" => new IssuanceLimits(GlobalAttempts: 1),
            "repository-minute" => new IssuanceLimits(RepositoryAdmissions: 0),
            "global-minute" => new IssuanceLimits(GlobalAdmissions: 0),
            _ => new IssuanceLimits()
        };
        if (limit == "retained") f.State.Change(IssuanceQuota.Key, row => row["RetainedIssuanceCount"] = 4096L);
        var issuer = f.Issuer(limits);

        var result = await BrokerRoutes.IssueAsync(InMemoryBroker.Request(f.Device.Secret), f.Device.DeviceId, () => issuer.IssueAsync, 1);

        Assert.Equal(429, result.Status);
        Assert.Null(result.Body);
        Assert.Equal(reason, result.Rejection);
    }
}
