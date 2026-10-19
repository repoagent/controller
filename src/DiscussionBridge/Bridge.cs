using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DiscussionBridge;

sealed class Bridge(GitHub github, Codex codex, StateStore store, HashSet<int> enroll)
{
    readonly HashSet<string> blockedLogged = [];

    public async Task<bool> Poll(CancellationToken ct)
    {
        List<JsonNode> discussions;
        try { discussions = await github.Discussions(ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Log.Write("poll-error", error.Message);
            return false;
        }
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
                if (bootstrap && !enroll.Contains(discussion["number"]!.GetValue<int>()) && !sources.Any(MentionsAgent)) continue;
                if (record is null)
                {
                    record = new JsonObject { ["url"] = discussion.Text("url"), ["sources"] = new JsonObject() };
                    store.Discussions[id] = record;
                    Log.Write("discovered", discussion.Text("url"));
                }
                var previous = record["sources"]!.AsObject();
                var current = new JsonObject();
                var changes = new JsonArray();
                var receipts = new HashSet<string>();
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
                        changes.Add(change);
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
                if (bootstrap && changes.Count == 0)
                {
                    changes.Add(new JsonObject { ["kind"] = "discussion.enrolled", ["subject"] = Public(discussion), ["replyTarget"] = new JsonObject { ["discussionId"] = id, ["replyToId"] = null } });
                    receipts.Add(id);
                }
                if (changes.Count == 0)
                {
                    // Quietly remember agent output so later human reactions can be compared.
                    if (!JsonNode.DeepEquals(previous, current)) { record["sources"] = current; store.Save(); }
                    continue;
                }
                var envelope = new JsonObject
                {
                    ["_bridge"] = new JsonObject { ["channel"] = "github-discussions", ["mode"] = bootstrap ? "bootstrap" : "continue", ["repository"] = github.Repository, ["agentLogin"] = github.AgentLogin },
                    ["discussion"] = Public(discussion), ["changes"] = changes
                };
                if (bootstrap) envelope["context"] = new JsonObject { ["existingComments"] = Json.Array(sources.Skip(1).Select(Public)) };
                // A durable stop marker prevents automatic retry after a lost native acknowledgement.
                record["pending"] = new JsonObject { ["operation"] = bootstrap ? "create-and-send" : "send", ["since"] = DateTimeOffset.UtcNow.ToString("O"), ["sourceIds"] = new JsonArray(receipts.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()) };
                store.Save();
                if (bootstrap)
                {
                    record["sessionId"] = await codex.Start(ct);
                    store.Save();
                    Log.Write("session-created", $"{discussion.Text("url")} session={record.Text("sessionId")}");
                }
                await codex.Accept(record.Text("sessionId"), envelope, ct);
                record["sources"] = current;
                record.Remove("pending");
                store.Save();
                Log.Write("forwarded", $"{discussion.Text("url")} session={record.Text("sessionId")} changes={changes.Count} sources={string.Join(',', receipts)}");
                foreach (var source in sources.Where(s => receipts.Contains(s.Text("id"))))
                {
                    try { await github.Receipt(source, ct); }
                    catch (Exception error) when (error is not OperationCanceledException) { succeeded = false; Log.Write("receipt-error", $"{source.Text("url")}: {error.Message}; accepted input will not be replayed"); }
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                succeeded = false;
                Log.Write(record?["pending"] is null ? "observation-error" : "manual-recovery", $"{discussion.Text("url")}: {error.Message}");
            }
        }
        return succeeded;
    }

    bool Own(JsonNode? actor) => string.Equals(actor?.Text("login"), github.AgentLogin, StringComparison.OrdinalIgnoreCase);
    bool MentionsAgent(JsonNode source) => !Own(source["author"]) && Regex.IsMatch(source.Text("body"), @"(?<![\w-])@" + Regex.Escape(github.AgentLogin) + @"(?![\w-])", RegexOptions.IgnoreCase);

    JsonObject Checkpoint(JsonNode source)
    {
        var content = new JsonObject { ["body"] = source.Text("body"), ["author"] = source["author"]?.Copy() };
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
        return copy;
    }
}
