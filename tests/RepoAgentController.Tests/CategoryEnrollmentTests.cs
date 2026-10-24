using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

// The controller entrypoint, its settings reader, state, outbox, retained log, gh process and native process boundary are real.
// Only GitHub's responses and the native JSON-RPC peer are controlled; that is not live GitHub category evidence.
[Collection("Managed checkout environment")]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")] // The App gh wrapper is Linux/macOS only.
public sealed class CategoryEnrollmentTests
{
    const string Setting = "REPOAGENT_ENROLLMENT_CATEGORIES";

    [Fact]
    public async Task A_new_Discussion_in_a_configured_category_enrolls_without_a_mention_and_others_do_not()
    {
        using var world = new World();
        world.Variables[Setting] = "Agents";
        Assert.Equal(0, await world.RunOnce());

        world.Clock.Advance(TimeSpan.FromMinutes(1));
        world.Discussion(1, "Agents");
        world.Discussion(2, "General");
        // The controller's own Discussions, such as its status dashboard, never enroll themselves.
        world.Discussion(3, "Agents", body: "<!-- repoagent:controller-dashboard -->\nController status", author: World.App);
        Assert.Equal(0, await world.RunOnce());

        Assert.Equal(["D1"], world.Delivered());
        Assert.Equal(["D1"], world.Sessions());
        Assert.Equal(["D1"], world.Eyes());
        Assert.Contains(world.Log(), line => line.Contains(" discovered https://github.com/owner/repository/discussions/1 enrollment=category"));
    }

    [Fact]
    public async Task Preexisting_Discussions_in_a_newly_selected_category_wait_for_a_mention_or_explicit_URL()
    {
        using var world = new World();
        var earlier = world.Clock.GetUtcNow().AddHours(-1);
        world.Discussion(1, "Agents", created: earlier);
        world.Discussion(2, "Agents", body: "Please look, @test-app[bot].", created: earlier);
        world.Discussion(3, "General", created: earlier);
        world.Discussion(4, "General", created: earlier);
        world.Comment(4, "C4", "Asking @test-app[bot] in a comment.");
        world.Variables[Setting] = "Agents";

        Assert.Equal(0, await world.RunOnce("--enroll", "https://github.com/owner/repository/discussions/3"));

        Assert.Equal(["D2", "D3", "D4"], world.Delivered().Order());
        Assert.Equal(["D2", "D3", "D4"], world.Sessions());
        Assert.DoesNotContain(world.Log(), line => line.Contains(" enrollment=category"));
    }

    [Fact]
    public async Task Operator_category_changes_apply_to_later_Discussions_while_enrolled_sessions_continue()
    {
        using var world = new World();
        world.Variables[Setting] = "Agents";
        Assert.Equal(0, await world.RunOnce());
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        world.Discussion(1, "Agents");
        Assert.Equal(0, await world.RunOnce());

        world.Variables[Setting] = "research, Q&A";
        Assert.Equal(0, await world.RunOnce());
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        world.Discussion(2, "Agents");
        world.Discussion(3, "Research");
        world.Comment(1, "C1", "A follow-up in the Discussion enrolled earlier.");
        Assert.Equal(0, await world.RunOnce());

        Assert.Equal(["D1", "D1", "D3"], world.Delivered());
        Assert.Equal(["D1", "D3"], world.Sessions());

        // Selecting a category again starts afresh: Discussions created while it was unselected stay preexisting.
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        world.Variables[Setting] = "Research, Agents";
        Assert.Equal(0, await world.RunOnce());
        Assert.Equal(["D1", "D3"], world.Sessions());
    }

    [Fact]
    public async Task A_failed_settings_read_after_a_restart_keeps_the_selection_and_its_recorded_start()
    {
        using var world = new World();
        world.Variables[Setting] = "Agents";
        Assert.Equal(0, await world.RunOnce());

        // The next lifetime cannot read the variable; a Discussion created in the category meanwhile still enrolls.
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        world.Statuses[Setting] = 503;
        world.Discussion(1, "Agents");
        Assert.Equal(0, await world.RunOnce());
        Assert.Contains(world.Log(), line => line.EndsWith($" repository-setting name={Setting} state=read-failed value=Agents reason=\"HTTP 503\""));
        Assert.Equal(["D1"], world.Delivered());

        // A Discussion created during the outage is not preexisting once the read recovers: the first start still applies.
        world.Discussion(2, "Agents");
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        world.Statuses.Clear();
        Assert.Equal(0, await world.RunOnce());
        Assert.Equal(["D1", "D2"], world.Delivered());
    }

    [Fact]
    public async Task A_running_controller_selects_from_its_settings_refresh_without_waiting_for_the_next_observation()
    {
        using var world = new World();
        var controller = world.Start("--interval", "120");
        int exit;
        try
        {
            await world.WaitForLog(" poll-completed success=True");
            await world.Clock.WaitForTimers(TimeSpan.FromMinutes(1), 2); // Credential renewal and settings refresh.
            await world.Clock.WaitForTimers(TimeSpan.FromMinutes(2), 1); // The next poll.

            world.Variables[Setting] = "Agents";
            world.Save();
            world.Clock.Advance(TimeSpan.FromMinutes(1));
            await world.WaitForLog($" repository-setting name={Setting} state=configured value=Agents");

            // Created after the refresh read the selection and before the next observation, like a webhook-mode gap.
            world.Discussion(1, "Agents", created: world.Clock.GetUtcNow().AddSeconds(30));
            world.Clock.Advance(TimeSpan.FromMinutes(1));
            await world.WaitForLog(" forwarded https://github.com/owner/repository/discussions/1 ");
        }
        finally { exit = await world.Stop(controller); }
        Assert.Equal(0, exit);
        Assert.Equal(["D1"], world.Delivered());
    }

    [Fact]
    public async Task A_category_removed_and_selected_again_between_observations_starts_afresh()
    {
        using var world = new World();
        world.Variables[Setting] = "Agents";
        var controller = world.Start("--interval", "150");
        int exit;
        try
        {
            await world.WaitForLog(" poll-completed success=True");
            await world.Clock.WaitForTimers(TimeSpan.FromMinutes(1), 2); // Credential renewal and settings refresh.
            await world.Clock.WaitForTimers(TimeSpan.FromSeconds(150), 1); // The next poll.

            // No poll observes a Discussion until the category is selected again. Each refresh is complete once both
            // one-minute loops wait again.
            world.Variables.Remove(Setting);
            world.Save();
            world.Clock.Advance(TimeSpan.FromMinutes(1));
            await world.WaitForLog($" repository-setting name={Setting} state=absent");
            await world.Clock.WaitForTimers(TimeSpan.FromMinutes(1), 2);

            world.Discussion(1, "Agents", created: world.Clock.GetUtcNow().AddSeconds(30)); // While the category is not selected.
            world.Variables[Setting] = "Agents";
            world.Save();
            world.Clock.Advance(TimeSpan.FromMinutes(1));
            await world.WaitForLog($" repository-setting name={Setting} state=configured value=Agents", occurrences: 2);
            await world.Clock.WaitForTimers(TimeSpan.FromMinutes(1), 2);

            world.Discussion(2, "Agents", created: world.Clock.GetUtcNow().AddSeconds(10));
            world.Clock.Advance(TimeSpan.FromSeconds(30));
            await world.WaitForLog(" forwarded https://github.com/owner/repository/discussions/2 ");
        }
        finally { exit = await world.Stop(controller); }
        Assert.Equal(0, exit);
        Assert.DoesNotContain(world.Log(), line => line.Contains(" discovered https://github.com/owner/repository/discussions/1"));
        Assert.Equal(["D2"], world.Delivered());
    }

    /// <summary>Runs the real controller entrypoint against a controlled GitHub repository and native peer.</summary>
    sealed class World : IDisposable
    {
        public const string App = "test-app";
        readonly RSA key = RSA.Create(2048);
        readonly string model, requests, native, gh, privateKey, state;
        readonly string? path = Environment.GetEnvironmentVariable("PATH");
        readonly JsonArray discussions = [];
        readonly JsonObject comments = [];
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "category-enrollment-" + Guid.NewGuid().ToString("N"));
        public Dictionary<string, string> Variables { get; } = [];
        /// <summary>HTTP failure statuses for variable reads, by name.</summary>
        public Dictionary<string, int> Statuses { get; } = [];
        public Clock Clock { get; } = new();
        public RepositorySettingsTests.Provider Provider { get; } = new();

        public World()
        {
            System.IO.Directory.CreateDirectory(Directory);
            privateKey = Path.Combine(Directory, "app.pem");
            File.WriteAllText(privateKey, key.ExportRSAPrivateKeyPem());
            model = Path.Combine(Directory, "github-model.json");
            requests = Path.Combine(Directory, "github-requests.jsonl");
            native = Path.Combine(Directory, "native-requests.jsonl");
            state = Path.Combine(Directory, "bridge.json");
            gh = Path.Combine(Directory, "gh");
            var tools = Path.Combine(Directory, "native-tools");
            System.IO.Directory.CreateDirectory(tools);
            WriteExecutable(gh, GitHub.Replace("MODEL", Quote(model)).Replace("REQUESTS", Quote(requests)));
            WriteExecutable(Path.Combine(tools, "codex"), Native.Replace("NATIVE_LOG", Quote(native)));
            Environment.SetEnvironmentVariable("PATH", tools + Path.PathSeparator + path);
            Save();
        }

        /// <summary>Adds a Discussion created now on the controlled clock unless another creation time is given.</summary>
        public void Discussion(int number, string category, string? body = null, string author = "participant", DateTimeOffset? created = null)
        {
            var text = body ?? $"Discussion {number} needs no mention.";
            var at = (created ?? Clock.GetUtcNow()).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
            discussions.Add((JsonNode)new JsonObject
            {
                ["id"] = "D" + number, ["number"] = number, ["url"] = "https://github.com/owner/repository/discussions/" + number,
                ["title"] = "Discussion " + number, ["body"] = text, ["bodyHTML"] = "<p>" + text + "</p>", ["createdAt"] = at, ["updatedAt"] = at,
                ["author"] = new JsonObject { ["__typename"] = author == App ? "Bot" : "User", ["login"] = author },
                ["closed"] = false, ["locked"] = false, ["answer"] = null,
                ["category"] = new JsonObject { ["id"] = "DIC_" + category.Replace(' ', '_'), ["name"] = category }
            });
            Save();
        }

        /// <summary>Adds a participant's top-level comment to a Discussion.</summary>
        public void Comment(int discussion, string id, string body)
        {
            var list = comments["D" + discussion] as JsonArray ?? [];
            comments["D" + discussion] = list;
            list.Add((JsonNode)new JsonObject
            {
                ["id"] = id, ["url"] = $"https://github.com/owner/repository/discussions/{discussion}#discussioncomment-{id}", ["body"] = body,
                ["bodyHTML"] = "<p>" + body + "</p>", ["createdAt"] = Clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["updatedAt"] = Clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["author"] = new JsonObject { ["__typename"] = "User", ["login"] = "participant" }, ["replyTo"] = null
            });
            Save();
        }

        // Under the controlled gh's own lock, which it holds while it reads the model or records a receipt in it.
        public void Save()
        {
            if (!File.Exists(model)) File.WriteAllText(model, new JsonObject { ["eyes"] = new JsonArray() }.ToJsonString()); // Before any gh runs.
            ControllerHarness.LockedJson(model, current =>
            {
                current["variables"] = new JsonObject(Variables.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value)));
                current["statuses"] = new JsonObject(Statuses.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, pair.Value)));
                current["discussions"] = discussions.DeepClone();
                current["comments"] = comments.DeepClone();
                return current;
            }, write: true);
        }

        string[] Arguments(params string[] options) => ["--repo", "owner/repository", "--app-id", "123", "--private-key", privateKey,
            "--state", state, "--workspace", Directory, "--repository-settings", "--status-port", ControllerHarness.FreePort().ToString(), .. options];

        public async Task<int> RunOnce(params string[] options)
        {
            Save();
            using var http = new HttpClient(Provider, disposeHandler: false);
            return await BridgeCommand.Run([.. Arguments(options), "--once"], http, gh, Clock, TestContext.Current.CancellationToken);
        }

        readonly CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        public Task<int> Start(params string[] options)
        {
            Save();
            return Task.Run(async () =>
            {
                using var http = new HttpClient(Provider, disposeHandler: false);
                return await BridgeCommand.Run(Arguments(options), http, gh, Clock, shutdown.Token);
            });
        }

        public async Task<int> Stop(Task<int> run)
        {
            await shutdown.CancelAsync();
            return await run.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
        }

        // A real-time watchdog for observing the controller's retained log; it never drives the controller's clock.
        public async Task WaitForLog(string text, int occurrences = 1)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (Log().Count(line => line.Contains(text, StringComparison.Ordinal)) < occurrences)
            {
                Assert.True(DateTime.UtcNow < deadline, $"{occurrences} log lines containing '{text}' were not observed before the watchdog deadline");
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        /// <summary>Discussion IDs in the order their envelopes reached native turns.</summary>
        public IReadOnlyList<string> Delivered() => Lines(native).Where(line => line["method"]?.GetValue<string>() == "turn/start")
            .Select(line => JsonNode.Parse(line["params"]!["input"]![0]!["text"]!.GetValue<string>().Split('\n', 2)[1])!["discussion"]!["id"]!.GetValue<string>())
            .ToList();

        public IReadOnlyList<string> Sessions() => File.Exists(state)
            ? JsonNode.Parse(File.ReadAllText(state))!["discussions"]!.AsObject()
                .Where(entry => entry.Value!["sessionId"]?.GetValue<string>() is { Length: > 0 }).Select(entry => entry.Key).Order().ToList()
            : [];

        public IReadOnlyList<string> Eyes() => ControllerHarness.LockedJson(model, current => current["eyes"]!.AsArray().Select(id => id!.GetValue<string>()).Order().ToList());

        public IReadOnlyList<string> Log()
        {
            var file = Path.Combine(state + ".logs", "controller.log");
            return File.Exists(file) ? File.ReadAllLines(file) : [];
        }

        static IEnumerable<JsonNode> Lines(string file) => File.Exists(file)
            ? File.ReadAllLines(file).Where(line => line.Length != 0).Select(line => JsonNode.Parse(line)!) : [];

        static string Quote(string value) => JsonValue.Create(value).ToJsonString();

        static void WriteExecutable(string file, string source)
        {
            File.WriteAllText(file, source);
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose()
        {
            shutdown.Dispose();
            Environment.SetEnvironmentVariable("PATH", path);
            key.Dispose();
            System.IO.Directory.Delete(Directory, true);
        }

        // Starts a distinct native thread per Discussion and acknowledges each turn as already completed.
        const string Native = """
            #!/usr/bin/python3
            import json,os,sys,uuid
            for line in sys.stdin:
             request=json.loads(line)
             with open(NATIVE_LOG,'a') as log:log.write(line.rstrip('\n')+'\n')
             if 'id' not in request:continue
             method=request['method'];params=request.get('params') or {}
             if method=='initialize':result={'codexHome':os.getcwd()}
             elif method=='thread/start':result={'thread':{'id':'thread-'+uuid.uuid4().hex,'status':{'type':'idle'}}}
             elif method=='thread/resume':result={'thread':{'id':params['threadId'],'status':{'type':'idle'}}}
             elif method=='turn/start':result={'turn':{'id':'turn-'+uuid.uuid4().hex,'status':'completed'}}
             elif method=='model/list':result={'data':[{'model':'gpt-6.1-sol','isDefault':True,'defaultReasoningEffort':'low','supportedReasoningEfforts':[{'reasoningEffort':'low'}]}]}
             elif method=='config/read':result={'config':{}}
             else:result={}
             print(json.dumps({'id':request['id'],'result':result}),flush=True)
            """;

        // A stateful controlled repository: Actions variables, Discussions with categories, comments and EYES receipts.
        const string GitHub = """
            #!/usr/bin/python3
            import fcntl,json,sys
            a=sys.argv[1:]
            endpoint=a[1];method=a[a.index('--method')+1];include='--include' in a
            request=json.loads(sys.stdin.read()) if '--input' in a else {}
            query=request.get('query','');variables=request.get('variables') or {}
            page=lambda nodes:{'nodes':nodes,'pageInfo':{'hasNextPage':False,'endCursor':None}}
            def reply(body,status=200):
             if include:sys.stdout.write('HTTP/2.0 %d Fixture\r\nContent-Type: application/json\r\n\r\n'%status)
             sys.stdout.write(json.dumps(body))
             if status>=400:sys.stderr.write('gh: Fixture failure (HTTP %d)\n'%status);sys.exit(1)
             sys.exit(0)
            with open(MODEL,'r+') as handle:
             fcntl.flock(handle,fcntl.LOCK_EX)
             world=json.load(handle)
             with open(REQUESTS,'a') as log:log.write(json.dumps({'method':method,'endpoint':endpoint,'query':query})+'\n')
             groups=lambda id:[{'content':'EYES','viewerHasReacted':id in world['eyes']}]
             message=lambda item:dict(item,reactionGroups=groups(item['id']))
             prefix='repos/owner/repository/actions/variables/'
             if method=='GET' and endpoint.startswith(prefix):
              name=endpoint[len(prefix):]
              if name in world.get('statuses',{}):reply({'message':'Fixture failure','status':str(world['statuses'][name])},world['statuses'][name])
              if name in world['variables']:reply({'name':name,'value':world['variables'][name],'created_at':'2026-10-23T00:00:00Z','updated_at':'2026-10-23T00:00:00Z'})
              reply({'message':'Not Found','status':'404'},404)
             if endpoint!='graphql':sys.stderr.write('Unexpected GitHub command\n');sys.exit(2)
             if 'addReaction' in query:
              if variables['id'] not in world['eyes']:world['eyes'].append(variables['id'])
              handle.seek(0);handle.truncate();json.dump(world,handle);handle.flush()
              reply({'data':{'addReaction':{'reaction':{'id':'R-'+variables['id']}}}})
             if 'query { viewer' in query:reply({'data':{'viewer':{'login':'test-app[bot]'}}})
             if 'discussions(' in query:reply({'data':{'repository':{'discussions':page([message(d) for d in world['discussions']])}}})
             if 'discussion(number' in query:reply({'data':{'repository':{'discussion':next((message(d) for d in world['discussions'] if d['number']==variables['number']),None)}}})
             if 'comments(' in query:reply({'data':{'node':{'comments':page([message(c) for c in world['comments'].get(variables['id'],[])])}}})
             if 'replies(' in query:reply({'data':{'node':{'replies':page([])}}})
             if 'reactions(' in query:reply({'data':{'node':{'reactions':page([])}}})
             if 'labels(' in query:reply({'data':{'node':{'labels':page([])}}})
             if 'reactionGroups' in query and 'node(' in query:reply({'data':{'node':{'reactionGroups':groups(variables['id'])}}})
             raise RuntimeError('Unexpected GitHub request: '+query)
            """;
    }

    /// <summary>Controlled time; reports armed timers by due time so a test moves time only after a loop is waiting.</summary>
    internal sealed class Clock : TimeProvider
    {
        readonly FakeTimeProvider time = new(DateTimeOffset.UtcNow);
        readonly Dictionary<TimeSpan, SemaphoreSlim> armed = [];
        public override DateTimeOffset GetUtcNow() => time.GetUtcNow();
        public override long GetTimestamp() => time.GetTimestamp();
        public override long TimestampFrequency => time.TimestampFrequency;
        public void Advance(TimeSpan duration) => time.Advance(duration);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = time.CreateTimer(callback, state, dueTime, period);
            Armed(dueTime).Release();
            return timer;
        }

        SemaphoreSlim Armed(TimeSpan due) { lock (armed) return armed.TryGetValue(due, out var count) ? count : armed[due] = new(0); }

        public async Task WaitForTimers(TimeSpan due, int count)
        {
            for (var i = 0; i < count; i++)
                await Armed(due).WaitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }
    }
}
