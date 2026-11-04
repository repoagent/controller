using System.Text.Json;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class CurrentCleanupTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public async Task No_due_work_is_one_query_and_zero_writes()
    {
        var (f, _, _) = await CurrentBrokerTests.Active();
        var writes = f.State.Commits;
        var lists = f.State.Lists;
        var reads = f.State.Reads;
        var cleanup = Cleaner(f);
        Assert.Equal(0, await cleanup.RunAsync(Token));
        Assert.Equal(writes, f.State.Commits);
        Assert.Equal(lists + 1, f.State.Lists);
        Assert.Equal(reads + 1, f.State.Reads); // The fixed schema check, no per-record reads.
    }
    [Fact]
    public async Task All_due_records_in_all_pages_are_processed_in_one_invocation()
    {
        var f = new InMemoryBroker();
        f.State.PageSize = 17;
        f.State.EmptyPages = 1;
        for (var i = 1; i <= 128; i++) f.State.Seed(CurrentRepository.Key(i), Due(i, f.Clock.GetUtcNow()));
        var quota = CurrentBrokerQuota.Empty();
        quota.Repositories = 128;
        f.State.Seed(CurrentBrokerQuota.Key, quota);
        var reads = f.State.Reads;
        var cleaner = Cleaner(f);
        Assert.Equal(0, await cleaner.RunAsync(Token));
        var summary = JsonSerializer.SerializeToElement(cleaner.Summary());
        Assert.Equal(128, summary.GetProperty("changedRecords").GetInt32());
        Assert.Equal(129, f.State.All().Count); // Fixed closed fences, no accumulating receipts/cursors.
        Assert.Equal(reads + 1, f.State.Reads);
        Assert.All(f.State.All().Where(r => r.Name.StartsWith("repository:", StringComparison.Ordinal)), row =>
        {
            var closed = BrokerRecord.Read<CurrentRepository>(row.Row);
            Assert.Equal("closed", closed.State);
            Assert.Null(closed.SetupSecretHash);
            Assert.Null(closed.NextCleanupAt);
        });
        var writes = f.State.Commits;
        Assert.Equal(0, await Cleaner(f).RunAsync(Token));
        Assert.Equal(writes, f.State.Commits);
    }
    [Fact]
    public async Task Concurrent_renewal_wins_over_a_stale_cleanup_decision()
    {
        var f = new InMemoryBroker();
        var key = CurrentRepository.Key(1);
        f.State.Seed(key, Due(1, f.Clock.GetUtcNow()));
        var competed = false;
        f.State.BeforeCommit = _ =>
        {
            if (!competed)
            {
                competed = true;
                var newer = Due(1, f.Clock.GetUtcNow().AddDays(1));
                f.State.Seed(key, newer);
            }
            return Task.CompletedTask;
        };
        var cleaner = Cleaner(f);
        Assert.Equal(0, await cleaner.RunAsync(Token));
        Assert.Equal("pending", f.State.Get(key)!.State);
        Assert.Equal(1, JsonSerializer.SerializeToElement(cleaner.Summary()).GetProperty("conflicts").GetInt32());
    }
    [Fact]
    public async Task Expired_setup_and_expired_enrollment_remove_secrets_without_extra_grace()
    {
        var f = new InMemoryBroker();
        var start = await f.Enrollment().StartRepositoryVariableAsync(CurrentBrokerTests.Request(), InMemoryBroker.SetupSecret, Token, 5);
        Assert.Equal(200, start.Status);
        f.Clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(0, await Cleaner(f).RunAsync(Token));
        Assert.Equal("closed", f.Row.State);
        Assert.Null(f.Row.SetupSecretHash);
        var (active, id, secret) = await CurrentBrokerTests.Active();
        active.Clock.Advance(TimeSpan.FromDays(90));
        Assert.Equal(401, (await active.Issuer().IssueAsync(id, secret, Token)).Status);
        Assert.Equal(0, await Cleaner(active).RunAsync(Token));
        Assert.Null(active.Row.ControllerSecretHash);
    }
    private static BrokerCleanup Cleaner(InMemoryBroker f) => new(f.State, f.DeploymentEpoch, new Budget(f.State), f.Clock);
    private static CurrentRepository Due(long id, DateTimeOffset now) => new()
    {
        SchemaVersion = 2,
        Window = 0,
        Admissions = 0,
        Reads = 0,
        Signs = 0,
        Mints = 0,
        RepositoryId = id,
        Epoch = InMemoryBroker.Epoch,
        State = "pending",
        EnrollmentId = id + "_" + Guid.NewGuid().ToString("N"),
        PendingId = id + "_" + Guid.NewGuid().ToString("N"),
        SetupSecretHash = new string('a', 64),
        Repository = "owner/project",
        Profile = "polling",
        Variable = "APPROVAL",
        ApprovalKind = ApprovalContract.AgentV5.Kind,
        SetupVersion = 5,
        PolicyVersion = ApprovalContract.AgentV5.Policy,
        Deadline = now.AddMinutes(-1),
        NextCleanupAt = now.AddMinutes(-1)
    };
    private sealed class Budget(InMemoryBrokerState state) : ICleanupBudget
    {
        public int Dispatches => state.Reads + state.Commits + state.Lists;
        public int BatchDispatches => state.Commits;
    }
}
