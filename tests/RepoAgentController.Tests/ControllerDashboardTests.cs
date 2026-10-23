using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Time.Testing;
using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

// The controller entrypoint, its state, status and outbox files, the installed gh and process boundaries are real.
// Only GitHub's HTTP responses and the native JSON-RPC peer are controlled; that is not live-provider evidence.
// The dashboard is observed as GitHub holds it: the Discussion the App authored in the configured category.
[Collection("Managed checkout environment")]
public sealed class ControllerDashboardTests
{
    const string Category = "Controller status";
    const string First = "https://github.com/owner/repository/discussions/1";

    [Fact]
    public async Task Dashboard_presents_current_status_and_latest_activity_and_its_heartbeat_advances_while_idle()
    {
        using var controller = new Controller();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = controller.Run(shutdown.Token, "--dashboard-category", Category);
        try
        {
            var published = await Until(() => controller.Dashboard() is { } dashboard &&
                dashboard.Body.Contains(First) && dashboard.Body.Contains("running") ? dashboard : null);
            Assert.Equal("Controller status", published.Category);
            Assert.Equal("RepoAgent controller status", published.Title);
            Assert.Contains("polling", published.Body);
            Assert.Contains("healthy", published.Body);
            Assert.Matches(@"https://github\.com/owner/repository/discussions/1\b[^\n]*\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ", published.Body);
            var before = Heartbeat(published.Body);

            // Nothing new arrives; only the controlled clock moves, and the next poll is an hour away.
            var idle = await Until(() =>
            {
                controller.Clock.Advance(TimeSpan.FromSeconds(1));
                return controller.Dashboard() is { } dashboard && Heartbeat(dashboard.Body) > before ? dashboard : null;
            });
            Assert.InRange(Heartbeat(idle.Body) - before, TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(32));
            Assert.Contains(First, idle.Body);
            Assert.Equal(1, controller.Writes("createDiscussion"));
            Assert.Equal(1, controller.Turns); // Publishing status never becomes agent input.

            // The next poll processes a participant's comment; the dashboard links that comment with its time.
            var comment = controller.Comment("C1", "A follow-up question.");
            controller.Clock.Advance(TimeSpan.FromHours(1));
            var processed = await Until(() => controller.Dashboard() is { } dashboard && dashboard.Body.Contains(comment) ? dashboard : null);
            Assert.Matches(Regex.Escape(comment) + @"\)[^\n]*\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ", processed.Body);
            Assert.Equal(2, controller.Turns);
            controller.AssertNoSecrets();
        }
        finally { await shutdown.CancelAsync(); }
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        // An operator stop is confirmed with its time; no heartbeat is claimed afterwards.
        var stopped = controller.Dashboard()!;
        TestContext.Current.TestOutputHelper?.WriteLine(stopped.Body);
        Assert.Matches(@"stopped at \d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ after a graceful shutdown", stopped.Body);
        Assert.DoesNotMatch(HeartbeatLine, stopped.Body);
        Assert.Equal(1, controller.Writes("createDiscussion"));
        controller.AssertNoSecrets();
    }

    [Fact]
    public async Task Heartbeats_superseded_during_a_rate_limited_outage_coalesce_into_one_write_of_the_latest()
    {
        using var controller = new Controller();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = controller.Run(shutdown.Token, "--dashboard-category", Category);
        try
        {
            await Until(() => controller.Dashboard() is { } dashboard && dashboard.Body.Contains(First) ? dashboard : null);
            controller.Respond(Response("updateDiscussion", 403,
                """{"message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again."}""", ("Retry-After", "120")));

            var limited = await Until(() =>
            {
                controller.Clock.Advance(TimeSpan.FromSeconds(1));
                return controller.Requests("updateDiscussion").SingleOrDefault(write => write["status"]!.GetValue<int>() == 403);
            });
            var written = controller.Requests("updateDiscussion").Count;

            // GitHub's signalled wait spans about four heartbeats; only the one write after it reaches GitHub.
            var recovered = await Until(() =>
            {
                controller.Clock.Advance(TimeSpan.FromSeconds(1));
                return controller.Requests("updateDiscussion").Skip(written).FirstOrDefault();
            });
            Assert.Equal(200, recovered["status"]!.GetValue<int>());
            var dashboard = controller.Dashboard()!;
            Assert.Equal(recovered["body"]!.GetValue<string>(), dashboard.Body);
            // Rendered at least three heartbeats after the refused one, and no later than now.
            Assert.InRange(Heartbeat(dashboard.Body), Heartbeat(limited["body"]!.GetValue<string>()) + TimeSpan.FromSeconds(88), controller.Clock.GetUtcNow());
            Assert.Contains("rate limit", dashboard.Body); // The outage itself is reported once delivery recovers.
            Assert.Equal(1, controller.Writes("createDiscussion"));
            Assert.Equal(1, controller.Turns);
        }
        finally { await shutdown.CancelAsync(); }
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Input_waiting_for_an_active_session_is_listed_as_queued()
    {
        using var controller = new Controller();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = controller.Run(shutdown.Token, "--dashboard-category", Category);
        try
        {
            var idle = await Until(() => controller.Dashboard() is { } dashboard && dashboard.Body.Contains(First) ? dashboard : null);
            Assert.Contains("### Active and queued sessions\nNone.", idle.Body);

            // The next turn stays active, so a later comment waits in the controller instead of steering it.
            controller.HoldTurns();
            controller.Comment("C1", "Start the long task.");
            controller.Clock.Advance(TimeSpan.FromHours(1));
            await Until(() => controller.Turns == 2 ? controller : null);
            controller.Comment("C2", "One more detail for it.");
            controller.Clock.Advance(TimeSpan.FromHours(1));
            var queued = await Until(() => controller.Dashboard() is { } dashboard && dashboard.Body.Contains("1 inputs waiting") ? dashboard : null);
            Assert.Matches(@"\*\*Sessions:\*\* 1 stored;[^\n]* 1 inputs waiting", queued.Body);
            Assert.Matches(@"### Active and queued sessions\n- \[Discussion #1\]\(" + Regex.Escape(First) + @"\): [^\n]*1 inputs waiting for the session to become idle\n", queued.Body);
            Assert.DoesNotContain("checkout refresh", queued.Body);
            Assert.Equal(2, controller.Turns);
            Assert.False(controller.HasEyes("C2"));
        }
        finally { await shutdown.CancelAsync(); }
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Owned_dashboard_is_found_again_and_never_becomes_agent_input_while_a_participant_copy_stays_ordinary()
    {
        using var controller = new Controller();
        Assert.Equal(0, await controller.RunOnce("--dashboard-category", Category));
        var dashboard = controller.Dashboard()!;
        Assert.Equal(1, controller.Sessions);

        // A participant asks the App about the dashboard, and copies its whole text into their own Discussion,
        // which listings return first; the operator even names the dashboard for explicit enrollment.
        controller.Comment("C9", "@test-app[bot] what does this status mean?", dashboard.Id, dashboard.Url);
        controller.Discussion("D2", 2, dashboard.Body + "\n\n@test-app[bot] please explain this copy.", Category);
        controller.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, await controller.RunOnce("--dashboard-category", Category, "--enroll", dashboard.Url));

        Assert.Equal(1, controller.Writes("createDiscussion")); // The owned dashboard was found again, not duplicated.
        Assert.All(controller.Requests("updateDiscussion"), write => Assert.Equal(dashboard.Id, write["subject"]!.GetValue<string>()));
        Assert.Matches(@"stopped at [^\n]*graceful", controller.Dashboard()!.Body);
        Assert.Equal(2, controller.Sessions); // Only the participant's copy started a session.
        Assert.Equal(2, controller.Turns);
        Assert.True(controller.HasEyes("D2"));
        Assert.False(controller.HasEyes(dashboard.Id));
        Assert.False(controller.HasEyes("C9"));
    }

    [Fact]
    public async Task Authentication_failure_turns_the_dashboard_title_to_attention_without_secrets_until_it_recovers()
    {
        using var controller = new Controller(direct: true);
        using var stdout = new CapturedConsole();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = controller.Run(shutdown.Token, "--dashboard-category", Category);
        try
        {
            var healthy = await Until(() => controller.Dashboard() is { } dashboard && dashboard.Body.Contains("**Authentication:** healthy") ? dashboard : null);
            Assert.Equal("RepoAgent controller status", healthy.Title);

            // GitHub refuses the scheduled renewal of the installation token, which then cannot be used.
            controller.App!.Reject = true;
            controller.Clock.Advance(TimeSpan.FromMinutes(54));
            await Until(() =>
            {
                controller.Clock.Advance(TimeSpan.FromSeconds(1));
                return stdout.Text.Contains(" app-refresh-error ") ? stdout : null;
            });
            controller.App.Reject = false;

            // The pending publication is retried with a fresh token and shows the failure; the next one shows the recovery.
            var attention = await Until(() =>
            {
                controller.Clock.Advance(TimeSpan.FromSeconds(1));
                return controller.Requests("updateDiscussion").LastOrDefault(write => write["status"]!.GetValue<int>() == 200 &&
                    write["title"]!.GetValue<string>() != healthy.Title);
            });
            Assert.Equal("Attention: RepoAgent controller authentication failed", attention["title"]!.GetValue<string>());
            Assert.Matches(@"\*\*Authentication:\*\* failed at [^\n]*HTTP 401", attention["body"]!.GetValue<string>());
            var recovered = await Until(() =>
            {
                controller.Clock.Advance(TimeSpan.FromSeconds(1));
                return controller.Dashboard() is { } dashboard && dashboard.Title == healthy.Title ? dashboard : null;
            });
            Assert.Contains("**Authentication:** healthy", recovered.Body);
            Assert.Matches(@"`app-refresh-error`: `[^`\n]*HTTP 401", recovered.Body); // The failure stays visible after recovery.
            controller.AssertNoSecrets();
        }
        finally { await shutdown.CancelAsync(); }
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_dashboard_category_is_reported_once_instead_of_retried_every_heartbeat()
    {
        using var controller = new Controller();
        using var stdout = new CapturedConsole();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = controller.Run(shutdown.Token, "--dashboard-category", "Missing category");
        try
        {
            await Until(() => stdout.Text.Contains(" dashboard-error ") ? stdout : null);
            for (var second = 0; second < 120; second++) // Four heartbeats.
            {
                controller.Clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }
            var failure = Assert.Single(stdout.Text.Split('\n'), line => line.Contains(" dashboard-error "));
            Assert.Contains("'Missing category' does not exist", failure);
            Assert.Contains("dashboard updates stop until the controller restarts", failure);
            Assert.Equal(1, controller.Writes("categories"));
            Assert.Null(controller.Dashboard());
            Assert.True(controller.HasEyes("D1")); // Other controller-owned work continues.
        }
        finally { await shutdown.CancelAsync(); }
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(1, controller.Writes("categories"));
    }

    sealed class CapturedConsole : IDisposable
    {
        readonly TextWriter previous = Console.Out;
        readonly StringWriter writer = new();
        public CapturedConsole() => Console.SetOut(TextWriter.Synchronized(writer));
        public string Text { get { lock (writer) return writer.ToString(); } }
        public void Dispose() => Console.SetOut(previous);
    }

    static JsonObject Response(string kind, int status, string body, params (string Name, string Value)[] headers) => new()
    {
        ["kind"] = kind, ["status"] = status, ["body"] = body,
        ["headers"] = new JsonObject(headers.Select(h => new KeyValuePair<string, JsonNode?>(h.Name, h.Value)))
    };

    static readonly Regex HeartbeatLine = new(@"Heartbeat\W+(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ)");

    static DateTimeOffset Heartbeat(string body) => DateTimeOffset.Parse(HeartbeatLine.Match(body) is { Success: true } match
        ? match.Groups[1].Value : throw new Xunit.Sdk.XunitException("Dashboard shows no heartbeat:\n" + body), CultureInfo.InvariantCulture);

    // A real-time watchdog for observing another process; it never drives the controller's clock.
    static async Task<T> Until<T>(Func<T?> observe) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (true)
        {
            try { if (observe() is { } value) return value; }
            catch (System.Text.Json.JsonException) { } // The controlled peer is rewriting its file.
            Assert.True(DateTime.UtcNow < deadline, "Condition was not observed before the watchdog deadline");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    // GitHub's App endpoints for direct key authentication, as a controlled transport (not provider evidence).
    sealed class DirectApp(FakeTimeProvider clock) : HttpMessageHandler
    {
        int tokens;
        public volatile bool Reject;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            JsonNode? result = request.RequestUri!.AbsolutePath switch
            {
                "/app" => new JsonObject { ["id"] = 123, ["slug"] = "test-app" },
                "/repos/owner/repository/installation" => new JsonObject { ["id"] = 234, ["app_id"] = 123, ["permissions"] = new JsonObject { ["discussions"] = "write" } },
                "/app/installations/234/access_tokens" when !Reject => new JsonObject
                {
                    ["token"] = "opaque-token-direct-" + Interlocked.Increment(ref tokens), ["expires_at"] = clock.GetUtcNow().AddHours(1).ToString("O")
                },
                _ => null
            };
            return Task.FromResult(result is null ? new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(result.ToJsonString(), System.Text.Encoding.UTF8, "application/json") });
        }
    }

    sealed record Published(string Id, string Url, string Title, string Body, string Category);

    /// <summary>Runs the real controller entrypoint against controlled GitHub and native peers across restarts.</summary>
    sealed class Controller : IDisposable
    {
        const string DeviceSecret = "abcdefghijklmnopqrstuvwxyz_0123456789-ABCDEFG";
        readonly BrokerAuthenticationTests.Fixture fixture = new();
        readonly string? path = Environment.GetEnvironmentVariable("PATH");
        readonly string world, requests, native;
        readonly bool direct;
        readonly HttpClient? appHttp;
        public FakeTimeProvider Clock => fixture.Clock;
        /// <summary>The controlled GitHub App token endpoint, when the controller signs as the App itself.</summary>
        public DirectApp? App { get; }

        /// <param name="direct">Authenticate with the App's private key instead of the credential broker.</param>
        public Controller(bool direct = false)
        {
            this.direct = direct;
            if (direct)
            {
                using var key = System.Security.Cryptography.RSA.Create(2048);
                File.WriteAllText(Path.Combine(fixture.Directory, "app.pem"), key.ExportRSAPrivateKeyPem());
                App = new DirectApp(fixture.Clock);
                appHttp = new HttpClient(App);
            }
            world = Path.Combine(fixture.Directory, "github-world.json");
            requests = Path.Combine(fixture.Directory, "github-requests.jsonl");
            native = Path.Combine(fixture.Directory, "native-requests.jsonl");
            var gh = (path ?? "").Split(Path.PathSeparator).Select(directory => Path.Combine(directory, "gh")).FirstOrDefault(File.Exists)
                ?? throw new InvalidOperationException("The installed gh is required: its --include output is the adapter compatibility boundary");
            var tools = Path.Combine(fixture.Directory, "native-tools");
            Directory.CreateDirectory(tools);
            WriteExecutable(Path.Combine(tools, "codex"), Native.Replace("NATIVE_LOG", Quote(native)));
            WriteExecutable(fixture.Gh, GitHub.Replace("WORLD", Quote(world)).Replace("REQUESTS", Quote(requests)).Replace("REAL_GH", Quote(gh)));
            File.WriteAllText(world, new JsonObject
            {
                ["discussions"] = new JsonArray(new JsonObject
                {
                    ["id"] = "D1", ["number"] = 1, ["url"] = First, ["title"] = "Fixture",
                    ["body"] = "Please review the fixture.", ["bodyHTML"] = "<p>Please review the fixture.</p>", ["closed"] = false, ["locked"] = false,
                    ["answer"] = null, ["category"] = new JsonObject { ["id"] = "DIC1", ["name"] = "General" },
                    ["author"] = new JsonObject { ["__typename"] = "User", ["login"] = "participant" }
                }),
                ["categories"] = new JsonArray(new JsonObject { ["id"] = "DIC1", ["name"] = "General" }, new JsonObject { ["id"] = "DIC2", ["name"] = Category }),
                ["comments"] = new JsonArray(), ["eyes"] = new JsonArray(), ["responses"] = new JsonArray()
            }.ToJsonString());
            Environment.SetEnvironmentVariable("PATH", tools + Path.PathSeparator + path);
        }

        /// <summary>Adds a participant's top-level comment to a Discussion (the first by default) and returns its URL.</summary>
        public string Comment(string id, string body, string discussion = "D1", string discussionUrl = First)
        {
            var url = discussionUrl + "#discussioncomment-" + id[1..];
            Update(state => state["comments"]!.AsArray().Add(new JsonObject
            {
                ["id"] = id, ["discussion"] = discussion, ["url"] = url, ["body"] = body, ["bodyHTML"] = "<p>" + body + "</p>",
                ["author"] = new JsonObject { ["__typename"] = "User", ["login"] = "participant" }, ["replyTo"] = null
            }));
            return url;
        }

        /// <summary>Adds a participant's Discussion ahead of all others, so listings return it first.</summary>
        public string Discussion(string id, int number, string body, string category)
        {
            var url = $"https://github.com/owner/repository/discussions/{number}";
            Update(state =>
            {
                var categoryNode = state["categories"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == category)!.DeepClone();
                state["discussions"]!.AsArray().Insert(0, new JsonObject
                {
                    ["id"] = id, ["number"] = number, ["url"] = url, ["title"] = "Participant copy", ["body"] = body, ["bodyHTML"] = "<p>copy</p>",
                    ["closed"] = false, ["locked"] = false, ["answer"] = null, ["category"] = categoryNode,
                    ["author"] = new JsonObject { ["__typename"] = "User", ["login"] = "participant" }
                });
            });
            return url;
        }

        public Task<int> RunOnce(params string[] extra) =>
            BridgeCommand.Run([.. Arguments, "--once", .. extra], appHttp ?? fixture.Http, fixture.Gh, fixture.Clock, TestContext.Current.CancellationToken);

        public int Sessions => Lines(native).Count(line => line["method"]?.GetValue<string>() == "thread/start");
        public bool HasEyes(string subject) => World(state => state["eyes"]!.AsArray().Any(id => id!.GetValue<string>() == subject));

        void Update(Action<JsonObject> change) => World(state => { change(state); return true; }, write: true);

        // Holds the controlled peer's exclusive lock on its world while reading or changing it, as GitHub would serialize them.
        T World<T>(Func<JsonObject, T> use, bool write = false)
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (true)
            {
                try
                {
                    using var file = new FileStream(world, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    var state = JsonNode.Parse(file)!.AsObject();
                    var result = use(state);
                    if (write)
                    {
                        file.SetLength(0);
                        file.Write(System.Text.Encoding.UTF8.GetBytes(state.ToJsonString()));
                    }
                    return result;
                }
                catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(10); }
            }
        }

        public Task<int> Run(CancellationToken shutdown, params string[] extra) =>
            BridgeCommand.Run([.. Arguments, .. extra], appHttp ?? fixture.Http, fixture.Gh, fixture.Clock, shutdown);

        string[] Arguments =>
        [
            "--repo", "owner/repository", "--app-id", "123", "--state", fixture.State,
            .. direct ? ["--private-key", Path.Combine(fixture.Directory, "app.pem")]
                : new[] { "--broker-url", "https://credentials.example.org", "--enrollment-file", fixture.EnrollmentFile },
            "--workspace", fixture.Directory, "--interval", "3600", "--enroll", First
        ];

        /// <summary>The single Discussion the App authored in the dashboard category, as GitHub holds it.</summary>
        public Published? Dashboard()
        {
            var owned = World(state => state["discussions"]!.AsArray().Where(d => d!["author"]!["login"]!.GetValue<string>() == "test-app" &&
                d["category"]!["name"]!.GetValue<string>() == Category).Select(d => d!.DeepClone()).ToArray());
            Assert.True(owned.Length <= 1, "More than one dashboard Discussion exists");
            return owned.SingleOrDefault() is { } d ? new(d["id"]!.GetValue<string>(), d["url"]!.GetValue<string>(), d["title"]!.GetValue<string>(), d["body"]!.GetValue<string>(),
                d["category"]!["name"]!.GetValue<string>()) : null;
        }

        public void Respond(params JsonObject[] responses) => Update(state =>
        {
            foreach (var response in responses) state["responses"]!.AsArray().Add(response);
        });

        public int Turns => Lines(native).Count(line => line["method"]?.GetValue<string>() == "turn/start");
        public int Writes(string kind) => Requests(kind).Count;
        public List<JsonNode> Requests(string kind) => Lines(requests).Where(line => line["kind"]!.GetValue<string>() == kind).ToList();

        /// <summary>No published title or body ever contained a credential.</summary>
        public void AssertNoSecrets()
        {
            foreach (var write in Lines(requests).Where(line => line["kind"]!.GetValue<string>() is "createDiscussion" or "updateDiscussion"))
            {
                var text = write.ToJsonString();
                Assert.DoesNotContain("opaque-token-", text); // Broker-issued installation tokens.
                Assert.DoesNotContain(DeviceSecret, text);
            }
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
            Environment.SetEnvironmentVariable("PATH", path);
            appHttp?.Dispose();
            fixture.Dispose();
        }

        /// <summary>Later native turns stay active instead of completing.</summary>
        public void HoldTurns() => File.WriteAllText(native + ".hold", "");

        // Every thread is idle when started or resumed; each accepted turn reports idle and completes at once, unless held.
        const string Native = """
            #!/usr/bin/python3
            import json,os,sys
            threads=0;turns=0
            def send(value):print(json.dumps(value),flush=True)
            for line in sys.stdin:
             request=json.loads(line)
             with open(NATIVE_LOG,'a') as log:log.write(line)
             if 'id' not in request:continue
             method=request['method'];idle={'type':'idle'}
             if method=='initialize':send({'id':request['id'],'result':{'codexHome':os.getcwd()}});continue
             if method=='turn/start':
              turns+=1;thread=request['params']['threadId'];turn={'id':'native-turn-%d'%turns,'status':'inProgress','items':[]}
              send({'id':request['id'],'result':{'turn':turn}})
              if os.path.exists(NATIVE_LOG+'.hold'):send({'method':'thread/status/changed','params':{'threadId':thread,'status':{'type':'active','activeFlags':[]}}});continue
              send({'method':'thread/status/changed','params':{'threadId':thread,'status':idle}})
              send({'method':'turn/completed','params':{'threadId':thread,'turn':dict(turn,status='completed')}})
              continue
             if method=='thread/start':threads+=1
             thread=request.get('params',{}).get('threadId') or 'native-T%d'%threads
             send({'id':request['id'],'result':{'thread':{'id':thread,'status':idle}}})
            """;

        // Reads answer from the controlled world. Controller-owned requests (--include) are relayed through the installed gh
        // to a one-shot local HTTP peer, so the controller consumes gh's actual status/header output. A queued fault response
        // replaces the next relayed request of its kind; it changes the world only when marked "apply".
        const string GitHub = """
            #!/usr/bin/python3
            import fcntl,http.server,json,os,subprocess,sys,threading
            args=sys.argv[1:]
            request=json.loads(sys.stdin.read()) if '--input' in args else {}
            query=request.get('query','');variables=request.get('variables') or {}
            include='--include' in args
            page=lambda nodes:{'nodes':nodes,'pageInfo':{'hasNextPage':False,'endCursor':None}}
            app={'__typename':'Bot','login':'test-app'}
            relay=None;change=None
            with open(WORLD,'r+') as handle:
             fcntl.flock(handle,fcntl.LOCK_EX)
             world=json.load(handle)
             groups=lambda id:[{'content':'EYES','viewerHasReacted':id in world['eyes']}]
             message=lambda item:dict(item,reactionGroups=groups(item['id']))
             entry={'include':include}
             if 'addReaction' in query:
              entry.update(kind='addReaction',subject=variables['id'])
              data={'addReaction':{'reaction':{'id':'R-'+variables['id']}}}
              change=lambda:world['eyes'].append(variables['id']) if variables['id'] not in world['eyes'] else None
             elif 'createDiscussion' in query:
              number=100+len(world['discussions'])
              category=next(c for c in world['categories'] if c['id']==variables['category'])
              created={'id':'DASH%d'%number,'number':number,'url':'https://github.com/owner/repository/discussions/%d'%number,
               'title':variables['title'],'body':variables['body'],'bodyHTML':'','closed':False,'locked':False,'answer':None,'category':category,'author':app}
              entry.update(kind='createDiscussion',subject=created['id'],title=variables['title'],body=variables['body'],repository=variables['repository'])
              data={'createDiscussion':{'discussion':{key:created[key] for key in ('id','number','url')}}}
              change=lambda:world['discussions'].append(created)
             elif 'updateDiscussion' in query:
              entry.update(kind='updateDiscussion',subject=variables['id'],title=variables['title'],body=variables['body'])
              target=next(d for d in world['discussions'] if d['id']==variables['id'])
              data={'updateDiscussion':{'discussion':{'id':target['id'],'url':target['url']}}}
              change=lambda:target.update(title=variables['title'],body=variables['body'])
             elif 'discussionCategories' in query:
              entry['kind']='categories';data={'repository':{'id':'R_repository','discussionCategories':page(world['categories'])}}
             elif 'categoryId' in query:
              entry['kind']='findDashboard'
              data={'repository':{'discussions':page([{key:d[key] for key in ('id','number','url','body','author')} for d in world['discussions'] if d['category']['id']==variables['category']])}}
             elif 'query { viewer' in query:entry['kind']='viewer';data={'viewer':{'login':'test-app[bot]'}}
             elif 'discussion(number:' in query:
              entry['kind']='discussion';data={'repository':{'discussion':next((message(d) for d in world['discussions'] if d['number']==variables['number']),None)}}
             elif 'discussions(' in query:entry['kind']='discussions';data={'repository':{'discussions':page([message(d) for d in world['discussions']])}}
             elif 'comments(' in query:entry['kind']='comments';data={'node':{'comments':page([message(c) for c in world['comments'] if c['discussion']==variables['id'] and c.get('replyTo') is None])}}
             elif 'replies(' in query:entry['kind']='replies';data={'node':{'replies':page([message(c) for c in world['comments'] if (c.get('replyTo') or {}).get('id')==variables['id']])}}
             elif 'reactions(' in query:entry['kind']='reactions';data={'node':{'reactions':page([])}}
             elif 'reactionGroups' in query and 'node(' in query:
              entry.update(kind='reconcile',subject=variables['id']);data={'node':{'reactionGroups':groups(variables['id'])}}
             else:raise RuntimeError('Unexpected GitHub request: '+query)
             if include:
              relay=next((r for r in world['responses'] if r.get('kind',entry['kind'])==entry['kind']),None)
              if relay is not None:world['responses'].remove(relay)
              else:relay={'status':200,'body':json.dumps({'data':data}),'apply':True}
              entry['status']=relay['status']
              if relay.get('apply') and change:change()
             elif change:change()
             with open(REQUESTS,'a') as log:log.write(json.dumps(entry)+'\n')
             handle.seek(0);handle.truncate();json.dump(world,handle)
            if relay is None:
             print(json.dumps({'data':data}));sys.exit(0)
            class Peer(http.server.BaseHTTPRequestHandler):
             def do_POST(self):
              self.rfile.read(int(self.headers.get('Content-Length','0')))
              self.send_response(relay['status'])
              for name,value in relay.get('headers',{}).items():self.send_header(name,value)
              self.send_header('Content-Type','application/json; charset=utf-8');self.end_headers()
              self.wfile.write(relay['body'].encode())
             def log_message(self,*_):pass
            server=http.server.HTTPServer(('127.0.0.1',0),Peer)
            threading.Thread(target=server.handle_request,daemon=True).start()
            proxy='http://127.0.0.1:%d'%server.server_port
            environment=dict(os.environ,HTTP_PROXY=proxy,http_proxy=proxy,NO_PROXY='',no_proxy='')
            # gh serves github.localhost over plain HTTP, the only host this local peer can answer for.
            result=subprocess.run([REAL_GH]+['github.localhost' if a=='github.com' else a for a in args],input=json.dumps(request),
             capture_output=True,text=True,env=environment,timeout=30)
            sys.stdout.write(result.stdout);sys.stderr.write(result.stderr);sys.exit(result.returncode)
            """;
    }
}
