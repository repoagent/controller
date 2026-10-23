using System.Globalization;
using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>
/// Observes Discussions into an ordered per-Discussion waiting queue in the state store, then dispatches the next
/// size-bounded batch only to a natively idle session. EYES follow native acceptance of the batch carrying the source.
/// A failed or unacknowledged delivery keeps its batch first in line and retries it with backoff; input that may already
/// have reached the native is sent again marked as a redelivery, accepting possible duplicate processing.
/// </summary>
sealed class Bridge(GitHub github, Codex codex, StateStore store, GitHubOutbox outbox, HashSet<int> enroll, CheckoutRefresh? refresh = null,
    TimeProvider? time = null)
{
    // Envelope characters per dispatched batch; the pinned native rejects turn input above 1,048,576 characters.
    const int MaximumBatchCharacters = 256 * 1024;
    // Delivery retries back off from five seconds, doubling up to five minutes, with no attempt limit.
    static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5), LastRetry = TimeSpan.FromMinutes(5);
    readonly TimeProvider clock = time ?? TimeProvider.System;
    // Serializes observation and idle dispatch over the shared state store.
    readonly SemaphoreSlim gate = new(1, 1), wake = new(0, 1);
    volatile bool inputWaiting = store.Discussions.Any(entry => entry.Value?["waiting"] is not null);

    /// <summary>True while any observed Discussion input waits in the controller for idle dispatch.</summary>
    public bool HasWaitingInput => inputWaiting;

    /// <summary>Retries every delivery an earlier controller lifetime left unacknowledged. Call once, before observing.</summary>
    public void ResumeInterrupted()
    {
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
            Log.Write("discussion-unavailable", $"{github.Repository} discussion={number}");
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
                if (bootstrap && record?["waiting"] is null && !enroll.Contains(discussion["number"]!.GetValue<int>()) && !sources.Any(github.IsEnrollmentMention)) continue;
                if (record is null)
                {
                    record = new JsonObject { ["url"] = discussion.Text("url"), ["sources"] = new JsonObject() };
                    store.Discussions[id] = record;
                    Log.Write("discovered", discussion.Text("url"));
                }
                var previous = record["sources"]!.AsObject();
                var current = new JsonObject();
                var changes = new JsonArray();
                var receipts = new List<string>(); // Receipt source of each change, by index.
                foreach (var source in sources)
                {
                    var sourceId = source.Text("id");
                    var version = Checkpoint(source);
                    current[sourceId] = version;
                    var old = previous[sourceId];
                    var context = new JsonObject { ["discussion"] = Public(discussion) };
                    var rootId = source["replyTo"]?.Text("id");
                    if (rootId is { Length: > 0 }) context["threadRoot"] = Public(sources.Single(s => s.Text("id") == rootId));
                    var target = new JsonObject { ["discussionId"] = id, ["replyToId"] = sourceId == id ? null : rootId ?? sourceId };
                    void Add(string kind, JsonNode? reaction = null)
                    {
                        var change = new JsonObject
                        {
                            ["kind"] = kind, ["subject"] = Public(source), ["context"] = context.Copy(), ["replyTarget"] = target.Copy()
                        };
                        if (old is not null && reaction is null) change["previous"] = old["content"]!.Copy();
                        if (reaction is not null) change["reaction"] = reaction.Copy();
                        changes.Add((JsonNode)change);
                        receipts.Add(sourceId);
                    }
                    if (!Own(source["author"]) && !JsonNode.DeepEquals(old?["content"], version["content"]))
                        Add(sourceId == id ? (old is null ? "discussion.created" : "discussion.changed") : (old is null ? "comment.created" : "comment.edited"));
                    // Discussion state can change independently of its original author.
                    else if (sourceId == id && !bootstrap && !JsonNode.DeepEquals(old?["state"], version["state"])) Add("discussion.stateChanged");
                    var oldReactions = old?["reactions"]?.AsObject() ?? new JsonObject();
                    var newReactions = version["reactions"]!.AsObject();
                    foreach (var reaction in newReactions)
                        if (!oldReactions.ContainsKey(reaction.Key)) Add("reaction.added", reaction.Value);
                    foreach (var reaction in oldReactions)
                        if (!newReactions.ContainsKey(reaction.Key)) Add("reaction.removed", reaction.Value);
                }
                if (bootstrap && changes.Count == 0 && record["waiting"] is null)
                {
                    changes.Add((JsonNode)new JsonObject { ["kind"] = "discussion.enrolled", ["subject"] = Public(discussion), ["replyTarget"] = new JsonObject { ["discussionId"] = id, ["replyToId"] = null } });
                    receipts.Add(id);
                }
                if (changes.Count != 0) Queue(record, discussion, sources, changes, receipts, bootstrap, current);
                // Quietly remember agent output so later human reactions can be compared.
                else if (!JsonNode.DeepEquals(previous, current)) { record["sources"] = current; store.Save(); }
                if (record["waiting"] is not JsonObject waiting) continue;
                if (!await Dispatch(record, ct) && changes.Count != 0)
                    Log.Write("input-waiting", $"{discussion.Text("url")} session={record.Text("sessionId")} status={(record.Text("sessionId") is { Length: > 0 } session ? codex.Status(session).ToString() : "none")} waiting={waiting["inputs"]!.AsArray().Count} sources={string.Join(',', receipts.Distinct())}");
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                succeeded = false;
                Log.Write("observation-error", $"{discussion.Text("url")}: {error.Message}");
            }
        }
        return succeeded;
    }

    /// <summary>
    /// Wakes on native session status changes, or when the earliest scheduled delivery retry falls due, and dispatches
    /// waiting input, oldest first, until stopped.
    /// </summary>
    /// <remarks>An in-flight dispatch finishes on <paramref name="ct"/>; <paramref name="stop"/> only ends waiting.</remarks>
    public async Task DispatchWhenIdle(CancellationToken stop, CancellationToken ct)
    {
        codex.StatusChanged += Wake;
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
        finally { codex.StatusChanged -= Wake; }
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

    static TimeSpan Backoff(int attempts) => TimeSpan.FromTicks(Math.Min(LastRetry.Ticks, FirstRetry.Ticks << Math.Min(attempts - 1, 16)));

    // Caller holds the gate. Observed changes wait durably, in order, with their receipt source; the checkpoint advances.
    void Queue(JsonObject record, JsonNode discussion, List<JsonNode> sources, JsonArray changes, List<string> receipts, bool bootstrap, JsonObject current)
    {
        var waiting = record["waiting"] as JsonObject ?? new JsonObject { ["inputs"] = new JsonArray() };
        waiting["discussion"] = Public(discussion);
        // Bootstrap context travels with the first batch delivered to the session.
        if (bootstrap && waiting["context"] is null) waiting["context"] = new JsonObject { ["existingComments"] = Json.Array(sources.Skip(1).Select(Public)) };
        var inputs = waiting["inputs"]!.AsArray();
        var observed = DateTimeOffset.UtcNow.ToString("O");
        for (var index = 0; index < changes.Count; index++)
        {
            var source = sources.First(candidate => candidate.Text("id") == receipts[index]);
            inputs.Add((JsonNode)new JsonObject
            {
                ["observedAt"] = observed, ["change"] = changes[index]!.Copy(),
                ["receipt"] = new JsonObject { ["id"] = receipts[index], ["url"] = source.Text("url"), ["reactionGroups"] = source["reactionGroups"]?.Copy() }
            });
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
        // An active, failed or unrecognized loaded session keeps its input here without taking global admission.
        if (!create && codex.Loaded(session) && codex.Status(session) != Codex.SessionStatus.Idle) return false;
        // After a failed attempt the batch waits for its backoff.
        if (RetryAt(waiting) > clock.GetUtcNow()) return false;
        var inputs = waiting["inputs"]!.AsArray();
        var bootstrap = waiting["context"] is not null;
        var redelivery = inputs[0]!["redelivery"] is not null;
        var envelope = new JsonObject
        {
            ["_bridge"] = new JsonObject { ["channel"] = "github-discussions", ["mode"] = bootstrap ? "bootstrap" : "continue", ["repository"] = github.Repository, ["agentLogin"] = github.AgentLogin, ["mentionLogin"] = github.MentionUser?.Login },
            ["discussion"] = waiting["discussion"]!.Copy(), ["changes"] = new JsonArray()
        };
        // The native may already hold this input; the marker lets the agent check its history before repeating work.
        if (redelivery) envelope["_bridge"]!["redelivery"] = true;
        if (bootstrap) envelope["context"] = waiting["context"]!.Copy();
        var count = Batch(envelope, inputs, record);
        envelope["changes"] = new JsonArray(inputs.Take(count).Select(input => input!["change"]!.Copy()).ToArray());
        var receipts = inputs.Take(count).Select(input => input!["receipt"]!).DistinctBy(receipt => receipt.Text("id")).ToArray();
        // Global admission: checkout refresh exclusion today; capacity limits can further hold dispatch here.
        using var admission = refresh is null ? null : await refresh.Admit(ct);
        // Durable while the native outcome is unknown; an interrupted lifetime leaves it for redelivery at the next start.
        var pending = new JsonObject { ["operation"] = create ? "create-and-send" : "send", ["since"] = DateTimeOffset.UtcNow.ToString("O"), ["sourceIds"] = new JsonArray(receipts.Select(receipt => (JsonNode?)JsonValue.Create(receipt.Text("id"))).ToArray()), ["inputs"] = count };
        var offered = false;
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
            if (codex.Status(session) != Codex.SessionStatus.Idle)
            {
                // Unknown, unloaded, failed or active native status is never assumed idle.
                if (record.Remove("pending")) store.Save();
                admission?.Abandon();
                return false;
            }
            if (!create) { record["pending"] = pending; store.Save(); }
            offered = true;
            await codex.Accept(session, envelope, ct, admission);
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
        for (var index = 0; index < count; index++) inputs.RemoveAt(0);
        waiting.Remove("context");
        waiting.Remove("retry");
        if (inputs.Count == 0) record.Remove("waiting");
        record.Remove("pending");
        store.Save();
        inputWaiting = store.Discussions.Any(entry => entry.Value?["waiting"] is not null);
        admission?.Confirm();
        Log.Write("forwarded", $"{record.Text("url")} session={session} changes={count} sources={string.Join(',', receipts.Select(receipt => receipt.Text("id")))} waiting={inputs.Count} latest={envelope["changes"]!.AsArray()[^1]!["subject"]!.Text("url")}" + (redelivery ? " redelivery=true" : ""));
        foreach (var source in receipts) outbox.Receipt(source);
        return true;
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
        var after = clock.GetUtcNow() + (immediate ? TimeSpan.Zero : Backoff(attempts));
        waiting["retry"] = new JsonObject { ["attempts"] = attempts, ["after"] = after.ToString("O") };
        record.Remove("pending");
        Wake();
        // Saved first: status and dashboard compose work from the state file when this event is logged.
        try { store.Save(); }
        finally
        {
            Log.Write("delivery-retry", $"{record.Text("url")} session={(record.Text("sessionId") is { Length: > 0 } session ? session : "none")} attempt={attempts} redelivery={(offered ? "true" : "false")} inputs={count} sources={string.Join(',', inputs.Take(count).Select(input => input!["receipt"]!.Text("id")).Distinct())} retry-at={after:O}: {reason}");
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
        return new JsonObject { ["content"] = content, ["state"] = state, ["reactions"] = reactions };
    }

    static JsonNode Public(JsonNode source)
    {
        var copy = source.Copy();
        copy.AsObject().Remove("reactionGroups");
        copy.AsObject().Remove("bodyHTML");
        return copy;
    }
}
