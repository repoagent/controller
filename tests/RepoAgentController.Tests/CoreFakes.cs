using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace RepoAgentController.Tests;

/// <summary>The repository's REPOAGENT_ Agent variables as GitHub holds them. A queued failure answers a read instead.</summary>
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
/// The repository's Discussions as GitHub holds them: the conversations the bridge reads, with their labels, the App's EYES
/// reactions, and the Discussions in each category, the App's dashboards among them. Each queued failure answers the next
/// write, in order; an applied failure makes the write first, as GitHub does when only its response is lost. A failed read
/// kind answers every read of that kind until it is cleared.
/// </summary>
sealed class InMemoryDiscussions(TimeProvider? clock = null) : IDiscussions
{
    public static readonly Actor Participant = new("participant", "User");
    static readonly IReadOnlySet<string> NoMentions = new HashSet<string>();
    public const string RepositoryId = "R_repository";
    public static readonly AppIdentity App = new("test-app");
    /// <summary>The App as GitHub reports the author of a Discussion it created.</summary>
    public static readonly Actor AppAccount = new(App.AgentLogin, "Bot");
    readonly Lock sync = new();
    readonly Dictionary<string, string> categories = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Posted> posted = [];
    readonly HashSet<string> eyes = [];
    readonly Queue<(GitHubRequestException Error, bool Applied)> failures = [];
    readonly List<string> calls = [];
    // Conversations by Discussion number, in the order they were opened; comments in the order they were written.
    readonly SortedList<int, Held> conversations = [];
    readonly Dictionary<string, Exception> readFailures = [];
    bool dashboardWritesHeld;
    DateTimeOffset Now => (clock ?? throw new InvalidOperationException("Writing a conversation needs the test's clock")).GetUtcNow();

    /// <summary>A Discussion in a category, as GitHub holds it now.</summary>
    public sealed record Posted(string Id, string Url, string CategoryId, Actor? Author, string Title, string Body);

    /// <summary>Adds a Discussion category; returns its ID.</summary>
    public string Category(string name)
    {
        lock (sync) return categories.TryGetValue(name, out var id) ? id : categories[name] = "DIC_" + name.Replace(' ', '_');
    }

    /// <summary>A Discussion <paramref name="author"/> posted in <paramref name="category"/> before; later posts are newer.</summary>
    public Posted Post(string category, Actor? author, string title, string body)
    {
        lock (sync) return Add(Category(category), author, title, body);
    }

    /// <summary>Later dashboard writes wait, unrequested, until their caller cancels them, as a GitHub that never answers.</summary>
    public void HoldDashboardWrites() { lock (sync) dashboardWritesHeld = true; }

    /// <summary>The next write fails with <paramref name="error"/>, after taking effect when <paramref name="applied"/>.</summary>
    public void Fail(GitHubRequestException error, bool applied = false) { lock (sync) failures.Enqueue((error, applied)); }

    /// <summary>Every request so far, in order, as its GraphQL field and subject, such as <c>addReaction D1</c>.</summary>
    public IReadOnlyList<string> Calls { get { lock (sync) return [.. calls]; } }

    /// <summary>How many times the App wrote its EYES reaction to <paramref name="subject"/>.</summary>
    public int Writes(string subject) => Calls.Count(call => call == "addReaction " + subject);

    public bool HasEyes(string subject) { lock (sync) return eyes.Contains(subject); }

    /// <summary>The Discussions in every category, oldest first.</summary>
    public IReadOnlyList<Posted> Posts { get { lock (sync) return [.. posted]; } }

    /// <summary>The Discussions the App created, oldest first.</summary>
    public IReadOnlyList<Posted> Dashboards { get { lock (sync) return [.. posted.Where(post => post.Author == AppAccount)]; } }

    /// <summary>Opens Discussion <paramref name="number"/> as D<paramref name="number"/>, created now in <paramref name="category"/>.</summary>
    public Discussion Open(int number, string body, string category = "General", Actor? author = null, DateTimeOffset? created = null) => Put(new(
        new("D" + number, "https://github.com/owner/repository/discussions/" + number, body, author ?? Participant, created ?? Now, created ?? Now,
            [new("EYES", false)], NoMentions, number, "Fixture", false, false, null, new(Category(category), category)), [], []));

    /// <summary>Holds this whole conversation as Discussion <c>Number</c>, as GitHub returns it to reads.</summary>
    public Discussion Put(DiscussionConversation conversation)
    {
        lock (sync) conversations[conversation.Discussion.Number] = new() { Discussion = conversation.Discussion, Reactions = [.. conversation.Reactions], Comments = [.. conversation.Comments] };
        return conversation.Discussion;
    }

    /// <summary>Adds a top-level comment, or a reply in the thread of <paramref name="replyTo"/>; an existing ID is edited instead.</summary>
    public void Comment(int number, string id, string body, Actor? author = null, string? replyTo = null) => Change(number, held =>
    {
        var index = held.Comments.FindIndex(comment => comment.Id == id);
        if (index >= 0) held.Comments[index] = held.Comments[index] with { Body = body, UpdatedAt = Now };
        else held.Comments.Add(new(id, $"https://github.com/owner/repository/discussions/{number}#discussioncomment-{id}", body, author ?? Participant,
            Now, Now, [new("EYES", false)], NoMentions, replyTo, []));
    });

    /// <summary>Deletes a comment or reply, with its reactions.</summary>
    public void Delete(int number, string id) => Change(number, held => held.Comments.RemoveAll(comment => comment.Id == id));

    /// <summary>Adds <paramref name="user"/>'s reaction to the Discussion or one of its comments.</summary>
    public void React(int number, string subject, string id, string content, string user) => Change(number, held =>
    {
        var reaction = new Reaction(id, content, new(user, "User"), Now);
        var index = held.Comments.FindIndex(comment => comment.Id == subject);
        if (index < 0) held.Reactions.Add(reaction);
        else held.Comments[index] = held.Comments[index] with { Reactions = [.. held.Comments[index].Reactions, reaction] };
    });

    /// <summary>Removes a reaction from the Discussion or one of its comments.</summary>
    public void Unreact(int number, string id) => Change(number, held =>
    {
        held.Reactions.RemoveAll(reaction => reaction.Id == id);
        for (var index = 0; index < held.Comments.Count; index++)
            held.Comments[index] = held.Comments[index] with { Reactions = [.. held.Comments[index].Reactions.Where(reaction => reaction.Id != id)] };
    });

    public void Answer(int number, string? comment) => Change(number, held => held.Discussion = held.Discussion with { AnswerId = comment });
    public void Edit(int number, string body) => Change(number, held => held.Discussion = held.Discussion with { Body = body, UpdatedAt = Now });

    /// <summary>Renames the Discussion and moves it to <paramref name="category"/>.</summary>
    public void Move(int number, string title, string category)
    {
        var moved = new DiscussionCategory(Category(category), category);
        Change(number, held => held.Discussion = held.Discussion with { Title = title, Category = moved });
    }

    /// <summary>Replaces the Discussion's labels; names are GitHub's, unvalidated.</summary>
    public void Labels(int number, params string[] names) => Change(number, held => held.Labels = names);

    /// <summary>GitHub deletes the Discussion: lists omit it and a lookup confirms it is gone.</summary>
    public void Remove(int number) { lock (sync) conversations.Remove(number); }

    /// <summary>Every later read of <paramref name="kind"/> (List, Find, Read or Labels) fails with <paramref name="error"/>, or succeeds again when it is null.</summary>
    public void FailReads(string kind, Exception? error)
    {
        lock (sync)
            if (error is null) readFailures.Remove(kind);
            else readFailures[kind] = error;
    }

    void Change(int number, Action<Held> change) { lock (sync) change(conversations[number]); }

    sealed class Held
    {
        public required Discussion Discussion;
        public required List<Reaction> Reactions;
        public required List<DiscussionComment> Comments;
        public string[] Labels = [];
    }

    Task<T> Reading<T>(string kind, Func<T> read)
    {
        lock (sync) return readFailures.TryGetValue(kind, out var error) ? Task.FromException<T>(error) : Task.FromResult(read());
    }

    // As GitHub reads them now: the App's EYES receipts show in each message's reaction groups.
    Discussion Seen(Discussion discussion) => eyes.Contains(discussion.Id) ? discussion with { ReactionGroups = Eyes(discussion.ReactionGroups) } : discussion;
    DiscussionComment Seen(DiscussionComment comment) => eyes.Contains(comment.Id) ? comment with { ReactionGroups = Eyes(comment.ReactionGroups) } : comment;
    static IReadOnlyList<ReactionGroup> Eyes(IReadOnlyList<ReactionGroup> groups) => [.. groups.Where(group => group.Content != "EYES"), new("EYES", true)];

    public Task<IReadOnlyList<Discussion>> List(CancellationToken ct) =>
        Reading<IReadOnlyList<Discussion>>("List", () => [.. conversations.Values.Select(held => Seen(held.Discussion))]);

    public Task<Discussion?> Find(int number, CancellationToken ct) =>
        Reading("Find", () => conversations.TryGetValue(number, out var held) ? Seen(held.Discussion) : null);

    /// <summary>The top-level comments in order, then the replies to each in that order, as GitHub's pages return them.</summary>
    public Task<DiscussionConversation> Read(Discussion discussion, CancellationToken ct) => Reading("Read", () =>
    {
        var held = conversations[discussion.Number];
        var roots = held.Comments.Where(comment => comment.ReplyToId is null).ToArray();
        var replies = roots.SelectMany(root => held.Comments.Where(comment => comment.ReplyToId == root.Id));
        return new DiscussionConversation(discussion, [.. held.Reactions], [.. roots.Concat(replies).Select(Seen)]);
    });

    public Task<IReadOnlyList<string>> Labels(string discussionId, CancellationToken ct) =>
        Reading<IReadOnlyList<string>>("Labels", () => conversations.Values.Single(held => held.Discussion.Id == discussionId).Labels);

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

    /// <summary>The category's Discussions, oldest first, in one page.</summary>
    public async IAsyncEnumerable<CategoryDiscussion> CategoryDiscussions(DashboardLocation at, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        Posted[] page;
        lock (sync)
        {
            calls.Add("discussions " + at.CategoryId);
            page = [.. posted.Where(post => post.CategoryId == at.CategoryId)];
        }
        foreach (var post in page) yield return new(post.Id, post.Url, post.Author, post.Body);
    }

    public async Task<DashboardDiscussion> CreateDashboard(DashboardLocation at, string title, string body, CancellationToken ct)
    {
        await HeldWrite(ct);
        Posted? created = null;
        await Write("createDiscussion " + at.CategoryId, () => created = Add(at.CategoryId, AppAccount, title, body));
        return new(created!.Id, created.Url);
    }

    public async Task UpdateDashboard(string id, string title, string body, CancellationToken ct)
    {
        await HeldWrite(ct);
        await Write("updateDiscussion " + id, () =>
        {
            var index = posted.FindIndex(post => post.Id == id);
            posted[index] = posted[index] with { Title = title, Body = body };
        });
    }

    // A held write is never requested: it records nothing and ends only when cancelled.
    Task HeldWrite(CancellationToken ct)
    {
        lock (sync) return dashboardWritesHeld ? Task.Delay(Timeout.InfiniteTimeSpan, ct) : Task.CompletedTask;
    }

    Posted Add(string category, Actor? author, string title, string body)
    {
        var number = 100 + posted.Count;
        var post = new Posted("DD" + number, "https://github.com/owner/repository/discussions/" + number, category, author, title, body);
        posted.Add(post);
        return post;
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

/// <summary>The controller's settings and outbox as its files hold them: only what was written survives into another lifetime.
/// A write can fail on demand, as a full or read-only disk does.</summary>
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

/// <summary>The status record as its file holds it: only the last written status survives into another lifetime. A status is
/// immutable, so the one written is the one kept.</summary>
sealed class InMemoryStatusRecord : IStatusRecord
{
    /// <summary>The status as last written, or as an earlier lifetime left it.</summary>
    public LifetimeStatus? Recorded { get; set; }

    public LifetimeStatus? Read() => Recorded;

    public void Write(LifetimeStatus status) => Recorded = status;
}

/// <summary>The managed checkout's recorded agent turns and admission, as a test sets them; none by default.</summary>
sealed class InMemoryCheckoutWork : ICheckoutWork
{
    public CheckoutWork? Work { get; set; }

    public CheckoutWork? Read() => Work;
}

/// <summary>
/// The Discussion state as the state file holds it: only what was written survives into another lifetime, so a change that was
/// never saved is lost on restart. A write can fail on demand, as a full or read-only disk does.
/// </summary>
sealed class InMemoryDiscussionStore : IDiscussionStore
{
    DiscussionState? saved;

    /// <summary>Later writes throw <see cref="IOException"/>, leaving the earlier state in place.</summary>
    public bool FailWrites { get; set; }

    /// <summary>The Discussion state as last written, or null when none was ever written.</summary>
    public DiscussionState? Saved => saved is null ? null : Copy(saved);

    /// <summary>Thrown by later reads, as a state file that cannot be read throws.</summary>
    public Exception? ReadFailure { get; set; }

    public DiscussionState? ReadDiscussions() => ReadFailure is { } failure ? throw failure : Saved;

    public void WriteDiscussions(DiscussionState state)
    {
        if (FailWrites) throw new IOException("No space left on device");
        saved = Copy(state);
    }

    // Every part the bridge changes in place is copied; the records it replaces whole are shared.
    static DiscussionState Copy(DiscussionState state)
    {
        var copy = new DiscussionState(state.Repository)
        { AgentLogin = state.AgentLogin, CategoryStartsAsRead = state.CategoryStartsAsRead, Unknown = state.Unknown, Unreadable = state.Unreadable };
        foreach (var (name, since) in state.CategoryStarts) copy.CategoryStarts[name] = since;
        foreach (var (id, record) in state.Discussions)
        {
            var waiting = record.Waiting is { } held ? new WaitingInput(held.DiscussionJson) { ContextJson = held.ContextJson, Retry = held.Retry, Unknown = held.Unknown } : null;
            waiting?.Inputs.AddRange(record.Waiting!.Inputs);
            var kept = copy.Discussions[id] = new(record.Url)
            {
                Sources = new(record.Sources),
                Labels = record.Labels,
                SessionId = record.SessionId,
                Waiting = waiting,
                Pending = record.Pending,
                Running = record.Running,
                Recovery = record.Recovery,
                Unknown = record.Unknown,
                Unreadable = record.Unreadable
            };
            foreach (var (key, value) in record.UnreadableKeys) kept.UnreadableKeys[key] = value;
        }
        return copy;
    }
}

/// <summary>
/// The agent runtime as its process answers and reports. Sessions start idle as S1, S2, and so on. Each turn request takes the
/// next scripted answer, else is accepted as T1, T2, and so on; an answer can report events first, as the reader can deliver a
/// completion before the request's response. Events are raised on the calling thread, in the order a test reports them.
/// </summary>
sealed class FakeAgentRuntime(int sessions = 0, int turns = 0) : IAgentRuntime
{
    readonly Queue<Func<AgentTurnRequest, AgentTurnAccepted>> answers = new();

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

    /// <summary>When set, starting a session fails with it and starts none.</summary>
    public Exception? SessionStartFailure { get; set; }

    public Task<(string SessionId, AgentSessionStatus Status)> StartSession(CancellationToken ct) => SessionStartFailure is { } failure
        ? Task.FromException<(string, AgentSessionStatus)>(failure) : Task.FromResult(($"S{++sessions}", AgentSessionStatus.Idle));

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

    /// <summary>Sessions started and turns requested so far, so a later runtime process can continue the numbering.</summary>
    public (int Sessions, int Turns) Counts => (sessions, turns);

    /// <summary>The model and effort of the agent's own configuration.</summary>
    public (string? Model, string? Effort) Configuration { get; set; }

    public IReadOnlyList<AgentModel> Catalogue { get; set; } = [new("gpt-6-luna", true, "medium", ["medium", "xhigh"])];

    public Task<(string? Model, string? Effort)> ConfiguredModel(CancellationToken ct) => Task.FromResult(Configuration);

    /// <summary>When set, reading the model catalogue fails with it, as a agent that cannot answer does.</summary>
    public Exception? ModelsFailure { get; set; }

    public Task<IReadOnlyList<AgentModel>> Models(CancellationToken ct) => ModelsFailure is { } failure
        ? Task.FromException<IReadOnlyList<AgentModel>>(failure) : Task.FromResult(Catalogue);

    public void Report(string session, AgentSessionStatus status) => Changed?.Invoke(new AgentSessionStatusChanged(Epoch, session, status));
    public void Started(string session, string turn) => Changed?.Invoke(new AgentTurnStarted(Epoch, session, turn));
    public void TransientFailure(string session, string turn, AgentFailure failure) => Changed?.Invoke(new AgentTurnCompleted(Epoch, session, turn,
        AgentTurnStatus.Failed, "failed", null) { Failure = failure });

    public void CapacityFailure(string session, string turn) => Changed?.Invoke(new AgentTurnCompleted(Epoch, session, turn,
        AgentTurnStatus.Failed, "failed", null)
    { Failure = new(AgentFailureKind.ModelCapacity) });
    /// <summary>Reports a turn ended with <paramref name="status"/>, sent as <paramref name="reported"/> or else as the status's
    /// lowercase name.</summary>
    public void Completed(string session, string turn, AgentTurnStatus status = AgentTurnStatus.Completed, string? rejection = null,
        int? epoch = null, string? reported = null, bool capacity = false, AgentFailure? failure = null) =>
        Changed?.Invoke(new AgentTurnCompleted(epoch ?? Epoch, session, turn, status, reported ?? status.ToString().ToLowerInvariant(), rejection)
        { Failure = failure ?? (capacity ? new(AgentFailureKind.ModelCapacity) : null) });
    public Func<CancellationToken, Task<AgentUsageObservation>> Usage { get; set; } = _ =>
        Task.FromResult(new AgentUsageObservation(AgentUsageAvailability.Unsupported, []));
    public Task<AgentUsageObservation> ReadUsage(CancellationToken ct) => Usage(ct);
    public void AccountChanged() => Changed?.Invoke(new AgentUsageContextChanged(Epoch));
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
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The test clock has managed-only signaling semaphores; timer callbacks and cancelled waits can outlive the observed operation. No wait handle is created.")]
sealed class SteppedClock(DateTimeOffset start) : TimeProvider
{
    readonly FakeTimeProvider time = new(start);
    readonly SemaphoreSlim armed = new(0);
    readonly Lock sync = new();
    // Held while a timer is armed and registered with the fake time, and while time moves: a loop seen waiting is waiting on a
    // timer that moving time can fire.
    readonly Lock arming = new();
    readonly List<TimeSpan> delays = [];
    readonly List<TaskCompletionSource> waiters = [];
    int pending;

    public override DateTimeOffset GetUtcNow() => time.GetUtcNow();
    public override long GetTimestamp() => time.GetTimestamp();
    public override long TimestampFrequency => time.TimestampFrequency;
    public void Advance(TimeSpan duration) { lock (arming) time.Advance(duration); }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Pending(this, period == Timeout.InfiniteTimeSpan);
        lock (arming)
        {
            if (dueTime != Timeout.InfiniteTimeSpan) lock (sync) delays.Add(dueTime);
            timer.Arm(dueTime);
            timer.Inner = time.CreateTimer(value => { timer.Fired(); callback(value); }, state, dueTime, period);
        }
        armed.Release();
        return timer;
    }

    /// <summary>Completes once the loop has armed its next timer, that is, once it waits again.</summary>
    public Task Armed() => armed.WaitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    /// <summary>Whether a timer was armed since the last <see cref="Armed"/>, without waiting.</summary>
    public bool WasArmed() => armed.CurrentCount != 0;

    /// <summary>The due time of each timer armed so far, in order: as it was created, and each time
    /// <see cref="ITimer.Change"/> re-armed it. An infinite due time arms nothing and is not recorded.</summary>
    public IReadOnlyList<TimeSpan> Delays { get { lock (sync) return [.. delays]; } }

    /// <summary>Completes once a timer is pending: armed, and neither fired nor disposed. However a loop arms its timers, it has
    /// one pending while it waits on this clock. A timer is armed and registered with the fake time under one lock, which this and
    /// <see cref="Advance"/> also take, so moving time once this completes can fire it.</summary>
    public Task Waiting()
    {
        lock (arming) lock (sync)
        {
            if (pending != 0) return Task.CompletedTask;
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add(waiting);
            return waiting.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    void Count(int change)
    {
        lock (sync)
        {
            pending += change;
            if (pending == 0) return;
            foreach (var waiter in waiters) waiter.TrySetResult();
            waiters.Clear();
        }
    }

    // A timer as the clock counts it: pending from when it is armed until a one-shot timer fires or any timer is disposed.
    sealed class Pending(SteppedClock clock, bool once) : ITimer
    {
        bool counted;
        public ITimer Inner { get; set; } = null!;

        public void Arm(TimeSpan dueTime) => Set(dueTime != Timeout.InfiniteTimeSpan);
        public void Fired() { if (once) Set(false); }

        void Set(bool armed)
        {
            lock (clock.sync)
            {
                if (counted == armed) return;
                counted = armed;
            }
            clock.Count(armed ? 1 : -1);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.arming)
            {
                if (dueTime != Timeout.InfiniteTimeSpan) lock (clock.sync) clock.delays.Add(dueTime);
                Arm(dueTime);
                return Inner.Change(dueTime, period);
            }
        }

        public void Dispose() { Set(false); Inner.Dispose(); }

        public ValueTask DisposeAsync() { Set(false); return Inner.DisposeAsync(); }
    }
}

/// <summary>
/// The managed checkout's admission as the bridge resolves it: every admission and resolution, in order, as "admit", "bind S1",
/// "accepted S1 T1", "abandon", "unconfirmed", "confirm" and "dispose". Every admission succeeds.
/// </summary>
sealed class RecordingCheckoutAdmission : ICheckoutAdmission
{
    readonly List<string> calls = [];

    public IReadOnlyList<string> Calls { get { lock (calls) return [.. calls]; } }

    public Task<ICheckoutTurn> Admit(CancellationToken ct)
    {
        Add("admit");
        return Task.FromResult<ICheckoutTurn>(new Turn(this));
    }

    void Add(string call) { lock (calls) calls.Add(call); }

    sealed class Turn(RecordingCheckoutAdmission owner) : ICheckoutTurn
    {
        public void Bind(string session) => owner.Add("bind " + session);
        public void Accepted(string session, string turn) => owner.Add($"accepted {session} {turn}");
        public void Abandon() => owner.Add("abandon");
        public void Unconfirmed() => owner.Add("unconfirmed");
        public void Confirm() => owner.Add("confirm");
        public void Dispose() => owner.Add("dispose");
    }
}

/// <summary>The refresh journal in memory, saved as a copy so later changes reach it only through a write. A write fails on
/// demand, before it saves anything.</summary>
sealed class InMemoryRefreshJournal : IRefreshJournal
{
    public Exception? PreserveFailure { get; set; }
    public List<RefreshJournal?> Evidence { get; } = [];
    public void PreserveStartupEvidence()
    {
        if (PreserveFailure is { } failure) throw failure;
        Evidence.Add(Saved);
    }
    readonly Lock sync = new();
    readonly List<(Func<RefreshJournal, bool> Condition, TaskCompletionSource Reached)> waiters = [];
    RefreshJournal? saved;

    public RefreshJournal? Saved { get { lock (sync) return saved is null ? null : Copy(saved); } set { lock (sync) saved = value is null ? null : Copy(value); } }
    public Exception? WriteFailure { get; set; }

    public RefreshJournal? Read() => Saved;

    public void Write(RefreshJournal journal)
    {
        if (WriteFailure is { } failure) throw failure;
        Saved = journal;
        lock (sync)
            foreach (var waiter in waiters.Where(waiter => waiter.Condition(journal)).ToArray())
            {
                waiters.Remove(waiter);
                waiter.Reached.TrySetResult();
            }
    }

    /// <summary>Completes once a journal meeting <paramref name="condition"/> is saved, or at once if the saved one does: the end
    /// of work a loop does on another thread.</summary>
    public Task Reached(Func<RefreshJournal, bool> condition)
    {
        lock (sync)
        {
            if (saved is not null && condition(saved)) return Task.CompletedTask;
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            waiters.Add((condition, reached));
            return reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    static RefreshJournal Copy(RefreshJournal journal)
    {
        var copy = new RefreshJournal(journal.Root, journal.Owner, journal.RepositoryId)
        {
            Requested = journal.Requested,
            Completed = journal.Completed,
            Blocked = journal.Blocked,
            ActiveUnreadable = journal.ActiveUnreadable,
            ActiveAsRead = journal.ActiveAsRead,
            Running = journal.Running,
            Reason = journal.Reason,
            Applying = journal.Applying,
            Unknown = journal.Unknown
        };
        copy.ReasonAsRead = journal.ReasonAsRead;
        copy.RunningUnreadable = journal.RunningUnreadable;
        copy.RunningAsRead = journal.RunningAsRead;
        foreach (var (id, admission) in journal.Active)
            copy.Active[id] = new(admission.Since)
            {
                Thread = admission.Thread,
                Turn = admission.Turn,
                Confirmed = admission.Confirmed,
                Unconfirmed = admission.Unconfirmed,
                Unknown = admission.Unknown,
                AsRead = admission.AsRead
            };
        return copy;
    }
}

/// <summary>The managed checkout's refresh as each test scripts it, in order; unscripted refreshes find the checkout current.</summary>
sealed class ScriptedCheckout : IRepositoryCheckout
{
    readonly Queue<Func<Action<string, string>, CancellationToken, Task<bool>>> outcomes = new();
    int refreshes;

    public int Refreshes => Volatile.Read(ref refreshes);

    public void Then(Func<Action<string, string>, CancellationToken, Task<bool>> outcome) => outcomes.Enqueue(outcome);

    /// <summary>The next refresh applies <paramref name="target"/> over <paramref name="previous"/>.</summary>
    public void Applies(string previous = "C1", string target = "C2") => Then((beforeApply, _) => { beforeApply(previous, target); return Task.FromResult(true); });

    /// <summary>The next refresh refuses safely with <paramref name="reason"/>.</summary>
    public void Defers(string reason) => Then((_, _) => Task.FromException<bool>(new CheckoutDeferred(reason)));

    public Task<bool> Refresh(Action<string, string> beforeApply, CancellationToken ct)
    {
        var outcome = outcomes.TryDequeue(out var scripted) ? scripted : (_, _) => Task.FromResult(false);
        Interlocked.Increment(ref refreshes);
        return outcome(beforeApply, ct);
    }
}

/// <summary>
/// A managed workspace and its checkout as their files and Git would report them, in memory. It starts as a settled clone of
/// the approved repository at <see cref="Head"/>, owned by <see cref="Owner"/> and recorded; a test changes what it needs.
/// Reads are recorded by name; a clone or write the workspace would refuse throws before it is recorded.
/// </summary>
sealed class InMemoryCheckout : ICheckoutUpdateWorkspace, ICheckoutUpdateFacts
{
    public const string State = "/srv/repoagent/state.json", RootPath = "/srv/repoagent/workspace/repository";
    public const string Head = "1111111111111111111111111111111111111111", Owner = "0123456789abcdef0123456789abcdef";
    public const string Remote = "https://github.com/owner/repository.git";

    public RepositoryCheckout Repository { get; set; } = new(345, "owner/repository", Remote, "trunk");
    public CheckoutRecord? Saved { get; set; } = Recorded();
    public CheckoutRoot RootEntry { get; set; } = CheckoutRoot.Directory;
    public string? Marker { get; set; } = Owner;
    public bool GitDirectory { get; set; } = true;
    public HashSet<string> Held { get; } = [];
    public List<string> Configuration { get; } =
    [
        "core.repositoryformatversion", "core.filemode", "core.bare", "core.logallrefupdates", "remote.origin.url",
        "remote.origin.fetch", "branch.trunk.remote", "branch.trunk.merge"
    ];
    public string TopLevelPath { get; set; } = RootPath;
    public bool Bare { get; set; }
    public List<string> Urls { get; } = [Remote];

    /// <summary>The branch HEAD points at, or null when it is detached.</summary>
    public string? Branch { get; set; } = "refs/heads/trunk";
    public bool TrackedChanges { get; set; }
    public bool UntrackedFiles { get; set; }
    public List<IndexEntry> Entries { get; } = [new(".gitignore", false, false), new("tracked-sentinel", false, false)];
    public Dictionary<string, string> Commits { get; } = new() { ["HEAD^{commit}"] = Head, ["refs/remotes/origin/trunk^{commit}"] = Head };

    public List<string> Reads { get; } = [];
    public List<string> Writes { get; } = [];
    public int Clones { get; private set; }

    /// <summary>The ownership record a clone of the approved repository at <see cref="Head"/> leaves.</summary>
    public static CheckoutRecord Recorded() => new()
    {
        Version = 1,
        State = State,
        Root = RootPath,
        Owner = Owner,
        RepositoryId = 345,
        Repository = "owner/repository",
        Remote = Remote,
        Branch = "trunk",
        Commit = Head
    };

    public string Target { get; set; } = Head;
    public FetchedBranch? Fetched { get; set; }
    public bool RecordUnchanged { get; set; } = true;
    public Action<string>? Reading { get; set; }
    public Action? Fetching { get; set; }
    public Exception? FetchFailure { get; set; }
    public Exception? PrepareFailure { get; set; }
    public bool ForceSettles { get; set; } = true;
    public Action? Forcing { get; set; }
    public (string Branch, string Target, IReadOnlyList<string> Hidden)? Forced { get; private set; }
    public string? RewrittenCommit { get; private set; }

    T Read<T>(string name, T value) { Reads.Add(name); Reading?.Invoke(name); return value; }
    Task<T> Git<T>(string name, T value) => Task.FromResult(Read(name, value));

    public Task<RepositoryCheckout> AuthoritativeRepository(CancellationToken ct) => Task.FromResult(Read("repository", Repository));
    public Task<string> AuthoritativeCommit(string branch, CancellationToken ct) => Git("authoritative-commit " + branch, Target);
    public bool Unchanged() => Read("record-unchanged", RecordUnchanged);
    public void PrepareFetch()
    {
        Reads.Add("prepare-fetch");
        if (PrepareFailure is { } error) throw error;
    }
    public Task<FetchedBranch> Fetch(CancellationToken ct)
    {
        Writes.Add("fetch");
        if (FetchFailure is { } error) throw error;
        var fetched = Fetched ?? new(Target);
        Commits["FETCH_HEAD^{commit}"] = fetched.Commit ?? "";
        Fetching?.Invoke();
        return Task.FromResult(fetched);
    }
    public void Rewrite(CheckoutRecord record)
    {
        Saved = record;
        RewrittenCommit = record.Commit;
        Writes.Add("rewrite " + record.Commit);
    }
    public Task Force(string branch, string target, IReadOnlyList<string> hidden, CancellationToken ct)
    {
        Forcing?.Invoke();
        Forced = (branch, target, hidden);
        Writes.Add("force " + target);
        if (ForceSettles)
        {
            Branch = "refs/heads/" + branch;
            Commits["HEAD^{commit}"] = target;
            TrackedChanges = false; UntrackedFiles = false;
            for (var i = 0; i < Entries.Count; i++) Entries[i] = Entries[i] with { SkipWorktree = false, AssumeUnchanged = false };
            Held.Remove("MERGE_HEAD"); Held.Remove("CHERRY_PICK_HEAD"); Held.Remove("REVERT_HEAD");
        }
        return Task.CompletedTask;
    }
    public CheckoutRecord? Record() => Read("record", Saved);
    public CheckoutRoot Root() => Read("root", RootEntry);

    public Task Clone(RepositoryCheckout repository, CancellationToken ct)
    {
        if (RootEntry != CheckoutRoot.Missing) throw new IOException("The clone target exists");
        RootEntry = CheckoutRoot.Directory;
        Clones++;
        Writes.Add("clone " + repository.Remote);
        return Task.CompletedTask;
    }

    string? ICheckoutWorkspace.Owner() => Read("owner", Marker);

    public void WriteOwner(string owner)
    {
        if (Marker is not null) throw new IOException("The owner marker exists");
        Marker = owner;
        Writes.Add("owner " + owner);
    }

    public void WriteRecord(CheckoutRecord record)
    {
        Saved = record;
        Writes.Add("record " + record.Owner);
    }

    public bool OwnGitDirectory() => Read("git-directory", GitDirectory);
    public bool Holds(string path) => Read("holds " + path, Held.Contains(path));
    public Task<IReadOnlyList<string>> ConfigurationKeys(CancellationToken ct) => Git<IReadOnlyList<string>>("configuration", [.. Configuration]);
    public Task<string> TopLevel(CancellationToken ct) => Git("top-level", TopLevelPath);
    public Task<bool> IsBare(CancellationToken ct) => Git("bare", Bare);
    public Task<IReadOnlyList<string>> OriginUrls(CancellationToken ct) => Git<IReadOnlyList<string>>("origin-urls", [.. Urls]);

    public Task<string> HeadBranch(CancellationToken ct)
    {
        Reads.Add("head-branch");
        return Branch is { } branch ? Task.FromResult(branch) : Task.FromException<string>(new IOException("HEAD is detached"));
    }

    public Task<string> HeadName(CancellationToken ct) => Git("head-name", Branch ?? "HEAD");
    public Task<bool> Changed(bool untracked, CancellationToken ct) => Git(untracked ? "changes" : "tracked-changes", TrackedChanges || untracked && UntrackedFiles);
    public Task<IReadOnlyList<IndexEntry>> Index(CancellationToken ct) => Git<IReadOnlyList<IndexEntry>>("index", [.. Entries]);
    public Task<string> Commit(string revision, CancellationToken ct) => Git("commit " + revision, Commits[revision]);
}
