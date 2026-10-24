using System.Text.Json;
using System.Text.Json.Nodes;
using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

// The controller entrypoint, its state, the App gh wrapper and process boundaries are real. GitHub's responses and the
// native JSON-RPC peer are controlled; the peer's model catalogue is copied from pinned Codex 0.163.0 model/list.
// That is not live GitHub or OpenAI account evidence.
[Collection("Managed checkout environment")]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")] // The App gh wrapper is Linux/macOS only.
public sealed class ModelSelectionTests
{
    [Fact]
    public async Task A_model_label_selects_the_native_model_and_reasoning_of_the_turn()
    {
        using var fixture = new Fixture();
        fixture.Labels("question", "repoagent:luna6:xhigh");

        Assert.Equal(0, await fixture.RunOnce());

        Assert.Equal([("gpt-6-luna", "xhigh")], fixture.Turns());
        Assert.Contains(" native-accepted session=native-T1 model=gpt-6-luna effort=xhigh selection=label turn=turn-1", fixture.Log);
    }

    [Fact]
    public async Task Without_a_model_label_the_turn_uses_the_repository_defaults()
    {
        using var fixture = new Fixture();
        fixture.Labels("question");
        fixture.Variable("REPOAGENT_MODEL", " Luna6 ");
        fixture.Variable("REPOAGENT_REASONING", "HIGH");

        Assert.Equal(0, await fixture.RunOnce("--repository-settings"));

        Assert.Contains(" repository-setting name=REPOAGENT_MODEL state=configured value=luna6", fixture.Log);
        Assert.Equal([("gpt-6-luna", "high")], fixture.Turns());
    }

    // Pinned Codex keeps a turn/start model or effort for later turns of the thread, so defaults are always sent explicitly.
    [Fact]
    public async Task A_label_change_applies_to_the_next_turn_without_starting_one_and_removal_restores_the_defaults()
    {
        using var fixture = new Fixture();
        fixture.Labels("repoagent:luna6:xhigh");
        Assert.Equal(0, await fixture.RunOnce());

        fixture.Labels("Repoagent:GPT-5.5:High");
        Assert.Equal(0, await fixture.RunOnce());
        Assert.Single(fixture.Turns()); // A label-only change does not invoke the agent.

        fixture.Comment("C1", "Next question.");
        Assert.Equal(0, await fixture.RunOnce());
        fixture.Labels();
        Assert.Equal(0, await fixture.RunOnce());
        fixture.Comment("C2", "Another question.");
        Assert.Equal(0, await fixture.RunOnce());

        Assert.Equal([("gpt-6-luna", "xhigh"), ("gpt-5.5", "high"), ("gpt-6.1-sol", "low")], fixture.Turns());
    }

    [Theory]
    [InlineData("repoagent:luna6:xhigh,repoagent:sol6.1:low", null, null,
        "conflicting model labels repoagent:luna6:xhigh, repoagent:sol6.1:low; keep one repoagent:<model>:<reasoning> label")]
    [InlineData("repoagent:luna6", null, null, "label repoagent:luna6 must be repoagent:<model>:<reasoning>, such as repoagent:sol6.1:xhigh")]
    [InlineData("repoagent:sun9:high", null, null,
        "model sun9 from label repoagent:sun9:high is not available to this Codex account; available: sol6.1 (gpt-6.1-sol), luna6 (gpt-6-luna), gpt-5.5")]
    [InlineData("repoagent:luna6:ultra", null, null,
        "reasoning ultra from label repoagent:luna6:ultra is not supported by gpt-6-luna; supported: low, medium, high, xhigh, max")]
    [InlineData("", "gpt-5.5", "max", "reasoning max from REPOAGENT_REASONING is not supported by gpt-5.5; supported: low, medium, high, xhigh")]
    [InlineData("", "gpt 6.1", null, "REPOAGENT_MODEL is invalid (must be a name of at most 64 letters, digits, '.', '_' or '-') and no earlier value is known; correct or clear the variable")]
    [InlineData("", null, "very high", "REPOAGENT_REASONING is invalid (must be a name of at most 64 letters, digits, '.', '_' or '-') and no earlier value is known; correct or clear the variable")]
    public async Task An_unusable_selection_holds_the_input_with_an_actionable_error_until_it_is_corrected(
        string labels, string? model, string? reasoning, string error)
    {
        using var fixture = new Fixture();
        fixture.Labels(labels.Split(',', StringSplitOptions.RemoveEmptyEntries));
        fixture.Variable("REPOAGENT_MODEL", model);
        fixture.Variable("REPOAGENT_REASONING", reasoning);

        Assert.Equal(0, await fixture.RunOnce("--repository-settings"));

        Assert.Empty(fixture.Turns());
        Assert.Equal(0, fixture.Receipts());
        Assert.Contains(" selection-error https://github.com/owner/repository/discussions/1: " + error, fixture.Log);
        var status = await fixture.Status();
        var failure = Assert.Single(status["failures"]!.AsArray(), item => item!["kind"]!.GetValue<string>() == "selection-error");
        Assert.Contains(error, failure!["message"]!.GetValue<string>());
        Assert.Equal(1, status["work"]!["waiting"]!.GetValue<int>());

        fixture.Labels();
        fixture.Variable("REPOAGENT_MODEL", null);
        fixture.Variable("REPOAGENT_REASONING", null);
        Assert.Equal(0, await fixture.RunOnce("--repository-settings"));

        Assert.Equal([("gpt-6.1-sol", "low")], fixture.Turns());
        Assert.Equal(1, fixture.Receipts());
    }

    // Whether a discussions-only runtime token may read Discussion labels is unverified against live GitHub.
    [Fact]
    public async Task A_failed_label_read_is_reported_and_keeps_the_last_observed_labels()
    {
        using var fixture = new Fixture();
        fixture.Labels("repoagent:luna6:xhigh");
        Assert.Equal(0, await fixture.RunOnce());

        fixture.Labels("repoagent:gpt-5.5:low");
        fixture.FailLabelReads();
        fixture.Comment("C1", "Next question.");
        Assert.Equal(0, await fixture.RunOnce());

        Assert.Equal([("gpt-6-luna", "xhigh"), ("gpt-6-luna", "xhigh")], fixture.Turns());
        Assert.Contains(" label-read-error https://github.com/owner/repository/discussions/1: ", fixture.Log);
        Assert.Contains("Resource not accessible by integration", fixture.Log);
        Assert.Contains((await fixture.Status())["failures"]!.AsArray(), item => item!["kind"]!.GetValue<string>() == "label-read-error");
    }

    sealed class Fixture : IDisposable
    {
        readonly string world, requests, native, nativeConfig, gh, privateKey;
        readonly string? path = Environment.GetEnvironmentVariable("PATH");
        readonly System.Security.Cryptography.RSA key = System.Security.Cryptography.RSA.Create(2048);
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "model-selection-" + Guid.NewGuid().ToString("N"));
        public RepositorySettingsTests.Provider Provider { get; } = new();
        public string Log { get; private set; } = "";

        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            privateKey = Path.Combine(Directory, "app.pem");
            File.WriteAllText(privateKey, key.ExportRSAPrivateKeyPem());
            world = Path.Combine(Directory, "github-world.json");
            requests = Path.Combine(Directory, "github-requests.jsonl");
            native = Path.Combine(Directory, "native-requests.jsonl");
            nativeConfig = Path.Combine(Directory, "native-config.json");
            gh = Path.Combine(Directory, "gh");
            var tools = Path.Combine(Directory, "native-tools");
            System.IO.Directory.CreateDirectory(tools);
            WriteExecutable(Path.Combine(tools, "codex"), Native.Replace("NATIVE_LOG", Quote(native)).Replace("NATIVE_CONFIG", Quote(nativeConfig)));
            WriteExecutable(gh, GitHub.Replace("WORLD", Quote(world)).Replace("REQUESTS", Quote(requests)));
            File.WriteAllText(nativeConfig, "{}");
            File.WriteAllText(world, new JsonObject
            {
                ["discussion"] = Message("D1", "Please review the fixture.", new JsonObject
                {
                    ["number"] = 1, ["url"] = "https://github.com/owner/repository/discussions/1", ["title"] = "Fixture",
                    ["closed"] = false, ["locked"] = false, ["answer"] = null, ["category"] = new JsonObject { ["id"] = "DIC1", ["name"] = "General" }
                }),
                ["comments"] = new JsonArray(), ["labels"] = new JsonArray(), ["variables"] = new JsonObject()
            }.ToJsonString());
            Environment.SetEnvironmentVariable("PATH", tools + Path.PathSeparator + path);
        }

        /// <summary>Replaces the Discussion's labels; label names are GitHub's, unvalidated.</summary>
        public void Labels(params string[] names) => Update(state => state["labels"] = new JsonArray(names.Select(name => (JsonNode)name).ToArray()));

        /// <summary>GitHub denies Discussion label reads, as for a token without label access.</summary>
        public void FailLabelReads() => Update(state => state["labelsDenied"] = true);

        /// <summary>Sets a direct repository Actions variable, or removes it when <paramref name="value"/> is null.</summary>
        public void Variable(string name, string? value) => Update(state =>
        {
            if (value is null) state["variables"]!.AsObject().Remove(name);
            else state["variables"]![name] = value;
        });

        /// <summary>Adds a participant's top-level comment.</summary>
        public void Comment(string id, string body) => Update(state => state["comments"]!.AsArray().Add((JsonNode)Message(id, body, new JsonObject
        {
            ["url"] = "https://github.com/owner/repository/discussions/1#discussioncomment-" + id, ["replyTo"] = null
        })));

        static JsonObject Message(string id, string body, JsonObject fields)
        {
            fields["id"] = id; fields["body"] = body; fields["bodyHTML"] = "<p>" + body + "</p>";
            fields["author"] = new JsonObject { ["__typename"] = "User", ["login"] = "participant" };
            fields["reactionGroups"] = new JsonArray();
            return fields;
        }

        void Update(Action<JsonObject> change)
        {
            var state = JsonNode.Parse(File.ReadAllText(world))!.AsObject();
            change(state);
            File.WriteAllText(world, state.ToJsonString());
        }

        public async Task<int> RunOnce(params string[] options)
        {
            var original = Console.Out;
            using var capture = new StringWriter();
            Console.SetOut(TextWriter.Synchronized(capture));
            try
            {
                using var http = new HttpClient(Provider, disposeHandler: false);
                return await BridgeCommand.Run([.. Arguments, "--once", .. options], http, gh, shutdownToken: TestContext.Current.CancellationToken);
            }
            finally
            {
                Console.SetOut(original);
                Log += capture.ToString();
                original.Write(capture.ToString());
            }
        }

        string[] Arguments =>
        [
            "--repo", "owner/repository", "--app-id", "123", "--private-key", privateKey, "--state", Path.Combine(Directory, "bridge.json"),
            "--workspace", Directory, "--interval", "3600", "--enroll", "https://github.com/owner/repository/discussions/1"
        ];

        /// <summary>The operator status command's JSON for this controller state.</summary>
        public async Task<JsonNode> Status()
        {
            var original = Console.Out;
            using var capture = new StringWriter();
            Console.SetOut(capture);
            try { Assert.Equal(0, await BridgeCommand.Run(["status", "--state", Path.Combine(Directory, "bridge.json")])); }
            finally { Console.SetOut(original); }
            return JsonNode.Parse(capture.ToString())!;
        }

        /// <summary>EYES receipt writes the controller sent to GitHub.</summary>
        public int Receipts() => Lines(requests).Count(line => line["query"]!.GetValue<string>().Contains("addReaction", StringComparison.Ordinal));

        /// <summary>Model and reasoning effort serialized into each native turn/start, in order.</summary>
        public (string? Model, string? Effort)[] Turns() => Lines(native).Where(line => line["method"]?.GetValue<string>() == "turn/start")
            .Select(line => (line["params"]?["model"]?.GetValue<string>(), line["params"]?["effort"]?.GetValue<string>())).ToArray();

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
            key.Dispose();
            System.IO.Directory.Delete(Directory, true);
        }

        // Answers from the controlled world; --include output mirrors gh's status/header framing.
        const string GitHub = """
            #!/usr/bin/python3
            import json,sys
            a=sys.argv[1:]
            world=json.load(open(WORLD))
            method=a[a.index('--method')+1];endpoint=a[1]
            request=json.loads(sys.stdin.read()) if '--input' in a else {}
            query=request.get('query','');variables=request.get('variables') or {}
            with open(REQUESTS,'a') as log:log.write(json.dumps({'method':method,'endpoint':endpoint,'query':query})+'\n')
            def reply(status,body):
             if '--include' in a:sys.stdout.write('HTTP/2.0 %d Fixture\r\nContent-Type: application/json\r\n\r\n'%status)
             sys.stdout.write(json.dumps(body))
             if status>=400:sys.stderr.write('gh: Fixture failure (HTTP %d)\n'%status);sys.exit(1)
             sys.exit(0)
            page=lambda nodes:{'nodes':nodes,'pageInfo':{'hasNextPage':False,'endCursor':None}}
            if endpoint=='graphql':
             if 'query { viewer' in query:reply(200,{'data':{'viewer':{'login':'test-app[bot]'}}})
             if 'addReaction' in query:reply(200,{'data':{'addReaction':{'reaction':{'id':'R-'+variables['id']}}}})
             if 'discussions(' in query:reply(200,{'data':{'repository':{'discussions':page([world['discussion']])}}})
             if 'labels(' in query and world.get('labelsDenied'):
              sys.stdout.write(json.dumps({'data':{'node':{'labels':None}},'errors':[{'type':'FORBIDDEN','path':['node','labels'],'message':'Resource not accessible by integration'}]}))
              sys.stderr.write('gh: Resource not accessible by integration\n');sys.exit(1)
             if 'labels(' in query:reply(200,{'data':{'node':{'labels':page([{'name':name} for name in world['labels']])}}})
             if 'comments(' in query:reply(200,{'data':{'node':{'comments':page(world['comments'])}}})
             for field in ('replies','reactions'):
              if field+'(' in query:reply(200,{'data':{'node':{field:page([])}}})
            prefix='repos/owner/repository/actions/variables/'
            if method=='GET' and endpoint.startswith(prefix):
             name=endpoint[len(prefix):]
             if name in world['variables']:reply(200,{'name':name,'value':world['variables'][name]})
             reply(404,{'message':'Not Found'})
            sys.stderr.write('Unexpected GitHub command '+repr(a)+'\n');sys.exit(2)
            """;

        // Each turn completes immediately, so every --once run finds the stored session idle.
        const string Native = """
            #!/usr/bin/python3
            import json,os,sys
            efforts=lambda *names:[{'reasoningEffort':name,'description':''} for name in names]
            model=lambda id,default,effort,supported:{'id':id,'model':id,'displayName':id,'description':'','hidden':False,'isDefault':default,'defaultReasoningEffort':effort,'supportedReasoningEfforts':efforts(*supported)}
            models=[model('gpt-6.1-sol',True,'low',('low','medium','high','xhigh','max','ultra')),
             model('gpt-6-luna',False,'medium',('low','medium','high','xhigh','max')),
             model('gpt-5.5',False,'medium',('low','medium','high','xhigh'))]
            turns=0
            for line in sys.stdin:
             request=json.loads(line)
             with open(NATIVE_LOG,'a') as log:log.write(line)
             if 'id' not in request:continue
             method=request['method'];after=None
             if method=='initialize':result={'codexHome':os.getcwd()}
             elif method=='model/list':result={'data':models,'nextCursor':None}
             elif method=='config/read':result={'config':json.load(open(NATIVE_CONFIG))}
             elif method in ('thread/start','thread/resume'):result={'thread':{'id':'native-T1','status':{'type':'idle'}}}
             elif method=='turn/start':
              turns+=1;result={'turn':{'id':'turn-%d'%turns,'status':'inProgress'}}
              after={'method':'turn/completed','params':{'threadId':'native-T1','turn':{'id':'turn-%d'%turns,'status':'completed'}}}
             else:result={}
             print(json.dumps({'id':request['id'],'result':result}),flush=True)
             if after:print(json.dumps(after),flush=True)
            """;
    }
}
