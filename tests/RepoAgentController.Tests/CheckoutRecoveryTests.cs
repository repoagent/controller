using Xunit;

namespace RepoAgentController.Tests;

public sealed class CheckoutRecoveryTests
{
    static readonly CheckoutBinding Binding = new("/workspace/repository", "0123456789abcdef0123456789abcdef", 345, "refs/heads/main");
    readonly SteppedClock clock = new(new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero));
    readonly InMemoryDiscussionStore discussions = new();
    readonly InMemoryRefreshJournal journals = new();

    public CheckoutRecoveryTests()
    {
        var state = new DiscussionState("owner/repository");
        var waiting = new WaitingInput("discussion") { ContextJson = "context" };
        waiting.Inputs.Add(new(clock.GetUtcNow(), "uncertain first input", null));
        waiting.Inputs.Add(new(clock.GetUtcNow(), "later input", null));
        state.Discussions["D1"] = new("https://github.com/owner/repository/discussions/1")
        {
            SessionId = "S1",
            Waiting = waiting,
            Pending = new("send", clock.GetUtcNow(), [], 1)
        };
        discussions.WriteDiscussions(state);
        var journal = new RefreshJournal(Binding.Root, Binding.Owner, Binding.RepositoryId)
        {
            Requested = 4, Completed = 3, Blocked = true, Running = 1,
            Reason = "Native activity or checkout update is uncertain; inspect state before recovery",
            Unknown = [new("future", "{\"keep\":\"verbatim\"}")]
        };
        journal.Active["admission"] = new(1) { Thread = "S1", Turn = "", Unconfirmed = true };
        journals.Saved = journal;
    }

    void Recover(bool confirmed = true) => CheckoutRecovery.Recover(Binding, discussions, journals, confirmed, clock);

    [Fact]
    public async Task Explicit_recovery_settles_a_legacy_generic_block_and_requeues_only_the_interrupted_batch_before_normal_startup()
    {
        Recover();

        var evidence = Assert.Single(journals.Evidence)!;
        Assert.True(evidence.Blocked);
        Assert.Single(evidence.Active);
        Assert.Equal(1, evidence.Running);
        Assert.Equal("{\"keep\":\"verbatim\"}", Assert.Single(evidence.Unknown).Json);
        var record = discussions.Saved!.Discussions["D1"];
        Assert.Equal("S1", record.SessionId);
        Assert.Null(record.Pending);
        Assert.Equal("context", record.Waiting!.ContextJson);
        Assert.Equal([true, false], record.Waiting.Inputs.Select(input => input.Redelivery));
        Assert.Equal(["uncertain first input", "later input"], record.Waiting.Inputs.Select(input => input.ChangeJson));
        Assert.Equal(clock.GetUtcNow(), record.Waiting.Retry!.After);
        Assert.False(journals.Saved!.Blocked);
        Assert.Empty(journals.Saved.Active);
        Assert.Equal(evidence.Unknown, journals.Saved.Unknown);
        Assert.Equal((4L, 3L), (journals.Saved.Requested, journals.Saved.Completed));
        var checkout = new ScriptedCheckout();
        await using var refresh = new CheckoutRefresh(checkout, Binding, journals, true, false, clock, new RecordingLog());
        await refresh.Start(new AgentSessions(new FakeAgentRuntime(), new RecordingLog(), refresh), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        using var admitted = await refresh.Admit(TestContext.Current.CancellationToken);
        admitted.Abandon();
        Assert.Equal(1, checkout.Refreshes);
    }

    [Theory]
    [InlineData("attestation")]
    [InlineData("unblocked")]
    [InlineData("applying")]
    [InlineData("missing journal")]
    [InlineData("foreign binding")]
    [InlineData("malformed reason")]
    [InlineData("malformed admission")]
    [InlineData("malformed pending")]
    public void Unproved_or_unsupported_recovery_keeps_original_work_and_the_fence(string problem)
    {
        var journal = journals.Saved!;
        if (problem == "unblocked") journal.Blocked = false;
        if (problem == "applying") journal.Applying = new("C1", "C2", 4);
        if (problem == "foreign binding") journal = new("/other", Binding.Owner, Binding.RepositoryId);
        if (problem == "malformed reason") journal.ReasonAsRead = "true";
        if (problem == "malformed admission") journal.Active["damaged"] = new(2) { AsRead = "false" };
        journals.Saved = problem == "missing journal" ? null : journal;
        if (problem == "malformed pending")
        {
            var state = discussions.Saved!;
            state.Discussions["D1"].Pending = new("send", clock.GetUtcNow(), [], 3);
            discussions.WriteDiscussions(state);
        }

        Assert.Throws<IOException>(() => Recover(problem != "attestation"));

        Assert.Empty(journals.Evidence);
        Assert.NotNull(discussions.Saved!.Discussions["D1"].Pending);
        Assert.All(discussions.Saved.Discussions["D1"].Waiting!.Inputs, input => Assert.False(input.Redelivery));
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("discussion write")]
    [InlineData("journal write")]
    public void A_failed_recovery_write_stays_fenced_and_can_retry_without_repeating_input_normalization(string failure)
    {
        if (failure == "snapshot") journals.PreserveFailure = new IOException("snapshot failed");
        if (failure == "discussion write") discussions.FailWrites = true;
        if (failure == "journal write") journals.WriteFailure = new IOException("journal failed");

        Assert.Throws<IOException>(() => Recover());

        Assert.True(journals.Saved!.Blocked);
        Assert.Single(journals.Saved.Active);
        var partial = discussions.Saved!.Discussions["D1"];
        Assert.Equal(failure == "journal write", partial.Pending is null);
        journals.PreserveFailure = null; discussions.FailWrites = false; journals.WriteFailure = null;

        Recover();

        Assert.False(journals.Saved.Blocked);
        Assert.Equal(1, discussions.Saved!.Discussions["D1"].Waiting!.Retry!.Attempts);
        Assert.Equal([true, false], discussions.Saved.Discussions["D1"].Waiting!.Inputs.Select(input => input.Redelivery));
    }
}
