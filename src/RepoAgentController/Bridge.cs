using System.Text.Json.Nodes;

namespace RepoAgentController;

/// <summary>
/// Observes Discussions into an ordered per-Discussion waiting queue in the state store, then dispatches the next
/// size-bounded batch only to a natively idle session. EYES follow native acceptance of the batch carrying the source.
/// </summary>
sealed class Bridge(GitHub github, Codex codex, StateStore store, GitHubOutbox outbox, HashSet<int> enroll, CheckoutRefresh? refresh = null,
    RepositorySettings? settings = null)
{
    // Envelope characters per dispatched batch; the pinned native rejects turn input above 1,048,576 characters.
    const int MaximumBatchCharacters = 256 * 1024;
    readonly HashSet<string> blockedLogged = [];
    // Model label and selection problems by kind and Discussion URL, each reported once until it changes or clears.
    readonly Dictionary<(string Kind, string Url), string> reported = [];
    // Serializes observation and idle dispatch over the shared state store.
    readonly SemaphoreSlim gate = new(1, 1), wake = new(0, 1);
    volatile bool inputWaiting = store.Discussions.Any(entry => entry.Value?["waiting"] is not null);

    /// <summary>True while any observed Discussion input waits in the controller for idle dispatch.</summary>
    public bool HasWaitingInput => inputWaiting;

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
            var id = discussion.Text("id");
            var record = store.Discussions[id]?.AsObject();
            if (record?["pending"] is not null)
            {
                if (blockedLogged.Add(id)) Log.Write("manual-recovery", $"{discussion.Text("url")} session={record.Text("sessionId")} pending={record["pending"]}");
                succeeded = false;
                continue;
            }
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
                await ObserveLabels(record, id, ct);
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
                Log.Write(record?["pending"] is null ? "observation-error" : "manual-recovery", $"{discussion.Text("url")}: {error.Message}");
            }
        }
        return succeeded;
    }

    /// <summary>Wakes on native session status changes and dispatches waiting input, oldest first, until stopped.</summary>
    /// <remarks>An in-flight dispatch finishes on <paramref name="ct"/>; <paramref name="stop"/> only ends waiting.</remarks>
    public async Task DispatchWhenIdle(CancellationToken stop, CancellationToken ct)
    {
        void Wake() { if (wake.CurrentCount == 0) try { wake.Release(); } catch (SemaphoreFullException) { } }
        codex.StatusChanged += Wake;
        try
        {
            while (true)
            {
                await wake.WaitAsync(stop);
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
                        { Log.Write(record["pending"] is null ? "dispatch-error" : "manual-recovery", $"{record.Text("url")}: {error.Message}"); }
                    }
                }
                finally { gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { codex.StatusChanged -= Wake; }
    }

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
        if (await Select(record, ct) is not { } selection) return false;
        var inputs = waiting["inputs"]!.AsArray();
        var bootstrap = waiting["context"] is not null;
        var envelope = new JsonObject
        {
            ["_bridge"] = new JsonObject { ["channel"] = "github-discussions", ["mode"] = bootstrap ? "bootstrap" : "continue", ["repository"] = github.Repository, ["agentLogin"] = github.AgentLogin, ["mentionLogin"] = github.MentionUser?.Login },
            ["discussion"] = waiting["discussion"]!.Copy(), ["changes"] = new JsonArray()
        };
        if (bootstrap) envelope["context"] = waiting["context"]!.Copy();
        var count = Batch(envelope, inputs, record);
        envelope["changes"] = new JsonArray(inputs.Take(count).Select(input => input!["change"]!.Copy()).ToArray());
        var receipts = inputs.Take(count).Select(input => input!["receipt"]!).DistinctBy(receipt => receipt.Text("id")).ToArray();
        // Global admission: checkout refresh exclusion today; capacity limits can further hold dispatch here.
        using var admission = refresh is null ? null : await refresh.Admit(ct);
        // A durable stop marker prevents automatic retry after a lost native acknowledgement.
        var pending = new JsonObject { ["operation"] = create ? "create-and-send" : "send", ["since"] = DateTimeOffset.UtcNow.ToString("O"), ["sourceIds"] = new JsonArray(receipts.Select(receipt => (JsonNode?)JsonValue.Create(receipt.Text("id"))).ToArray()), ["inputs"] = count };
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
        await codex.Accept(session, envelope, selection, ct, admission);
        for (var index = 0; index < count; index++) inputs.RemoveAt(0);
        waiting.Remove("context");
        if (inputs.Count == 0) record.Remove("waiting");
        record.Remove("pending");
        store.Save();
        inputWaiting = store.Discussions.Any(entry => entry.Value?["waiting"] is not null);
        admission?.Confirm();
        Log.Write("forwarded", $"{record.Text("url")} session={session} changes={count} sources={string.Join(',', receipts.Select(receipt => receipt.Text("id")))} waiting={inputs.Count}");
        foreach (var source in receipts) outbox.Receipt(source);
        return true;
    }

    // Caller holds the gate. The validated selection for the Discussion's next turn, or null after reporting why its input waits.
    async Task<ModelChoice?> Select(JsonObject record, CancellationToken ct)
    {
        var labels = record["labels"]?.AsArray().Select(label => label!.GetValue<string>()).ToArray() ?? [];
        var (nativeModel, nativeReasoning) = await codex.ConfiguredModel(ct);
        var catalogue = await codex.Models(ct);
        try
        {
            var selection = ModelSelection.Resolve(labels, settings?["REPOAGENT_MODEL"].Value, settings?["REPOAGENT_REASONING"].Value,
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
    static int Batch(JsonObject envelope, JsonArray inputs, JsonObject record)
    {
        long length = envelope.ToJsonString().Length;
        var count = 0;
        foreach (var input in inputs)
        {
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
