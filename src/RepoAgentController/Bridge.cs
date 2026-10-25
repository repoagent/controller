using System.Globalization;
using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>
/// Observes Discussions into an ordered per-Discussion waiting queue in the state store, then dispatches the next
/// size-bounded batch only to a natively idle session. EYES follow native acceptance of the batch carrying the source.
/// A failed or unacknowledged delivery keeps its batch first in line and retries it with backoff; input that may already
/// have reached the native is sent again marked as a redelivery, accepting possible duplicate processing.
/// </summary>
sealed class Bridge(string repository, IDiscussions discussions, AppIdentity identity, AgentSessions sessions, StateStore store, GitHubOutbox outbox,
    HashSet<int> enroll, RepositorySettings settings, ICheckoutAdmission? refresh = null, TimeProvider? time = null)
{
    // Envelope characters per dispatched batch; the pinned native rejects turn input above 1,048,576 characters.
    const int MaximumBatchCharacters = 256 * 1024;
    // Bootstrap context characters, leaving room in the first batch for the Discussion and its first changes.
    const int MaximumContextCharacters = MaximumBatchCharacters / 2;
    public const string ActiveSessionSetting = "REPOAGENT_MAX_ACTIVE_SESSIONS";
    /// <summary>Native turns that may run at once across Discussions while REPOAGENT_MAX_ACTIVE_SESSIONS has no effective value.</summary>
    public const int DefaultActiveSessions = 4;
    readonly HashSet<string> held = [];
    // Delivery retries back off from five seconds, doubling up to five minutes, with no attempt limit.
    static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5), LastRetry = TimeSpan.FromMinutes(5);
    readonly TimeProvider clock = time ?? TimeProvider.System;
    // Reads the same repository settings as the active-session limit.
    readonly CategoryEnrollment categories = new(settings, identity, store);
    // Model label and selection problems by kind and Discussion URL, each reported once until it changes or clears.
    readonly Dictionary<(string Kind, string Url), string> reported = [];
    // Serializes observation and idle dispatch over the shared state store.
    readonly SemaphoreSlim gate = new(1, 1), wake = new(0, 1);
    // Caller holds the gate. The batch each running turn carries, by turn ID, until its outcome; this lifetime only.
    readonly Dictionary<string, RunningBatch> running = [];

    // Read at each admission decision, so a refreshed repository value applies to the next dispatch.
    int ActiveSessionLimit => settings[ActiveSessionSetting].Value is { } value
        ? int.Parse(value, CultureInfo.InvariantCulture) : DefaultActiveSessions;

    /// <summary>Retries every delivery an earlier controller lifetime left unacknowledged. Call once, before observing.</summary>
    public void ResumeInterrupted()
    {
        // Turns end with the native process of their lifetime, however their marker was left.
        foreach (var record in store.Discussions.Values.Where(record => record.Running is not null || record.UnreadableKeys.ContainsKey(RecordKey.Running)).ToArray())
        {
            record.Running = null;
            record.UnreadableKeys.Remove(RecordKey.Running);
            store.Save();
        }
        foreach (var record in store.Discussions.Values.Where(record => record.Pending is not null).ToArray())
            Interrupted(record);
    }

    public async Task<bool> Poll(CancellationToken ct)
    {
        IReadOnlyList<Discussion> listed;
        try { listed = await discussions.List(ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.Write("poll-error", error.Message);
            return false;
        }
        return await Observe(listed, ct);
    }

    public async Task<bool> Observe(int number, CancellationToken ct)
    {
        try
        {
            var discussion = await discussions.Find(number, ct);
            if (discussion is not null) return await Observe([discussion], ct);
            await Forget(number, ct);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.Write("observation-error", $"discussion={number}: {error.Message}");
            return false;
        }
    }

    async Task<bool> Observe(IReadOnlyList<Discussion> observed, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await ObserveExclusive(observed, ct); }
        finally { gate.Release(); }
    }

    async Task<bool> ObserveExclusive(IReadOnlyList<Discussion> observed, CancellationToken ct)
    {
        Settle();
        var succeeded = true;
        foreach (var discussion in observed)
        {
            ct.ThrowIfCancellationRequested();
            if (identity.IsDashboard(discussion.Author, discussion.Body)) continue; // Operational status, never agent input, however enrolled.
            var id = discussion.Id;
            var record = store.Discussions.GetValueOrDefault(id);
            // A delivery still pending in this lifetime failed as its native stopped; the next start retries it.
            if (record?.Pending is not null) { succeeded = false; continue; }
            try
            {
                // Left as read, reporting why it could not be read, as observing it failed before the records were typed.
                if (record?.Problem is { } problem) throw new InvalidDataException(problem);
                // Read every page, including reactions to agent-authored messages.
                var conversation = await discussions.Read(discussion, ct);
                var sources = conversation.Messages;
                var bootstrap = string.IsNullOrEmpty(record?.SessionId);
                var byCategory = false;
                if (bootstrap && record?.Waiting is null && !enroll.Contains(discussion.Number) &&
                    !sources.Any(source => identity.IsEnrollmentMention(source.Message.Author, source.Message.Body, source.Message.MentionedUsers)))
                {
                    if (!categories.Selects(discussion)) continue;
                    byCategory = true;
                }
                if (record is null)
                {
                    record = new DiscussionRecord(discussion.Url);
                    store.Discussions[id] = record;
                    Log.Write("discovered", discussion.Url + (byCategory ? " enrollment=category" : ""));
                }
                await ObserveLabels(record, id, ct);
                var previous = record.Sources;
                var current = new OrderedDictionary<string, SourceCheckpoint>();
                var changes = new List<JsonObject>();
                var receipts = new List<string?>(); // Receipt source of each change, by index; none for a deletion.
                // The bootstrap context captured by this observation carries the existing bodies that fit its budget; the
                // changes of later comments carry their own body, so a long conversation splits into bounded batches.
                var snapshot = bootstrap && record.Waiting?.ContextJson is null;
                var (context, inContext) = snapshot ? Context(conversation.Comments) : (null, []);
                foreach (var (source, reactions) in sources)
                {
                    var sourceId = source.Id;
                    var version = SourceCheckpoint.Of(source, reactions, identity);
                    current[sourceId] = version;
                    var old = previous.GetValueOrDefault(sourceId);
                    // The Discussion ID travels once per dispatched batch; each change keeps its own thread root.
                    var target = new JsonObject { ["replyToId"] = sourceId == id ? null : (source as DiscussionComment)?.ReplyToId ?? sourceId };
                    var state = Changed(old?.State, version.State, old?.ReadAsChanged ?? []);
                    void Add(string kind, JsonObject? content = null, JsonNode? reaction = null)
                    {
                        var subject = DiscussionJson.Subject(source);
                        foreach (var field in content ?? []) subject[field.Key] = field.Value?.Copy();
                        var change = new JsonObject { ["kind"] = kind, ["subject"] = subject, ["replyTarget"] = target.Copy() };
                        if (reaction is not null) change["reaction"] = reaction.Copy();
                        changes.Add(change);
                        receipts.Add(sourceId);
                    }
                    if (!identity.IsAgentActor(source.Author) && (old?.Content != version.Content || old.ReadAsChanged.Contains(CheckpointPart.Content)))
                    {
                        var content = new JsonObject { ["body"] = source.Body };
                        foreach (var field in state) content[field.Key] = field.Value?.Copy();
                        Add(sourceId == id ? (old is null ? "discussion.created" : "discussion.changed") : (old is null ? "comment.created" : "comment.edited"),
                            snapshot && old is null && (sourceId == id || inContext.Contains(sourceId)) ? null : content);
                    }
                    // Discussion state can change independently of its original author.
                    else if (sourceId == id && !bootstrap && (old is null || old.State != version.State || old.ReadAsChanged.Any(part => part != CheckpointPart.Content))) Add("discussion.stateChanged", state);
                    var oldReactions = old?.Reactions ?? [];
                    var (oldIds, newIds) = (oldReactions.Select(reaction => reaction.Id).ToHashSet(), version.Reactions.Select(reaction => reaction.Id).ToHashSet());
                    foreach (var reaction in version.Reactions)
                        if (!oldIds.Contains(reaction.Id)) Add("reaction.added", reaction: DiscussionJson.Public(reaction));
                    foreach (var reaction in oldReactions)
                        if (!newIds.Contains(reaction.Id)) Add("reaction.removed", reaction: DiscussionJson.Public(reaction));
                }
                // Sources returned every page, so a checkpointed comment it lacks is confirmed deleted; a failed or partial
                // read throws above and leaves the checkpoint alone. A deletion carries identity, attribution and routing
                // only, and no receipt: nothing is left to react to. A reply in a thread that is gone too routes top-level.
                foreach (var (sourceId, old) in previous)
                {
                    if (current.ContainsKey(sourceId)) continue;
                    var subject = new JsonObject { ["id"] = sourceId, ["author"] = old.Content.Author is { } login ? new JsonObject { ["login"] = login } : null };
                    var root = old.ReplyTo ?? "";
                    if (root.Length != 0) subject["replyTo"] = new JsonObject { ["id"] = root };
                    changes.Add(new JsonObject
                    {
                        ["kind"] = "comment.deleted", ["subject"] = subject,
                        ["replyTarget"] = new JsonObject { ["replyToId"] = current.ContainsKey(root) ? root : null }
                    });
                    receipts.Add(null);
                }
                if (bootstrap && changes.Count == 0 && record.Waiting is null)
                {
                    changes.Add(new JsonObject { ["kind"] = "discussion.enrolled", ["subject"] = DiscussionJson.Subject(discussion), ["replyTarget"] = new JsonObject { ["replyToId"] = null } });
                    receipts.Add(id);
                }
                if (changes.Count != 0) Queue(record, conversation, changes, receipts, context, current);
                // Quietly remember agent output so later human reactions can be compared.
                else if (!Same(previous, current)) { record.Sources = current; store.Save(); }
                if (record.Waiting is not { } waiting) continue;
                if (!await Dispatch(record, ct) && changes.Count != 0)
                    Log.Write("input-waiting", $"{discussion.Url} session={record.SessionId ?? ""} status={(record.SessionId is { Length: > 0 } session ? sessions.Status(session).ToString() : "none")} waiting={waiting.Inputs.Count} sources={string.Join(',', receipts.OfType<string>().Distinct())} active={sessions.ActiveTurns} limit={ActiveSessionLimit}");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                succeeded = false;
                Log.Write("observation-error", $"{discussion.Url}: {error.Message}");
            }
        }
        return succeeded;
    }

    /// <summary>Runs <paramref name="action"/> while no observation or dispatch runs, so it never interleaves with native admission.</summary>
    public async Task<T> ExcludingDispatch<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            Settle(); // Returned input is queued before a native restart can forget the turn that held it.
            return await action();
        }
        finally { gate.Release(); }
    }

    /// <summary>Reconciles category enrollment starts with a refreshed repository settings change while no observation or
    /// dispatch runs, so a category removed and selected again between observations starts afresh.</summary>
    public async Task ReconcileCategoryEnrollment(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { categories.Reconcile(); }
        // The starts stay reconciled in memory and are saved with the next state change; settings refresh continues.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Write("repository-settings-error", $"the category enrollment starts could not be saved: {error.Message}");
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Wakes on native session status and repository setting changes, or when the earliest scheduled delivery retry falls
    /// due, and dispatches waiting input, oldest first, until stopped.
    /// </summary>
    /// <remarks>An in-flight dispatch finishes on <paramref name="ct"/>; <paramref name="stop"/> only ends waiting.</remarks>
    public async Task DispatchWhenIdle(CancellationToken stop, CancellationToken ct)
    {
        sessions.StatusChanged += Wake;
        settings.Changed += Wake; // A raised active-session limit admits waiting input without another native event.
        try
        {
            TimeSpan delay;
            await gate.WaitAsync(stop);
            try { delay = NextRetry(); }
            finally { gate.Release(); }
            while (true)
            {
                await Sleep(delay, stop);
                delay = await DispatchReady(stop, ct);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            sessions.StatusChanged -= Wake;
            settings.Changed -= Wake;
        }
    }

    /// <summary>
    /// One pass of <see cref="DispatchWhenIdle"/>: settles ended turns, then dispatches waiting input, oldest first. Returns the
    /// time until the earliest scheduled delivery retry, or <see cref="Timeout.InfiniteTimeSpan"/> when none is scheduled.
    /// </summary>
    public Task<TimeSpan> DispatchReady(CancellationToken ct) => DispatchReady(ct, ct);

    async Task<TimeSpan> DispatchReady(CancellationToken stop, CancellationToken ct)
    {
        await gate.WaitAsync(stop);
        try
        {
            Settle();
            // Unreadable waiting input fails its delivery below, as reading it for delivery did before the records were typed.
            var ready = store.Discussions.Values.Where(record => (record.Waiting is not null || record.UnreadableKeys.ContainsKey(RecordKey.Waiting)) && record.Pending is null)
                .OrderBy(record => record.Waiting?.Inputs[0].ObservedAt ?? DateTimeOffset.MinValue).ToArray();
            foreach (var record in ready)
            {
                try { await Dispatch(record, ct); }
                catch (Exception error) when (error is not OperationCanceledException)
                { Log.Write("dispatch-error", $"{record.Url}: {error.Message}"); }
            }
            return NextRetry();
        }
        finally { gate.Release(); }
    }

    void Wake() { if (wake.CurrentCount == 0) try { wake.Release(); } catch (SemaphoreFullException) { } }

    async Task Sleep(TimeSpan delay, CancellationToken stop)
    {
        if (delay == Timeout.InfiniteTimeSpan) { await wake.WaitAsync(stop); return; }
        using var due = new CancellationTokenSource(delay, clock);
        using var either = CancellationTokenSource.CreateLinkedTokenSource(stop, due.Token);
        try { await wake.WaitAsync(either.Token); }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
    }

    // Caller holds the gate. Time until the earliest retry still in the future; a due retry waits for observation or status.
    TimeSpan NextRetry()
    {
        var now = clock.GetUtcNow();
        var due = store.Discussions.Values.Select(record => record.Waiting is { } waiting ? RetryAt(waiting) : DateTimeOffset.MinValue)
            .Where(at => at > now).DefaultIfEmpty().Min();
        return due == default ? Timeout.InfiniteTimeSpan : due - now;
    }

    static DateTimeOffset RetryAt(WaitingInput waiting) => waiting.Retry?.After ?? DateTimeOffset.MinValue;

    // Caller holds the gate. Model labels are configuration, not input: a change is kept for the next turn and queues nothing.
    async Task ObserveLabels(DiscussionRecord record, string id, CancellationToken ct)
    {
        string[] labels;
        try { labels = (await discussions.Labels(id, ct)).Where(ModelSelection.IsLabel).Order(StringComparer.Ordinal).ToArray(); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Conversation continues on the last observed labels; the failure stays visible in logs and status.
            Report("label-read-error", record.Url, error.Message + "; the last observed model labels still apply");
            return;
        }
        Report("label-read-error", record.Url, null);
        // Labels in a form no writer leaves never equal those observed, as their JSON compared before the records were typed.
        if (!record.UnreadableKeys.Remove(RecordKey.Labels) && (record.Labels is null ? labels.Length == 0 : labels.Length != 0 && record.Labels.SequenceEqual(labels))) return;
        record.Labels = labels.Length == 0 ? null : labels;
        store.Save();
    }

    // Caller holds the gate. Observed changes wait durably, in order, with their receipt source; the checkpoint advances.
    void Queue(DiscussionRecord record, DiscussionConversation conversation, List<JsonObject> changes, List<string?> receipts, JsonObject? context,
        OrderedDictionary<string, SourceCheckpoint> current)
    {
        var discussion = DiscussionJson.Public(conversation.Discussion).ToJsonString();
        var waiting = record.Waiting ?? new WaitingInput(discussion);
        record.UnreadableKeys.Remove(RecordKey.Waiting);
        waiting.DiscussionJson = discussion;
        // Bootstrap context travels with the first batch delivered to the session.
        if (context is not null) waiting.ContextJson = context.ToJsonString();
        var sources = conversation.Messages;
        var observed = clock.GetUtcNow();
        for (var index = 0; index < changes.Count; index++)
            waiting.Inputs.Add(new(observed, changes[index].ToJsonString(),
                receipts[index] is { } subject ? EyesReceipt.Of(sources.First(source => source.Message.Id == subject).Message) : null));
        record.Waiting = waiting;
        record.Sources = current;
        store.Save();
    }

    // Caller holds the gate. Returns whether a batch was accepted; its EYES receipts are then retained in the outbox.
    async Task<bool> Dispatch(DiscussionRecord record, CancellationToken ct)
    {
        // Its waiting input, session ID, and URL for the reports below, threw when read for delivery before the records were typed.
        if (record.UnreadableKeys.FirstOrDefault(key => key.Key is RecordKey.Waiting or RecordKey.SessionId or RecordKey.Url).Value is { } unreadable) throw new InvalidDataException(unreadable.Reason);
        var waiting = record.Waiting!;
        var session = record.SessionId ?? "";
        var create = session.Length == 0;
        if (sessions.AuthenticationFailure is not null)
        {
            // A turn would fail after its EYES receipt; queued input waits for replacement credentials.
            if (held.Add(record.Url)) Log.Write("input-held", $"{record.Url} waiting={waiting.Inputs.Count}: Codex account credentials were rejected; input waits for replacement credentials");
            return false;
        }
        held.Clear();
        // An active or unrecognized loaded session keeps its input here without taking global admission. The session of a
        // failed turn (systemError) takes its next input without a restart.
        if (!create && sessions.Loaded(session) && !AgentSessions.Admits(sessions.Status(session))) return false;
        // After a failed attempt the batch waits for its backoff.
        if (RetryAt(waiting) > clock.GetUtcNow()) return false;
        // Global capacity counts running native turns only; stored idle sessions and retrying deliveries take no slot.
        if (sessions.ActiveTurns >= ActiveSessionLimit) return false;
        var inputs = waiting.Inputs;
        var bootstrap = waiting.ContextJson is not null;
        // Bootstrap supplies the initial Discussion; a continuation shares only its identity, once per batch.
        var discussion = JsonNode.Parse(waiting.DiscussionJson)!;
        var redelivery = inputs[0].Redelivery;
        var envelope = new JsonObject
        {
            ["_bridge"] = new JsonObject { ["channel"] = "github-discussions", ["mode"] = bootstrap ? "bootstrap" : "continue", ["repository"] = repository, ["agentLogin"] = identity.AgentLogin, ["mentionLogin"] = identity.MentionUser?.Login },
            ["discussion"] = bootstrap ? discussion : new JsonObject { ["id"] = discussion.Text("id"), ["number"] = discussion["number"]?.Copy(), ["url"] = discussion.Text("url") }
        };
        // The native may already hold this input; the marker lets the agent check its history before repeating work.
        if (redelivery) envelope["_bridge"]!["redelivery"] = true;
        if (bootstrap) envelope["context"] = JsonNode.Parse(waiting.ContextJson!);
        envelope["changes"] = new JsonArray();
        var count = Batch(envelope, inputs, record);
        envelope["changes"] = new JsonArray(inputs.Take(count).Select(input => JsonNode.Parse(input.ChangeJson)).ToArray());
        var receipts = inputs.Take(count).Select(input => input.Receipt).OfType<EyesReceipt>().DistinctBy(receipt => receipt.SubjectId).ToArray();
        // Global admission: checkout refresh exclusion; the active-session limit was checked above.
        using var admission = refresh is null ? null : await refresh.Admit(ct);
        // Selected after admission applied any due checkout refresh, so the refreshed project configuration counts.
        // Its native queries are read-only, so a failure or unusable selection releases the admission.
        ModelChoice? selection;
        try { selection = await Select(record, ct); }
        catch { admission?.Abandon(); throw; }
        if (selection is null) { admission?.Abandon(); return false; }
        // Durable while the native outcome is unknown; an interrupted lifetime leaves it for redelivery at the next start.
        var pending = new PendingDelivery(create ? "create-and-send" : "send", clock.GetUtcNow(), [.. receipts.Select(receipt => receipt.SubjectId)], count);
        var offered = false;
        var turn = "";
        try
        {
            if (create)
            {
                record.Pending = pending;
                store.Save();
                record.SessionId = session = await sessions.Start(ct);
                admission?.Bind(session);
                store.Save();
                Log.Write("session-created", $"{record.Url} session={session}");
            }
            else
            {
                admission?.Bind(session);
                await sessions.Load(session, ct);
            }
            if (!AgentSessions.Admits(sessions.Status(session)))
            {
                // Unknown, unloaded or active native status is never assumed idle.
                if (record.Pending is not null) { record.Pending = null; store.Save(); }
                admission?.Abandon();
                return false;
            }
            if (!create) { record.Pending = pending; store.Save(); }
            offered = true;
            turn = await sessions.Accept(session, envelope.ToJsonString(), selection, admission is null ? null : admission.Accepted, ct);
        }
        // A failure while this lifetime stops leaves pending; the next start retries it as an interrupted delivery. Credential
        // loss kills the native before the lifetime token is cancelled, so it is checked directly.
        catch (Exception error) when (!ct.IsCancellationRequested && !sessions.CredentialLost)
        {
            // Nothing offered or refused: no native turn holds this input. Otherwise one may, so the checkout stays excluded.
            try { if (!offered || AgentSessions.Refused(error)) admission?.Abandon(); else admission?.Unconfirmed(); }
            finally { Retry(record, count, offered, error.Message); }
            return false;
        }
        // Kept until the turn's outcome: a provider rejection of the Codex account credentials returns it to the queue.
        running[turn] = new(waiting.DiscussionJson, waiting.ContextJson, [.. inputs.Take(count)]);
        inputs.RemoveRange(0, count);
        waiting.ContextJson = null;
        waiting.Retry = null;
        if (inputs.Count == 0) record.Waiting = null;
        record.Pending = null;
        // Status reports the running turn in every mode until the native reports it completed.
        record.Running = new(turn, clock.GetUtcNow());
        store.Save();
        admission?.Confirm();
        Log.Write("forwarded", $"{record.Url} session={session} changes={count} sources={string.Join(',', receipts.Select(receipt => receipt.SubjectId))} waiting={inputs.Count} latest={envelope["changes"]!.AsArray()[^1]!["subject"]!.Text("url")}" + (redelivery ? " redelivery=true" : ""));
        foreach (var source in receipts) outbox.Receipt(source.SubjectId, source.Url, DiscussionMessage.HasEyes(source.ReactionGroups));
        return true;
    }

    // Caller holds the gate. Ends the running marker of every turn the native completed or lost. Input of a turn the provider
    // rejected for its Codex account credentials goes back first in line as a redelivery; it waits for replacement credentials,
    // since the rejection holds all dispatch.
    void Settle()
    {
        var (ended, active) = sessions.TakeTurns();
        var events = new List<(string Kind, string Message)>();
        foreach (var outcome in ended)
        {
            var batch = running.Remove(outcome.Turn, out var value) ? value : null;
            var record = store.Discussions.Values.FirstOrDefault(record => record.Running?.Turn == outcome.Turn);
            if (record is null) continue;
            record.Running = null;
            events.Add(("turn-ended", $"{record.Url} session={outcome.Session} turn={outcome.Turn} status={outcome.Status}"));
            if (outcome.CredentialsRejected && batch is not null) events.Add(("input-requeued", Requeue(record, batch, outcome.Turn)));
        }
        // A turn lost with its native process (disconnect) reports no outcome.
        foreach (var record in store.Discussions.Values.Where(record => record.Running is not null && !active.Contains(record.SessionId ?? "")).ToArray())
        {
            var turn = record.Running!.Turn;
            running.Remove(turn);
            record.Running = null;
            events.Add(("turn-ended", $"{record.Url} session={record.SessionId ?? ""} turn={turn} status=lost"));
        }
        if (events.Count == 0) return;
        // Saved first: status and dashboard compose work from the state file when these events are logged.
        store.Save();
        foreach (var (kind, message) in events) Log.Write(kind, message);
    }

    // Caller holds the gate and saves. The native accepted this batch but the provider refused the Codex account, so no agent
    // work ran; the history may still hold the input, so it is marked as a redelivery. Returns the event to log once saved.
    string Requeue(DiscussionRecord record, RunningBatch batch, string turn)
    {
        var waiting = record.Waiting ?? new WaitingInput(batch.DiscussionJson);
        if (batch.ContextJson is { } context && waiting.ContextJson is null) waiting.ContextJson = context;
        var returned = batch.Inputs;
        waiting.Inputs.InsertRange(0, returned.Select(input => input with { Redelivery = true }));
        record.Waiting = waiting;
        return $"{record.Url} session={record.SessionId ?? ""} turn={turn} inputs={returned.Count} sources={Sources(returned)}: " +
            "Codex account credentials were rejected; the input is redelivered once replacement credentials apply";
    }

    // A delivery attempt that ended with an earlier controller lifetime is retried at once; once its
    // session existed, a turn/start may have carried the input. Pre-queue controllers checkpointed only after acceptance,
    // so observation finds their in-flight changes again.
    void Interrupted(DiscussionRecord record)
    {
        if (record.Waiting is not { } waiting) { record.Pending = null; store.Save(); return; }
        var count = Math.Min(record.Pending!.Inputs ?? 0, waiting.Inputs.Count);
        Retry(record, count, !string.IsNullOrEmpty(record.SessionId), "delivery was interrupted with an earlier controller lifetime", immediate: true);
    }

    // Caller holds the gate. The failed batch stays first in line with a backoff; input a turn/start carried is marked
    // for redelivery, since the native may have accepted it without acknowledgement.
    void Retry(DiscussionRecord record, int count, bool offered, string reason, bool immediate = false)
    {
        var waiting = record.Waiting!;
        var inputs = waiting.Inputs;
        if (offered) for (var index = 0; index < count; index++) inputs[index] = inputs[index] with { Redelivery = true };
        var attempts = (waiting.Retry?.Attempts ?? 0) + 1;
        var after = clock.GetUtcNow() + (immediate ? TimeSpan.Zero : RetryDelay.Doubling(FirstRetry, LastRetry, attempts));
        waiting.Retry = new(attempts, after);
        record.Pending = null;
        Wake();
        // Saved first: status and dashboard compose work from the state file when this event is logged.
        try { store.Save(); }
        finally
        {
            Log.Write("delivery-retry", $"{record.Url} session={(record.SessionId is { Length: > 0 } session ? session : "none")} attempt={attempts} redelivery={(offered ? "true" : "false")} inputs={count} sources={Sources(inputs.Take(count))} retry-at={after:O}: {reason}");
        }
    }

    // Caller holds the gate. The validated selection for the Discussion's next turn, or null after reporting why its input waits.
    async Task<ModelChoice?> Select(DiscussionRecord record, CancellationToken ct)
    {
        // Labels in a form no writer leaves threw when read for selection before the records were typed.
        if (record.UnreadableKeys.GetValueOrDefault(RecordKey.Labels) is { } unreadable) throw new InvalidDataException(unreadable.Reason);
        var labels = record.Labels ?? [];
        var (nativeModel, nativeReasoning) = await sessions.ConfiguredModel(ct);
        var catalogue = await sessions.Models(ct);
        try
        {
            var selection = ModelSelection.Resolve(labels, settings[ModelSelection.ModelSetting], settings[ModelSelection.ReasoningSetting],
                nativeModel, nativeReasoning, catalogue);
            Report("selection-error", record.Url, null);
            return selection;
        }
        catch (ModelSelectionException error)
        {
            // Never substituted: the input waits until the labels, settings or account offer a usable selection.
            Report("selection-error", record.Url, error.Message);
            return null;
        }
    }

    void Report(string kind, string url, string? problem)
    {
        if (problem is null) reported.Remove((kind, url));
        else if (reported.GetValueOrDefault((kind, url)) != problem)
        {
            reported[(kind, url)] = problem;
            Log.Write(kind, $"{url}: {problem}");
        }
    }

    // Leading inputs whose changes fit one envelope; at least one, so an oversized change is sent alone, never dropped.
    // Redelivered and fresh input never share a batch, so the redelivery marker covers every change it carries.
    static int Batch(JsonObject envelope, List<ObservedInput> inputs, DiscussionRecord record)
    {
        long length = envelope.ToJsonString().Length;
        var count = 0;
        var redelivery = inputs[0].Redelivery;
        foreach (var input in inputs)
        {
            if (input.Redelivery != redelivery) break;
            var next = input.ChangeJson.Length + (count == 0 ? 0 : 1);
            if (count != 0 && length + next > MaximumBatchCharacters) break;
            length += next;
            count++;
        }
        if (count < inputs.Count) Log.Write("batch-split", $"{record.Url} dispatching={count} remaining={inputs.Count - count}");
        if (length > MaximumBatchCharacters) Log.Write("batch-oversized", $"{record.Url} characters={length}");
        return count;
    }

    // GitHub confirmed deletion: end its whole controller record, so its waiting input, delivery retry, pending marker, running
    // turn, model labels and checkpoints are gone and nothing of it is dispatched. A turn the native still runs keeps its slot
    // until the native ends it. Native conversation history is retained separately.
    async Task Forget(int number, CancellationToken ct)
    {
        var url = $"https://github.com/{repository}/discussions/{number}";
        await gate.WaitAsync(ct);
        try
        {
            var forgotten = store.Discussions.Where(entry => string.Equals(entry.Value.Url, url, StringComparison.OrdinalIgnoreCase)).ToArray();
            // Saved before this lifetime's own view changes: a failed save keeps every record, so a retry forgets it again.
            if (forgotten.Length != 0) store.Remove([.. forgotten.Select(entry => entry.Key)]);
            foreach (var (_, record) in forgotten)
            {
                if (record.Running?.Turn is { Length: > 0 } turn) running.Remove(turn);
                var stored = record.Url;
                held.Remove(stored);
                foreach (var key in reported.Keys.Where(key => key.Url == stored).ToArray()) reported.Remove(key);
            }
            if (forgotten.Length == 0) return;
            Log.Write("discussion-forgotten", $"{url} reason=not-found session={string.Join(',', forgotten.Select(entry => entry.Value.SessionId ?? ""))}");
        }
        finally { gate.Release(); }
    }

    // The checkpointed state fields whose values differ, with their current values, in checkpoint order. A field with no
    // earlier value differs unless it is null now.
    static JsonObject Changed(CheckpointState? before, CheckpointState? after, IReadOnlyList<CheckpointPart> readAsChanged)
    {
        var changed = new JsonObject();
        if (after is null) return changed;
        if (before?.Title != after.Title || readAsChanged.Contains(CheckpointPart.Title)) changed["title"] = after.Title;
        if (before?.Closed != after.Closed || readAsChanged.Contains(CheckpointPart.Closed)) changed["closed"] = after.Closed;
        if (before?.Locked != after.Locked || readAsChanged.Contains(CheckpointPart.Locked)) changed["locked"] = after.Locked;
        if (before?.AnswerId != after.AnswerId || readAsChanged.Contains(CheckpointPart.Answer)) changed["answer"] = DiscussionJson.Answer(after.AnswerId);
        if (before?.Category != after.Category || readAsChanged.Contains(CheckpointPart.Category)) changed["category"] = DiscussionJson.Category(after.Category);
        return changed;
    }

    // The same messages, each with an equal checkpoint, in any order.
    static bool Same(OrderedDictionary<string, SourceCheckpoint> previous, OrderedDictionary<string, SourceCheckpoint> current) =>
        previous.Count == current.Count && previous.All(entry => current.TryGetValue(entry.Key, out var checkpoint) && checkpoint.Equals(entry.Value));

    // The distinct receipt sources of these inputs, in order.
    static string Sources(IEnumerable<ObservedInput> inputs) =>
        string.Join(',', inputs.Select(input => input.Receipt).OfType<EyesReceipt>().Select(receipt => receipt.SubjectId).Distinct());

    // The leading existing comments within the context budget, and how many later ones carry their body in their change
    // instead (App-authored ones have no change).
    static (JsonObject? Context, HashSet<string> Included) Context(IReadOnlyList<DiscussionComment> comments)
    {
        var existing = new JsonArray();
        var included = new HashSet<string>();
        long length = 0;
        foreach (var source in comments)
        {
            var comment = DiscussionJson.Public(source);
            length += comment.ToJsonString().Length + 1;
            if (length > MaximumContextCharacters) break;
            existing.Add((JsonNode)comment);
            included.Add(source.Id);
        }
        var context = new JsonObject { ["existingComments"] = existing };
        if (included.Count < comments.Count) context["laterComments"] = comments.Count - included.Count;
        return (context, included);
    }

    // A running turn's input, as it waited: the Discussion and bootstrap context as agent input carries them, and the inputs.
    sealed record RunningBatch(string DiscussionJson, string? ContextJson, IReadOnlyList<ObservedInput> Inputs);
}
