using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

// Real controller lifetimes, wrapper processes, files and logs; only the broker, GitHub and native peers are
// controlled. Every broker-issued token is a canary: consumers must see it, persistent outputs must not.
[Collection("Managed checkout environment")]
public sealed class MemoryOnlyTokenTests
{
    const string Canary = "opaque-token-";

    [Fact]
    public async Task Controller_and_agent_gh_use_the_current_App_token_across_renewal_and_restart_without_persisting_it()
    {
        if (!OperatingSystem.IsLinux()) return; // Shipping Linux process fixtures.
        using var fixture = new BrokerAuthenticationTests.Fixture();
        using var peers = new Peers(fixture);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = BridgeCommand.Run(Arguments(fixture, "--interval", "3600"), fixture.Http, fixture.Gh, fixture.Clock, shutdown.Token);
        try
        {
            // A fresh start: the agent's gh, launched by the native session through its wrapper, and the
            // controller's own GitHub calls both carry the newly acquired App token.
            await peers.Eventually(calls => calls.Any(call => call.Agent));
            Assert.All(peers.Calls(), call => Assert.Equal(Canary + "1", call.Token));
            Assert.Contains(peers.Calls(), call => !call.Agent);
            AssertNotPersisted(fixture, peers);

            // Renewal while running: existing wrappers and later controller calls observe the new token.
            fixture.Clock.Advance(TimeSpan.FromMinutes(56));
            await Until(() => fixture.Transport.Requests.Count == 2);
            var renewed = await fixture.InvokeWrapper();
            Assert.Equal(0, renewed.Exit);
            Assert.Contains(Canary + "2", renewed.Output);
            Assert.DoesNotContain(Canary + "1", renewed.Output);
            Assert.DoesNotContain("human-token", renewed.Output);
            var before = peers.Calls().Count;
            fixture.Clock.Advance(TimeSpan.FromMinutes(5)); // The next scheduled poll.
            await peers.Eventually(calls => calls.Skip(before).Any(call => !call.Agent));
            Assert.All(peers.Calls().Skip(before), call => Assert.Equal(Canary + "2", call.Token));
            AssertNotPersisted(fixture, peers);
        }
        finally
        {
            await shutdown.CancelAsync();
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
        }

        // A stopped controller leaves no usable credential behind, and the wrapper never falls back to ambient sign-in.
        var stopped = await fixture.InvokeWrapper();
        Assert.NotEqual(0, stopped.Exit);
        Assert.DoesNotContain("gh-invoked", stopped.Output);
        AssertNotPersisted(fixture, peers);

        // Restart acquires a fresh lease from the broker instead of reusing anything from the earlier lifetime.
        var calls = peers.Calls().Count;
        Assert.Equal(0, await BridgeCommand.Run(Arguments(fixture, "--once"), fixture.Http, fixture.Gh, fixture.Clock, TestContext.Current.CancellationToken));
        Assert.Equal(3, fixture.Transport.Requests.Count);
        Assert.All(peers.Calls().Skip(calls), call => Assert.Equal(Canary + "3", call.Token));
        AssertNotPersisted(fixture, peers);
    }

    // Every file the controller and its native peer own: state, logs, credential directory, workspace and history.
    static void AssertNotPersisted(BrokerAuthenticationTests.Fixture fixture, Peers peers)
    {
        var canary = Encoding.UTF8.GetBytes(Canary);
        foreach (var file in Directory.EnumerateFiles(fixture.Directory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            if (new FileInfo(file).Length == 0) continue; // Held lease files are empty and exclusively locked.
            Assert.False(File.ReadAllBytes(file).AsSpan().IndexOf(canary) >= 0, "Token persisted in " + Path.GetRelativePath(fixture.Directory, file));
        }
        Assert.All(peers.Calls(), call => Assert.DoesNotContain(call.Arguments, argument => argument.Contains(Canary)));
    }

    static string[] Arguments(BrokerAuthenticationTests.Fixture fixture, params string[] extra) =>
    [
        "--repo", "owner/repository", "--app-id", "123", "--state", fixture.State,
        "--broker-url", "https://credentials.example.org", "--enrollment-file", fixture.EnrollmentFile,
        "--workspace", fixture.Directory, .. extra
    ];

    static async Task Until(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Expected controller progress was not observed");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    sealed record Call(bool Agent, string Token, string[] Arguments);

    // Controlled GitHub (gh) and native peers. Observations go to a test-owned file outside every controller path.
    sealed class Peers : IDisposable
    {
        readonly string observations = Path.Combine(Path.GetTempPath(), "memory-token-observations-" + Guid.NewGuid().ToString("N"));
        readonly Dictionary<string, string?> previous = new();

        public Peers(BrokerAuthenticationTests.Fixture fixture)
        {
            var tools = Path.Combine(fixture.Directory, "native-tools");
            Directory.CreateDirectory(tools);
            WriteExecutable(Path.Combine(tools, "codex"), Native);
            WriteExecutable(fixture.Gh, GitHub);
            Set("PATH", tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
            Set("MEMORY_TOKEN_OBSERVATIONS", observations);
        }

        void Set(string name, string value)
        {
            previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public List<Call> Calls() => !File.Exists(observations) ? [] : File.ReadAllLines(observations).Select(line =>
        {
            var call = JsonNode.Parse(line)!;
            var arguments = call["argv"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
            return new Call(arguments is ["auth", "token"], call["token"]!.GetValue<string>(), arguments);
        }).Where(call => call.Arguments is not ["capture"]).ToList();

        public Task Eventually(Func<List<Call>, bool> condition) => Until(() => condition(Calls()));

        public void Dispose()
        {
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            File.Delete(observations);
        }

        static void WriteExecutable(string path, string source)
        {
            File.WriteAllText(path, source);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // An agent turn asks its gh for the token, as an agent's own GitHub work would.
        const string Native = """
            #!/usr/bin/python3
            import json,os,subprocess,sys
            for line in sys.stdin:
             request=json.loads(line);method=request.get('method')
             if 'id' not in request:continue
             if method=='turn/start':subprocess.run(['gh','auth','token'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
             result={'codexHome':os.getcwd()} if method=='initialize' else {'turn':{'id':'native-turn'}} if method=='turn/start' else {'thread':{'id':'native-T1','status':{'type':'idle'}}}
             print(json.dumps({'id':request['id'],'result':result}),flush=True)
            """;

        const string GitHub = """
            #!/usr/bin/python3
            import json,os,sys
            a=sys.argv[1:];token=os.environ.get('GH_TOKEN','')
            with open(os.environ['MEMORY_TOKEN_OBSERVATIONS'],'a') as log:log.write(json.dumps({'argv':a,'token':token})+'\n')
            if a==['auth','token']:print(token);sys.exit(0)
            if a==['capture']:print('gh-invoked '+token);sys.exit(0)
            if a[:2]!=['api','graphql']:raise RuntimeError('Unexpected GitHub command')
            q=json.load(sys.stdin)['query'];page={'nodes':[],'pageInfo':{'hasNextPage':False,'endCursor':None}}
            discussion={'id':'D1','number':1,'url':'https://github.com/owner/repository/discussions/1','body':'@test-app[bot] work','bodyHTML':'work','title':'fixture','author':{'__typename':'User','login':'participant'},'reactionGroups':[{'content':'EYES','viewerHasReacted':True}]}
            if 'query { viewer' in q:data={'viewer':{'login':'test-app[bot]'}}
            elif 'discussions(' in q:data={'repository':{'discussions':dict(page,nodes=[discussion])}}
            else:data={'node':{'comments' if 'comments(' in q else 'reactions':page}}
            print(json.dumps({'data':data}))
            """;
    }
}
