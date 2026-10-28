using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace RepoAgentController.Tests;

/// <summary>Current GitHub authority and renewal decisions, without files, sockets, HTTP or processes.</summary>
public sealed class GitHubTokenLifetimeTests
{
    static readonly DateTimeOffset Start = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public async Task A_token_is_renewed_at_the_five_minute_boundary_and_not_before(long ticks, bool renews)
    {
        using var world = new World();
        await world.Initialize();
        world.Clock.Advance(TimeSpan.FromMinutes(55) + TimeSpan.FromTicks(ticks));

        var held = await world.Lifetime.GetCredential(Token);

        Assert.Equal(renews ? "token-2" : "token-1", held.Token);
        Assert.Equal(renews ? 2 : 1, world.Source.Acquisitions);
        Assert.Equal(("owner/repository", "polling-clone"), (held.Repository, held.Profile));
    }

    [Fact]
    public async Task Initialization_binds_the_identity_and_scope_without_exposing_an_uninitialized_token()
    {
        using var world = new World();
        Assert.Equal("", world.Lifetime.AgentLogin);
        Assert.Null(world.Lifetime.ForAgent());
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Lifetime.GetToken(Token));
        Assert.Equal(0, world.Source.Acquisitions);

        await world.Initialize();

        Assert.Equal(234, world.Source.ExpectedInstallation);
        Assert.Equal("test-app[bot]", world.Lifetime.AgentLogin);
        Assert.Equal(345, world.Lifetime.RepositoryId);
        Assert.Equal("settings not granted", world.Lifetime.SettingsUnreadable);
        Assert.Same(await world.Lifetime.GetCredential(Token), world.Lifetime.ForAgent());
        Assert.Equal(("app-token-refreshed", $"actor=test-app[bot] expires={Start.AddHours(1):O}"), Assert.Single(world.Log.Entries));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public async Task Agent_handoff_requires_more_than_thirty_seconds_and_never_renews(long ticks, bool available)
    {
        using var world = new World();
        await world.Initialize();
        world.Clock.Advance(TimeSpan.FromMinutes(59.5) + TimeSpan.FromTicks(ticks));

        Assert.Equal(available, world.Lifetime.ForAgent() is not null);
        Assert.Equal(1, world.Source.Acquisitions);
    }

    [Fact]
    public async Task Concurrent_renewals_share_one_acquisition_and_one_current_token()
    {
        using var world = new World(finalFailure: true);
        await world.Initialize();
        world.Clock.Advance(TimeSpan.FromMinutes(55));
        var acquired = new TaskCompletionSource<GitHubTokenLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Source.AcquireNext = ct => acquired.Task.WaitAsync(ct);

        var requests = Enumerable.Range(0, 8).Select(_ => world.Lifetime.GetCredential(Token)).ToArray();
        Assert.Equal(2, world.Source.Acquisitions);
        acquired.SetResult(new("renewed", world.Clock.GetUtcNow().AddHours(1)));
        var tokens = await Task.WhenAll(requests);

        Assert.Equal(2, world.Source.Acquisitions);
        Assert.All(tokens, token => Assert.Same(tokens[0], token));
        Assert.Same(tokens[0], world.Lifetime.ForAgent());
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_renewal_neither_acquires_nor_invalidates_authority()
    {
        using var world = new World(finalFailure: true);
        await world.Initialize();
        world.Clock.Advance(TimeSpan.FromMinutes(55));
        var acquired = new TaskCompletionSource<GitHubTokenLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Source.AcquireNext = ct => acquired.Task.WaitAsync(ct);
        var owner = world.Lifetime.GetCredential(Token);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var cancelled = world.Lifetime.GetCredential(waiting.Token);
        await waiting.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(2, world.Source.Acquisitions);
        Assert.False(world.Lifetime.AuthorityLost.IsCancellationRequested);
        Assert.Equal("token-1", world.Lifetime.ForAgent()!.Token);
        acquired.SetResult(new("renewed", world.Clock.GetUtcNow().AddHours(1)));
        Assert.Equal("renewed", (await owner).Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_renewal_clears_the_token_and_only_recoverable_authority_can_acquire_again(bool finalFailure)
    {
        using var world = new World(finalFailure);
        await world.Initialize();
        world.Clock.Advance(TimeSpan.FromMinutes(55));
        world.Source.AcquireNext = _ => Task.FromException<GitHubTokenLease>(new IOException("provider unavailable"));
        var signalled = false;
        var unavailableWhenSignalled = false;
        using var loss = world.Lifetime.AuthorityLost.Register(() =>
        {
            signalled = true;
            unavailableWhenSignalled = world.Lifetime.ForAgent() is null;
        });

        await Assert.ThrowsAsync<IOException>(() => world.Lifetime.GetCredential(Token));

        Assert.Null(world.Lifetime.ForAgent());
        world.Source.AcquireNext = null;
        if (finalFailure)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => world.Lifetime.GetCredential(Token));
            Assert.Equal("Broker credential renewal failed; this controller lifetime cannot resume. Check authorization and pending recovery state before restarting.", error.Message);
            Assert.Equal(2, world.Source.Acquisitions);
            using var restarted = new World(finalFailure: true);
            await restarted.Initialize();
            Assert.NotNull(restarted.Lifetime.ForAgent());
        }
        else
        {
            Assert.Equal("token-3", await world.Lifetime.GetToken(Token));
            Assert.Equal(3, world.Source.Acquisitions);
            Assert.False(world.Lifetime.AuthorityLost.CanBeCanceled);
        }
        Assert.Equal(finalFailure, world.Lifetime.AuthorityLost.IsCancellationRequested);
        Assert.Equal(finalFailure, signalled);
        Assert.Equal(finalFailure, unavailableWhenSignalled);
    }

    [Fact]
    public async Task Throttled_renewal_keeps_the_live_token_and_coordinates_retry_before_recovery()
    {
        using var world = new World(finalFailure: true);
        await world.Initialize();
        world.Clock.Advance(TimeSpan.FromMinutes(56));
        world.Source.AcquireNext = _ => Task.FromException<GitHubTokenLease>(new GitHubTokenThrottledException(TimeSpan.FromMinutes(1)));

        Assert.Equal("token-1", await world.Lifetime.GetToken(Token));
        Assert.False(world.Lifetime.AuthorityLost.IsCancellationRequested);
        world.Source.AcquireNext = null;
        var callers = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => world.Lifetime.GetToken(Token)));
        Assert.All(callers, token => Assert.Equal("token-1", token));
        Assert.Equal(2, world.Source.Acquisitions);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("token-3", await world.Lifetime.GetToken(Token));
        Assert.Equal("token-3", world.Lifetime.ForAgent()!.Token);
    }

    [Fact]
    public async Task A_long_throttle_cannot_keep_owned_authority_past_the_held_tokens_safety_cutoff()
    {
        using var world = new World(finalFailure: true);
        await world.Initialize();
        world.Clock.Advance(TimeSpan.FromMinutes(56));
        world.Source.AcquireNext = _ => Task.FromException<GitHubTokenLease>(new GitHubTokenThrottledException(TimeSpan.FromDays(1)));
        Assert.Equal("token-1", await world.Lifetime.GetToken(Token));

        world.Clock.Advance(TimeSpan.FromMinutes(3.5));

        Assert.True(world.Lifetime.AuthorityLost.IsCancellationRequested);
        Assert.Null(world.Lifetime.ForAgent());
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Lifetime.GetToken(Token));
        Assert.Equal(2, world.Source.Acquisitions);
    }

    [Fact]
    public async Task Startup_throttling_waits_for_the_shared_retry_instead_of_exiting_and_can_be_cancelled()
    {
        using var world = new World(finalFailure: true);
        world.Source.AcquireNext = _ => Task.FromException<GitHubTokenLease>(new GitHubTokenThrottledException(TimeSpan.FromMinutes(1)));
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var startup = world.Lifetime.Initialize(234, stopping.Token);
        Assert.False(startup.IsCompleted);
        Assert.Null(world.Lifetime.ForAgent());
        Assert.False(world.Lifetime.AuthorityLost.IsCancellationRequested);
        world.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(1, world.Source.Acquisitions);
        await stopping.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
        Assert.False(world.Lifetime.AuthorityLost.IsCancellationRequested);
    }

    [Fact]
    public async Task A_rejected_held_token_is_never_returned_during_throttle_and_renewal_disarms_old_expiry()
    {
        using var world = new World(finalFailure: true);
        await world.Initialize();
        world.Clock.Advance(TimeSpan.FromMinutes(56));
        world.Source.AcquireNext = _ => Task.FromException<GitHubTokenLease>(new GitHubTokenThrottledException(TimeSpan.FromMinutes(1)));
        await world.Lifetime.GetToken(Token);
        world.Lifetime.Reject("token-1");
        Assert.Null(world.Lifetime.ForAgent());
        world.Source.AcquireNext = null;
        var waiting = world.Lifetime.GetToken(Token);
        Assert.False(waiting.IsCompleted);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("token-3", await waiting);
        world.Clock.Advance(TimeSpan.FromMinutes(3));
        Assert.False(world.Lifetime.AuthorityLost.IsCancellationRequested);
        Assert.Equal("token-3", world.Lifetime.ForAgent()!.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cutoff_log_failure_cannot_prevent_authority_loss_or_escape_the_timer(bool throttleLogFails)
    {
        var clock = new FakeTimeProvider(Start);
        using var source = new Source(clock, finalFailure: true);
        using var lifetime = new GitHubTokenLifetime(source, clock, new FailingCutoffLog(throttleLogFails));
        using var observer = lifetime.AuthorityLost.Register(() => throw new InvalidOperationException("observer failed after cancellation"));
        await lifetime.Initialize(234, Token);
        clock.Advance(TimeSpan.FromMinutes(56));
        source.AcquireNext = _ => Task.FromException<GitHubTokenLease>(new GitHubTokenThrottledException(TimeSpan.FromDays(1)));
        await lifetime.GetToken(Token);

        var escaped = Record.Exception(() => clock.Advance(TimeSpan.FromMinutes(3.5)));

        Assert.Null(escaped);
        Assert.True(lifetime.AuthorityLost.IsCancellationRequested);
        Assert.Null(lifetime.ForAgent());
    }

    sealed class FailingCutoffLog(bool throttleLogFails) : IControllerLog
    {
        public void Write(string kind, string message)
        {
            if (kind == "app-refresh-error" || throttleLogFails && kind == "app-token-throttled") throw new IOException("status disk unavailable");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_late_acquisition_cannot_publish_after_caller_cancellation_or_expiry(bool expiry)
    {
        using var world = new World(finalFailure: true);
        await world.Initialize();
        world.Clock.Advance(TimeSpan.FromMinutes(56));
        world.Source.AcquireNext = _ => Task.FromException<GitHubTokenLease>(new GitHubTokenThrottledException(TimeSpan.FromMinutes(1)));
        await world.Lifetime.GetToken(Token);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        var lease = new TaskCompletionSource<GitHubTokenLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Source.AcquireNext = _ => lease.Task; // A provider may complete despite cancellation.
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var pending = world.Lifetime.GetToken(caller.Token);
        if (expiry) world.Clock.Advance(TimeSpan.FromMinutes(2.5));
        else await caller.CancelAsync();
        lease.SetResult(new("late-token", world.Clock.GetUtcNow().AddHours(1)));

        if (expiry) await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(world.Lifetime.ForAgent());
        Assert.Single(world.Log.Entries, entry => entry.Kind == "app-token-refreshed");
    }

    [Fact]
    public async Task Definitive_failure_after_throttling_still_ends_authority_and_cannot_reuse_the_held_token()
    {
        using var world = new World(finalFailure: true);
        await world.Initialize();
        world.Clock.Advance(TimeSpan.FromMinutes(56));
        world.Source.AcquireNext = _ => Task.FromException<GitHubTokenLease>(new GitHubTokenThrottledException(TimeSpan.FromMinutes(1)));
        await world.Lifetime.GetToken(Token);
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        world.Source.AcquireNext = _ => Task.FromException<GitHubTokenLease>(new InvalidOperationException("credential rejected"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Lifetime.GetToken(Token));

        Assert.True(world.Lifetime.AuthorityLost.IsCancellationRequested);
        Assert.Null(world.Lifetime.ForAgent());
        world.Source.AcquireNext = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Lifetime.GetToken(Token));
        Assert.Equal(3, world.Source.Acquisitions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_is_distinct_from_an_internal_acquisition_timeout(bool callerCancelled)
    {
        using var world = new World(finalFailure: true);
        await world.Initialize();
        world.Clock.Advance(TimeSpan.FromMinutes(55));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var deadline = new CancellationTokenSource();
        world.Source.AcquireNext = _ =>
        {
            if (callerCancelled) caller.Cancel();
            else deadline.Cancel();
            return Task.FromException<GitHubTokenLease>(new OperationCanceledException(callerCancelled ? caller.Token : deadline.Token));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => world.Lifetime.GetCredential(caller.Token));

        Assert.Null(world.Lifetime.ForAgent());
        Assert.Equal(!callerCancelled, world.Lifetime.AuthorityLost.IsCancellationRequested);
        world.Source.AcquireNext = null;
        if (callerCancelled) Assert.Equal("token-3", await world.Lifetime.GetToken(Token));
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => world.Lifetime.GetCredential(Token));
            Assert.Equal(2, world.Source.Acquisitions);
        }
    }

    [Fact]
    public async Task Rejecting_an_old_token_cannot_drop_its_replacement_but_rejecting_the_current_one_does()
    {
        using var world = new World();
        await world.Initialize();
        world.Clock.Advance(TimeSpan.FromMinutes(55));
        var current = await world.Lifetime.GetCredential(Token);

        world.Lifetime.Reject("token-1");
        Assert.Same(current, world.Lifetime.ForAgent());
        world.Lifetime.Reject(current.Token);
        Assert.Null(world.Lifetime.ForAgent());
        Assert.Equal(2, world.Source.Acquisitions);
        Assert.Equal("token-3", await world.Lifetime.GetToken(Token));
    }

    [Fact]
    public async Task Invalidation_revokes_the_held_token_without_a_source_attempt()
    {
        using var world = new World(finalFailure: true);
        await world.Initialize();

        world.Lifetime.Invalidate();

        Assert.Null(world.Lifetime.ForAgent());
        Assert.Equal(1, world.Source.Acquisitions);
        Assert.False(world.Lifetime.AuthorityLost.IsCancellationRequested);
        Assert.Equal("token-2", await world.Lifetime.GetToken(Token));
    }

    [Fact]
    public async Task Disposal_revokes_the_token_and_disposes_its_source()
    {
        var world = new World();
        await world.Initialize();

        world.Dispose();

        Assert.Null(world.Lifetime.ForAgent());
        Assert.True(world.Source.Disposed);
    }

    [Fact]
    public async Task Scheduled_recoverable_failure_is_reported_and_the_next_minute_can_restore_authority()
    {
        var clock = new SteppedClock(Start);
        using var source = new Source(clock, finalFailure: false);
        var log = new RecordingLog();
        using var lifetime = new GitHubTokenLifetime(source, clock, log);
        await lifetime.Initialize(234, Token);
        source.AcquireNext = _ => Task.FromException<GitHubTokenLease>(new IOException("provider unavailable"));
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var loop = lifetime.RefreshUntilCancelled(stopping.Token);
        try
        {
            await clock.Armed();
            clock.Advance(TimeSpan.FromMinutes(55));
            await clock.Armed();
            Assert.Equal(2, source.Acquisitions);
            Assert.Null(lifetime.ForAgent());
            Assert.Contains(("app-refresh-error", "provider unavailable"), log.Entries);
            source.AcquireNext = null;
            clock.Advance(TimeSpan.FromMinutes(1));
            await clock.Armed();
            Assert.Equal("token-3", lifetime.ForAgent()!.Token);
            Assert.Equal(3, source.Acquisitions);
        }
        finally { await stopping.CancelAsync(); await loop; }
        Assert.All(clock.Delays, delay => Assert.Equal(TimeSpan.FromMinutes(1), delay));
    }

    [Fact]
    public async Task Scheduled_final_failure_reports_the_error_and_ends_the_loop_with_authority_lost()
    {
        var clock = new SteppedClock(Start);
        using var source = new Source(clock, finalFailure: true);
        var log = new RecordingLog();
        using var lifetime = new GitHubTokenLifetime(source, clock, log);
        await lifetime.Initialize(234, Token);
        source.AcquireNext = _ => Task.FromException<GitHubTokenLease>(new IOException("provider unavailable"));
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var loop = lifetime.RefreshUntilCancelled(stopping.Token);
        await clock.Armed();
        clock.Advance(TimeSpan.FromMinutes(55));

        await Assert.ThrowsAsync<InvalidOperationException>(() => loop);

        Assert.Equal(2, source.Acquisitions);
        Assert.Null(lifetime.ForAgent());
        Assert.True(lifetime.AuthorityLost.IsCancellationRequested);
        Assert.Contains(("app-refresh-error", "provider unavailable"), log.Entries);
    }

    sealed class World : IDisposable
    {
        public FakeTimeProvider Clock { get; } = new(Start);
        public Source Source { get; }
        public RecordingLog Log { get; } = new();
        public GitHubTokenLifetime Lifetime { get; }
        public World(bool finalFailure = false)
        {
            Source = new(Clock, finalFailure);
            Lifetime = new(Source, Clock, Log);
        }
        public Task Initialize() => Lifetime.Initialize(234, Token);
        public void Dispose() => Lifetime.Dispose();
    }

    sealed class Source(TimeProvider clock, bool finalFailure) : IGitHubTokenSource
    {
        public bool RequiresNewLifetimeOnFailure => finalFailure;
        public string AppSlug => "test-app";
        public long? RepositoryId => 345;
        public string? GitRepository => "owner/repository";
        public string? GitProfile => "polling-clone";
        public string? SettingsUnreadable => "settings not granted";
        public int Acquisitions { get; private set; }
        public long? ExpectedInstallation { get; private set; }
        public bool Disposed { get; private set; }
        public Func<CancellationToken, Task<GitHubTokenLease>>? AcquireNext { get; set; }
        public Task Initialize(long? expectedInstallation, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            ExpectedInstallation = expectedInstallation;
            return Task.CompletedTask;
        }
        public Task<GitHubTokenLease> Acquire(CancellationToken ct)
        {
            Acquisitions++;
            return AcquireNext?.Invoke(ct) ?? Task.FromResult(new GitHubTokenLease("token-" + Acquisitions, clock.GetUtcNow().AddHours(1)));
        }
        public void Dispose() => Disposed = true;
    }
}
