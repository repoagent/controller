using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

[Collection("Managed checkout environment")]
public sealed class BrokerLifetimeTests
{
    [Theory]
    [InlineData("turn", false)]
    [InlineData("initialize", false)]
    public async Task Failed_broker_renewal_stops_owned_native_work_and_preserves_recovery(string stage, bool webhooks)
        => await Exercise(stage, webhooks, false);

    [Fact]
    public async Task User_cancellation_keeps_a_successful_exit_and_drains_native_work()
        => await Exercise("turn", false, true);

    static async Task Exercise(string stage, bool webhooks, bool userCancellation)
    {
        if (!OperatingSystem.IsLinux()) return; // Shipping Linux process-tree boundary, observed through /proc.
        using var fixture = new BrokerAuthenticationTests.Fixture(webhooks: webhooks);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var ready = new TcpListener(IPAddress.Loopback, 0);
        ready.Start();
        var settingsPath = Path.Combine(fixture.Directory, "native-settings.json");
        var history = Path.Combine(fixture.Directory, "history.jsonl");
        var requests = Path.Combine(fixture.Directory, "native-requests.jsonl");
        var eof = Path.Combine(fixture.Directory, "stdin-closed");
        var tools = Path.Combine(fixture.Directory, "native-tools");
        Directory.CreateDirectory(tools);
        var settings = new JsonObject
        {
            ["port"] = ((IPEndPoint)ready.LocalEndpoint).Port, ["stage"] = stage,
            ["tokenFile"] = fixture.TokenFile, ["history"] = history, ["requests"] = requests,
            ["eof"] = eof, ["graceful"] = userCancellation
        };
        File.WriteAllText(settingsPath, settings.ToJsonString());
        WriteExecutable(Path.Combine(tools, "codex"), Native);
        WriteExecutable(fixture.Gh, GitHub);
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["NATIVE_DRAIN_SETTINGS"] = settingsPath
        };
        var previous = environment.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        Process? parent = null, child = null;
        using var unrelated = Process.Start("sleep", "300")!;
        Task<int>? run = null;
        try
        {
            foreach (var pair in environment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            var args = new List<string>
            {
                "--repo", "owner/repository", "--app-id", "123", "--state", fixture.State,
                "--broker-url", "https://credentials.example.org", "--enrollment-file", fixture.EnrollmentFile,
                "--workspace", fixture.Directory, "--interval", "3600"
            };
            if (webhooks)
            {
                using var port = new TcpListener(IPAddress.Loopback, 0);
                port.Start();
                args.AddRange(["--webhook-url", "https://controller.example.org", "--listen-port",
                    ((IPEndPoint)port.LocalEndpoint).Port.ToString()]);
            }
            run = BridgeCommand.Run(args.ToArray(), fixture.Http, fixture.Gh, fixture.Clock, shutdown.Token);
            using var connection = await ready.AcceptTcpClientAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            using var reader = new StreamReader(connection.GetStream());
            var proof = JsonNode.Parse((await reader.ReadLineAsync(TestContext.Current.CancellationToken))!)!;
            parent = Process.GetProcessById(proof["parent"]!.GetValue<int>());
            child = Process.GetProcessById(proof["child"]!.GetValue<int>());
            Assert.True(proof["credentialCopied"]!.GetValue<bool>());
            Assert.False(parent.HasExited);
            Assert.False(child.HasExited);
            Assert.False(run.IsCompleted); // Native initialized or received turn input; startup failure cannot pass.
            var state = File.ReadAllText(fixture.State);
            var enrollment = File.ReadAllText(fixture.EnrollmentFile);
            var nativeHistory = File.ReadAllText(history);
            var admitted = File.ReadAllText(requests);
            if (stage == "turn")
            {
                Assert.Contains("turn/start", admitted);
                Assert.NotNull(JsonNode.Parse(state)!["discussions"]!["D1"]!["pending"]);
            }
            Assert.Equal(1, await fixture.Start()); // Existing live controller still owns the actual state lock.
            if (userCancellation) await shutdown.CancelAsync();
            else
            {
                fixture.Transport.Status = HttpStatusCode.Unauthorized;
                fixture.Transport.Requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Clock.Advance(TimeSpan.FromMinutes(56));
                await fixture.Transport.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            }
            // Real-time deadline is a failure watchdog, never the renewal clock.
            var exit = await run.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
            Assert.Equal(userCancellation ? 0 : 1, exit);
            AssertStopped(parent);
            AssertStopped(child);
            Assert.False(unrelated.HasExited);
            Assert.Equal(state, File.ReadAllText(fixture.State));
            Assert.Equal(enrollment, File.ReadAllText(fixture.EnrollmentFile));
            Assert.Equal(nativeHistory, File.ReadAllText(history));
            Assert.Equal(admitted, File.ReadAllText(requests));
            if (!userCancellation) Assert.False(File.Exists(eof)); // Kill the tree before EOF can orphan a bearer holder.
            var denied = await fixture.InvokeWrapper();
            Assert.NotEqual(0, denied.Exit);
            Assert.DoesNotContain("gh-invoked", denied.Output);
            using var released = new FileStream(fixture.State + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            await shutdown.CancelAsync();
            foreach (var process in new[] { parent, child, unrelated })
                if (process is not null && !process.HasExited) process.Kill(true);
            if (run is not null) await run.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            parent?.Dispose(); child?.Dispose();
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    static void AssertStopped(Process process)
    {
        var stat = "/proc/" + process.Id + "/stat";
        Assert.True(process.HasExited || !File.Exists(stat) || File.ReadAllText(stat).Split(") ")[1][0] == 'Z',
            "Owned native process is still executing: " + process.Id);
    }

    static void WriteExecutable(string path, string source)
    {
        File.WriteAllText(path, source);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    // Only the unmanaged JSON-RPC peer is controlled; OS processes, pipes, token cache and locks are real.
    const string Native = """"
        #!/usr/bin/python3
        import json,os,subprocess,sys
        s=json.load(open(os.environ['NATIVE_DRAIN_SETTINGS']))
        child=None
        def start_child():
         global child
         code=r"""
        import json,os,socket,threading
        s=json.load(open(os.environ['NATIVE_DRAIN_SETTINGS']))
        token=json.load(open(s['tokenFile']))['token']
        with socket.create_connection(('127.0.0.1',s['port'])) as ready:
         ready.sendall((json.dumps({'parent':os.getppid(),'child':os.getpid(),'credentialCopied':token.startswith('opaque-token-')})+'\n').encode())
        threading.Event().wait()
        """
         with open(s['history'],'w') as history:history.write('retain native history\n')
         child=subprocess.Popen([sys.executable,'-c',code],stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL)
        for line in sys.stdin:
         request=json.loads(line)
         with open(s['requests'],'a') as log:log.write(line)
         method=request['method']
         if method=='initialize' and s['stage']=='initialize':start_child();continue
         if method=='turn/start':start_child();continue
         if 'id' not in request:continue
         result={'codexHome':os.getcwd()} if method=='initialize' else {'thread':{'id':'native-T1'}}
         print(json.dumps({'id':request['id'],'result':result}),flush=True)
        with open(s['eof'],'w') as marker:marker.write('EOF')
        if child and s['graceful']:child.terminate();child.wait()
        """";

    const string GitHub = """
        #!/usr/bin/python3
        import json,sys
        a=sys.argv[1:]
        if a[:2]==['api','graphql']:
         q=json.load(sys.stdin)['query'];page={'nodes':[],'pageInfo':{'hasNextPage':False,'endCursor':None}}
         discussion={'id':'D1','number':1,'url':'https://github.com/owner/repository/discussions/1','body':'@test-app[bot] work','bodyHTML':'work','title':'fixture','author':{'__typename':'User','login':'participant'},'reactionGroups':[]}
         if 'query { viewer' in q:data={'viewer':{'login':'test-app[bot]'}}
         elif 'discussions(' in q:data={'repository':{'discussions':dict(page,nodes=[discussion])}}
         else:data={'node':{'comments' if 'comments(' in q else 'reactions':page}}
         print(json.dumps({'data':data}))
        elif len(a)>1 and a[1].startswith('repos/owner/repository/hooks'):
         body=json.load(sys.stdin);print(json.dumps({'id':1,'active':body.get('active',True)}))
        else:raise RuntimeError('Unexpected GitHub command')
        """;
}
