using Xunit;

namespace RepoAgent.Auth.Tests;

// The core's tests trust these guarantees of the fake; a fake without them would let wrong concurrency code pass.
public sealed class InMemoryBrokerStateTests
{
    private const string Repository = "1";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly InMemoryBrokerState state = new();
    private static readonly StateKey<RepositoryGate> Gate = RepositoryGate.Key(Repository);
    private static StateKey<RevocationReceipt> Receipt(string enrollment) => RevocationReceipt.Key(Repository, enrollment);
    private static RepositoryGate NewGate() => new() { RepositoryId = Repository, State = "active", Sequence = 1 };

    [Fact]
    public async Task Every_add_and_replace_issues_a_new_version_even_when_nothing_changed()
    {
        var added = (await state.CommitAsync(new StateTransaction().Add(Gate, NewGate()), Token))[0]!;
        var read = (await state.ReadAsync(Gate, Token))!;
        Assert.Equal(added, read.Version);

        var touched = (await state.CommitAsync(new StateTransaction().Replace(Gate, read.Value, read.Version), Token))[0]!;

        Assert.NotEqual(added, touched);
        Assert.Equal(touched, (await state.ReadAsync(Gate, Token))!.Version);
        await Assert.ThrowsAsync<StateConflict>(() => state.CommitAsync(new StateTransaction().Replace(Gate, read.Value, added), Token));
    }

    [Theory]
    [InlineData("add-existing")]
    [InlineData("stale-replace")]
    [InlineData("stale-delete")]
    public async Task A_conflicting_write_fails_the_whole_transaction_and_writes_nothing(string conflict)
    {
        var stale = state.Seed(Gate, NewGate());
        var current = state.Change(Gate, row => row["Sequence"] = 2L);
        var transaction = new StateTransaction().Add(Receipt("new"), new RevocationReceipt { RepositoryId = Repository });
        _ = conflict switch
        {
            "add-existing" => transaction.Add(Gate, NewGate()),
            "stale-replace" => transaction.Replace(Gate, NewGate(), stale),
            _ => transaction.Delete(Gate, stale),
        };

        await Assert.ThrowsAsync<StateConflict>(() => state.CommitAsync(transaction, Token));

        Assert.Equal(1, state.Conflicts);
        Assert.Null(state.Row(Receipt("new")));
        Assert.Equal(current, state.Version(Gate));
        Assert.Equal(2L, state.Get(Gate)!.Sequence);
    }

    [Theory]
    [InlineData(StateWriteKind.Replace)]
    [InlineData(StateWriteKind.Delete)]
    public async Task Replace_or_delete_of_a_missing_record_is_unavailable_and_never_a_conflict(StateWriteKind kind)
    {
        var transaction = kind == StateWriteKind.Replace
            ? new StateTransaction().Replace(Gate, NewGate(), new("v1"))
            : new StateTransaction().Delete(Gate, new("v1"));

        await Assert.ThrowsAsync<BrokerStateUnavailable>(() => state.CommitAsync(transaction, Token));

        Assert.Null(state.Row(Gate));
    }

    [Fact]
    public void A_transaction_holds_at_most_32_distinct_records_of_one_scope()
    {
        var transaction = new StateTransaction();
        for (var i = 0; i < StateTransaction.MaxWrites; i++) transaction.Add(Receipt("e" + i), new RevocationReceipt());

        Assert.Throws<InvalidOperationException>(() => transaction.Add(Receipt("e32"), new RevocationReceipt()));
        Assert.Throws<InvalidOperationException>(() => new StateTransaction().Add(Gate, NewGate()).Add(RepositoryGate.Key("2"), NewGate()));
        Assert.Throws<InvalidOperationException>(() => new StateTransaction().Add(Gate, NewGate()).Delete(Gate, new("v1")));
    }

    [Fact]
    public async Task Reads_return_copies_that_never_change_the_stored_record()
    {
        state.Seed(Gate, NewGate());
        var read = (await state.ReadAsync(Gate, Token))!;
        read.Value.State = "closed";
        state.Row(Gate)!["State"] = "closed";

        Assert.Equal("active", (await state.ReadAsync(Gate, Token))!.Value.State);
    }

    [Fact]
    public async Task Hooks_commit_competing_writes_and_lose_responses_of_committed_transactions()
    {
        var version = state.Seed(Gate, NewGate());
        var competing = true;
        state.BeforeCommit = async _ =>
        {
            if (!competing) return;
            competing = false;
            var current = (await state.ReadAsync(Gate, Token))!;
            await state.CommitAsync(new StateTransaction().Replace(Gate, current.Value, current.Version), Token);
        };
        await Assert.ThrowsAsync<StateConflict>(() => state.CommitAsync(new StateTransaction().Replace(Gate, NewGate(), version), Token));

        state.AfterCommit = _ => throw new BrokerStateUnavailable("Lost response.");
        var read = (await state.ReadAsync(Gate, Token))!;
        read.Value.State = "closed";
        await Assert.ThrowsAsync<BrokerStateUnavailable>(() => state.CommitAsync(new StateTransaction().Replace(Gate, read.Value, read.Version), Token));

        Assert.Equal("closed", state.Get(Gate)!.State);
        Assert.NotEqual(read.Version, state.Version(Gate));
    }
}
