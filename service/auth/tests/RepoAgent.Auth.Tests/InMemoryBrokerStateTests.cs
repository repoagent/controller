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
    public async Task Listing_is_ordinal_within_its_range_in_pages_of_at_most_32()
    {
        state.PageSize = 100;
        var names = Enumerable.Range(0, 34).Select(i => "e" + i.ToString("D2")).Concat(["E", "F", "a", "_"]).ToList();
        foreach (var name in names) state.Seed(Receipt(name), new RevocationReceipt { EnrollmentId = name });
        state.Seed(Gate, NewGate());
        state.Seed(RevocationReceipt.Key("2", "other"), new RevocationReceipt());
        var range = new StateRange<RevocationReceipt>("repository:" + Repository, "revocation:", "revocation;");

        var first = await state.ListAsync(range, "revocation:E", null, Token);
        var second = await state.ListAsync(range, "revocation:E", first.Continuation, Token);

        var expected = names.Where(name => string.CompareOrdinal(name, "E") > 0).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(32, first.Records.Count);
        Assert.NotNull(first.Continuation);
        Assert.Null(second.Continuation);
        Assert.Equal(expected, first.Records.Concat(second.Records).Select(record => record.Record.Value.EnrollmentId));
        Assert.All(first.Records, record => Assert.Equal(record.Record.Version, state.Version(record.Key)));
    }

    [Fact]
    public async Task An_empty_page_can_still_carry_a_continuation()
    {
        state.Seed(Receipt("a"), new RevocationReceipt { EnrollmentId = "a" });
        var range = new StateRange<RevocationReceipt>("repository:" + Repository, "revocation:", "revocation;");
        state.EmptyPages = 1;

        var empty = await state.ListAsync(range, "", null, Token);
        var next = await state.ListAsync(range, "", empty.Continuation, Token);

        Assert.Empty(empty.Records);
        Assert.NotNull(empty.Continuation);
        Assert.Equal("a", Assert.Single(next.Records).Record.Value.EnrollmentId);
        Assert.Null(next.Continuation);
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
