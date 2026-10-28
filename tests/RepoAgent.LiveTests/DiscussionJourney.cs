using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace RepoAgent.LiveTests;

/// <summary>Opt-in compatibility evidence through the packaged operator path and real providers.</summary>
public sealed class DiscussionJourney
{
    readonly string run = Guid.NewGuid().ToString("N")[..12];
    readonly List<JsonNode> fixtures = [];
    readonly string repository = Environment.GetEnvironmentVariable("REPOAGENT_REPOSITORY") ?? "";
    readonly string actor = Environment.GetEnvironmentVariable("REPOAGENT_ACTOR") ?? "";
    readonly string evidence = Environment.GetEnvironmentVariable("REPOAGENT_EVIDENCE") ?? "/evidence";
    static bool HostedSmoke => Environment.GetEnvironmentVariable("REPOAGENT_LIVE_CASE") == "hosted-smoke";
    readonly List<object> outcomes = [];

    [Fact(Timeout = 3_600_000)]
    public async Task Packaged_bridge_preserves_conversations_skills_and_receipts_across_replacement()
    {
        Assert.True(Environment.GetEnvironmentVariable("REPOAGENT_LIVE") == "1", "NOT EXERCISED: set REPOAGENT_LIVE=1 after configuring separate agent and participant credentials.");
        Assert.NotEmpty(repository);
        Assert.NotEmpty(actor);
        Directory.CreateDirectory(evidence);
        try
        {
            var participant = (await Api("query { viewer { login } }"))["viewer"]!["login"]!.GetValue<string>();
            Assert.NotEqual(actor, participant);
            var parts = repository.Split('/');
            var repo = (await Api("query($o:String!,$r:String!){repository(owner:$o,name:$r){id discussionCategories(first:20){nodes{id name isAnswerable}}}}", new { o = parts[0], r = parts[1] }))["repository"]!;
            var categories = repo["discussionCategories"]!["nodes"]!.AsArray();
            var category = categories.FirstOrDefault(c => c!["isAnswerable"]!.GetValue<bool>()) ?? throw new InvalidOperationException("NOT EXERCISED: repository needs an answerable Discussion category.");
            if (HostedSmoke)
            {
                var marker = "HOSTED_SMOKE_" + run;
                var fixture = await Create(repo, category, "mention", $"@{actor} Authorized disposable installer smoke. Reply once with {marker}, using the authenticated GitHub response skill. Only perform this test request.");
                var number = fixture["number"]!.GetValue<int>();
                await Until(async () => (await Logs()).Split('\n').Any(line => IsEvent(line, "webhook-delivery") &&
                    line.Contains($" event=discussion action=created discussion={number} scheduled=True", StringComparison.Ordinal)), "the Discussion creation webhook through the Quick Tunnel");
                await Until(async () => (await Logs()).Split('\n').Any(line => IsEvent(line, "webhook-observed") &&
                    line.Contains($" discussion={number} success=True ", StringComparison.Ordinal)), "successful webhook observation for the fixture");
                var reply = await Reply(fixture, marker);
                await Receipt(fixture["id"]!.GetValue<string>());
                var session = await Session(fixture);
                await Until(async () =>
                {
                    var log = await Logs();
                    return log.Split('\n').Any(line => IsEvent(line, "agent-accepted") && line.Contains(" session=" + session + " ", StringComparison.Ordinal)) && AgentTurnsFinished(log);
                }, "the accepted smoke turn completed before credential closure");
                Pass("HOSTED_INSTALLER", new { discussion = fixture["url"]!.ToString(), reply = reply["url"]!.ToString(), actor, webhook = "observed", receipt = "EYES" });
                await Api("mutation($id:ID!){deleteDiscussion(input:{id:$id}){clientMutationId}}", new { id = fixture["id"]!.ToString() });
                fixtures.Clear();
                return;
            }
            var inspection = JsonNode.Parse(await File.ReadAllTextAsync("/data/control/runtime-inspection.json", TestContext.Current.CancellationToken))!;
            Assert.False(inspection["privileged"]!.GetValue<bool>());
            Pass("C14", new { runtime = inspection, selectedSkills = JsonNode.Parse(await File.ReadAllTextAsync("/data/control/skills.json", TestContext.Current.CancellationToken)) });
            if (Environment.GetEnvironmentVariable("REPOAGENT_LIVE_CASE") == "skill")
            {
                var expectedSkill = "SKILL_ACTION_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("repoagent-skill-v1:" + run)))[..16];
                var fixture = await Create(repo, category, "selected skill", $"Authorized disposable acceptance fixture. Use $acceptance-marker with correlation ID {run}, then publish its returned marker once. If the skill is unavailable, reply SKILL_UNAVAILABLE instead; do not invent an implementation.");
                await Control("replace", fixture["url"]!.ToString());
                await Reply(fixture, expectedSkill);
                Pass("C06", new { correlation = run, expectedSkill });
                await File.WriteAllTextAsync(Path.Combine(evidence, fixture["number"] + ".json"), (await Snapshot(fixture)).ToJsonString(), TestContext.Current.CancellationToken);
                await Api("mutation($id:ID!){deleteDiscussion(input:{id:$id}){clientMutationId}}", new { id = fixture["id"]!.ToString() });
                return;
            }
            var aMarker = "LIVE_A_" + run;
            var bMarker = "LIVE_B_" + run;
            var recoveryOnly = Environment.GetEnvironmentVariable("REPOAGENT_LIVE_CASE") == "recovery";
            var explicitJourney = recoveryOnly || Environment.GetEnvironmentVariable("REPOAGENT_LIVE_CASE") == "explicit-journey";
            var mention = explicitJourney ? "" : "@" + actor + " ";
            var a = await Create(repo, category, "mention", $"{mention}Authorized disposable live acceptance test. Reply once with {aMarker}. Remember the word LYCHEE for continuation; use the authenticated GitHub response skill. Only perform this test's requests.");
            if (explicitJourney) await Control("replace", a["url"]!.ToString());
            var initial = await Reply(a, aMarker);
            await Receipt(a["id"]!.GetValue<string>());
            var originalSession = await Session(a);
            Pass(explicitJourney ? "SETUP_EXPLICIT" : "C01", new { discussion = a["url"]!.ToString(), session = originalSession, reply = initial["url"]!.ToString() });
            await Until(async () => AgentTurnsFinished(await Logs()), "initial turn completed before container replacement");

            if (!recoveryOnly)
            {
                var b = await Create(repo, category, "explicit URL", $"Authorized disposable explicit-enrollment test. Reply once with {bMarker}; remember MANGOSTEEN.");
                await Control("replace", b["url"]!.GetValue<string>());
                await Reply(b, bMarker);
                await Receipt(b["id"]!.GetValue<string>());
                Assert.NotEqual(originalSession, await Session(b));
                Pass("C02", new { discussion = b["url"]!.ToString(), session = await Session(b) });

                var root1 = await Comment(a, "Reply once with ROOT_ONE_" + run);
                var root2 = await Comment(a, "Reply once with ROOT_TWO_" + run);
                await Reply(a, "ROOT_ONE_" + run, root1["id"]!.ToString());
                await Reply(a, "ROOT_TWO_" + run, root2["id"]!.ToString());
                var nested = await Comment(a, "Reply once with NESTED_" + run, root1["id"]!.ToString());
                await Reply(a, "NESTED_" + run, root1["id"]!.ToString());
                await Receipt(nested["id"]!.ToString());
                Pass("C03", new { firstRoot = root1["url"]!.ToString(), secondRoot = root2["url"]!.ToString(), nested = nested["url"]!.ToString() });

                await Receipt(root1["id"]!.ToString());
                await Api("mutation($id:ID!,$b:String!){updateDiscussionComment(input:{commentId:$id,body:$b}){comment{id}}}", new { id = root1["id"]!.ToString(), b = "Edited request: reply once with EDITED_" + run });
                await Reply(a, "EDITED_" + run, root1["id"]!.ToString());
                await Receipt(root1["id"]!.ToString());
                Assert.Contains("receipt-reused " + root1["url"]!.ToString(), await Logs());
                Pass("C04", new { editedSource = root1["url"]!.ToString() });

                var baseline = CountAcceptedSources(await Logs(), initial["id"]!.ToString());
                await Api("mutation($id:ID!){addReaction(input:{subjectId:$id,content:HOORAY}){reaction{id}}}", new { id = initial["id"]!.ToString() });
                await Until(async () => CountAcceptedSources(await Logs(), initial["id"]!.ToString()) > baseline, "human reaction to App output accepted");
                await Receipt(initial["id"]!.ToString());
                baseline = CountAcceptedSources(await Logs(), initial["id"]!.ToString());
                await Api("mutation($id:ID!){removeReaction(input:{subjectId:$id,content:HOORAY}){clientMutationId}}", new { id = initial["id"]!.ToString() });
                await Until(async () => CountAcceptedSources(await Logs(), initial["id"]!.ToString()) > baseline, "human reaction removal accepted");
                Pass("C05", new { reactionSubject = initial["url"]!.ToString() });

                var expected = "SKILL_ACTION_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("repoagent-skill-v1:" + run)))[..16];
                var skillRequest = await Comment(a, $"Use $acceptance-marker with correlation ID {run}, then publish its returned marker once in this thread. If that skill is unavailable, reply SKILL_UNAVAILABLE instead; do not invent an implementation.");
                await Reply(a, expected, skillRequest["id"]!.ToString());
                Pass("C06", new { skill = "acceptance-marker", correlation = run, expected });

                var hold = await Comment(a, $"Use $acceptance-marker's marker.py with arguments {run} --hold. It creates a ready file and waits up to 300 seconds for the test runner's release file. Run it now and publish its result once after release. If the skill is unavailable, report SKILL_UNAVAILABLE.");
                await Until(() => Task.FromResult(File.Exists("/workspace/hold-ready-" + run)), "agent skill action is active");
                var during = await Comment(a, "This additional input arrives while the previous tool is waiting. When you receive it, reply once with ACTIVE_SECOND_" + run);
                await Until(async () => Waiting(await Logs(), during["id"]!.ToString()), "second input waits during agent work");
                Assert.Equal(0, CountAcceptedSources(await Logs(), during["id"]!.ToString()));
                Assert.Equal(0, Eyes(await Source(during["id"]!.ToString())));
                var activeGate = JsonNode.Parse(await File.ReadAllTextAsync("/workspace/hold-ready-" + run, TestContext.Current.CancellationToken))!;
                Assert.True(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 < activeGate["expiresAt"]!.GetValue<double>(), "agent skill gate remains active while the additional input waits");
                Assert.False(File.Exists("/workspace/hold-release-" + run));
                File.WriteAllText("/workspace/hold-release-" + run, "release");
                await Until(async () => CountAcceptedSources(await Logs(), during["id"]!.ToString()) > 0, "second input accepted after agent idle");
                await Receipt(during["id"]!.ToString());
                await Reply(a, expected, hold["id"]!.ToString());
                await Reply(a, "ACTIVE_SECOND_" + run, during["id"]!.ToString());
                Pass("C07", new { activeSource = hold["url"]!.ToString(), additionalSource = during["url"]!.ToString() });

                await Until(async () => AgentTurnsFinished(await Logs()), "agent turns completed before quiet observation");
                var quietBaseline = Forwarded(await Logs());
                await StableAcrossPolls(log => Forwarded(log) == quietBaseline, "unchanged polls and App-only output stay quiet");
                Pass("C08", new { acceptedInputs = quietBaseline, completeUnchangedPolls = 2 });

                await Control("replace");
                var continued = await Comment(a, "After container replacement, reply once with RESTART_" + run + " and the fruit you remember from my first request.");
                var afterRestart = await Reply(a, "RESTART_" + run, continued["id"]!.ToString());
                Assert.Contains("LYCHEE", afterRestart["body"]!.ToString(), StringComparison.OrdinalIgnoreCase);
                Assert.Equal(originalSession, await Session(a));
                Assert.Contains("session-resumed " + originalSession, await Logs());
                Pass("C09", new { session = originalSession, reply = afterRestart["url"]!.ToString() });
                await Until(async () => AgentTurnsFinished(await Logs()), "continued turn completed before the rejection fixture");
            }

            // The operator path stops the bridge, backs up state and substitutes an invalid agent ID.
            await Control("invalidate-session", a["id"]!.ToString());
            var rejected = await Comment(a, "Rejected-input fixture: reply only if this input is accepted. Marker REJECTED_" + run);
            await Until(async () => HasRetryForSource(await Logs(), a["url"]!.ToString(), rejected["id"]!.ToString()), "agent rejection retried for this source and the deliberately invalid session");
            Assert.Equal(0, Eyes(await Source(rejected["id"]!.ToString())));
            var rejectedCount = Forwarded(await Logs());
            await StableAcrossPolls(log => Forwarded(log) == rejectedCount, "rejected input keeps retrying without acceptance", requireSuccessfulPolls: false);
            Assert.Equal(0, CountAcceptedSources(await Logs(), rejected["id"]!.ToString()));
            Assert.Equal(0, Eyes(await Source(rejected["id"]!.ToString())));
            await Control("restore-session", a["id"]!.ToString());
            await Reply(a, "REJECTED_" + run, rejected["id"]!.ToString());
            Pass("C10", new { rejectedSource = rejected["url"]!.ToString(), recoveredSession = originalSession });

            // Answer selection is observable state; continuation keeps the same session.
            await Api("mutation($id:ID!){markDiscussionCommentAsAnswer(input:{id:$id}){discussion{id}}}", new { id = initial["id"]!.ToString() });
            var answerContinuation = await Comment(a, "This Discussion has an accepted answer. Continue normally and reply once with ANSWER_CONT_" + run);
            await Reply(a, "ANSWER_CONT_" + run, answerContinuation["id"]!.ToString());
            Assert.Equal(originalSession, await Session(a));
            Pass("C11", new { session = originalSession });

            await Until(async () => AgentTurnsFinished(await Logs()), "turn completion before the credential fault fixture");
            // The controller holds its App token only in memory: the fixture revokes that live token at GitHub
            // through the controller's credential handoff, and only a new controller lifetime acquires another.
            var beforeFault = (await Logs()).Split('\n').Length;
            await Control("revoke-credential");
            var outage = await Comment(a, "Credential-revocation fixture: reply once with CREDENTIAL_FAULT_" + run);
            await Until(async () => (await Logs()).Split('\n').Skip(beforeFault).Any(line => IsEvent(line, "poll-error") && line.Contains("401")),
                "real GitHub rejection of the revoked controller credential");
            Assert.Equal(0, CountAcceptedSources(await Logs(), outage["id"]!.ToString()));
            Assert.Equal(0, Eyes(await Source(outage["id"]!.ToString())));
            await Control("replace");
            await Reply(a, "CREDENTIAL_FAULT_" + run, outage["id"]!.ToString());
            await Receipt(outage["id"]!.ToString());
            await StableAcrossPolls(log => CountAcceptedSources(log, outage["id"]!.ToString()) == 1, "input from the credential outage is accepted exactly once");
            Pass("C12", new { source = outage["url"]!.ToString(), mechanism = "real GitHub revoked the controller's in-memory App token; a replacement lifetime acquired a fresh token", acceptedOnce = true });

            foreach (var fixture in fixtures)
            {
                await File.WriteAllTextAsync(Path.Combine(evidence, fixture["number"] + ".json"), (await Snapshot(fixture)).ToJsonString(), TestContext.Current.CancellationToken);
                await Api("mutation($id:ID!){deleteDiscussion(input:{id:$id}){clientMutationId}}", new { id = fixture["id"]!.ToString() });
            }
            Pass("CLEANUP", new { deletedDiscussions = fixtures.Count });
        }
        finally
        {
            foreach (var fixture in fixtures)
            {
                if (File.Exists(Path.Combine(evidence, fixture["number"] + ".json"))) continue;
                try { await File.WriteAllTextAsync(Path.Combine(evidence, fixture["number"] + ".json"), (await Snapshot(fixture)).ToJsonString(), CancellationToken.None); }
                catch (Exception error) { await File.WriteAllTextAsync(Path.Combine(evidence, fixture["number"] + "-observation.txt"), error.Message, CancellationToken.None); }
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, "outcomes.json"), System.Text.Json.JsonSerializer.Serialize(outcomes), CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(evidence, "fixtures.json"), new JsonArray(fixtures.Select(x => x.DeepClone()).ToArray()).ToJsonString(), CancellationToken.None);
        }
    }

    static async Task<JsonNode> Api(string query, object? variables = null)
    {
        var start = new ProcessStartInfo("gh") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (!HostedSmoke) start.Environment["GH_CONFIG_DIR"] = "/data/participant";
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "GH_ENTERPRISE_TOKEN", "GITHUB_ENTERPRISE_TOKEN", "GH_REPO", "GH_DEBUG", "DEBUG" }) if (!HostedSmoke || name is not ("GH_TOKEN" or "GITHUB_TOKEN")) start.Environment.Remove(name);
        foreach (var arg in new[] { "api", "graphql", "--hostname", "github.com", "--method", "POST", "--input", "-" }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        await child.StandardInput.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { query, variables = variables ?? new { } }));
        child.StandardInput.Close();
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        try { await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(60)); }
        catch { if (!child.HasExited) child.Kill(true); throw; }
        var text = await output;
        Assert.True(child.ExitCode == 0, "GitHub operation failed: " + await errors);
        var result = JsonNode.Parse(text)!;
        Assert.Null(result["errors"]);
        return result["data"]!;
    }

    async Task<JsonNode> Create(JsonNode repo, JsonNode category, string title, string body)
    {
        var discussion = (await Api("mutation($i:CreateDiscussionInput!){createDiscussion(input:$i){discussion{id number url}}}", new { i = new { repositoryId = repo["id"]!.ToString(), categoryId = category["id"]!.ToString(), title = HostedSmoke ? $"[RepoAgent hosted smoke {run}] {title}" : $"[RepoAgent live {run}] {title}", body = HostedSmoke ? "<!-- repoagent.hosted-smoke/v1 -->\n" + body : body } }))["createDiscussion"]!["discussion"]!;
        fixtures.Add(discussion.DeepClone());
        return discussion;
    }

    static async Task<JsonNode> Comment(JsonNode discussion, string body, string? replyTo = null) =>
        (await Api("mutation($d:ID!,$b:String!,$r:ID){addDiscussionComment(input:{discussionId:$d,body:$b,replyToId:$r}){comment{id url}}}", new { d = discussion["id"]!.ToString(), b = body, r = replyTo }))["addDiscussionComment"]!["comment"]!;

    async Task<JsonNode> Snapshot(JsonNode discussion)
    {
        var parts = repository.Split('/');
        const string query = """
            query($o:String!,$r:String!,$n:Int!) {
              repository(owner:$o,name:$r) {
                discussion(number:$n) {
                  id url answer { id }
                  comments(first:50) { nodes {
                    id url body author { login }
                    replies(first:20) { nodes { id url body author { login } replyTo { id } } }
                  } }
                }
              }
            }
            """;
        return (await Api(query, new { o = parts[0], r = parts[1], n = discussion["number"]!.GetValue<int>() }))["repository"]!["discussion"]!;
    }

    async Task<JsonNode> Reply(JsonNode discussion, string marker, string? root = null)
    {
        JsonNode? match = null;
        await Until(async () =>
        {
            var data = await Snapshot(discussion);
            var candidates = root is null ? data["comments"]!["nodes"]!.AsArray() : data["comments"]!["nodes"]!.AsArray().SingleOrDefault(x => x!["id"]!.ToString() == root)?["replies"]?["nodes"]?.AsArray();
            if (marker.StartsWith("SKILL_ACTION_", StringComparison.Ordinal) && candidates?.Any(x => IsActor(x!["author"]?["login"]?.ToString()) && x["body"]!.ToString().Contains("SKILL_UNAVAILABLE", StringComparison.Ordinal)) == true)
                Assert.Fail("Selected skill action was absent: agent agent reported SKILL_UNAVAILABLE.");
            match = candidates?.FirstOrDefault(x => IsActor(x!["author"]?["login"]?.ToString()) && x["body"]!.ToString().Contains(marker, StringComparison.Ordinal));
            return match is not null;
        }, "App reply " + marker);
        return match!;
    }

    bool IsActor(string? login) => login == actor || login == actor.Replace("[bot]", "");
    int Eyes(JsonNode source) => source["reactionGroups"]!.AsArray().Where(g => g!["content"]!.ToString() == "EYES").SelectMany(g => g!["users"]!["nodes"]!.AsArray()).Count(u => IsActor(u!["login"]!.ToString()));
    static async Task<JsonNode> Source(string id) => (await Api("""
        query($id:ID!) { node(id:$id) {
          ... on Discussion { id reactionGroups { content users(first:5) { nodes { login } } } }
          ... on DiscussionComment { id reactionGroups { content users(first:5) { nodes { login } } } }
        } }
        """, new { id }))["node"]!;
    Task Receipt(string id) => Until(async () => Eyes(await Source(id)) == 1, "one source-specific EYES");

    static async Task Control(string operation, string? argument = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var request = new JsonObject { ["id"] = id, ["operation"] = operation, ["argument"] = argument };
        var path = "/data/control/request.json";
        await File.WriteAllTextAsync(path + ".tmp", request.ToJsonString());
        File.Move(path + ".tmp", path, true);
        await Until(() => Task.FromResult(File.Exists("/data/control/response-" + id + ".json")), "host lifecycle operation " + operation);
        var response = JsonNode.Parse(await File.ReadAllTextAsync("/data/control/response-" + id + ".json"))!;
        Assert.True(response["ok"]!.GetValue<bool>(), response["error"]?.ToString());
    }

    static async Task<string> Logs()
    {
        if (HostedSmoke)
        {
            var path = Environment.GetEnvironmentVariable("REPOAGENT_SMOKE_LOG") ?? throw new InvalidOperationException("Hosted smoke log path is required");
            try { return await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken); }
            catch (IOException) { return ""; }
        }
        await Control("capture");
        return await File.ReadAllTextAsync("/data/control/bridge.log");
    }
    static async Task<string> Session(JsonNode discussion)
    {
        var url = discussion["url"]!.ToString();
        string? session = null;
        await Until(async () =>
        {
            var line = (await Logs()).Split('\n').LastOrDefault(x => x.Contains("forwarded " + url + " session="));
            if (line is not null) session = line[(line.LastIndexOf("session=", StringComparison.Ordinal) + 8)..].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            return session is not null;
        }, "agent session ID");
        return session!;
    }
    [Fact]
    public void Retry_evidence_matches_the_source_and_the_invalid_session()
    {
        const string log = """
            2026-10-20T00:26:25.2314303+00:00 delivery-retry https://github.com/egil/repoagent/discussions/23 session=invalid-agent-session attempt=1 redelivery=false inputs=2 sources=DC_kwDOVQFj7c4BIsCJ,DC_kwDOVQFj7c4BIsCK retry-at=2026-10-20T00:26:30.2314303+00:00: Agent request failed: {
              "code": -32600,
              "message": "thread not found"
            }
            2026-10-20T00:26:50.0000000+00:00 poll-completed success=True elapsed-ms=25000
            """;
        const string url = "https://github.com/egil/repoagent/discussions/23";
        Assert.True(HasRetryForSource(log, url, "DC_kwDOVQFj7c4BIsCK"));
        Assert.True(HasRetryForSource(log, url, "DC_kwDOVQFj7c4BIsCJ"));
        Assert.False(HasRetryForSource(log, url, "DC_kwDOVQFj7c4BIsC"));
        Assert.False(HasRetryForSource(log.Replace(" delivery-retry ", " observation-error quoted: delivery-retry "), url, "DC_kwDOVQFj7c4BIsCK"));
        Assert.False(HasRetryForSource(log, url + "0", "DC_kwDOVQFj7c4BIsCK"));
        Assert.False(HasRetryForSource(log.Replace("session=invalid-agent-session", "session=other"), url, "DC_kwDOVQFj7c4BIsCK"));
    }

    static bool HasRetryForSource(string log, string url, string sourceId) => log.Split('\n').Any(line =>
        IsEvent(line, "delivery-retry") && line.Contains(" delivery-retry " + url + " session=invalid-agent-session ", StringComparison.Ordinal) &&
        Regex.Match(line, @" sources=(\S*) ") is { Success: true } sources && sources.Groups[1].Value.Split(',').Contains(sourceId));

    static bool IsEvent(string line, string kind)
    {
        var timestampEnd = line.IndexOf(' ');
        return timestampEnd > 0 && line.AsSpan(timestampEnd + 1).StartsWith(kind + " ", StringComparison.Ordinal);
    }
    static int CountAcceptedSources(string log, string id) => log.Split('\n').Count(x => IsEvent(x, "forwarded") && x.Contains(id));
    static int Forwarded(string log) => log.Split('\n').Count(x => IsEvent(x, "forwarded"));
    static bool Waiting(string log, string id) => log.Split('\n').Any(x => IsEvent(x, "input-waiting") && x.Contains(id));
    static bool AgentTurnsFinished(string log)
    {
        var lines = log.Split('\n');
        var ready = Array.FindLastIndex(lines, line => IsEvent(line, "ready"));
        var current = lines.Skip(Math.Max(ready, 0));
        var accepted = new HashSet<string>();
        var completed = new HashSet<string>();
        foreach (var line in current)
        {
            if (IsEvent(line, "agent-accepted")) accepted.Add(line[(line.LastIndexOf("turn=", StringComparison.Ordinal) + 5)..].Trim());
            if (!IsEvent(line, "agent-event")) continue;
            // The controller logs only redacted fields of agent events, never the raw message.
            var completion = Regex.Match(line, @" agent-event method=turn/completed thread=\S* turn=(\S+) ");
            if (completion.Success) completed.Add(completion.Groups[1].Value);
        }
        return accepted.IsSubsetOf(completed);
    }
    static async Task Until(Func<Task<bool>> condition, string expectation)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        Assert.Fail("Timed out waiting for " + expectation);
    }
    static async Task StableAcrossPolls(Func<string, bool> condition, string expectation, bool requireSuccessfulPolls = true)
    {
        static int Completed(string log, bool successful) => log.Split('\n').Count(line => IsEvent(line, "poll-completed") && (!successful || line.Contains("success=True")));
        var baseline = Completed(await Logs(), requireSuccessfulPolls);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(12);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var log = await Logs();
            Assert.True(condition(log), expectation);
            // The first completion may be an in-flight cycle; two more prove full unchanged observations.
            if (Completed(log, requireSuccessfulPolls) >= baseline + 3) return;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        Assert.Fail("Timed out observing completed polling cycles: " + expectation);
    }
    void Pass(string id, object detail)
    {
        outcomes.Add(new { id, status = "passed", at = DateTimeOffset.UtcNow, detail });
        Console.WriteLine($"[PASS] {id}: {System.Text.Json.JsonSerializer.Serialize(detail)}");
    }
}
