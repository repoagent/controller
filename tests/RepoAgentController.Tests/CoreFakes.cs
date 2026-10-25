using System.Runtime.CompilerServices;
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

    public void Answer(int number, string? comment) => Change(number, held => held.Discussion = held.Discussion with { AnswerId = comment });
    public void Edit(int number, string body) => Change(number, held => held.Discussion = held.Discussion with { Body = body, UpdatedAt = Now });

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
        Posted? created = null;
        await Write("createDiscussion " + at.CategoryId, () => created = Add(at.CategoryId, AppAccount, title, body));
        return new(created!.Id, created.Url);
    }

    public Task UpdateDashboard(string id, string title, string body, CancellationToken ct) => Write("updateDiscussion " + id, () =>
    {
        var index = posted.FindIndex(post => post.Id == id);
        posted[index] = posted[index] with { Title = title, Body = body };
    });

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

    /// <summary>Sessions started and turns requested so far, so a later runtime process can continue the numbering.</summary>
    public (int Sessions, int Turns) Counts => (sessions, turns);

    /// <summary>The model and effort of the agent's own configuration.</summary>
    public (string? Model, string? Effort) Configuration { get; set; }

    public IReadOnlyList<NativeModel> Catalogue { get; set; } = [new("gpt-6-luna", true, "medium", ["medium", "xhigh"])];

    public Task<(string? Model, string? Effort)> ConfiguredModel(CancellationToken ct) => Task.FromResult(Configuration);

    public Task<IReadOnlyList<NativeModel>> Models(CancellationToken ct) => Task.FromResult(Catalogue);

    public void Report(string session, AgentSessionStatus status) => Changed?.Invoke(new AgentSessionStatusChanged(Epoch, session, status));
    public void Started(string session, string turn) => Changed?.Invoke(new AgentTurnStarted(Epoch, session, turn));
    /// <summary>Reports a turn ended with <paramref name="status"/>, sent as <paramref name="reported"/> or else as the status's
    /// lowercase name.</summary>
    public void Completed(string session, string turn, AgentTurnStatus status = AgentTurnStatus.Completed, string? rejection = null,
        int? epoch = null, string? reported = null) =>
        Changed?.Invoke(new AgentTurnCompleted(epoch ?? Epoch, session, turn, status, reported ?? status.ToString().ToLowerInvariant(), rejection));
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
