using Xunit;

namespace RepoAgentController.Tests;

// The polling driver uses real Core observation, acceptance and durable output; only its external peers and time are controlled.
public sealed class ControllerPollingTests
{
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    static CancellationToken TestToken => TestContext.Current.CancellationToken;
    static Task Bounded(Task task) => task.WaitAsync(TimeSpan.FromSeconds(10), TestToken);

    [Fact]
    public async Task A_precanceled_stop_prevents_even_the_initial_observation_and_once_flush()
    {
        await using var world = new World();
        world.Open(1);
        world.QueueReceipt();
        await world.Stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => world.Run(once: true));

        Assert.Empty(world.Native.Requests);
        Assert.Null(world.Durable.Saved);
        Assert.Single(world.State.Outbox!.Operations);
        Assert.DoesNotContain("addReaction retained", world.GitHub.Calls);
        Assert.Empty(world.Polls);
        Assert.Empty(world.Clock.Delays);
    }

    [Fact]
    public async Task Input_is_observed_immediately_and_new_input_waits_the_entire_next_interval()
    {
        await using var world = new World();
        world.Open(1);
        var run = world.Run();
        await world.Clock.Armed();
        Assert.NotNull(world.Saved(1).Running);
        Assert.Single(world.Native.Requests);
        AssertPoll(Assert.Single(world.Polls), succeeded: true);
        world.Open(2);

        world.Clock.Advance(Interval - TimeSpan.FromTicks(1));
        Assert.Single(world.Native.Requests);
        Assert.False(world.Durable.Saved!.Discussions.ContainsKey("D2"));
        world.Clock.Advance(TimeSpan.FromTicks(1));
        await world.Clock.Armed();

        Assert.Equal(2, world.Native.Requests.Count);
        Assert.NotNull(world.Saved(2).Running);
        Assert.Equal([Interval, Interval], world.Clock.Delays);
        await world.Stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(run));
        world.Open(3);
        world.Clock.Advance(Interval + Interval);
        Assert.Equal(2, world.Native.Requests.Count);
        Assert.Equal(2, world.Polls.Count);
    }

    [Fact]
    public async Task Slow_acceptance_does_not_overlap_or_consume_the_interval_for_the_next_observation()
    {
        await using var world = new World(holdAcknowledgement: true);
        world.Open(1);
        var run = world.Run();
        await Bounded(world.Runtime.Entered.Task);
        Assert.NotNull(world.Saved(1).Pending);
        world.Open(2);

        world.Clock.Advance(Interval + Interval + Interval);
        Assert.Single(world.Native.Requests);
        Assert.False(world.Durable.Saved!.Discussions.ContainsKey("D2"));
        Assert.Empty(world.Polls);
        Assert.Empty(world.Clock.Delays); // The interval cannot run concurrently with this observation.
        world.Runtime.Release.TrySetResult();
        await world.Clock.Armed();
        Assert.NotNull(world.Saved(1).Running);
        Assert.Single(world.Native.Requests);
        world.Clock.Advance(Interval - TimeSpan.FromTicks(1));
        Assert.Single(world.Native.Requests);
        world.Clock.Advance(TimeSpan.FromTicks(1));
        await world.Clock.Armed();

        Assert.Equal(2, world.Native.Requests.Count);
        Assert.NotNull(world.Saved(2).Running);
        Assert.Equal([Interval, Interval], world.Clock.Delays);
        await world.Stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(run));
    }

    [Fact]
    public async Task A_failed_observation_waits_the_normal_interval_before_observing_later_input()
    {
        await using var world = new World();
        world.GitHub.FailReads("List", new IOException("fixture list unavailable"));
        var run = world.Run();
        await world.Clock.Armed();
        AssertPoll(Assert.Single(world.Polls), succeeded: false);
        Assert.Empty(world.Native.Requests);
        world.GitHub.FailReads("List", null);
        world.Open(1);

        world.Clock.Advance(Interval - TimeSpan.FromTicks(1));
        Assert.Empty(world.Native.Requests);
        world.Clock.Advance(TimeSpan.FromTicks(1));
        await world.Clock.Armed();

        Assert.NotNull(world.Saved(1).Running);
        Assert.Single(world.Native.Requests);
        AssertPoll(world.Polls[1], succeeded: true);
        Assert.Equal([Interval, Interval], world.Clock.Delays);
        await world.Stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(run));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Once_always_attempts_durable_output_and_succeeds_only_if_observation_and_flush_succeed(bool observed, bool delivered)
    {
        await using var world = new World();
        world.Open(1);
        world.QueueReceipt();
        if (!observed) world.GitHub.FailReads("List", new IOException("fixture list unavailable"));
        if (!delivered) world.GitHub.Fail(new GitHubRequestException(GitHubFailure.Transient, "fixture write unavailable"));

        Assert.Equal(observed && delivered ? 0 : 1, await world.Run(once: true).WaitAsync(TimeSpan.FromSeconds(10), TestToken));

        Assert.Contains("addReaction retained", world.GitHub.Calls);
        Assert.Equal(delivered, await world.GitHub.HasEyesReceipt("retained", TestToken));
        Assert.Equal(!delivered, world.State.Outbox!.Operations.Any(operation => operation.Key == "receipt:retained"));
        AssertPoll(Assert.Single(world.Polls), observed);
        Assert.Empty(world.Clock.Delays);
        if (observed)
        {
            Assert.Single(world.Native.Requests);
            Assert.NotNull(world.Saved(1).Running); // --once does not drain an unfinished, genuinely accepted turn.
            Assert.Null(world.Saved(1).Pending);
        }
        else Assert.Empty(world.Native.Requests);
    }

    [Fact]
    public async Task Once_awaits_output_even_after_a_false_poll_and_ordinary_stop_does_not_cancel_that_output()
    {
        await using var world = new World(holdReceipt: true);
        world.GitHub.FailReads("List", new IOException("fixture list unavailable"));
        world.QueueReceipt();
        var run = world.Run(once: true);

        Assert.Same(world.Discussions.Entered.Task, await Task.WhenAny(run, world.Discussions.Entered.Task).WaitAsync(TimeSpan.FromSeconds(10), TestToken));
        Assert.False(run.IsCompleted);
        await world.Stop.CancelAsync();
        Assert.False(world.Discussions.ReceiptToken.IsCancellationRequested);
        Assert.False(run.IsCompleted);
        world.Discussions.Release.TrySetResult();

        Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(10), TestToken));
        Assert.True(await world.GitHub.HasEyesReceipt("retained", TestToken));
        Assert.Empty(world.State.Outbox!.Operations);
        Assert.Empty(world.Clock.Delays);
        Assert.Empty(world.Native.Requests);
    }

    [Fact]
    public async Task Ordinary_stop_preserves_entered_acceptance_then_cancels_the_delay_without_starting_another_pass()
    {
        await using var world = new World(holdAcknowledgement: true);
        world.Open(1);
        var run = world.Run();
        await Bounded(world.Runtime.Entered.Task);
        Assert.NotNull(world.Saved(1).Pending);

        await world.Stop.CancelAsync();
        Assert.False(world.Runtime.RequestToken.IsCancellationRequested);
        Assert.False(run.IsCompleted);
        world.Runtime.Release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(run));

        Assert.NotNull(world.Saved(1).Running);
        Assert.Null(world.Saved(1).Pending);
        Assert.Single(world.Native.Requests);
        AssertPoll(Assert.Single(world.Polls), succeeded: true);
        Assert.DoesNotContain(world.Log.Entries, entry => entry.Kind == "turn-ended");
        world.Open(2);
        world.Clock.Advance(Interval);
        Assert.Single(world.Native.Requests);
    }

    [Fact]
    public async Task Activity_cancellation_interrupts_entered_acceptance_without_forging_completion_or_a_completed_poll()
    {
        await using var world = new World(holdAcknowledgement: true);
        world.Open(1);
        var run = world.Run();
        await Bounded(world.Runtime.Entered.Task);

        await world.Activity.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Bounded(run));

        Assert.True(world.Runtime.RequestToken.IsCancellationRequested);
        Assert.False(world.Stop.IsCancellationRequested);
        Assert.NotNull(world.Saved(1).Pending); // Issued native input may have executed; retain its unresolved evidence.
        Assert.Null(world.Saved(1).Running);
        Assert.Empty(world.Polls);
        Assert.Empty(world.Clock.Delays);
        Assert.DoesNotContain(world.Log.Entries, entry => entry.Kind == "turn-ended");
    }

    static void AssertPoll(string message, bool succeeded)
    {
        Assert.StartsWith($"success={succeeded} elapsed-ms=", message, StringComparison.Ordinal);
        Assert.True(double.TryParse(message[(message.IndexOf("elapsed-ms=", StringComparison.Ordinal) + 11)..], out var elapsed));
        Assert.True(elapsed >= 0); // Stopwatch duration stays observable; fake time is only the scheduling clock.
    }

    sealed class World : IAsyncDisposable
    {
        const string Repository = "owner/repository";
        public SteppedClock Clock { get; } = new(new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero));
        public RecordingLog Log { get; } = new();
        public InMemoryDiscussionStore Durable { get; } = new();
        public InMemoryControllerState State { get; } = new();
        public FakeAgentRuntime Native { get; } = new();
        public InMemoryDiscussions GitHub { get; }
        public HeldRuntime Runtime { get; }
        public HeldReceipts Discussions { get; }
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        public CancellationTokenSource Activity { get; } = CancellationTokenSource.CreateLinkedTokenSource(TestToken);
        public GitHubOutbox Outbox { get; }
        readonly ControllerPolling driver;
        Task<int>? running;

        public World(bool holdAcknowledgement = false, bool holdReceipt = false)
        {
            GitHub = new(Clock);
            Runtime = new(Native, holdAcknowledgement);
            Discussions = new(GitHub, holdReceipt);
            var sessions = new AgentSessions(Runtime, Log);
            var settings = new RepositorySettingsRefresh(RepositorySettingsCatalogue.Settings, null, State, Clock, Log);
            Outbox = new(Repository, Discussions, InMemoryDiscussions.App, State, Clock, Log);
            var bridge = new Bridge(Repository, Discussions, InMemoryDiscussions.App, sessions, new StateStore(Durable, Repository),
                Outbox, [], settings, new AgentInputJson(), Log, Clock);
            driver = new(bridge, Outbox, Clock, Log, Interval);
        }

        public IReadOnlyList<string> Polls => [.. Log.Entries.Where(entry => entry.Kind == "poll-completed").Select(entry => entry.Message)];
        public DiscussionRecord Saved(int number) => Durable.Saved!.Discussions["D" + number];
        public void Open(int number) => GitHub.Open(number, "@test-app[bot] please observe fixture " + number);
        public void QueueReceipt() => Outbox.Receipt("retained", "https://github.com/owner/repository/discussions/99", false);
        public Task<int> Run(bool once = false) => running = driver.Run(once, Stop.Token, Activity.Token);

        public async ValueTask DisposeAsync()
        {
            await Stop.CancelAsync();
            await Activity.CancelAsync();
            Runtime.Release.TrySetResult();
            Discussions.Release.TrySetResult();
            try
            {
                if (running is not null)
                    try { await Bounded(running); }
                    catch (OperationCanceledException) { }
            }
            finally
            {
                await Outbox.DisposeAsync();
                Activity.Dispose();
                Stop.Dispose();
            }
        }
    }

    // Only the acknowledgement is held; real fake-native request/event behavior remains unchanged.
    sealed class HeldRuntime(IAgentRuntime inner, bool hold) : IAgentRuntime
    {
        bool entered;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken RequestToken { get; private set; }
        public event Action<AgentRuntimeEvent>? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }
        public Task Restart(Action<int> whileStopped, CancellationToken ct) => inner.Restart(whileStopped, ct);
        public Task<(string SessionId, AgentSessionStatus Status)> StartSession(CancellationToken ct) => inner.StartSession(ct);
        public Task<AgentSessionStatus> ResumeSession(string sessionId, CancellationToken ct) => inner.ResumeSession(sessionId, ct);
        public Task<(string? Model, string? Effort)> ConfiguredModel(CancellationToken ct) => inner.ConfiguredModel(ct);
        public Task<IReadOnlyList<AgentModel>> Models(CancellationToken ct) => inner.Models(ct);
        public async Task<AgentTurnAccepted> StartTurn(AgentTurnRequest request, CancellationToken ct)
        {
            var accepted = await inner.StartTurn(request, ct);
            if (hold && !entered)
            {
                entered = true;
                RequestToken = ct;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return accepted;
        }
    }

    // A typed external write rendezvous; every query and receipt effect still belongs to InMemoryDiscussions.
    sealed class HeldReceipts(IDiscussions inner, bool hold) : IDiscussions
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ReceiptToken { get; private set; }
        public Task<IReadOnlyList<Discussion>> List(CancellationToken ct) => inner.List(ct);
        public Task<Discussion?> Find(int number, CancellationToken ct) => inner.Find(number, ct);
        public Task<DiscussionConversation> Read(Discussion discussion, CancellationToken ct) => inner.Read(discussion, ct);
        public Task<IReadOnlyList<string>> Labels(string discussionId, CancellationToken ct) => inner.Labels(discussionId, ct);
        public Task<bool> HasEyesReceipt(string subjectId, CancellationToken ct) => inner.HasEyesReceipt(subjectId, ct);
        public Task<DashboardLocation> DashboardCategory(string name, CancellationToken ct) => inner.DashboardCategory(name, ct);
        public IAsyncEnumerable<CategoryDiscussion> CategoryDiscussions(DashboardLocation at, CancellationToken ct) => inner.CategoryDiscussions(at, ct);
        public Task<DashboardDiscussion> CreateDashboard(DashboardLocation at, string title, string body, CancellationToken ct) => inner.CreateDashboard(at, title, body, ct);
        public Task UpdateDashboard(string id, string title, string body, CancellationToken ct) => inner.UpdateDashboard(id, title, body, ct);
        public async Task AddEyesReceipt(string subjectId, CancellationToken ct)
        {
            if (hold)
            {
                ReceiptToken = ct;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            await inner.AddEyesReceipt(subjectId, ct);
        }
    }
}
