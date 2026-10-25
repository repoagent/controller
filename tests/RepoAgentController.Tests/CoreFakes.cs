using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace RepoAgentController.Tests;

/// <summary>The repository's REPOAGENT_ Actions variables as GitHub holds them. A queued failure answers a read instead.</summary>
sealed class InMemoryRepositorySettings : IRepositorySettings
{
    readonly Lock sync = new();
    readonly Dictionary<string, string> variables = [];
    readonly Dictionary<string, Exception> failures = [];
    readonly List<string> reads = [];

    /// <summary>Sets a variable, or removes it when <paramref name="value"/> is null, and clears any failure of its reads.</summary>
    public void Set(string name, string? value)
    {
        lock (sync)
        {
            failures.Remove(name);
            if (value is null) variables.Remove(name);
            else variables[name] = value;
        }
    }

    /// <summary>Later reads of <paramref name="name"/> fail with <paramref name="failure"/> until it is set again.</summary>
    public void Fail(string name, Exception failure) { lock (sync) failures[name] = failure; }

    /// <summary>Every read so far, in order, by variable name.</summary>
    public IReadOnlyList<string> Reads { get { lock (sync) return [.. reads]; } }

    public Task<string?> Read(string name, CancellationToken ct)
    {
        lock (sync)
        {
            reads.Add(name);
            return failures.TryGetValue(name, out var failure) ? Task.FromException<string?>(failure) : Task.FromResult(variables.GetValueOrDefault(name));
        }
    }
}

/// <summary>repoagent/controller's published baseline releases, in the order GitHub lists them. A set failure answers reads instead.</summary>
sealed class InMemoryBaselineReleases : IBaselineReleases
{
    readonly Lock sync = new();
    readonly List<BaselineRelease> releases = [];
    Exception? failure;
    int reads;

    /// <summary>Publishes a release, listed first as GitHub lists the most recently created release.</summary>
    public void Publish(BaselineRelease release) { lock (sync) releases.Insert(0, release); }

    /// <summary>Later reads fail with <paramref name="error"/>, or succeed again when it is null.</summary>
    public void Fail(Exception? error) { lock (sync) failure = error; }

    public int Reads { get { lock (sync) return reads; } }

    public Task<IReadOnlyList<BaselineRelease>> Published(CancellationToken ct)
    {
        lock (sync)
        {
            reads++;
            return failure is not null ? Task.FromException<IReadOnlyList<BaselineRelease>>(failure) : Task.FromResult<IReadOnlyList<BaselineRelease>>([.. releases]);
        }
    }
}

/// <summary>
/// The repository's Discussions as GitHub holds the App's EYES reactions and dashboards. Each queued failure answers the next write,
/// in order; an applied failure makes the write first, as GitHub does when only its response is lost.
/// </summary>
sealed class InMemoryDiscussions : IDiscussions
{
    public const string RepositoryId = "R_repository";
    readonly Lock sync = new();
    readonly Dictionary<string, string> categories = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Dashboard> dashboards = [];
    readonly HashSet<string> eyes = [];
    readonly Queue<(GitHubRequestException Error, bool Applied)> failures = [];
    readonly List<string> calls = [];

    /// <summary>A Discussion the App authored as its dashboard, as GitHub holds it now.</summary>
    public sealed record Dashboard(string Id, string Url, string CategoryId, string Title, string Body);

    /// <summary>Adds a Discussion category; returns its ID.</summary>
    public string Category(string name)
    {
        lock (sync) return categories.TryGetValue(name, out var id) ? id : categories[name] = "DIC_" + name.Replace(' ', '_');
    }

    /// <summary>A dashboard an earlier lifetime created in <paramref name="category"/>.</summary>
    public Dashboard Created(string category, string title, string body)
    {
        lock (sync) return Add(Category(category), title, body);
    }

    /// <summary>The next write fails with <paramref name="error"/>, after taking effect when <paramref name="applied"/>.</summary>
    public void Fail(GitHubRequestException error, bool applied = false) { lock (sync) failures.Enqueue((error, applied)); }

    /// <summary>Every request so far, in order, as its GraphQL field and subject, such as <c>addReaction D1</c>.</summary>
    public IReadOnlyList<string> Calls { get { lock (sync) return [.. calls]; } }

    /// <summary>How many times the App wrote its EYES reaction to <paramref name="subject"/>.</summary>
    public int Writes(string subject) => Calls.Count(call => call == "addReaction " + subject);

    public bool HasEyes(string subject) { lock (sync) return eyes.Contains(subject); }

    public IReadOnlyList<Dashboard> Dashboards { get { lock (sync) return [.. dashboards]; } }

    public Task AddEyesReceipt(string subjectId, CancellationToken ct) => Write("addReaction " + subjectId, () => eyes.Add(subjectId));

    public Task<bool> HasEyesReceipt(string subjectId, CancellationToken ct)
    {
        lock (sync)
        {
            calls.Add("reactionGroups " + subjectId);
            return Task.FromResult(eyes.Contains(subjectId));
        }
    }

    public Task<DashboardLocation> DashboardCategory(string name, CancellationToken ct)
    {
        lock (sync)
        {
            calls.Add("discussionCategories " + name);
            return categories.TryGetValue(name, out var id) ? Task.FromResult(new DashboardLocation(RepositoryId, id))
                : Task.FromException<DashboardLocation>(new GitHubRequestException(GitHubFailure.Permanent, $"dashboard Discussion category '{name}' does not exist"));
        }
    }

    public Task<DashboardDiscussion?> FindDashboard(DashboardLocation at, CancellationToken ct)
    {
        lock (sync)
        {
            calls.Add("discussions " + at.CategoryId);
            return Task.FromResult(dashboards.FirstOrDefault(dashboard => dashboard.CategoryId == at.CategoryId) is { } owned ? new DashboardDiscussion(owned.Id, owned.Url) : null);
        }
    }

    public async Task<DashboardDiscussion> CreateDashboard(DashboardLocation at, string title, string body, CancellationToken ct)
    {
        Dashboard? created = null;
        await Write("createDiscussion " + at.CategoryId, () => created = Add(at.CategoryId, title, body));
        return new(created!.Id, created.Url);
    }

    public Task UpdateDashboard(string id, string title, string body, CancellationToken ct) => Write("updateDiscussion " + id, () =>
    {
        var index = dashboards.FindIndex(dashboard => dashboard.Id == id);
        dashboards[index] = dashboards[index] with { Title = title, Body = body };
    });

    Dashboard Add(string category, string title, string body)
    {
        var number = 100 + dashboards.Count;
        var dashboard = new Dashboard("DD" + number, "https://github.com/owner/repository/discussions/" + number, category, title, body);
        dashboards.Add(dashboard);
        return dashboard;
    }

    Task Write(string call, Action apply)
    {
        lock (sync)
        {
            calls.Add(call);
            if (!failures.TryDequeue(out var failure))
            {
                apply();
                return Task.CompletedTask;
            }
            if (failure.Applied) apply();
            return Task.FromException(failure.Error);
        }
    }
}

/// <summary>The controller state as its files hold it: only what was written survives into another lifetime. A write can fail
/// on demand, as a full or read-only disk does.</summary>
sealed class InMemoryControllerState : IControllerState
{
    RecordedSettings? settings;

    /// <summary>Later writes throw <see cref="IOException"/>, leaving the earlier record in place.</summary>
    public bool FailWrites { get; set; }

    public int SettingsWrites { get; private set; }

    public RecordedSettings? ReadSettings() => settings;

    public void WriteSettings(RecordedSettings recorded)
    {
        if (FailWrites) throw new IOException("No space left on device");
        SettingsWrites++;
        settings = new(new Dictionary<string, SettingValue>(recorded.Settings));
    }

    /// <summary>The outbox's retained operations as last written, or as an earlier lifetime left them.</summary>
    public OutboxState? Outbox { get; set; }

    public OutboxState? ReadOutbox() => Outbox;

    public int OutboxWrites { get; private set; }

    public void WriteOutbox(OutboxState outbox)
    {
        if (FailWrites) throw new IOException("No space left on device");
        OutboxWrites++;
        Outbox = outbox with { Operations = [.. outbox.Operations] };
    }
}

/// <summary>
/// The agent runtime as its process answers and reports. Sessions start idle as S1, S2, and so on. Each turn request takes the
/// next scripted answer, else is accepted as T1, T2, and so on; an answer can report events first, as the reader can deliver a
/// completion before the request's response. Events are raised on the calling thread, in the order a test reports them.
/// </summary>
sealed class FakeAgentRuntime : IAgentRuntime
{
    readonly Queue<Func<AgentTurnRequest, AgentTurnAccepted>> answers = new();
    int sessions, turns;

    public event Action<AgentRuntimeEvent>? Changed;
    public int Epoch { get; private set; } = 1;
    /// <summary>Each turn request, recorded once its answer is decided.</summary>
    public List<AgentTurnRequest> Requests { get; } = [];
    public List<string> Resumed { get; } = [];
    public AgentSessionStatus Resumes { get; set; } = AgentSessionStatus.Idle;

    /// <summary>Answers the next turn request: <paramref name="answer"/> runs as the runtime handles it and can report events.</summary>
    public void Answer(Func<AgentTurnRequest, AgentTurnAccepted> answer) => answers.Enqueue(answer);

    public Task Restart(Action<int> whileStopped, CancellationToken ct)
    {
        whileStopped(++Epoch);
        return Task.CompletedTask;
    }

    public Task<(string SessionId, AgentSessionStatus Status)> StartSession(CancellationToken ct) =>
        Task.FromResult(($"S{++sessions}", AgentSessionStatus.Idle));

    /// <summary>Runs as the runtime handles a resume, before its response; it can report events.</summary>
    public Action<string>? Resuming { get; set; }

    public Task<AgentSessionStatus> ResumeSession(string sessionId, CancellationToken ct)
    {
        Resuming?.Invoke(sessionId);
        Resumed.Add(sessionId);
        return Task.FromResult(Resumes);
    }

    public Task<AgentTurnAccepted> StartTurn(AgentTurnRequest request, CancellationToken ct)
    {
        var answer = answers.TryDequeue(out var scripted) ? scripted : _ => new($"T{turns + 1}", false);
        try { return Task.FromResult(answer(request)); }
        catch (Exception error) { return Task.FromException<AgentTurnAccepted>(error); }
        finally { turns++; Requests.Add(request); }
    }

    public Task<(string? Model, string? Effort)> ConfiguredModel(CancellationToken ct) => Task.FromResult<(string?, string?)>((null, null));

    public Task<IReadOnlyList<NativeModel>> Models(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<NativeModel>>([new("gpt-6-luna", true, "medium", ["medium", "xhigh"])]);

    public void Report(string session, AgentSessionStatus status) => Changed?.Invoke(new AgentSessionStatusChanged(Epoch, session, status));
    public void Started(string session, string turn) => Changed?.Invoke(new AgentTurnStarted(Epoch, session, turn));
    public void Completed(string session, string turn, string status = "completed", string? rejection = null, int? epoch = null) =>
        Changed?.Invoke(new AgentTurnCompleted(epoch ?? Epoch, session, turn, status, rejection));
    public void Disconnect() => Changed?.Invoke(new AgentRuntimeDisconnected(Epoch));
}

/// <summary>The controller's log, as kind and message pairs in the order the core wrote them.</summary>
sealed class RecordingLog : IControllerLog
{
    readonly Lock sync = new();
    readonly List<(string Kind, string Message)> entries = [];
    readonly List<(int Count, TaskCompletionSource Reached)> waiters = [];

    public IReadOnlyList<(string Kind, string Message)> Entries { get { lock (sync) return [.. entries]; } }

    public void Write(string kind, string message)
    {
        lock (sync)
        {
            entries.Add((kind, message));
            foreach (var waiter in waiters.Where(waiter => entries.Count >= waiter.Count).ToArray())
            {
                waiters.Remove(waiter);
                waiter.Reached.TrySetResult();
            }
        }
    }

    /// <summary>Completes once <paramref name="count"/> entries are written: the end of work a loop does on another thread when it
    /// arms no further timer.</summary>
    public Task Written(int count)
    {
        lock (sync)
        {
            if (entries.Count >= count) return Task.CompletedTask;
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((count, reached));
            return reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>Controlled time that reports each timer a loop arms, so a test moves time only once the loop waits.</summary>
sealed class SteppedClock(DateTimeOffset start) : TimeProvider
{
    readonly FakeTimeProvider time = new(start);
    readonly SemaphoreSlim armed = new(0);

    public override DateTimeOffset GetUtcNow() => time.GetUtcNow();
    public override long GetTimestamp() => time.GetTimestamp();
    public override long TimestampFrequency => time.TimestampFrequency;
    public void Advance(TimeSpan duration) => time.Advance(duration);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = time.CreateTimer(callback, state, dueTime, period);
        armed.Release();
        return timer;
    }

    /// <summary>Completes once the loop has armed its next timer, that is, once it waits again.</summary>
    public Task Armed() => armed.WaitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    /// <summary>Whether a timer was armed since the last <see cref="Armed"/>, without waiting.</summary>
    public bool WasArmed() => armed.CurrentCount != 0;
}
