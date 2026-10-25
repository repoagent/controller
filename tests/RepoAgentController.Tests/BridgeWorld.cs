using System.Text.Json.Nodes;
using Xunit;

namespace RepoAgentController.Tests;

/// <summary>
/// The bridge in-process as the controller composes it, across controller lifetimes: the real Bridge, AgentSessions,
/// RepositorySettings, GitHubOutbox and ControllerStatus over state files in a temporary directory, the readable Discussions
/// fake, and a new agent runtime fake for each lifetime, as each lifetime owns its runtime process. Time moves only when a test
/// advances it. What a loop would do on its next wake is a call here: <see cref="Dispatch"/> for the dispatch loop,
/// <see cref="Deliver"/> for the outbox and <see cref="Refresh"/> for the settings refresh.
/// </summary>
sealed class BridgeWorld : IAsyncDisposable
{
    public const string Repository = "owner/repository";
    public static readonly DateTimeOffset Start = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    readonly string directory = Path.Combine(Path.GetTempPath(), "bridge-" + Guid.NewGuid().ToString("N"));
    readonly List<LogEntry> log = [];
    readonly List<AgentTurnRequest> ended = [];
    readonly Queue<Func<AgentTurnRequest, AgentTurnAccepted>> answers = [];
    readonly List<(string Kind, int Count, TaskCompletionSource Reached)> waiters = [];
    readonly IDisposable observing;
    Lifetime? lifetime;
    (int Sessions, int Turns) counts;
    int mostActive;

    public SteppedClock Clock { get; } = new(Start);
    public InMemoryDiscussions GitHub { get; }
    public InMemoryRepositorySettings Variables { get; } = new();
    /// <summary>Whether lifetimes read the REPOAGENT_ variables, as <c>--repository-settings</c> selects.</summary>
    public bool RepositorySettings { get; set; } = true;
    public string StatePath { get; }
    /// <summary>Prepares each lifetime's agent runtime before the bridge uses it, as the agent's catalogue and configuration.</summary>
    public Action<FakeAgentRuntime>? PrepareRuntime { get; set; }

    /// <summary>Observes the controller log in the creating test's async flow; create it in the test method.</summary>
    public BridgeWorld()
    {
        Directory.CreateDirectory(directory);
        StatePath = Path.Combine(directory, "bridge.json");
        GitHub = new(Clock);
        observing = Log.Observe(entry =>
        {
            lock (log)
            {
                log.Add(entry);
                foreach (var waiter in waiters.Where(waiter => log.Count(logged => logged.Kind == waiter.Kind) >= waiter.Count).ToArray())
                {
                    waiters.Remove(waiter);
                    waiter.Reached.TrySetResult();
                }
            }
            lifetime?.Status.Record(entry);
        });
    }

    sealed record Lifetime(CancellationTokenSource Stop, StateStore Store, ControllerStatus Status, RepositorySettings Settings, GitHubOutbox Outbox,
        FakeAgentRuntime Runtime, AgentSessions Sessions)
    {
        public Bridge Bridge { get; set; } = null!;
    }

    Lifetime Current => lifetime ?? throw new InvalidOperationException("No controller lifetime is running");
    public CancellationToken Token => Current.Stop.Token;
    public FakeAgentRuntime Runtime => Current.Runtime;
    public AgentSessions Sessions => Current.Sessions;
    public Bridge Bridge => Current.Bridge;
    public GitHubOutbox Outbox => Current.Outbox;

    /// <summary>
    /// Starts a controller lifetime, ending the running one: state, settings and outbox are read again from their files, the
    /// runtime process is new, and deliveries an earlier lifetime left unacknowledged are retried.
    /// </summary>
    public async Task Begin(params int[] enroll)
    {
        await End();
        var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var runtime = new FakeAgentRuntime(counts.Sessions, counts.Turns);
        PrepareRuntime?.Invoke(runtime);
        while (answers.TryDequeue(out var answer)) runtime.Answer(answer);
        var current = new Lifetime(stop, new StateStore(StatePath, Repository), new ControllerStatus(StatePath, Repository, "polling", Clock),
            new RepositorySettings(RepositorySettingsCatalogue.Settings, RepositorySettings ? Variables : null, new ControllerStateFiles(StatePath), Clock, new ControllerLog()),
            new GitHubOutbox(Repository, GitHub, InMemoryDiscussions.App, new ControllerStateFiles(StatePath), Clock, new ControllerLog()),
            runtime, new AgentSessions(runtime, new ControllerLog()));
        lifetime = current;
        current.Sessions.TurnsChanged += current.Status.Running;
        current.Sessions.TurnsChanged += running => { lock (log) mostActive = Math.Max(mostActive, running.Length); };
        await current.Settings.Refresh(stop.Token);
        current.Bridge = new(Repository, GitHub, InMemoryDiscussions.App, current.Sessions, current.Store, current.Outbox, [.. enroll], current.Settings, time: Clock);
        current.Bridge.ResumeInterrupted();
    }

    /// <summary>Ends the running lifetime as a stopping controller does: work in flight is cancelled and its files are released.</summary>
    public async Task End()
    {
        if (lifetime is not { } ending) return;
        lifetime = null;
        counts = ending.Runtime.Counts;
        ended.AddRange(ending.Runtime.Requests);
        await ending.Stop.CancelAsync();
        await ending.Outbox.DisposeAsync();
        ending.Status.Dispose();
        ending.Store.Dispose();
        ending.Stop.Dispose();
    }

    /// <summary>The running lifetime begins to stop: work in flight sees its cancellation.</summary>
    public void Interrupt() => Current.Stop.Cancel();

    /// <summary>A <c>--once</c> lifetime: one observation and one delivery attempt. True when both succeeded, as exit code 0 says.</summary>
    public async Task<bool> RunOnce(params int[] enroll)
    {
        await Begin(enroll);
        try { return await Poll() & await Deliver(); }
        finally { await End(); }
    }

    /// <summary>Answers the next turn request, of this lifetime or the next one to start.</summary>
    public void Answer(Func<AgentTurnRequest, AgentTurnAccepted> answer)
    {
        if (lifetime is null) answers.Enqueue(answer);
        else Runtime.Answer(answer);
    }

    public Task<bool> Poll() => Bridge.Poll(Token);

    /// <summary>Observes one Discussion by number, as a webhook delivery does.</summary>
    public Task<bool> Observe(int number) => Bridge.Observe(number, Token);

    /// <summary>The dispatch loop's next pass, as a status change, a settings change or a due retry wakes it.</summary>
    public Task<TimeSpan> Dispatch() => Bridge.DispatchReady(Token);

    /// <summary>One attempt of each due outbox operation; true when nothing failed and nothing is retained.</summary>
    public Task<bool> Deliver() => Outbox.Flush(Token);

    /// <summary>The settings refresh loop's next pass: a refresh, then category enrollment reconciliation after a change.</summary>
    public async Task Refresh()
    {
        if (await Current.Settings.Refresh(Token)) await Bridge.ReconcileCategoryEnrollment(Token);
    }

    /// <summary>The agent ends a turn in pinned Codex's order: the session's status first, then the turn's completion.</summary>
    public void Finish(string session, string turn, bool failed = false, string? rejection = null)
    {
        Runtime.Report(session, failed ? AgentSessionStatus.SystemError : AgentSessionStatus.Idle);
        Runtime.Completed(session, turn, failed ? AgentTurnStatus.Failed : AgentTurnStatus.Completed, rejection);
    }

    /// <summary>Ends the turn the Discussion's saved record shows running.</summary>
    public void Finish(int number, bool failed = false)
    {
        var (session, turn) = Running(number);
        Finish(session, turn, failed);
    }

    public (string Session, string Turn) Running(int number) =>
        (Record(number)!["sessionId"]!.GetValue<string>(), Record(number)!["running"]!["turn"]!.GetValue<string>());

    /// <summary>Every turn request of every lifetime so far, in order.</summary>
    public IReadOnlyList<AgentTurnRequest> Turns => [.. ended, .. lifetime?.Runtime.Requests ?? []];

    public JsonNode Envelope(int turn) => JsonNode.Parse(Turns[turn].Input)!;

    /// <summary>The subject ID of each change an agent input carries, in order.</summary>
    public static string[] Subjects(JsonNode envelope) => [.. envelope["changes"]!.AsArray().Select(change => change!["subject"]!["id"]!.GetValue<string>())];

    /// <summary>The most agent sessions that ran a turn at once.</summary>
    public int MostActive { get { lock (log) return mostActive; } }

    /// <summary>The message of each log entry of <paramref name="kind"/>, in order.</summary>
    public IReadOnlyList<string> Logged(string kind) { lock (log) return [.. log.Where(entry => entry.Kind == kind).Select(entry => entry.Message)]; }

    /// <summary>Completes once <paramref name="count"/> entries of <paramref name="kind"/> are logged, as a loop on another thread
    /// logs the end of its work.</summary>
    public Task WhenLogged(string kind, int count)
    {
        lock (log)
        {
            if (log.Count(entry => entry.Kind == kind) >= count) return Task.CompletedTask;
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((kind, count, reached));
            return reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The Discussion's record as the state file holds it.</summary>
    public JsonObject? Record(int number) => JsonNode.Parse(File.ReadAllText(StatePath))!["discussions"]!["D" + number] as JsonObject;

    /// <summary>The Discussion IDs with an agent session, as the state file holds them.</summary>
    public IReadOnlyList<string> WithSessions => [.. JsonNode.Parse(File.ReadAllText(StatePath))!["discussions"]!.AsObject()
        .Where(entry => entry.Value!["sessionId"]?.GetValue<string>() is { Length: > 0 }).Select(entry => entry.Key).Order()];

    /// <summary>The operator status of the running lifetime, or as the last one left it.</summary>
    public JsonObject Status() => lifetime?.Status.Snapshot() ?? ControllerStatus.Read(StatePath, Clock)!;

    /// <summary>The numbers of the Discussions with input waiting, as status reports them.</summary>
    public int[] Waiting()
    {
        var status = Status();
        var waiting = status["sessions"]!.AsArray().Where(session => session!["waiting"] is not null).ToArray();
        Assert.Equal(waiting.Sum(session => session!["waiting"]!.GetValue<int>()), status["work"]!["waiting"]!.GetValue<int>());
        return [.. waiting.Select(session => int.Parse(session!["discussion"]!.GetValue<string>().Split('/')[^1]))];
    }

    public static string Url(int number) => "https://github.com/owner/repository/discussions/" + number;

    public async ValueTask DisposeAsync()
    {
        await End();
        observing.Dispose();
        Directory.Delete(directory, true);
    }
}
