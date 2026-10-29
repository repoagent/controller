using System.Globalization;

namespace RepoAgentController;

/// <summary>
/// Observes Discussions into an ordered per-Discussion waiting queue in the state store, then dispatches the next
/// size-bounded batch only to a idle at the agent runtime session. EYES follow agent acceptance of the batch carrying the source.
/// A failed or unacknowledged delivery keeps its batch first in line and retries it with backoff; input that may already
/// have reached the agent is sent again marked as a redelivery, accepting possible duplicate processing.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Managed-only semaphores have no wait handles; callbacks and dispatch share their lifetime and may still unwind during controller shutdown.")]
public sealed class Bridge(string repository, IDiscussions discussions, AppIdentity identity, AgentSessions sessions, StateStore store, GitHubOutbox outbox,
    HashSet<int> enroll, RepositorySettingsRefresh settings, IAgentInput input, IControllerLog log, TimeProvider clock, ICheckoutAdmission? admission = null)
{
    // Envelope characters per dispatched batch; the pinned agent rejects turn input above 1,048,576 characters.
    const int MaximumBatchCharacters = 256 * 1024;
    // Bootstrap context characters, leaving room in the first batch for the Discussion and its first changes.
    const int MaximumContextCharacters = MaximumBatchCharacters / 2;
    public const string ActiveSessionSetting = "REPOAGENT_MAX_ACTIVE_SESSIONS";
    /// <summary>Agent turns that may run at once across Discussions while REPOAGENT_MAX_ACTIVE_SESSIONS has no effective value.</summary>
    public const int DefaultActiveSessions = 4;
    readonly HashSet<string> held = [];
    // Delivery retries back off from five seconds, doubling up to five minutes, with no attempt limit.
    static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5), LastRetry = TimeSpan.FromMinutes(5);
    // Reads the same repository settings as the active-session limit.
    readonly CategoryEnrollment categories = new(settings, identity, store);
    // Model label and selection problems by kind and Discussion URL, each reported once until it changes or clears.
    readonly Dictionary<(string Kind, string Url), string> reported = [];
    // Serializes observation and idle dispatch over the shared state store.
    readonly SemaphoreSlim gate = new(1, 1), wake = new(0, 1);
    // Caller holds the gate. The batch each running turn carries, by turn ID, until its outcome; this lifetime only.
    readonly Dictionary<string, RunningBatch> running = [];
    // Permanent for this lifetime, guarded by gate and checked by every admission path.
    bool admissionStopped;

    // Read at each admission decision, so a refreshed repository value applies to the next dispatch.
    int ActiveSessionLimit => settings[ActiveSessionSetting].Value is { } value
        ? int.Parse(value, CultureInfo.InvariantCulture) : DefaultActiveSessions;

    /// <summary>Retries every delivery an earlier controller lifetime left unacknowledged. Call once, before observing.</summary>
    public void ResumeInterrupted()
    {
        // Turns end with the agent process of their lifetime, however their marker was left.
        foreach (var record in store.Discussions.Values.Where(record => record.Running is not null || record.UnreadableKeys.ContainsKey(RecordKey.Running)).ToArray())
        {
            if (record.Recovery is { } recovery)
                record.Recovery = recovery with { Suspended = "recovery turn ended with the controller; inspect prior work before continuing" };
            record.Running = null;
            record.UnreadableKeys.Remove(RecordKey.Running);
            store.Save();
        }
        foreach (var record in store.Discussions.Values.Where(record => record.Pending is not null).ToArray())
            if (record.Pending!.Operation is "capacity-recovery" or "transient-recovery")
            {
                if (record.Recovery is { } recovery)
                    record.Recovery = recovery with { Suspended = "recovery delivery acknowledgement was lost; inspect prior work before continuing" };
                record.Pending = null;
                store.Save();
            }
            else Interrupted(record);
    }

    public async Task<bool> Poll(CancellationToken ct)
    {
        IReadOnlyList<Discussion> listed;
        try { listed = await discussions.List(ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            log.Write("poll-error", error.Message);
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
            log.Write("observation-error", $"discussion={number}: {error.Message}");
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
            // A delivery still pending in this lifetime failed as its agent stopped; the next start retries it.
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
                    log.Write("discovered", discussion.Url + (byCategory ? " enrollment=category" : ""));
                }
                await ObserveLabels(record, id, ct);
                var previous = record.Sources;
                var current = new OrderedDictionary<string, SourceCheckpoint>();
                var changes = new List<string>();
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
                    var target = sourceId == id ? null : (source as DiscussionComment)?.ReplyToId ?? sourceId;
                    // A content change carries the state fields that changed with it; a state change carries them alone.
                    var state = new AgentChange("", source, target) { State = version.State, Fields = Changed(old?.State, version.State, old?.ReadAsChanged ?? []) };
                    void Add(AgentChange change)
                    {
                        changes.Add(input.Change(change));
                        receipts.Add(sourceId);
                    }
                    if (!identity.IsAgentActor(source.Author) && (old?.Content != version.Content || old.ReadAsChanged.Contains(CheckpointPart.Content)))
                    {
                        var kind = sourceId == id ? (old is null ? "discussion.created" : "discussion.changed") : (old is null ? "comment.created" : "comment.edited");
                        // Bootstrap supplies new content in the Discussion or its context instead.
                        Add(snapshot && old is null && (sourceId == id || inContext.Contains(sourceId)) ? new(kind, source, target) : state with { Kind = kind, Body = source.Body });
                    }
                    // Discussion state can change independently of its original author.
                    else if (sourceId == id && !bootstrap && (old is null || old.State != version.State || old.ReadAsChanged.Any(part => part != CheckpointPart.Content)))
                        Add(state with { Kind = "discussion.stateChanged" });
                    var oldReactions = old?.Reactions ?? [];
                    var (oldIds, newIds) = (oldReactions.Select(reaction => reaction.Id).ToHashSet(), version.Reactions.Select(reaction => reaction.Id).ToHashSet());
                    foreach (var reaction in version.Reactions)
                        if (!oldIds.Contains(reaction.Id)) Add(new("reaction.added", source, target) { Reaction = reaction });
                    foreach (var reaction in oldReactions)
                        if (!newIds.Contains(reaction.Id)) Add(new("reaction.removed", source, target) { Reaction = reaction });
                }
                // Sources returned every page, so a checkpointed comment it lacks is confirmed deleted; a failed or partial
                // read throws above and leaves the checkpoint alone. A deletion carries identity, attribution and routing
                // only, and no receipt: nothing is left to react to. A reply in a thread that is gone too routes top-level.
                foreach (var (sourceId, old) in previous)
                {
                    if (current.ContainsKey(sourceId)) continue;
                    var root = old.ReplyTo ?? "";
                    changes.Add(input.Deletion(new(sourceId, old.Content.Author, root.Length != 0 ? root : null, current.ContainsKey(root) ? root : null)));
                    receipts.Add(null);
                }
                if (bootstrap && changes.Count == 0 && record.Waiting is null)
                {
                    changes.Add(input.Change(new("discussion.enrolled", discussion, null)));
                    receipts.Add(id);
                }
                if (changes.Count != 0) Queue(record, conversation, changes, receipts, context, current);
                // Quietly remember agent output so later human reactions can be compared.
                else if (!Same(previous, current)) { record.Sources = current; store.Save(); }
                if (record.Waiting is not { } waiting) continue;
                if (!await Dispatch(record, ct) && changes.Count != 0)
                    log.Write("input-waiting", $"{discussion.Url} session={record.SessionId ?? ""} status={(record.SessionId is { Length: > 0 } session ? sessions.Status(session).ToString() : "none")} waiting={waiting.Inputs.Count} sources={string.Join(',', receipts.OfType<string>().Distinct())} active={sessions.ActiveTurns} limit={ActiveSessionLimit}");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                succeeded = false;
                log.Write("observation-error", $"{discussion.Url}: {error.Message}");
            }
        }
        return succeeded;
    }

    /// <summary>Runs <paramref name="action"/> while no observation or dispatch runs, so it never interleaves with agent admission.</summary>
    public async Task<T> ExcludingDispatch<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            Settle(); // Returned input is queued before a agent restart can forget the turn that held it.
            return await action();
        }
        finally { gate.Release(); }
    }

    /// <summary>Applies replacement agent credentials between dispatches and only at a proven global idle boundary.</summary>
    public Task<bool> ReplaceAgentCredentials(Action install, CancellationToken ct) => ExcludingDispatch(async () =>
    {
        if (admissionStopped) return false;
        if (admission is null) return await sessions.RestartWhenIdle(install, ct);
        var restarted = false;
        return await admission.CommitAtIdle(async idle => restarted = await sessions.RestartWhenIdle(install, idle), ct) && restarted;
    }, ct);

    /// <summary>Permanently stops new admission, then settles agent work as it ends. Only a hard abort cancels the wait.</summary>
    public async Task StopAndDrain(CancellationToken abort)
    {
        // A private pulse for each caller: dispatch has its own wake, and concurrent stops must not consume each other's signal.
        var notification = new object();
        TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Notify() { lock (notification) changed.TrySetResult(); }
        sessions.StatusChanged += Notify; // Raised after the turn journal observes the agent event.
        try
        {
            while (true)
            {
                Task next;
                lock (notification)
                {
                    changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    next = changed.Task;
                }
                await gate.WaitAsync(abort);
                try
                {
                    abort.ThrowIfCancellationRequested();
                    admissionStopped = true;
                    var settled = Settle();
                    abort.ThrowIfCancellationRequested();
                    if (settled) return;
                }
                finally { gate.Release(); }
                await next.WaitAsync(abort);
            }
        }
        finally { sessions.StatusChanged -= Notify; }
    }

    /// <summary>Reconciles category enrollment starts with a refreshed repository settings change while no observation or
    /// dispatch runs, so a category removed and selected again between observations starts afresh.</summary>
    public async Task ReconcileCategoryEnrollment(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { categories.Reconcile(); }
        // The starts stay reconciled in memory and are saved with the next state change; settings refresh continues. These are
        // the failures the Discussion store reports for a save that fails.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            log.Write("repository-settings-error", $"the category enrollment starts could not be saved: {error.Message}");
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Wakes on agent session status and repository setting changes, or when the earliest scheduled delivery retry falls
    /// due, and dispatches waiting input, oldest first, until stopped.
    /// </summary>
    /// <remarks>An in-flight dispatch finishes on <paramref name="ct"/>; <paramref name="stop"/> only ends waiting.</remarks>
    public async Task DispatchWhenIdle(CancellationToken stop, CancellationToken ct)
    {
        sessions.StatusChanged += Wake;
        settings.Changed += Wake; // A raised active-session limit admits waiting input without another agent event.
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
            var ready = store.Discussions.Values.Where(record => (record.Recovery is not null || record.Waiting is not null || record.UnreadableKeys.ContainsKey(RecordKey.Waiting)) && record.Pending is null)
                .OrderBy(record => record.Waiting?.Inputs[0].ObservedAt ?? DateTimeOffset.MinValue).ToArray();
            foreach (var record in ready)
            {
                try { await Dispatch(record, ct); }
                catch (Exception error) when (error is not OperationCanceledException)
                { log.Write("dispatch-error", $"{record.Url}: {error.Message}"); }
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
        var due = store.Discussions.Values.Select(record => record.Recovery is { Suspended: null } recovery && record.Running is null
                ? recovery.After : record.Waiting is { } waiting && record.Recovery is not { Suspended: null } ? RetryAt(waiting) : DateTimeOffset.MinValue)
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
    void Queue(DiscussionRecord record, DiscussionConversation conversation, List<string> changes, List<string?> receipts, string? context,
        OrderedDictionary<string, SourceCheckpoint> current)
    {
        var discussion = input.Discussion(conversation.Discussion);
        var waiting = record.Waiting ?? new WaitingInput(discussion);
        record.UnreadableKeys.Remove(RecordKey.Waiting);
        waiting.DiscussionJson = discussion;
        // Bootstrap context travels with the first batch delivered to the session.
        if (context is not null) waiting.ContextJson = context;
        var sources = conversation.Messages;
        var observed = clock.GetUtcNow();
        for (var index = 0; index < changes.Count; index++)
            waiting.Inputs.Add(new(observed, changes[index],
                receipts[index] is { } subject ? EyesReceipt.Of(sources.First(source => source.Message.Id == subject).Message) : null));
        record.Waiting = waiting;
        record.Sources = current;
        store.Save();
    }

    // Caller holds the gate. Returns whether a batch was accepted; its EYES receipts are then retained in the outbox.
    async Task<bool> Dispatch(DiscussionRecord record, CancellationToken ct)
    {
        if (admissionStopped) return false;
        ct.ThrowIfCancellationRequested();
        if (record.Recovery is { Suspended: null }) return await Recover(record, ct);
        // A suspended intent never generates a turn. Explicit later participant input can still finish the work once normal
        // credentials and checkout admission permit it, and only its successful outcome clears the retained intent.
        if (record.Recovery is not null && record.Waiting is null) return false;
        // Its waiting input, session ID, and URL for the reports below, threw when read for delivery before the records were typed.
        if (record.UnreadableKeys.FirstOrDefault(key => key.Key is RecordKey.Waiting or RecordKey.SessionId or RecordKey.Url or RecordKey.Recovery).Value is { } unreadable) throw new InvalidDataException(unreadable.Reason);
        var waiting = record.Waiting!;
        var session = record.SessionId ?? "";
        var create = session.Length == 0;
        if (sessions.AuthenticationFailure is not null)
        {
            // A turn would fail after its EYES receipt; queued input waits for replacement credentials.
            if (held.Add(record.Url)) log.Write("input-held", $"{record.Url} waiting={waiting.Inputs.Count}: Codex account credentials were rejected; input waits for replacement credentials");
            return false;
        }
        held.Clear();
        // An active or unrecognized loaded session keeps its input here without taking global admission. The session of a
        // failed turn (systemError) takes its next input without a restart.
        if (!create && sessions.Loaded(session) && !AgentSessions.Admits(sessions.Status(session))) return false;
        // After a failed attempt the batch waits for its backoff.
        if (RetryAt(waiting) > clock.GetUtcNow()) return false;
        // Global capacity counts running agent turns only; stored idle sessions and retrying deliveries take no slot.
        if (sessions.ActiveTurns >= ActiveSessionLimit) return false;
        var inputs = waiting.Inputs;
        // Bootstrap supplies the initial Discussion; a continuation shares only its identity, once per batch. The agent may
        // already hold redelivered input; the marker lets the agent check its history before repeating work.
        var redelivery = inputs[0].Redelivery;
        var envelope = new AgentEnvelope(repository, identity, waiting.DiscussionJson, waiting.ContextJson, redelivery, []);
        var count = Batch(input.Envelope(envelope).Length, inputs, record);
        var turnInput = input.Envelope(envelope with { Changes = [.. inputs.Take(count).Select(waited => waited.ChangeJson)] });
        // The batch's last change is about its receipt's subject; a deletion has neither.
        var latest = inputs[count - 1].Receipt?.Url ?? "";
        var receipts = inputs.Take(count).Select(input => input.Receipt).OfType<EyesReceipt>().DistinctBy(receipt => receipt.SubjectId).ToArray();
        // Global admission: checkout refresh exclusion; the active-session limit was checked above.
        using var checkout = admission is null ? null : await admission.Admit(ct);
        // Selected after admission applied any due checkout refresh, so the refreshed project configuration counts.
        // Its agent queries are read-only, so a failure or unusable selection releases the admission.
        ModelChoice? selection;
        try { selection = await Select(record, ct); }
        catch { checkout?.Abandon(); throw; }
        if (selection is null) { checkout?.Abandon(); return false; }
        // Durable while the agent outcome is unknown; an interrupted lifetime leaves it for redelivery at the next start.
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
                checkout?.Bind(session);
                store.Save();
                log.Write("session-created", $"{record.Url} session={session}");
            }
            else
            {
                checkout?.Bind(session);
                await sessions.Load(session, ct);
            }
            if (!AgentSessions.Admits(sessions.Status(session)))
            {
                // Unknown, unloaded or active agent status is never assumed idle.
                if (record.Pending is not null) { record.Pending = null; store.Save(); }
                checkout?.Abandon();
                return false;
            }
            if (!create) { record.Pending = pending; store.Save(); }
            offered = true;
            turn = await sessions.Accept(session, turnInput, selection, checkout is null ? null : checkout.Accepted, ct);
        }
        // A failure while this lifetime stops leaves pending; the next start retries it as an interrupted delivery. Credential
        // loss kills the agent before the lifetime token is cancelled, so it is checked directly.
        catch (Exception error) when (!ct.IsCancellationRequested && !sessions.CredentialLost)
        {
            // Nothing offered or refused: no agent turn holds this input. Otherwise one may, so the checkout stays excluded.
            try { if (!offered || AgentSessions.Refused(error)) checkout?.Abandon(); else checkout?.Unconfirmed(); }
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
        // Status reports the running turn in every mode until the agent reports it completed.
        record.Running = new(turn, clock.GetUtcNow()) { Selection = selection };
        store.Save();
        checkout?.Confirm();
        log.Write("forwarded", $"{record.Url} session={session} changes={count} sources={string.Join(',', receipts.Select(receipt => receipt.SubjectId))} waiting={inputs.Count} latest={latest}" + (redelivery ? " redelivery=true" : ""));
        foreach (var source in receipts) outbox.Receipt(source.SubjectId, source.Url, DiscussionMessage.HasEyes(source.ReactionGroups));
        return true;
    }

    // Recovery is an instruction to inspect the existing history, never a replay of an accepted Discussion envelope.
    async Task<bool> Recover(DiscussionRecord record, CancellationToken ct)
    {
        var recovery = record.Recovery!;
        if (record.Problem is { } problem) throw new InvalidDataException(problem);
        if (recovery.Suspended is not null || recovery.After > clock.GetUtcNow() || record.Running is not null ||
            record.Pending is not null || sessions.AuthenticationFailure is not null || sessions.CredentialLost ||
            sessions.ActiveTurns >= ActiveSessionLimit) return false;
        var session = record.SessionId;
        if (string.IsNullOrEmpty(session)) return false; // Never substitute a new session for the retained history.
        if (sessions.Loaded(session) && !AgentSessions.Admits(sessions.Status(session))) return false;
        using var checkout = admission is null ? null : await admission.Admit(ct);
        var offered = false;
        string turn;
        try
        {
            ct.ThrowIfCancellationRequested();
            checkout?.Bind(session);
            await sessions.Load(session, ct);
            if (!AgentSessions.Admits(sessions.Status(session))) { checkout?.Abandon(); return false; }
            ct.ThrowIfCancellationRequested();
            if (sessions.CredentialLost || sessions.AuthenticationFailure is not null) { checkout?.Abandon(); return false; }
            record.Pending = new(recovery.Failure.Kind == AgentFailureKind.ModelCapacity ? "capacity-recovery" : "transient-recovery", clock.GetUtcNow(), [], 0);
            store.Save();
            offered = true;
            var continuation = $"The accepted turn {recovery.FailedTurn} ended because {recovery.Failure.Description}. " +
                "Continue unfinished work in this same session. First inspect prior conversation history, the current workspace, " +
                "and external effects (including GitHub replies, commits and pull requests) to determine what already happened. " +
                "Preserve completed work and finish only what remains. Do not blindly replay the original request or assume no work ran. " +
                "If the work is already complete, report that without repeating external effects. Keep the existing Discussion reply routing and authenticated identity.";
            turn = await sessions.Accept(session, continuation, recovery.Selection, checkout is null ? null : checkout.Accepted, ct);
        }
        catch (Exception error) when (!ct.IsCancellationRequested && !sessions.CredentialLost)
        {
            try { if (!offered || AgentSessions.Refused(error)) checkout?.Abandon(); else checkout?.Unconfirmed(); }
            finally
            {
                // A request failure is not a confirmed terminal capacity failure. Keep evidence and never blindly retry it.
                record.Recovery = recovery with
                {
                    Suspended = offered && !AgentSessions.Refused(error)
                    ? "recovery delivery acknowledgement uncertain; inspect prior work before continuing"
                    : "recovery could not start; operator attention required"
                };
                record.Pending = null;
                store.Save();
                log.Write(RecoveryEvent(recovery, "held"), $"{record.Url} session={session} {RecoveryReason(recovery)} hold={record.Recovery.Suspended}");
            }
            return false;
        }
        record.Pending = null;
        record.Running = new(turn, clock.GetUtcNow()) { Selection = recovery.Selection };
        store.Save();
        checkout?.Confirm();
        log.Write(RecoveryEvent(recovery, "started"), $"{record.Url} session={session} turn={turn} {RecoveryReason(recovery)} attempts={recovery.Attempts} model={recovery.Selection.Model} effort={recovery.Selection.Effort}");
        return true;
    }

    // Caller holds the gate. Ends the running marker of every turn the agent completed or lost. Input of a turn the provider
    // rejected for its Codex account credentials goes back first in line as a redelivery; it waits for replacement credentials,
    // since the rejection holds all dispatch. Returns whether that same atomic snapshot held no agent work, only after its
    // outcomes are saved: a later ActiveTurns read could see a completion this pass has not yet consumed.
    static string RecoveryEvent(CapacityRecovery recovery, string action) =>
        (recovery.Failure.Kind == AgentFailureKind.ModelCapacity ? "capacity" : "transient") + "-recovery-" + action;

    static string RecoveryReason(CapacityRecovery recovery) => "reason=" + recovery.Failure.Reason +
        (recovery.Failure.HttpStatus is { } status ? $" http-status={status}" : "");

    bool Settle()
    {
        var (ended, active) = sessions.TakeTurns();
        var events = new List<(string Kind, string Message)>();
        foreach (var outcome in ended)
        {
            var record = store.Discussions.Values.FirstOrDefault(record => record.SessionId == outcome.Session && record.Running?.Turn == outcome.Turn);
            if (record is null) continue;
            var batch = running.Remove(outcome.Turn, out var value) ? value : null;
            var selection = record.Running!.Selection;
            record.Running = null;
            events.Add(("turn-ended", $"{record.Url} session={outcome.Session} turn={outcome.Turn} status={outcome.Status}"));
            if (outcome.Failure is { Recoverable: true } failure && selection is not null)
            {
                var recovery = new CapacityRecovery(outcome.Turn, selection, (record.Recovery?.Attempts ?? 0) + 1, clock.GetUtcNow() + TimeSpan.FromSeconds(120)) { Failure = failure };
                record.Recovery = recovery;
                events.Add((RecoveryEvent(recovery, "scheduled"), $"{record.Url} session={outcome.Session} failed-turn={outcome.Turn} {RecoveryReason(recovery)} attempts={recovery.Attempts} retry-at={recovery.After:O} model={selection.Model} effort={selection.Effort}"));
            }
            else if (record.Recovery is { } recovery)
            {
                if (outcome.Status == "completed")
                {
                    record.Recovery = null;
                    events.Add((RecoveryEvent(recovery, "completed"), $"{record.Url} session={outcome.Session} turn={outcome.Turn} {RecoveryReason(recovery)} attempts={recovery.Attempts}"));
                }
                else record.Recovery = recovery with { Suspended = "recovery ended without success: " + outcome.Status };
            }
            if (outcome.CredentialsRejected && batch is not null) events.Add(("input-requeued", Requeue(record, batch, outcome.Turn)));
        }
        // A turn lost with its agent process (disconnect) reports no outcome.
        foreach (var record in store.Discussions.Values.Where(record => record.Running is not null && !active.Contains(record.SessionId ?? "")).ToArray())
        {
            var turn = record.Running!.Turn;
            running.Remove(turn);
            record.Running = null;
            if (record.Recovery is { } recovery)
                record.Recovery = recovery with { Suspended = "agent runtime lost during recovery; checkout safety must be established before continuing" };
            events.Add(("turn-ended", $"{record.Url} session={record.SessionId ?? ""} turn={turn} status=lost"));
        }
        if (events.Count != 0)
        {
            // Saved first: status and dashboard compose work from the saved state when these events are logged.
            store.Save();
            foreach (var (kind, message) in events) log.Write(kind, message);
        }
        return active.Count == 0;
    }

    // Caller holds the gate and saves. The agent accepted this batch but the provider refused the Codex account, so no agent
    // work ran; the history may still hold the input, so it is marked as a redelivery. Returns the event to log once saved.
    static string Requeue(DiscussionRecord record, RunningBatch batch, string turn)
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
    // for redelivery, since the agent may have accepted it without acknowledgement.
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
        // Saved first: status and dashboard compose work from the saved state when this event is logged.
        try { store.Save(); }
        finally
        {
            log.Write("delivery-retry", $"{record.Url} session={(record.SessionId is { Length: > 0 } session ? session : "none")} attempt={attempts} redelivery={(offered ? "true" : "false")} inputs={count} sources={Sources(inputs.Take(count))} retry-at={after:O}: {reason}");
        }
    }

    // Caller holds the gate. The validated selection for the Discussion's next turn, or null after reporting why its input waits.
    async Task<ModelChoice?> Select(DiscussionRecord record, CancellationToken ct)
    {
        // Labels in a form no writer leaves threw when read for selection before the records were typed.
        if (record.UnreadableKeys.GetValueOrDefault(RecordKey.Labels) is { } unreadable) throw new InvalidDataException(unreadable.Reason);
        var labels = record.Labels ?? [];
        var (agentModel, agentReasoning) = await sessions.ConfiguredModel(ct);
        var catalogue = await sessions.Models(ct);
        try
        {
            var selection = ModelSelection.Resolve(labels, settings[ModelSelection.ModelSetting], settings[ModelSelection.ReasoningSetting],
                agentModel, agentReasoning, catalogue);
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
            log.Write(kind, $"{url}: {problem}");
        }
    }

    // Leading inputs whose changes fit one envelope; at least one, so an oversized change is sent alone, never dropped.
    // Redelivered and fresh input never share a batch, so the redelivery marker covers every change it carries.
    int Batch(long length, List<ObservedInput> inputs, DiscussionRecord record)
    {
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
        if (count < inputs.Count) log.Write("batch-split", $"{record.Url} dispatching={count} remaining={inputs.Count - count}");
        if (length > MaximumBatchCharacters) log.Write("batch-oversized", $"{record.Url} characters={length}");
        return count;
    }

    // GitHub confirmed deletion: end its whole controller record, so its waiting input, delivery retry, pending marker, running
    // turn, model labels and checkpoints are gone and nothing of it is dispatched. A turn the agent still runs keeps its slot
    // until the agent ends it. Agent conversation history is retained separately.
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
            log.Write("discussion-forgotten", $"{url} reason=not-found session={string.Join(',', forgotten.Select(entry => entry.Value.SessionId ?? ""))}");
        }
        finally { gate.Release(); }
    }

    // The checkpointed state fields whose values differ, in checkpoint order. A field with no earlier value differs unless it is
    // null now.
    static List<CheckpointPart> Changed(CheckpointState? before, CheckpointState? after, IReadOnlyList<CheckpointPart> readAsChanged)
    {
        if (after is null) return [];
        var changed = new List<CheckpointPart>();
        if (before?.Title != after.Title || readAsChanged.Contains(CheckpointPart.Title)) changed.Add(CheckpointPart.Title);
        if (before?.Closed != after.Closed || readAsChanged.Contains(CheckpointPart.Closed)) changed.Add(CheckpointPart.Closed);
        if (before?.Locked != after.Locked || readAsChanged.Contains(CheckpointPart.Locked)) changed.Add(CheckpointPart.Locked);
        if (before?.AnswerId != after.AnswerId || readAsChanged.Contains(CheckpointPart.Answer)) changed.Add(CheckpointPart.Answer);
        if (before?.Category != after.Category || readAsChanged.Contains(CheckpointPart.Category)) changed.Add(CheckpointPart.Category);
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
    (string? Context, HashSet<string> Included) Context(IReadOnlyList<DiscussionComment> comments)
    {
        var existing = new List<DiscussionComment>();
        var included = new HashSet<string>();
        long length = 0;
        foreach (var source in comments)
        {
            length += input.Comment(source).Length + 1;
            if (length > MaximumContextCharacters) break;
            existing.Add(source);
            included.Add(source.Id);
        }
        return (input.Context(existing, comments.Count - included.Count), included);
    }

    // A running turn's input, as it waited: the Discussion and bootstrap context as agent input carries them, and the inputs.
    sealed record RunningBatch(string DiscussionJson, string? ContextJson, IReadOnlyList<ObservedInput> Inputs);
}
