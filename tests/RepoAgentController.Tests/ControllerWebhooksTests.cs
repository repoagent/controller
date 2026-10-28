using Xunit;

namespace RepoAgentController.Tests;

// Authenticated webhook decisions through the real bridge and its durable output, without HTTP, processes or provider JSON.
public sealed class ControllerWebhooksTests
{
    const string Repository = "owner/repository";
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    static CancellationToken Token => TestContext.Current.CancellationToken;
    static WebhookObservation Delivery(int? number = 1) => new(WebhookChange.Discussion, Guid.NewGuid(), Repository)
    { Action = "created", DiscussionNumber = number, DiscussionAuthor = InMemoryDiscussions.Participant };

    [Fact]
    public async Task An_authenticated_Discussion_is_observed_then_acknowledged_through_the_durable_outbox()
    {
        await using var world = new World();
        world.Start();
        await world.Observed("reconciliation-completed", 1);
        world.Open(1);

        Assert.Equal(WebhookDisposition.Accepted, world.Driver.Accept(Delivery()));
        await world.Observed("webhook-observed", 1);

        Assert.NotNull(world.Saved(1).Running);
        Assert.Null(world.Saved(1).Pending);
        Assert.Single(world.Native.Requests);
        Assert.True(await world.Outbox.Flush(Token));
        Assert.True(await world.GitHub.HasEyesReceipt("D1", Token));
    }

    [Fact]
    public async Task Unrelated_invalid_and_controller_owned_events_do_not_schedule_agent_input()
    {
        await using var world = new World();
        (WebhookObservation Notice, WebhookDisposition Expected)[] cases =
        [
            (Delivery() with { Repository = "other/repository" }, WebhookDisposition.WrongRepository),
            (Delivery() with { DeliveryId = null }, WebhookDisposition.Invalid),
            (Delivery() with { Change = WebhookChange.Ping }, WebhookDisposition.Ignored),
            (Delivery() with { Change = WebhookChange.Other }, WebhookDisposition.Ignored),
            (Delivery() with { Change = WebhookChange.Push }, WebhookDisposition.Ignored),
            (Delivery() with { Action = "Created!" }, WebhookDisposition.Invalid),
            (Delivery(null), WebhookDisposition.Invalid),
            (Delivery(0), WebhookDisposition.Invalid),
            (Delivery() with { DiscussionAuthor = InMemoryDiscussions.AppAccount, DiscussionBody = AppIdentity.DashboardMarker }, WebhookDisposition.Ignored),
            (Delivery() with { Change = WebhookChange.Comment, Sender = InMemoryDiscussions.AppAccount }, WebhookDisposition.Ignored)
        ];
        foreach (var (notice, expected) in cases) Assert.Equal(expected, world.Driver.Accept(notice));
        world.Start();
        await world.Observed("reconciliation-completed", 1);
        Assert.Empty(world.Native.Requests);
        Assert.DoesNotContain(world.Log.Entries, entry => entry.Kind == "webhook-observed");
    }

    [Fact]
    public async Task Pending_notifications_for_the_same_Discussion_coalesce_into_one_observation()
    {
        await using var world = new World();
        world.Open(1);
        Assert.Equal(WebhookDisposition.Accepted, world.Driver.Accept(Delivery()));
        Assert.Equal(WebhookDisposition.Accepted, world.Driver.Accept(Delivery()));
        world.Start();
        await world.Observed("reconciliation-completed", 1);
        Assert.Single(world.Log.Entries, entry => entry.Kind == "webhook-observed");
        Assert.Single(world.Native.Requests);
        Assert.NotNull(world.Saved(1).Running);
    }

    [Fact]
    public async Task A_notification_during_an_observation_reads_again_and_keeps_later_input_waiting()
    {
        await using var world = new World();
        world.Start();
        await world.Observed("reconciliation-completed", 1);
        world.Open(1);
        world.Native.Answer(_ =>
        {
            world.GitHub.Comment(1, "DC_later", "please retain this later input");
            Assert.Equal(WebhookDisposition.Accepted, world.Driver.Accept(Delivery() with { Change = WebhookChange.Comment }));
            return new("T1", false);
        });

        Assert.Equal(WebhookDisposition.Accepted, world.Driver.Accept(Delivery()));
        await world.Observed("webhook-observed", 2);

        Assert.Single(world.Native.Requests);
        Assert.NotNull(world.Saved(1).Running);
        Assert.Single(world.Saved(1).Waiting!.Inputs);
        Assert.Equal(2, world.Log.Entries.Count(entry => entry.Kind == "webhook-observed"));
    }

    [Fact]
    public async Task Reconciliation_waits_for_the_injected_interval_and_stops_scheduling_on_shutdown()
    {
        await using var world = new World();
        world.Start();
        await world.Observed("reconciliation-completed", 1);
        await world.Clock.Armed();
        world.Open(2);
        world.Clock.Advance(Interval - TimeSpan.FromTicks(1));
        Assert.Empty(world.Native.Requests);
        world.Clock.Advance(TimeSpan.FromTicks(1));
        await world.Observed("reconciliation-completed", 2);
        Assert.Single(world.Native.Requests);
        await world.Stop.CancelAsync();
        await world.End();
        world.Clock.Advance(Interval);
        Assert.Equal(2, world.Log.Entries.Count(entry => entry.Kind == "reconciliation-completed"));
        Assert.True(world.Registration.Deactivated);
    }

    [Fact]
    public async Task Ordinary_stop_preserves_entered_acceptance_then_deactivates_registration()
    {
        await using var world = new World(holdAcceptance: true);
        world.Open(1);
        world.Start();
        await world.Runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await world.Stop.CancelAsync();
        Assert.False(world.Runtime.Activity.IsCancellationRequested);
        Assert.False(world.Running!.IsCompleted);
        world.Runtime.Release.TrySetResult();
        await world.End();
        Assert.NotNull(world.Saved(1).Running);
        Assert.Single(world.Native.Requests);
        Assert.True(world.Registration.Deactivated);
    }

    [Fact]
    public async Task A_registration_failure_deactivates_without_starting_observation()
    {
        await using var world = new World();
        world.Registration.Failure = new IOException("registration unconfirmed");
        await Assert.ThrowsAsync<IOException>(() => world.Driver.Run(Token, Token));
        Assert.True(world.Registration.Deactivated);
        Assert.Empty(world.Native.Requests);
        Assert.DoesNotContain(world.Log.Entries, entry => entry.Kind == "ready");
    }

    [Fact]
    public async Task A_push_only_persists_a_bound_default_ref_hint_without_touching_the_checkout()
    {
        await using var world = new World(managed: true);
        var push = Delivery() with { Change = WebhookChange.Push, RepositoryId = 345, PushedRef = "refs/heads/main" };
        Assert.Equal(WebhookDisposition.WrongRepository, world.Driver.Accept(push with { RepositoryId = 999 }));
        Assert.Equal(WebhookDisposition.Invalid, world.Driver.Accept(push with { PushedRef = "bad\nref" }));
        Assert.Equal(WebhookDisposition.Ignored, world.Driver.Accept(push with { PushedRef = "refs/heads/other" }));
        Assert.Equal(0, world.Journal.Saved!.Requested);
        Assert.Equal(WebhookDisposition.Accepted, world.Driver.Accept(push));
        Assert.Equal(1, world.Journal.Saved.Requested);
        Assert.Equal(0, world.Checkout.Refreshes);
        Assert.Empty(world.Native.Requests);
    }

    [Fact]
    public async Task A_failed_read_does_not_hold_later_notifications_in_the_coalescing_set()
    {
        await using var world = new World();
        world.Start();
        await world.Observed("reconciliation-completed", 1);
        world.Open(1);
        world.GitHub.FailReads("Find", new IOException("read unconfirmed"));
        Assert.Equal(WebhookDisposition.Accepted, world.Driver.Accept(Delivery()));
        await world.Observed("webhook-observed", 1);
        Assert.Empty(world.Native.Requests);
        Assert.Contains(world.Log.Entries, entry => entry.Kind == "webhook-observed" && entry.Message.Contains("success=False", StringComparison.Ordinal));
        world.GitHub.FailReads("Find", null);
        Assert.Equal(WebhookDisposition.Accepted, world.Driver.Accept(Delivery()));
        await world.Observed("webhook-observed", 2);
        Assert.Single(world.Native.Requests);
        Assert.NotNull(world.Saved(1).Running);
    }
    sealed class World : IAsyncDisposable
    {
        public SteppedClock Clock { get; } = new(new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero));
        public RecordingLog Log { get; } = new();
        public InMemoryDiscussionStore Durable { get; } = new();
        public InMemoryControllerState State { get; } = new();
        public FakeAgentRuntime Native { get; } = new();
        public InMemoryDiscussions GitHub { get; }
        public Registration Registration { get; } = new();
        public InMemoryRefreshJournal Journal { get; } = new();
        public ScriptedCheckout Checkout { get; } = new();
        public GateRuntime Runtime { get; }
        public CancellationTokenSource Stop { get; } = CancellationTokenSource.CreateLinkedTokenSource(Token);
        public ControllerWebhooks Driver { get; }
        public GitHubOutbox Outbox { get; }
        public Task? Running { get; private set; }
        readonly CheckoutRefresh? refresh;

        public World(bool holdAcceptance = false, bool managed = false)
        {
            GitHub = new(Clock);
            Runtime = new(Native, holdAcceptance);
            var sessions = new AgentSessions(Runtime, Log);
            var settings = new RepositorySettings(RepositorySettingsCatalogue.Settings, null, State, Clock, Log);
            Outbox = new(Repository, GitHub, InMemoryDiscussions.App, State, Clock, Log);
            var bridge = new Bridge(Repository, GitHub, InMemoryDiscussions.App, sessions, new StateStore(Durable, Repository),
                Outbox, [], settings, new AgentInputJson(), Log, Clock);
            if (managed) refresh = new(Checkout, new("/workspace/repository", "owner", 345, "refs/heads/main"), Journal, false, false, Clock, Log);
            Driver = new(Repository, InMemoryDiscussions.App, bridge, Registration, new("https://fixture.invalid/github/webhook"),
                8000, Interval, Clock, Log, refresh);
        }

        public void Open(int number) => GitHub.Open(number, "@test-app[bot] please observe this fixture");
        public DiscussionRecord Saved(int number) => Durable.Saved!.Discussions["D" + number];
        public void Start() => Running = Driver.Run(Stop.Token, Token);
        public async Task Observed(string kind, int count)
        {
            while (Log.Entries.Count(entry => entry.Kind == kind) < count) await Log.Written(Log.Entries.Count + 1);
        }
        public async Task End()
        {
            if (Running is null) return;
            try { await Running.WaitAsync(TimeSpan.FromSeconds(10), Token); }
            catch (OperationCanceledException) when (Stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            await Stop.CancelAsync();
            Runtime.Release.TrySetResult();
            await End();
            Driver.Dispose();
            if (refresh is not null) await refresh.DisposeAsync();
            await Outbox.DisposeAsync();
            Stop.Dispose();
        }
    }

    sealed class Registration : IWebhookRegistration
    {
        public string Secret => "fixture-secret";
        public bool Deactivated { get; private set; }
        public IOException? Failure { get; set; }
        public Task Configure(Uri endpoint, bool push, CancellationToken ct) => Failure is { } error ? Task.FromException(error) : Task.CompletedTask;
        public Task<bool> Deactivate() { Deactivated = true; return Task.FromResult(true); }
    }

    sealed class GateRuntime(IAgentRuntime inner, bool hold) : IAgentRuntime
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Activity { get; private set; }
        public event Action<AgentRuntimeEvent>? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }
        public Task Restart(Action<int> whileStopped, CancellationToken ct) => inner.Restart(whileStopped, ct);
        public Task<(string SessionId, AgentSessionStatus Status)> StartSession(CancellationToken ct) => inner.StartSession(ct);
        public Task<AgentSessionStatus> ResumeSession(string sessionId, CancellationToken ct) => inner.ResumeSession(sessionId, ct);
        public Task<(string? Model, string? Effort)> ConfiguredModel(CancellationToken ct) => inner.ConfiguredModel(ct);
        public Task<IReadOnlyList<NativeModel>> Models(CancellationToken ct) => inner.Models(ct);
        public async Task<AgentTurnAccepted> StartTurn(AgentTurnRequest request, CancellationToken ct)
        {
            if (hold)
            {
                Activity = ct;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return await inner.StartTurn(request, ct);
        }
    }
}
