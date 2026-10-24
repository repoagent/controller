using System.Globalization;
using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>
/// Observes Discussions into an ordered per-Discussion waiting queue in the state store, then dispatches the next
/// size-bounded batch only to a natively idle session. EYES follow native acceptance of the batch carrying the source.
/// A failed or unacknowledged delivery keeps its batch first in line and retries it with backoff; input that may already
/// have reached the native is sent again marked as a redelivery, accepting possible duplicate processing.
/// </summary>
sealed class Bridge(GitHub github, Codex codex, StateStore store, GitHubOutbox outbox, HashSet<int> enroll, RepositorySettings settings,
    CheckoutRefresh? refresh = null, TimeProvider? time = null)
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
    readonly CategoryEnrollment categories = new(settings, github, store);
    // Model label and selection problems by kind and Discussion URL, each reported once until it changes or clears.
    readonly Dictionary<(string Kind, string Url), string> reported = [];
    // Serializes observation and idle dispatch over the shared state store.
    readonly SemaphoreSlim gate = new(1, 1), wake = new(0, 1);
    // Caller holds the gate. The batch each running turn carries, by turn ID, until its outcome; this lifetime only.
    readonly Dictionary<string, JsonObject> running = [];
    volatile bool inputWaiting = store.Discussions.Any(entry => entry.Value?["waiting"] is not null);

    /// <summary>True while any observed Discussion input waits in the controller for idle dispatch.</summary>
    public bool HasWaitingInput => inputWaiting;

    // Read at each admission decision, so a refreshed repository value applies to the next dispatch.
    int ActiveSessionLimit => settings[ActiveSessionSetting].Value is { } value
        ? int.Parse(value, CultureInfo.InvariantCulture) : DefaultActiveSessions;

    /// <summary>Retries every delivery an earlier controller lifetime left unacknowledged. Call once, before observing.</summary>
    public void ResumeInterrupted()
    {
        // Turns end with the native process of their lifetime.
        foreach (var record in store.Discussions.Select(entry => entry.Value!.AsObject()).Where(record => record["running"] is not null).ToArray())
        {
            record.Remove("running");
            store.Save();
        }
        foreach (var record in store.Discussions.Select(entry => entry.Value!.AsObject()).Where(record => record["pending"] is not null).ToArray())
            Interrupted(record);
    }

    public async Task<bool> Poll(CancellationToken ct)
    {
        List<JsonNode> discussions;
        try { discussions = await github.Discussions(ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.Write("poll-error", error.Message);
            return false;
        }
        return await Observe(discussions, ct);
    }

    public async Task<bool> Observe(int number, CancellationToken ct)
    {
        try
        {
            var discussion = await github.Discussion(number, ct);
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

    async Task<bool> Observe(List<JsonNode> discussions, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { return await ObserveExclusive(discussions, ct); }
        finally { gate.Release(); }
    }

    async Task<bool> ObserveExclusive(List<JsonNode> discussions, CancellationToken ct)
    {
        Settle();
        var succeeded = true;
        foreach (var discussion in discussions)
        {
            ct.ThrowIfCancellationRequested();
            if (github.IsDashboard(discussion)) continue; // Operational status, never agent input, however enrolled.
            var id = discussion.Text("id");
            var record = store.Discussions[id]?.AsObject();
            // A delivery still pending in this lifetime failed as its native stopped; the next start retries it.
            if (record?["pending"] is not null) { succeeded = false; continue; }
            try
            {
                // Read every page, including reactions to agent-authored messages.
                var sources = await github.Sources(discussion, ct);
                var bootstrap = record is null || record.Text("sessionId").Length == 0;
                var byCategory = false;
                if (bootstrap && record?["waiting"] is null && !enroll.Contains(discussion["number"]!.GetValue<int>()) && !sources.Any(github.IsEnrollmentMention))
                {
                    if (!categories.Selects(discussion)) continue;
                    byCategory = true;
                }
                if (record is null)
                {
                    record = new JsonObject { ["url"] = discussion.Text("url"), ["sources"] = new JsonObject() };
                    store.Discussions[id] = record;
                    Log.Write("discovered", discussion.Text("url") + (byCategory ? " enrollment=category" : ""));
                }
                await ObserveLabels(record, id, ct);
                var previous = record["sources"]!.AsObject();
                var current = new JsonObject();
                var changes = new JsonArray();
                var receipts = new List<string?>(); // Receipt source of each change, by index; none for a deletion.
                // The bootstrap context captured by this observation carries the existing bodies that fit its budget; the
                // changes of later comments carry their own body, so a long conversation splits into bounded batches.
                var snapshot = bootstrap && record["waiting"]?["context"] is null;
                var (context, inContext) = snapshot ? Context(sources) : (null, []);
                foreach (var source in sources)
                {
                    var sourceId = source.Text("id");
                    var version = Checkpoint(source);
                    current[sourceId] = version;
                    var old = previous[sourceId];
                    // The Discussion ID travels once per dispatched batch; each change keeps its own thread root.
                    var target = new JsonObject { ["replyToId"] = sourceId == id ? null : source["replyTo"]?.Text("id") ?? sourceId };
                    var state = Changed(old?["state"], version["state"]!);
                    void Add(string kind, JsonObject? content = null, JsonNode? reaction = null)
                    {
                        var subject = Subject(source);
                        foreach (var field in content ?? []) subject[field.Key] = field.Value?.Copy();
                        var change = new JsonObject { ["kind"] = kind, ["subject"] = subject, ["replyTarget"] = target.Copy() };
                        if (reaction is not null) change["reaction"] = reaction.Copy();
                        changes.Add((JsonNode)change);
                        receipts.Add(sourceId);
                    }
                    if (!Own(source["author"]) && !JsonNode.DeepEquals(old?["content"], version["content"]))
                    {
                        var content = new JsonObject { ["body"] = source.Text("body") };
                        foreach (var field in state) content[field.Key] = field.Value?.Copy();
                        Add(sourceId == id ? (old is null ? "discussion.created" : "discussion.changed") : (old is null ? "comment.created" : "comment.edited"),
                            snapshot && old is null && (sourceId == id || inContext.Contains(sourceId)) ? null : content);
                    }
                    // Discussion state can change independently of its original author.
                    else if (sourceId == id && !bootstrap && !JsonNode.DeepEquals(old?["state"], version["state"])) Add("discussion.stateChanged", state);
                    var oldReactions = old?["reactions"]?.AsObject() ?? new JsonObject();
                    var newReactions = version["reactions"]!.AsObject();
                    foreach (var reaction in newReactions)
                        if (!oldReactions.ContainsKey(reaction.Key)) Add("reaction.added", reaction: reaction.Value);
                    foreach (var reaction in oldReactions)
                        if (!newReactions.ContainsKey(reaction.Key)) Add("reaction.removed", reaction: reaction.Value);
                }
                // Sources returned every page, so a checkpointed comment it lacks is confirmed deleted; a failed or partial
                // read throws above and leaves the checkpoint alone. A deletion carries identity, attribution and routing
                // only, and no receipt: nothing is left to react to. A reply in a thread that is gone too routes top-level.
                foreach (var (sourceId, old) in previous)
                {
                    if (current.ContainsKey(sourceId)) continue;
                    var subject = new JsonObject { ["id"] = sourceId, ["author"] = old!["content"]!["author"]?.Copy() };
                    var root = old.Text("replyTo");
                    if (root.Length != 0) subject["replyTo"] = new JsonObject { ["id"] = root };
                    changes.Add((JsonNode)new JsonObject
                    {
                        ["kind"] = "comment.deleted", ["subject"] = subject,
                        ["replyTarget"] = new JsonObject { ["replyToId"] = current.ContainsKey(root) ? root : null }
                    });
                    receipts.Add(null);
                }
                if (bootstrap && changes.Count == 0 && record["waiting"] is null)
                {
                    changes.Add((JsonNode)new JsonObject { ["kind"] = "discussion.enrolled", ["subject"] = Subject(discussion), ["replyTarget"] = new JsonObject { ["replyToId"] = null } });
                    receipts.Add(id);
                }
                if (changes.Count != 0) Queue(record, discussion, sources, changes, receipts, context, current);
                // Quietly remember agent output so later human reactions can be compared.
                else if (!JsonNode.DeepEquals(previous, current)) { record["sources"] = current; store.Save(); }
                if (record["waiting"] is not JsonObject waiting) continue;
                if (!await Dispatch(record, ct) && changes.Count != 0)
                    Log.Write("input-waiting", $"{discussion.Text("url")} session={record.Text("sessionId")} status={(record.Text("sessionId") is { Length: > 0 } session ? codex.Status(session).ToString() : "none")} waiting={waiting["inputs"]!.AsArray().Count} sources={string.Join(',', receipts.OfType<string>().Distinct())} active={codex.ActiveTurns} limit={ActiveSessionLimit}");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                succeeded = false;
                Log.Write("observation-error", $"{discussion.Text("url")}: {error.Message}");
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

    /// <summary>Applies a refreshed repository settings change to the category selection starts while no observation or
    /// dispatch runs, so a category removed and selected again between observations starts afresh.</summary>
    public async Task ApplySettings(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { categories.Reconcile(); }
        // The starts stay reconciled in memory and are saved with the next state change; settings refresh continues.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log.Write("repository-settings-error", $"the category selection starts could not be saved: {error.Message}");
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
        codex.StatusChanged += Wake;
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
                await gate.WaitAsync(stop);
                try
                {
                    Settle();
                    var ready = store.Discussions.Select(entry => entry.Value!.AsObject())
                        .Where(record => record["waiting"] is not null && record["pending"] is null)
                        .OrderBy(record => record["waiting"]!["inputs"]![0]!.Text("observedAt"), StringComparer.Ordinal).ToArray();
                    foreach (var record in ready)
                    {
                        try { await Dispatch(record, ct); }
                        catch (Exception error) when (error is not OperationCanceledException)
                        { Log.Write("dispatch-error", $"{record.Text("url")}: {error.Message}"); }
                    }
                    delay = NextRetry();
                }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            codex.StatusChanged -= Wake;
            settings.Changed -= Wake;
        }
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
        var due = store.Discussions.Select(entry => entry.Value?["waiting"] as JsonObject)
            .Select(waiting => waiting is null ? DateTimeOffset.MinValue : RetryAt(waiting)).Where(at => at > now).DefaultIfEmpty().Min();
        return due == default ? Timeout.InfiniteTimeSpan : due - now;
    }

    static DateTimeOffset RetryAt(JsonObject waiting) => waiting["retry"]?["after"] is JsonValue after
        ? DateTimeOffset.Parse(after.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : DateTimeOffset.MinValue;


    // Caller holds the gate. Model labels are configuration, not input: a change is kept for the next turn and queues nothing.
    async Task ObserveLabels(JsonObject record, string id, CancellationToken ct)
    {
        string[] labels;
        try { labels = (await github.Labels(id, ct)).Where(ModelSelection.IsLabel).Order(StringComparer.Ordinal).ToArray(); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Conversation continues on the last observed labels; the failure stays visible in logs and status.
            Report("label-read-error", record.Text("url"), error.Message + "; the last observed model labels still apply");
            return;
        }
        Report("label-read-error", record.Text("url"), null);
        var observed = labels.Length == 0 ? null : new JsonArray(labels.Select(label => (JsonNode?)JsonValue.Create(label)).ToArray());
        if (JsonNode.DeepEquals(record["labels"], observed)) return;
        if (observed is null) record.Remove("labels"); else record["labels"] = observed;
        store.Save();
    }

    // Caller holds the gate. Observed changes wait durably, in order, with their receipt source; the checkpoint advances.
    void Queue(JsonObject record, JsonNode discussion, List<JsonNode> sources, JsonArray changes, List<string?> receipts, JsonObject? context, JsonObject current)
    {
        var waiting = record["waiting"] as JsonObject ?? new JsonObject { ["inputs"] = new JsonArray() };
        waiting["discussion"] = Public(discussion);
        // Bootstrap context travels with the first batch delivered to the session.
        if (context is not null) waiting["context"] = context;
        var inputs = waiting["inputs"]!.AsArray();
        var observed = clock.GetUtcNow().ToString("O");
        for (var index = 0; index < changes.Count; index++)
        {
            var input = new JsonObject { ["observedAt"] = observed, ["change"] = changes[index]!.Copy() };
            if (receipts[index] is { } subject)
            {
                var source = sources.First(candidate => candidate.Text("id") == subject);
                input["receipt"] = new JsonObject { ["id"] = subject, ["url"] = source.Text("url"), ["reactionGroups"] = source["reactionGroups"]?.Copy() };
            }
            inputs.Add((JsonNode)input);
        }
        record["waiting"] = waiting;
        record["sources"] = current;
        store.Save();
        inputWaiting = true;
    }

    // Caller holds the gate. Returns whether a batch was accepted; its EYES receipts are then retained in the outbox.
    async Task<bool> Dispatch(JsonObject record, CancellationToken ct)
    {
        var waiting = record["waiting"]!.AsObject();
        var session = record.Text("sessionId");
        var create = session.Length == 0;
        if (codex.AuthenticationFailure is not null)
        {
            // A turn would fail after its EYES receipt; queued input waits for replacement credentials.
            if (held.Add(record.Text("url"))) Log.Write("input-held", $"{record.Text("url")} waiting={waiting["inputs"]!.AsArray().Count}: Codex account credentials were rejected; input waits for replacement credentials");
            return false;
        }
        held.Clear();
        // An active or unrecognized loaded session keeps its input here without taking global admission. The session of a
        // failed turn (systemError) takes its next input without a restart.
        if (!create && codex.Loaded(session) && !Codex.Admits(codex.Status(session))) return false;
        // After a failed attempt the batch waits for its backoff.
        if (RetryAt(waiting) > clock.GetUtcNow()) return false;
        // Global capacity counts running native turns only; stored idle sessions and retrying deliveries take no slot.
        if (codex.ActiveTurns >= ActiveSessionLimit) return false;
        var inputs = waiting["inputs"]!.AsArray();
        var bootstrap = waiting["context"] is not null;
        // Bootstrap supplies the initial Discussion; a continuation shares only its identity, once per batch.
        var discussion = waiting["discussion"]!;
        var redelivery = inputs[0]!["redelivery"] is not null;
        var envelope = new JsonObject
        {
            ["_bridge"] = new JsonObject { ["channel"] = "github-discussions", ["mode"] = bootstrap ? "bootstrap" : "continue", ["repository"] = github.Repository, ["agentLogin"] = github.AgentLogin, ["mentionLogin"] = github.MentionUser?.Login },
            ["discussion"] = bootstrap ? discussion.Copy() : new JsonObject { ["id"] = discussion.Text("id"), ["number"] = discussion["number"]?.Copy(), ["url"] = discussion.Text("url") }
        };
        // The native may already hold this input; the marker lets the agent check its history before repeating work.
        if (redelivery) envelope["_bridge"]!["redelivery"] = true;
        if (bootstrap) envelope["context"] = waiting["context"]!.Copy();
        envelope["changes"] = new JsonArray();
        var count = Batch(envelope, inputs, record);
        envelope["changes"] = new JsonArray(inputs.Take(count).Select(input => input!["change"]!.Copy()).ToArray());
        var receipts = inputs.Take(count).Select(input => input!["receipt"]).OfType<JsonNode>().DistinctBy(receipt => receipt.Text("id")).ToArray();
        // Global admission: checkout refresh exclusion; the active-session limit was checked above.
        using var admission = refresh is null ? null : await refresh.Admit(ct);
        // Selected after admission applied any due checkout refresh, so the refreshed project configuration counts.
        // Its native queries are read-only, so a failure or unusable selection releases the admission.
        ModelChoice? selection;
        try { selection = await Select(record, ct); }
        catch { admission?.Abandon(); throw; }
        if (selection is null) { admission?.Abandon(); return false; }
        // Durable while the native outcome is unknown; an interrupted lifetime leaves it for redelivery at the next start.
        var pending = new JsonObject { ["operation"] = create ? "create-and-send" : "send", ["since"] = clock.GetUtcNow().ToString("O"), ["sourceIds"] = new JsonArray(receipts.Select(receipt => (JsonNode?)JsonValue.Create(receipt.Text("id"))).ToArray()), ["inputs"] = count };
        var offered = false;
        var turn = "";
        try
        {
            if (create)
            {
                record["pending"] = pending;
                store.Save();
                record["sessionId"] = session = await codex.Start(ct);
                admission?.Bind(session);
                store.Save();
                Log.Write("session-created", $"{record.Text("url")} session={session}");
            }
            else
            {
                admission?.Bind(session);
                await codex.Load(session, ct);
            }
            if (!Codex.Admits(codex.Status(session)))
            {
                // Unknown, unloaded or active native status is never assumed idle.
                if (record.Remove("pending")) store.Save();
                admission?.Abandon();
                return false;
            }
            if (!create) { record["pending"] = pending; store.Save(); }
            offered = true;
            turn = await codex.Accept(session, envelope, selection, ct, admission);
        }
        // A failure while this lifetime stops leaves pending; the next start retries it as an interrupted delivery. Credential
        // loss kills the native before the lifetime token is cancelled, so it is checked directly.
        catch (Exception error) when (!ct.IsCancellationRequested && !codex.CredentialLost)
        {
            // Nothing offered or refused: no native turn holds this input. Otherwise one may, so the checkout stays excluded.
            try { if (!offered || error is Codex.RequestRejected) admission?.Abandon(); else admission?.Unconfirmed(); }
            finally { Retry(record, count, offered, error.Message); }
            return false;
        }
        // Kept until the turn's outcome: a provider rejection of the Codex account credentials returns it to the queue.
        var batch = new JsonObject { ["discussion"] = discussion.Copy(), ["inputs"] = new JsonArray(inputs.Take(count).Select(input => input!.Copy()).ToArray()) };
        if (bootstrap) batch["context"] = waiting["context"]!.Copy();
        running[turn] = batch;
        for (var index = 0; index < count; index++) inputs.RemoveAt(0);
        waiting.Remove("context");
        waiting.Remove("retry");
        if (inputs.Count == 0) record.Remove("waiting");
        record.Remove("pending");
        // Status reports the running turn in every mode until the native reports it completed.
        record["running"] = new JsonObject { ["turn"] = turn, ["since"] = clock.GetUtcNow().ToString("O") };
        store.Save();
        inputWaiting = store.Discussions.Any(entry => entry.Value?["waiting"] is not null);
        admission?.Confirm();
        Log.Write("forwarded", $"{record.Text("url")} session={session} changes={count} sources={string.Join(',', receipts.Select(receipt => receipt.Text("id")))} waiting={inputs.Count} latest={envelope["changes"]!.AsArray()[^1]!["subject"]!.Text("url")}" + (redelivery ? " redelivery=true" : ""));
        foreach (var source in receipts) outbox.Receipt(source);
        return true;
    }

    // Caller holds the gate. Ends the running marker of every turn the native completed or lost. Input of a turn the provider
    // rejected for its Codex account credentials goes back first in line as a redelivery; it waits for replacement credentials,
    // since the rejection holds all dispatch.
    void Settle()
    {
        var (ended, active) = codex.TakeTurns();
        var events = new List<(string Kind, string Message)>();
        foreach (var outcome in ended)
        {
            var batch = running.Remove(outcome.Turn, out var value) ? value : null;
            var record = store.Discussions.Select(entry => entry.Value as JsonObject)
                .FirstOrDefault(record => record?["running"]?.Text("turn") == outcome.Turn);
            if (record is null) continue;
            record.Remove("running");
            events.Add(("turn-ended", $"{record.Text("url")} session={outcome.Session} turn={outcome.Turn} status={outcome.Status}"));
            if (outcome.CredentialsRejected && batch is not null) events.Add(("input-requeued", Requeue(record, batch, outcome.Turn)));
        }
        // A turn lost with its native process (disconnect) reports no outcome.
        foreach (var record in store.Discussions.Select(entry => entry.Value as JsonObject)
            .Where(record => record?["running"] is not null && !active.Contains(record.Text("sessionId"))).ToArray())
        {
            var turn = record!["running"]!.Text("turn");
            running.Remove(turn);
            record.Remove("running");
            events.Add(("turn-ended", $"{record.Text("url")} session={record.Text("sessionId")} turn={turn} status=lost"));
        }
        if (events.Count == 0) return;
        // Saved first: status and dashboard compose work from the state file when these events are logged.
        store.Save();
        inputWaiting = store.Discussions.Any(entry => entry.Value?["waiting"] is not null);
        foreach (var (kind, message) in events) Log.Write(kind, message);
    }

    // Caller holds the gate and saves. The native accepted this batch but the provider refused the Codex account, so no agent
    // work ran; the history may still hold the input, so it is marked as a redelivery. Returns the event to log once saved.
    string Requeue(JsonObject record, JsonObject batch, string turn)
    {
        var waiting = record["waiting"] as JsonObject ?? new JsonObject { ["inputs"] = new JsonArray() };
        waiting["discussion"] ??= batch["discussion"]!.Copy();
        if (batch["context"] is { } context && waiting["context"] is null) waiting["context"] = context.Copy();
        var inputs = waiting["inputs"]!.AsArray();
        var returned = batch["inputs"]!.AsArray();
        for (var index = returned.Count - 1; index >= 0; index--)
        {
            var input = returned[index]!.Copy();
            input["redelivery"] = true;
            inputs.Insert(0, input);
        }
        record["waiting"] = waiting;
        return $"{record.Text("url")} session={record.Text("sessionId")} turn={turn} inputs={returned.Count} sources={string.Join(',', returned.Select(input => input!["receipt"]).OfType<JsonNode>().Select(receipt => receipt.Text("id")).Distinct())}: " +
            "Codex account credentials were rejected; the input is redelivered once replacement credentials apply";
    }

    // A delivery attempt that ended with an earlier controller lifetime is retried at once; once its
    // session existed, a turn/start may have carried the input. Pre-queue controllers checkpointed only after acceptance,
    // so observation finds their in-flight changes again.
    void Interrupted(JsonObject record)
    {
        var pending = record["pending"]!;
        if (record["waiting"]?["inputs"] is not JsonArray inputs) { record.Remove("pending"); store.Save(); return; }
        var count = Math.Min(pending["inputs"]?.GetValue<int>() ?? 0, inputs.Count);
        Retry(record, count, record.Text("sessionId").Length != 0, "delivery was interrupted with an earlier controller lifetime", immediate: true);
    }

    // Caller holds the gate. The failed batch stays first in line with a backoff; input a turn/start carried is marked
    // for redelivery, since the native may have accepted it without acknowledgement.
    void Retry(JsonObject record, int count, bool offered, string reason, bool immediate = false)
    {
        var waiting = record["waiting"]!.AsObject();
        var inputs = waiting["inputs"]!.AsArray();
        if (offered) for (var index = 0; index < count; index++) inputs[index]!["redelivery"] = true;
        var attempts = (waiting["retry"]?["attempts"]?.GetValue<int>() ?? 0) + 1;
        var after = clock.GetUtcNow() + (immediate ? TimeSpan.Zero : RetryDelay.Doubling(FirstRetry, LastRetry, attempts));
        waiting["retry"] = new JsonObject { ["attempts"] = attempts, ["after"] = after.ToString("O") };
        record.Remove("pending");
        Wake();
        // Saved first: status and dashboard compose work from the state file when this event is logged.
        try { store.Save(); }
        finally
        {
            Log.Write("delivery-retry", $"{record.Text("url")} session={(record.Text("sessionId") is { Length: > 0 } session ? session : "none")} attempt={attempts} redelivery={(offered ? "true" : "false")} inputs={count} sources={string.Join(',', inputs.Take(count).Select(input => input!["receipt"]).OfType<JsonNode>().Select(receipt => receipt.Text("id")).Distinct())} retry-at={after:O}: {reason}");
        }
    }

    // Caller holds the gate. The validated selection for the Discussion's next turn, or null after reporting why its input waits.
    async Task<ModelChoice?> Select(JsonObject record, CancellationToken ct)
    {
        var labels = record["labels"]?.AsArray().Select(label => label!.GetValue<string>()).ToArray() ?? [];
        var (nativeModel, nativeReasoning) = await codex.ConfiguredModel(ct);
        var catalogue = await codex.Models(ct);
        try
        {
            var selection = ModelSelection.Resolve(labels, settings[ModelSelection.ModelSetting], settings[ModelSelection.ReasoningSetting],
                nativeModel, nativeReasoning, catalogue);
            Report("selection-error", record.Text("url"), null);
            return selection;
        }
        catch (ModelSelectionException error)
        {
            // Never substituted: the input waits until the labels, settings or account offer a usable selection.
            Report("selection-error", record.Text("url"), error.Message);
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
    static int Batch(JsonObject envelope, JsonArray inputs, JsonObject record)
    {
        long length = envelope.ToJsonString().Length;
        var count = 0;
        var redelivery = inputs[0]!["redelivery"] is not null;
        foreach (var input in inputs)
        {
            if ((input!["redelivery"] is not null) != redelivery) break;
            var next = input!["change"]!.ToJsonString().Length + (count == 0 ? 0 : 1);
            if (count != 0 && length + next > MaximumBatchCharacters) break;
            length += next;
            count++;
        }
        if (count < inputs.Count) Log.Write("batch-split", $"{record.Text("url")} dispatching={count} remaining={inputs.Count - count}");
        if (length > MaximumBatchCharacters) Log.Write("batch-oversized", $"{record.Text("url")} characters={length}");
        return count;
    }

    // GitHub confirmed deletion: end its whole controller record, so its waiting input, delivery retry, pending marker, running
    // turn, model labels and checkpoints are gone and nothing of it is dispatched. A turn the native still runs keeps its slot
    // until the native ends it. Native conversation history is retained separately.
    async Task Forget(int number, CancellationToken ct)
    {
        var url = $"https://github.com/{github.Repository}/discussions/{number}";
        await gate.WaitAsync(ct);
        try
        {
            var forgotten = store.Discussions.Where(entry => string.Equals(entry.Value?.Text("url"), url, StringComparison.OrdinalIgnoreCase)).ToArray();
            // Saved before this lifetime's own view changes: a failed save keeps every record, so a retry forgets it again.
            if (forgotten.Length != 0) store.Remove([.. forgotten.Select(entry => entry.Key)]);
            foreach (var (_, record) in forgotten)
            {
                if (record!["running"]?.Text("turn") is { Length: > 0 } turn) running.Remove(turn);
                var stored = record.Text("url");
                held.Remove(stored);
                foreach (var key in reported.Keys.Where(key => key.Url == stored).ToArray()) reported.Remove(key);
            }
            // Its waiting input no longer retains durable work.
            inputWaiting = store.Discussions.Any(entry => entry.Value?["waiting"] is not null);
            if (forgotten.Length == 0) return;
            Log.Write("discussion-forgotten", $"{url} reason=not-found session={string.Join(',', forgotten.Select(entry => entry.Value!.Text("sessionId")))}");
        }
        finally { gate.Release(); }
    }

    bool Own(JsonNode? actor) => github.IsAgentActor(actor);

    JsonObject Checkpoint(JsonNode source)
    {
        // Actor type is observation metadata; adding it must not replay accepted content.
        var content = new JsonObject
        {
            ["body"] = source.Text("body"),
            ["author"] = source["author"] is { } author ? new JsonObject { ["login"] = author.Text("login") } : null
        };
        var state = new JsonObject();
        foreach (var name in new[] { "title", "closed", "locked", "answer", "category" })
            if (source.AsObject().ContainsKey(name)) state[name] = source[name]?.Copy();
        var reactions = new JsonObject();
        foreach (var reaction in source["reactions"]!.AsArray())
            if (!Own(reaction!["user"])) reactions[reaction.Text("id")] = reaction.Copy();
        // updatedAt changes for our receipt/output too; content and actor-filtered reactions determine work.
        var version = new JsonObject { ["content"] = content, ["state"] = state, ["reactions"] = reactions };
        // A reply's thread root, so its deletion can still be attributed and routed. Not compared for work.
        if (source["replyTo"] is { } parent) version["replyTo"] = parent.Text("id");
        return version;
    }

    // A change subject's identity and attribution. Unchanged bodies, titles and parent comments are left out; the agent
    // has them from earlier input or fetches them by ID.
    static JsonObject Subject(JsonNode source)
    {
        var subject = new JsonObject { ["id"] = source.Text("id"), ["url"] = source.Text("url"), ["author"] = source["author"]?.Copy() };
        if (source["replyTo"] is { } parent) subject["replyTo"] = parent.Copy();
        return subject;
    }

    // The checkpointed state fields whose values differ, with their current values.
    static JsonObject Changed(JsonNode? before, JsonNode after)
    {
        var changed = new JsonObject();
        foreach (var field in after.AsObject())
            if (!JsonNode.DeepEquals(before?[field.Key], field.Value)) changed[field.Key] = field.Value?.Copy();
        return changed;
    }

    // The leading existing comments within the context budget, and how many later ones carry their body in their change
    // instead (App-authored ones have no change).
    static (JsonObject? Context, HashSet<string> Included) Context(List<JsonNode> sources)
    {
        var comments = new JsonArray();
        var included = new HashSet<string>();
        long length = 0;
        foreach (var source in sources.Skip(1))
        {
            var comment = Public(source);
            length += comment.ToJsonString().Length + 1;
            if (length > MaximumContextCharacters) break;
            comments.Add(comment);
            included.Add(source.Text("id"));
        }
        var context = new JsonObject { ["existingComments"] = comments };
        if (included.Count < sources.Count - 1) context["laterComments"] = sources.Count - 1 - included.Count;
        return (context, included);
    }

    static JsonNode Public(JsonNode source)
    {
        var copy = source.Copy();
        copy.AsObject().Remove("reactionGroups");
        copy.AsObject().Remove("bodyHTML");
        return copy;
    }
}
