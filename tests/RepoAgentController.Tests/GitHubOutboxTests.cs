using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

// The controller entrypoint, its state and outbox files, the installed gh and process boundaries are real.
// Only GitHub's HTTP responses and the native JSON-RPC peer are controlled; that is not live-provider evidence.
[Collection("Managed checkout environment")]
public sealed class GitHubOutboxTests
{
    [Fact]
    public async Task Permanent_receipt_failure_leaves_active_delivery_with_an_actionable_nonsecret_log()
    {
        using var controller = new Controller();
        controller.Respond(Response(200, """{"data":{"addReaction":null},"errors":[{"type":"FORBIDDEN","path":["addReaction"],"message":"Resource not accessible by integration"}]}"""));

        var (exit, log) = await controller.RunOnceLogged();
        Assert.Equal(1, exit);
        var failure = Assert.Single(log.Split('\n'), line => line.Contains(" receipt-error "));
        Assert.Contains("https://github.com/owner/repository/discussions/1 subject=D1", failure);
        Assert.Contains("FORBIDDEN: Resource not accessible by integration", failure);
        Assert.Contains("permanent", failure);
        Assert.DoesNotContain("opaque-token", log);

        controller.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, await controller.RunOnce());
        Assert.Equal(1, controller.Writes("D1"));
        Assert.False(controller.HasEyes("D1"));
        Assert.Equal(1, controller.Turns);
    }

    [Fact]
    public async Task Superseded_operation_is_coalesced_without_dropping_unrelated_retained_operations()
    {
        using var controller = new Controller();
        controller.Comment("C1", "First observation.");
        controller.Respond(SecondaryRateLimit());
        Assert.Equal(1, await controller.RunOnce()); // D1 is rate-limited; C1 waits behind the installation-wide pause.

        controller.Clock.Advance(TimeSpan.FromSeconds(30));
        controller.Comment("C1", "Edited observation.");
        Assert.Equal(1, await controller.RunOnce());
        Assert.Equal(2, controller.Turns); // The edit is new input and supersedes C1's still-pending receipt.

        controller.Clock.Advance(TimeSpan.FromSeconds(32));
        Assert.Equal(0, await controller.RunOnce());
        Assert.True(controller.HasEyes("D1"));
        Assert.True(controller.HasEyes("C1"));
        Assert.Equal(2, controller.Writes("D1"));
        Assert.Equal(1, controller.Writes("C1"));
        Assert.Equal(2, controller.Turns);
    }

    static JsonObject SecondaryRateLimit() => Response(403, """{"message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again.","documentation_url":"https://docs.github.com/graphql/overview/rate-limits-and-node-limits-for-the-graphql-api#secondary-rate-limits"}""",
        ("Retry-After", "61"));

    static JsonObject Response(int status, string body, params (string Name, string Value)[] headers) => new()
    {
        ["status"] = status, ["body"] = body,
        ["headers"] = new JsonObject(headers.Select(h => new KeyValuePair<string, JsonNode?>(h.Name, h.Value)))
    };

    /// <summary>Runs the real controller entrypoint against controlled GitHub and native peers across restarts.</summary>
    internal sealed class Controller : IDisposable
    {
        readonly BrokerAuthenticationTests.Fixture fixture = new();
        readonly string? path = Environment.GetEnvironmentVariable("PATH");
        readonly string world, requests, native, nativeReject;
        public FakeTimeProvider Clock => fixture.Clock;

        public Controller()
        {
            world = Path.Combine(fixture.Directory, "github-world.json");
            requests = Path.Combine(fixture.Directory, "github-requests.jsonl");
            native = Path.Combine(fixture.Directory, "native-requests.jsonl");
            nativeReject = Path.Combine(fixture.Directory, "native-reject");
            var gh = (path ?? "").Split(Path.PathSeparator).Select(directory => Path.Combine(directory, "gh")).FirstOrDefault(File.Exists)
                ?? throw new InvalidOperationException("The installed gh is required: its --include output is the adapter compatibility boundary");
            var tools = Path.Combine(fixture.Directory, "native-tools");
            Directory.CreateDirectory(tools);
            WriteExecutable(Path.Combine(tools, "codex"), Native.Replace("NATIVE_LOG", Quote(native)).Replace("NATIVE_REJECT", Quote(nativeReject)));
            WriteExecutable(fixture.Gh, GitHub.Replace("WORLD", Quote(world)).Replace("REQUESTS", Quote(requests)).Replace("REAL_GH", Quote(gh)));
            File.WriteAllText(world, new JsonObject
            {
                ["discussion"] = new JsonObject
                {
                    ["id"] = "D1", ["number"] = 1, ["url"] = "https://github.com/owner/repository/discussions/1", ["title"] = "Fixture",
                    ["body"] = "Please review the fixture.", ["bodyHTML"] = "<p>Please review the fixture.</p>", ["closed"] = false, ["locked"] = false,
                    ["answer"] = null, ["category"] = new JsonObject { ["id"] = "DIC1", ["name"] = "General" },
                    ["author"] = new JsonObject { ["__typename"] = "User", ["login"] = "participant" }
                },
                ["comments"] = new JsonArray(), ["reactions"] = new JsonObject(), ["eyes"] = new JsonArray(), ["responses"] = new JsonArray()
            }.ToJsonString());
            Environment.SetEnvironmentVariable("PATH", tools + Path.PathSeparator + path);
        }

        public void Respond(params JsonObject[] responses) => Update(state =>
        {
            foreach (var response in responses) state["responses"]!.AsArray().Add(response);
        });

        /// <summary>Adds or edits a participant's top-level comment, or a reply in the thread of <paramref name="replyTo"/>.</summary>
        public void Comment(string id, string body, string author = "participant", string? replyTo = null) => Update(state =>
        {
            var comments = state["comments"]!.AsArray();
            if (comments.FirstOrDefault(c => c!["id"]!.GetValue<string>() == id) is { } existing) existing["body"] = body;
            else comments.Add(new JsonObject
            {
                ["id"] = id, ["url"] = "https://github.com/owner/repository/discussions/1#discussioncomment-" + id, ["body"] = body,
                ["bodyHTML"] = "<p>" + body + "</p>", ["author"] = new JsonObject { ["__typename"] = "User", ["login"] = author },
                ["replyTo"] = replyTo is null ? null : new JsonObject { ["id"] = replyTo }
            });
        });

        /// <summary>Serves every GitHub read connection in pages of <paramref name="size"/>; with <paramref name="failLaterPages"/>,
        /// each request for a page after the first fails as gh does on an HTTP 502.</summary>
        public void Paginate(int size, bool failLaterPages = false) => Update(state =>
        {
            state["pageSize"] = size;
            state["failLaterPages"] = failLaterPages;
        });

        /// <summary>Deletes a comment or reply, with its reactions, from what GitHub returns.</summary>
        public void Delete(string id) => Update(state =>
        {
            var comments = state["comments"]!.AsArray();
            comments.Remove(comments.Single(c => c!["id"]!.GetValue<string>() == id));
            state["reactions"]!.AsObject().Remove(id);
        });

        /// <summary>Adds a participant's reaction to the Discussion or a comment.</summary>
        public void React(string subject, string id, string content, string actor) => Update(state =>
        {
            var reactions = state["reactions"]!.AsObject();
            if (reactions[subject] is null) reactions[subject] = new JsonArray();
            reactions[subject]!.AsArray().Add(new JsonObject
            {
                ["id"] = id, ["content"] = content, ["user"] = new JsonObject { ["__typename"] = "User", ["login"] = actor },
                ["createdAt"] = "2026-10-23T12:00:00Z"
            });
        });

        /// <summary>The native peer answers the next <paramref name="count"/> turn/start requests with a JSON-RPC error.</summary>
        public void RejectTurns(int count) => File.WriteAllText(nativeReject, count.ToString(System.Globalization.CultureInfo.InvariantCulture));

        /// <summary>Marks a comment as the Discussion's answer, or clears the answer.</summary>
        public void Answer(string? comment) => Update(state =>
            state["discussion"]!["answer"] = comment is null ? null : new JsonObject { ["id"] = comment });

        // Under the controlled gh's own lock, which it holds while it reads or rewrites the world.
        void Update(Action<JsonObject> change) => ControllerHarness.LockedJson(world, state => { change(state); return state; }, write: true);

        public Task<int> RunOnce() => BridgeCommand.Run([.. Arguments, "--once"], fixture.Http, fixture.Gh, fixture.Clock,
            TestContext.Current.CancellationToken);

        public Task<int> Run(CancellationToken shutdown) => BridgeCommand.Run(Arguments, fixture.Http, fixture.Gh, fixture.Clock, shutdown);

        public async Task<(int Exit, string Log)> RunOnceLogged()
        {
            var original = Console.Out;
            using var capture = new StringWriter();
            Console.SetOut(TextWriter.Synchronized(capture));
            try { return (await RunOnce(), capture.ToString()); }
            finally { Console.SetOut(original); }
        }

        string[] Arguments =>
        [
            "--repo", "owner/repository", "--app-id", "123", "--state", fixture.State,
            "--broker-url", "https://credentials.example.org", "--enrollment-file", fixture.EnrollmentFile,
            "--workspace", fixture.Directory, "--interval", "3600", "--enroll", "https://github.com/owner/repository/discussions/1",
            "--status-port", ControllerHarness.FreePort().ToString()
        ];

        public int Turns => Lines(native).Count(line => line["method"]?.GetValue<string>() == "turn/start");

        /// <summary>The bridge envelope text of each native turn/start, exactly as the native process received it.</summary>
        public IReadOnlyList<string> Inputs => Lines(native).Where(line => line["method"]?.GetValue<string>() == "turn/start")
            .Select(line => line["params"]!["input"]![0]!["text"]!.GetValue<string>())
            .Select(text => text.StartsWith(Skill, StringComparison.Ordinal) ? text[Skill.Length..] : throw new InvalidOperationException("Unexpected native input: " + text))
            .ToArray();

        const string Skill = "$github-discussion\n";

        public int Writes(string subject) => Lines(requests).Count(line => line["kind"]!.GetValue<string>() == "addReaction" && line["subject"]?.GetValue<string>() == subject);
        public bool HasEyes(string subject) => ControllerHarness.LockedJson(world, state => state["eyes"]!.AsArray().Any(id => id!.GetValue<string>() == subject));

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
            fixture.Dispose();
        }

        const string Native = """
            #!/usr/bin/python3
            import json,os,sys
            for line in sys.stdin:
             request=json.loads(line)
             with open(NATIVE_LOG,'a') as log:log.write(line)
             if 'id' not in request:continue
             method=request['method']
             rejects=int(open(NATIVE_REJECT).read()) if method=='turn/start' and os.path.exists(NATIVE_REJECT) else 0
             if rejects:
              open(NATIVE_REJECT,'w').write(str(rejects-1))
              print(json.dumps({'id':request['id'],'error':{'code':-32000,'message':'turn rejected'}}),flush=True);continue
             result={'codexHome':os.getcwd()} if method=='initialize' else {'data':[{'model':'gpt-6.1-sol','isDefault':True,'defaultReasoningEffort':'low','supportedReasoningEfforts':[{'reasoningEffort':'low'}]}]} if method=='model/list' else {'config':{}} if method=='config/read' else {'turn':{'id':'native-turn'}} if method=='turn/start' else {'thread':{'id':'native-T1','status':{'type':'idle'}}}
             print(json.dumps({'id':request['id'],'result':result}),flush=True)
            """;

        // Reads answer from the controlled world. Controller-owned writes and their result reads are relayed through
        // the installed gh to a one-shot local HTTP peer, so the controller consumes gh's actual status/header output.
        const string GitHub = """
            #!/usr/bin/python3
            import fcntl,http.server,json,os,subprocess,sys,threading
            args=sys.argv[1:]
            request=json.loads(sys.stdin.read()) if '--input' in args else {}
            query=request.get('query','');variables=request.get('variables') or {}
            relay=None;kind='read';data=None
            with open(WORLD,'r+') as handle:
             fcntl.flock(handle,fcntl.LOCK_EX)
             world=json.load(handle)
             size=world.get('pageSize') or 0;cursor=variables.get('cursor')
             if cursor and world.get('failLaterPages'):sys.stderr.write('gh: HTTP 502: Bad Gateway (https://api.github.com/graphql)\n');sys.exit(1)
             def page(nodes):
              end=int(cursor or 0)+size if size else len(nodes)
              return {'nodes':nodes[int(cursor or 0):end],'pageInfo':{'hasNextPage':end<len(nodes),'endCursor':str(end) if end<len(nodes) else None}}
             groups=lambda id:[{'content':'EYES','viewerHasReacted':id in world['eyes']}]
             message=lambda item:dict(item,reactionGroups=groups(item['id']))
             if 'addReaction' in query:
              kind='addReaction';subject=variables['id']
              relay=world['responses'].pop(0) if world['responses'] else {'status':200,'body':json.dumps({'data':{'addReaction':{'reaction':{'id':'R-'+subject}}}}),'apply':True}
              if relay.get('apply') and subject not in world['eyes']:world['eyes'].append(subject)
             elif 'query { viewer' in query:data={'viewer':{'login':'test-app[bot]'}}
             elif 'discussions(' in query:data={'repository':{'discussions':page([message(world['discussion'])])}}
             elif 'comments(' in query:data={'node':{'comments':page([message(c) for c in world['comments'] if c.get('replyTo') is None])}}
             elif 'replies(' in query:data={'node':{'replies':page([message(c) for c in world['comments'] if (c.get('replyTo') or {}).get('id')==variables['id']])}}
             elif 'reactions(' in query:data={'node':{'reactions':page(world['reactions'].get(variables['id'],[]))}}
             elif 'labels(' in query:data={'node':{'labels':page([])}}
             elif 'reactionGroups' in query and 'node(' in query:
              kind='reconcile';subject=variables['id']
              relay=world['responses'].pop(0) if world['responses'] else {'status':200,'body':json.dumps({'data':{'node':{'reactionGroups':groups(subject)}}})}
             else:raise RuntimeError('Unexpected GitHub request: '+query)
             with open(REQUESTS,'a') as log:log.write(json.dumps({'kind':kind,'subject':variables.get('id'),'include':'--include' in args})+'\n')
             handle.seek(0);handle.truncate();json.dump(world,handle)
            if relay is None:
             print(json.dumps({'data':data}));sys.exit(0)
            class Peer(http.server.BaseHTTPRequestHandler):
             def do_POST(self):
              # gh streams --input - as a chunked body. Read all of it before answering: closing with request bytes unread
              # resets the connection, and gh then reports a broken pipe or loses the response it already received.
              if self.headers.get('Transfer-Encoding','').lower()=='chunked':
               while (size:=int(self.rfile.readline().split(b';')[0],16)):self.rfile.read(size+2)
               while self.rfile.readline() not in (b'\r\n',b'\n',b''):pass
              else:self.rfile.read(int(self.headers.get('Content-Length','0')))
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
