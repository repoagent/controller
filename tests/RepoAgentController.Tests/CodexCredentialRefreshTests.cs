using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

// Operator-supplied Codex account credentials, observed through real controller lifetimes driving the pinned native
// app-server (CHECKOUT_NATIVE, else codex on PATH). The broker, GitHub and the model endpoint are controlled: the
// endpoint records which fixture credential the real native presented for each turn. Network egress of the native
// is closed and its home is private to the test, so no host Codex sign-in is read or used.
[Collection("Managed checkout environment")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class CodexCredentialRefreshTests
{
    const string Discussion = "https://github.com/owner/repository/discussions/1";
    const string HistoryMarker = "HISTORY-MARKER-bootstrap";

    [Fact]
    public async Task Replaced_Codex_credentials_resume_held_work_in_the_same_session_without_restarting_the_controller()
    {
        if (!OperatingSystem.IsLinux()) return; // Shipping Linux process fixtures.
        using var fixture = new BrokerAuthenticationTests.Fixture();
        using var model = new ModelEndpoint();
        using var native = new NativeEnvironment(fixture, model);
        native.Supply("sk-canary-A");
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = BridgeCommand.Run(native.Arguments("--interval", "60"), fixture.Http, fixture.Gh, fixture.Clock, shutdown.Token);
        try
        {
            // Startup: the bootstrap turn authenticates with the separately supplied credential.
            await model.Eventually(requests => requests.Count >= 1);
            Assert.Equal("Bearer sk-canary-A", model.Requests()[0].Authorization);
            await OperatorStatusTests.Eventually(fixture.State, status => status["codexAuthentication"]?["state"]?.GetValue<string>() == "healthy");
            var session = Session(fixture);

            // The account credential expires: the turn fails at the provider and status says what the operator must do.
            model.Reject("Bearer sk-canary-A");
            native.Comment("C1", "first comment after expiry");
            fixture.Clock.Advance(TimeSpan.FromSeconds(60));
            var failed = await OperatorStatusTests.Eventually(fixture.State, status => status["codexAuthentication"]?["state"]?.GetValue<string>() == "failed");
            Assert.Contains("--codex-auth", failed["codexAuthentication"]!["error"]!.GetValue<string>());
            Assert.Contains(failed["failures"]!.AsArray(), item => item!["kind"]!.GetValue<string>() == "codex-auth-failed");
            Assert.False(run.IsCompleted);

            // Later input is held instead of being accepted into a turn that cannot authenticate: no turn, no EYES.
            var attempts = model.Requests().Count;
            native.Comment("C2", "second comment while credentials are rejected");
            fixture.Clock.Advance(TimeSpan.FromSeconds(60));
            await Until(async () => (await OperatorStatusTests.RetainedLogs(fixture.State)).Split('\n').Any(line => line.Contains(" input-held ") && line.Contains(Discussion)));
            Assert.Equal(attempts, model.Requests().Count);
            Assert.DoesNotContain("C2", native.Receipts());

            // The operator replaces the supplied file while the controller keeps running; time passes for its
            // checks and polls. The held input reaches the same native session, resumed with its history,
            // using the replacement credential.
            native.Supply("sk-canary-B");
            await model.Eventually(requests => requests.Skip(attempts).Any(request => request.Authorization == "Bearer sk-canary-B"),
                () => fixture.Clock.Advance(TimeSpan.FromSeconds(5)));
            var resumed = model.Requests().Skip(attempts).First(request => request.Authorization == "Bearer sk-canary-B");
            Assert.All(model.Requests().Skip(attempts), request => Assert.Equal("Bearer sk-canary-B", request.Authorization));
            Assert.Contains("second comment while credentials are rejected", resumed.Body);
            Assert.Contains(HistoryMarker, resumed.Body);
            Assert.Equal(session, Session(fixture));
            await Until(() => Task.FromResult(native.Receipts().Contains("C2")));
            var healthy = await OperatorStatusTests.Eventually(fixture.State, status => status["codexAuthentication"]?["state"]?.GetValue<string>() == "healthy");
            Assert.False(run.IsCompleted); // One controller lifetime throughout.
            var logs = await OperatorStatusTests.RetainedLogs(fixture.State);
            Assert.Contains(logs.Split('\n'), line => line.Contains(" session-resumed ") && line.Contains(session));
            foreach (var secret in new[] { "sk-canary-A", "sk-canary-B" })
            {
                Assert.DoesNotContain(secret, healthy.ToJsonString());
                Assert.DoesNotContain(secret, logs);
            }
        }
        finally
        {
            await shutdown.CancelAsync();
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_replacement_never_interrupts_a_running_turn_and_applies_before_later_input()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new BrokerAuthenticationTests.Fixture();
        using var model = new ModelEndpoint();
        using var native = new NativeEnvironment(fixture, model);
        native.Supply("sk-canary-A");
        model.Hold();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = BridgeCommand.Run(native.Arguments("--interval", "60"), fixture.Http, fixture.Gh, fixture.Clock, shutdown.Token);
        try
        {
            // The bootstrap turn is running while its model response is held; the operator replaces the credentials.
            await model.Eventually(requests => requests.Count == 1);
            native.Supply("sk-canary-B");
            await Until(async () =>
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(5));
                return (await OperatorStatusTests.RetainedLogs(fixture.State)).Contains(" codex-auth-replaced ");
            });
            for (var check = 0; check < 5; check++) { fixture.Clock.Advance(TimeSpan.FromSeconds(5)); await Task.Delay(200, TestContext.Current.CancellationToken); }
            Assert.DoesNotContain(" codex-auth-applied ", await OperatorStatusTests.RetainedLogs(fixture.State));

            // The turn completes on the credentials it started with; only then does the owned app-server restart.
            model.Release();
            await Until(async () =>
            {
                fixture.Clock.Advance(TimeSpan.FromSeconds(5));
                return (await OperatorStatusTests.RetainedLogs(fixture.State)).Contains(" codex-auth-applied ");
            });
            var lines = (await OperatorStatusTests.RetainedLogs(fixture.State)).Split('\n');
            var completed = Array.FindIndex(lines, line => line.Contains(" native-event method=turn/completed ") && line.Contains(" status=completed "));
            Assert.InRange(completed, 0, Array.FindIndex(lines, line => line.Contains(" codex-auth-applied ")));
            Assert.Equal(["Bearer sk-canary-A"], model.Requests().Select(request => request.Authorization));

            // Later input uses the replacement.
            native.Comment("C1", "first comment after replacement");
            await model.Eventually(requests => requests.Count == 2, () => fixture.Clock.Advance(TimeSpan.FromSeconds(5)));
            Assert.Equal("Bearer sk-canary-B", model.Requests()[1].Authorization);
            Assert.False(run.IsCompleted);
        }
        finally
        {
            model.Release();
            await shutdown.CancelAsync();
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None));
        }
    }

    [Fact]
    public async Task Controller_restarts_keep_the_natives_refreshed_credentials_until_the_operator_supplies_new_ones()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new BrokerAuthenticationTests.Fixture();
        using var model = new ModelEndpoint();
        using var native = new NativeEnvironment(fixture, model);
        async Task<string> Lifetime(int request)
        {
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var run = BridgeCommand.Run(native.Arguments("--interval", "60"), fixture.Http, fixture.Gh, fixture.Clock, shutdown.Token);
            try { await model.Eventually(requests => requests.Count == request); }
            finally
            {
                await shutdown.CancelAsync();
                Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None));
            }
            return model.Requests()[request - 1].Authorization;
        }

        native.Supply("sk-canary-A");
        Assert.Equal("Bearer sk-canary-A", await Lifetime(1));

        // The native refreshes its own sign-in, as Codex rotates ChatGPT tokens into its home. An unchanged supply must
        // not overwrite that on restart, or every restart would reinstate an already-rotated refresh token.
        native.RefreshedByNative("sk-canary-rotated");
        native.Comment("C1", "first comment after a native refresh");
        Assert.Equal("Bearer sk-canary-rotated", await Lifetime(2));

        // New operator-supplied credentials are installed by the next start.
        native.Supply("sk-canary-B");
        native.Comment("C2", "second comment after a new supply");
        Assert.Equal("Bearer sk-canary-B", await Lifetime(3));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("not-json")]
    [InlineData("no-credential")]
    public async Task Unusable_supplied_Codex_credentials_deny_startup_before_GitHub_or_native_use(string supply)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new BrokerAuthenticationTests.Fixture();
        using var model = new ModelEndpoint();
        using var native = new NativeEnvironment(fixture, model);
        const string Canary = "sk-canary-unusable";
        if (supply == "not-json") File.WriteAllText(native.Supplied, "{\"OPENAI_API_KEY\":\"" + Canary);
        if (supply == "no-credential") File.WriteAllText(native.Supplied, new JsonObject { ["auth_mode"] = "apikey", ["note"] = Canary }.ToJsonString());

        Assert.Equal(1, await BridgeCommand.Run(native.Arguments("--once"), fixture.Http, fixture.Gh, fixture.Clock, TestContext.Current.CancellationToken));

        Assert.Empty(fixture.Transport.Requests); // No GitHub App credential was acquired.
        Assert.False(native.Started);
        Assert.False(File.Exists(Path.Combine(native.Home, "auth.json")));
        var status = await OperatorStatusTests.Status(fixture.State);
        Assert.Equal("failed", status["controller"]!["state"]!.GetValue<string>());
        Assert.Contains(status["failures"]!.AsArray(), item => item!["kind"]!.GetValue<string>() == "fatal" &&
            item["message"]!.GetValue<string>().Contains("--codex-auth file " + native.Supplied));
        Assert.DoesNotContain(Canary, status.ToJsonString());
        Assert.DoesNotContain(Canary, await OperatorStatusTests.RetainedLogs(fixture.State));
    }

    static string Session(BrokerAuthenticationTests.Fixture fixture) =>
        JsonNode.Parse(File.ReadAllText(fixture.State))!["discussions"]!["D1"]!["sessionId"]!.GetValue<string>();

    static async Task Until(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition was not reached");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    // A credential-checking Responses endpoint. Each request's bearer is the fixture credential the native used.
    internal sealed class ModelEndpoint : IDisposable
    {
        readonly HttpListener listener = new();
        readonly List<(string Authorization, string Body)> requests = [];
        readonly HashSet<string> rejected = [];
        public string Origin { get; }

        public ModelEndpoint()
        {
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0)) { probe.Start(); port = ((IPEndPoint)probe.LocalEndpoint).Port; }
            Origin = $"http://127.0.0.1:{port}";
            listener.Prefixes.Add(Origin + "/");
            listener.Start();
            _ = Serve();
        }

        TaskCompletionSource released = Released();
        static TaskCompletionSource Released() { var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); gate.SetResult(); return gate; }

        public void Reject(string authorization) { lock (requests) rejected.Add(authorization); }
        /// <summary>Keeps later model responses, and so their turns, running until <see cref="Release"/>.</summary>
        public void Hold() { lock (requests) released = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public void Release() { lock (requests) released.TrySetResult(); }
        public List<(string Authorization, string Body)> Requests() { lock (requests) return [.. requests]; }

        public async Task Eventually(Func<List<(string Authorization, string Body)>, bool> condition, Action? tick = null)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!condition(Requests()))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Expected model requests were not observed: " + string.Join(", ", Requests().Select(r => r.Authorization)));
                tick?.Invoke();
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }
        }

        async Task Serve()
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception error) when (error is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
                _ = Task.Run(() => Respond(context));
            }
        }

        void Respond(HttpListenerContext context)
        {
            using var response = context.Response;
            if (context.Request.HttpMethod != "POST" || context.Request.Url!.AbsolutePath != "/v1/responses") { response.StatusCode = 404; return; }
            var encoded = context.Request.Headers["Content-Encoding"] is { Length: > 0 } encoding && encoding != "identity";
            var body = encoded ? "<encoded>" : new StreamReader(context.Request.InputStream).ReadToEnd();
            var authorization = context.Request.Headers["Authorization"] ?? "";
            int number;
            bool reject;
            Task hold;
            lock (requests) { requests.Add((authorization, body)); number = requests.Count; reject = rejected.Contains(authorization); hold = released.Task; }
            hold.Wait(TimeSpan.FromSeconds(60));
            byte[] payload;
            if (reject)
            {
                response.StatusCode = 401;
                response.ContentType = "application/json";
                payload = Encoding.UTF8.GetBytes("""{"error":{"message":"fixture credential expired","type":"invalid_request_error","code":"invalid_api_key"}}""");
            }
            else
            {
                var id = "fixture-response-" + number;
                var item = $$"""{"type":"message","role":"assistant","id":"fixture-message-{{number}}","content":[{"type":"output_text","text":"Fixture assistant {{number}}."}]}""";
                var usage = """{"input_tokens":0,"input_tokens_details":null,"output_tokens":0,"output_tokens_details":null,"total_tokens":0}""";
                var events = new[]
                {
                    ("response.created", $$$"""{"type":"response.created","response":{"id":"{{{id}}}"}}"""),
                    ("response.output_item.done", $$$"""{"type":"response.output_item.done","item":{{{item}}}}"""),
                    ("response.completed", $$$"""{"type":"response.completed","response":{"id":"{{{id}}}","usage":{{{usage}}}}}""")
                };
                response.ContentType = "text/event-stream";
                payload = Encoding.UTF8.GetBytes(string.Concat(events.Select(e => $"event: {e.Item1}\ndata: {e.Item2}\n\n")));
            }
            response.ContentLength64 = payload.Length;
            response.OutputStream.Write(payload);
        }

        public void Dispose() { listener.Stop(); listener.Close(); }
    }

    // A private native home bound to the model endpoint, the real native behind a closed-egress launcher, an
    // operator secret directory holding the supplied credential file, and a GitHub peer with one enrolled Discussion.
    internal sealed class NativeEnvironment : IDisposable
    {
        readonly BrokerAuthenticationTests.Fixture fixture;
        readonly Dictionary<string, string?> previous = [];
        readonly List<JsonObject> comments = [];
        readonly string started;
        public string Home { get; }
        public string Supplied { get; }
        public string Workspace { get; }
        public bool Started => File.Exists(started);

        public NativeEnvironment(BrokerAuthenticationTests.Fixture fixture, ModelEndpoint model)
        {
            this.fixture = fixture;
            var real = Environment.GetEnvironmentVariable("CHECKOUT_NATIVE") ?? (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator).Select(path => Path.Combine(path, "codex")).First(File.Exists);
            Home = Path.Combine(fixture.Directory, "native-home");
            Directory.CreateDirectory(Home);
            File.SetUnixFileMode(Home, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllText(Path.Combine(Home, "config.toml"), $"""
                model = "gpt-6.1-sol"
                model_provider = "fixture"
                approval_policy = "never"
                sandbox_mode = "danger-full-access"
                web_search = "disabled"
                chatgpt_base_url = "{model.Origin}/backend-api/"
                [model_providers.fixture]
                name = "Credential-checking local fixture"
                base_url = "{model.Origin}/v1"
                wire_api = "responses"
                requires_openai_auth = true
                supports_websockets = false
                request_max_retries = 0
                stream_max_retries = 0

                """);
            var secrets = Path.Combine(fixture.Directory, "operator-secrets");
            Directory.CreateDirectory(secrets);
            Supplied = Path.Combine(secrets, "auth.json");
            Workspace = Path.Combine(fixture.Directory, "workspace");
            Directory.CreateDirectory(Workspace);
            var tools = Path.Combine(fixture.Directory, "native-tools");
            Directory.CreateDirectory(tools);
            var isolated = Path.Combine(fixture.Directory, "native-user");
            Directory.CreateDirectory(isolated);
            started = Path.Combine(fixture.Directory, "native-started");
            Executable(Path.Combine(tools, "codex"), $"""
                #!/bin/sh
                echo started >> '{started}'
                export HOME='{isolated}' NO_PROXY=127.0.0.1 no_proxy=127.0.0.1
                export HTTPS_PROXY=http://127.0.0.1:9 https_proxy=http://127.0.0.1:9 HTTP_PROXY=http://127.0.0.1:9 http_proxy=http://127.0.0.1:9 ALL_PROXY=http://127.0.0.1:9 all_proxy=http://127.0.0.1:9
                exec '{real}' "$@"
                """);
            Executable(fixture.Gh, GitHubPeer);
            Comments();
            foreach (var (name, value) in new[] { ("PATH", tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")), ("CODEX_HOME", Home) })
            {
                previous[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public string[] Arguments(params string[] extra) =>
        [
            "--repo", "owner/repository", "--app-id", "123", "--state", fixture.State,
            "--broker-url", "https://credentials.example.org", "--enrollment-file", fixture.EnrollmentFile,
            "--workspace", Workspace, "--codex-auth", Supplied, .. extra
        ];

        /// <summary>Atomically replaces the operator's credential file, as a secret mount update would.</summary>
        public void Supply(string apiKey)
        {
            var temporary = Supplied + ".next";
            File.WriteAllText(temporary, new JsonObject { ["auth_mode"] = "apikey", ["OPENAI_API_KEY"] = apiKey }.ToJsonString());
            File.Move(temporary, Supplied, true);
        }

        /// <summary>Rewrites the native home's credentials as the native's own token refresh does.</summary>
        public void RefreshedByNative(string apiKey) =>
            File.WriteAllText(Path.Combine(Home, "auth.json"), new JsonObject { ["auth_mode"] = "apikey", ["OPENAI_API_KEY"] = apiKey }.ToJsonString());

        public void Comment(string id, string body)
        {
            comments.Add(new JsonObject { ["id"] = id, ["url"] = Discussion + "#discussioncomment-" + id, ["body"] = body });
            Comments();
        }

        void Comments() => File.WriteAllText(Path.Combine(fixture.Directory, "comments.json"), new JsonArray(comments.Select(c => (JsonNode)c.DeepClone()).ToArray()).ToJsonString());

        public string[] Receipts()
        {
            var path = Path.Combine(fixture.Directory, "receipts.log");
            return File.Exists(path) ? File.ReadAllLines(path) : [];
        }

        public void Dispose()
        {
            foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
        }

        static void Executable(string path, string source)
        {
            File.WriteAllText(path, source);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // One mention-enrolled Discussion whose comments come from comments.json; EYES receipts are appended to receipts.log.
        const string GitHubPeer = $$$"""
            #!/usr/bin/python3
            import json,os,sys
            here=os.path.dirname(os.path.abspath(sys.argv[0]))
            a=sys.argv[1:]
            if a[:2]!=['api','graphql']:raise RuntimeError('Unexpected GitHub command')
            request=json.load(sys.stdin);q=request['query'];v=request.get('variables') or {}
            page=lambda nodes:{'nodes':nodes,'pageInfo':{'hasNextPage':False,'endCursor':None}}
            author={'__typename':'User','login':'participant'}
            discussion={'id':'D1','number':1,'url':'{{{Discussion}}}','body':'@test-app[bot] {{{HistoryMarker}}}','bodyHTML':'','title':'fixture','author':author,'reactionGroups':[],'closed':False,'locked':False,'answer':None,'category':{'id':'K1','name':'General'},'createdAt':'2026-10-23T00:00:00Z','updatedAt':'2026-10-23T00:00:00Z'}
            comments=[dict(c,author=author,bodyHTML='',reactionGroups=[],replyTo=None,createdAt='2026-10-23T00:00:00Z',updatedAt='2026-10-23T00:00:00Z') for c in json.load(open(os.path.join(here,'comments.json')))]
            if 'addReaction' in q:
             with open(os.path.join(here,'receipts.log'),'a') as log:log.write(v['id']+'\n')
             data={'addReaction':{'reaction':dict(id='R-'+v['id'])}}
            elif 'query { viewer' in q:data={'viewer':{'login':'test-app[bot]'}}
            elif 'discussions(' in q:data={'repository':{'discussions':page([discussion])}}
            elif 'comments(' in q:data={'node':{'comments':page(comments if v.get('id')=='D1' else [])}}
            elif 'replies(' in q:data={'node':{'replies':page([])}}
            elif 'reactions(' in q:data={'node':{'reactions':page([])}}
            else:raise RuntimeError('Unexpected GitHub query')
            if '--include' in a:print('HTTP/2.0 200 OK\n')
            print(json.dumps({'data':data}))
            """;
    }
}
