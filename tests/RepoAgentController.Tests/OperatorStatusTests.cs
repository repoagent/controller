using System.Diagnostics;
using System.Text.Json.Nodes;
using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

// Operator status and retained logs are observed only through the controller's public status/logs
// commands, run as separate processes, over real controller lifetimes. GitHub, broker and native
// peers are controlled transports; files, processes, restart and rotation are real.
[Collection("Managed checkout environment")]
public sealed class OperatorStatusTests
{
    const string Discussion = "https://github.com/owner/repository/discussions/1";
    const string DeviceSecret = "abcdefghijklmnopqrstuvwxyz_0123456789-ABCDEFG";
    const string TranscriptCanary = "PRIVATE-TRANSCRIPT-CANARY";

    [Fact]
    public async Task Status_and_retained_logs_explain_a_failed_delivery_of_a_running_controller_without_secrets()
    {
        if (!OperatingSystem.IsLinux()) return; // Shipping Linux process fixtures.
        using var fixture = new BrokerAuthenticationTests.Fixture();
        using var environment = new NativeEnvironment(fixture, FailingNative, EnrollingGitHub);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var stdout = new CapturedConsole();
        var run = BridgeCommand.Run(ControllerArguments(fixture, "--interval", "3600"), fixture.Http, fixture.Gh, fixture.Clock, shutdown.Token);
        try
        {
            // The native peer rejects the first delivery; the controller keeps running afterwards.
            var running = await Eventually(fixture.State, status =>
                status["failures"]?.AsArray().Count > 0 && status["controller"]?["state"]?.GetValue<string>() == "running");
            Assert.False(run.IsCompleted);
            Assert.Equal("owner/repository", running["repository"]!.GetValue<string>());
            Assert.Equal("test-app[bot]", running["actor"]!.GetValue<string>());
            Assert.Equal("healthy", running["authentication"]!["state"]!.GetValue<string>());
            var session = Assert.Single(running["sessions"]!.AsArray())!;
            Assert.Equal(Discussion, session["discussion"]!.GetValue<string>());
            Assert.Equal("native-T1", session["session"]!.GetValue<string>());
            Assert.Equal("pending", session["state"]!.GetValue<string>());
            Assert.Equal(1, running["work"]!["pending"]!.GetValue<int>());
            var failure = Assert.Single(running["failures"]!.AsArray(), item => item!["message"]!.GetValue<string>().Contains(Discussion))!;

            // The same failure and its session are correlated in the retained log by nonsecret identifiers.
            var retained = await RetainedLogs(fixture.State);
            Assert.Contains(failure["message"]!.GetValue<string>(), retained);
            Assert.Contains(retained.Split('\n'), line => line.Contains(Discussion) && line.Contains("native-T1"));
            AssertNoSecrets(running.ToJsonString(), retained);
        }
        finally
        {
            await shutdown.CancelAsync();
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
        }

        // A stopped controller still explains the unresolved delivery without its checkpoint files.
        var stopped = await Status(fixture.State);
        Assert.Equal("stopped", stopped["controller"]!["state"]!.GetValue<string>());
        Assert.Equal("pending", Assert.Single(stopped["sessions"]!.AsArray())!["state"]!.GetValue<string>());
        Assert.Contains(stopped["failures"]!.AsArray(), item => item!["message"]!.GetValue<string>().Contains(Discussion));
        AssertNoSecrets(stopped.ToJsonString(), await RetainedLogs(fixture.State));
        Assert.Contains(Discussion, stdout.Text); // Retention adds to stdout; it does not replace it.
    }

    [Fact]
    public async Task Retained_logs_survive_restarts_and_rotate_within_the_configured_bound()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new BrokerAuthenticationTests.Fixture();
        using var environment = new NativeEnvironment(fixture, IdleNative, FailingPollGitHub);
        const int FileBytes = 1024, Files = 2;
        var retention = Path.Combine(fixture.Directory, "state.json.logs"); // Documented location beside --state.
        var markers = new List<string>();
        using var stdout = new CapturedConsole();
        for (var lifetime = 1; lifetime <= 6; lifetime++)
        {
            // Each controller lifetime reports a distinct poll failure; about two fit in one file.
            var marker = $"lifetime-{lifetime}-{Guid.NewGuid():N}";
            markers.Add(marker);
            File.WriteAllText(Path.Combine(fixture.Directory, "poll-failure"), marker);
            var exit = await BridgeCommand.Run(ControllerArguments(fixture, "--once", "--log-file-bytes", FileBytes.ToString(), "--log-files", Files.ToString()),
                fixture.Http, fixture.Gh, fixture.Clock, TestContext.Current.CancellationToken);
            Assert.Equal(1, exit); // The failed poll, not a startup failure.
            var retained = await RetainedLogs(fixture.State);
            Assert.Contains(marker, retained);
            Assert.Contains(marker, stdout.Text);
            if (lifetime == 2) Assert.Contains(markers[0], retained); // The earlier lifetime survived restart.
            var stored = Directory.GetFiles(retention);
            Assert.InRange(stored.Length, 1, Files);
            Assert.InRange(stored.Sum(file => new FileInfo(file).Length), 1, FileBytes * Files);
        }
        var final = await RetainedLogs(fixture.State);
        Assert.DoesNotContain(markers[0], final); // Rotated out rather than accumulated.
        Assert.Contains(markers[^2], final);
        Assert.Contains(markers[0], stdout.Text); // Stdout is unaffected by retention bounds.
    }

    [Fact]
    public async Task Status_reports_failed_authentication_after_broker_renewal_is_denied()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new BrokerAuthenticationTests.Fixture();
        using var environment = new NativeEnvironment(fixture, IdleNative, QuietGitHub);
        using var stdout = new CapturedConsole();
        var run = BridgeCommand.Run(ControllerArguments(fixture, "--interval", "3600"), fixture.Http, fixture.Gh, fixture.Clock, TestContext.Current.CancellationToken);
        var healthy = await Eventually(fixture.State, status => status["controller"]?["state"]?.GetValue<string>() == "running");
        Assert.Equal("healthy", healthy["authentication"]!["state"]!.GetValue<string>());

        fixture.Transport.Status = System.Net.HttpStatusCode.Unauthorized;
        fixture.Clock.Advance(TimeSpan.FromMinutes(56)); // Scheduled renewal reaches the denying broker.
        Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        var failed = await Status(fixture.State);
        Assert.Equal("failed", failed["authentication"]!["state"]!.GetValue<string>());
        Assert.Equal("failed", failed["controller"]!["state"]!.GetValue<string>());
        var retained = await RetainedLogs(fixture.State);
        Assert.Contains(failed["authentication"]!["error"]!.GetValue<string>(), retained);
        AssertNoSecrets(failed.ToJsonString(), retained);
    }

    static void AssertNoSecrets(params string[] outputs)
    {
        foreach (var output in outputs)
        {
            Assert.DoesNotContain("opaque-token-", output); // Broker-issued installation tokens.
            Assert.DoesNotContain(DeviceSecret, output);
            Assert.DoesNotContain(TranscriptCanary, output);
        }
    }

    static string[] ControllerArguments(BrokerAuthenticationTests.Fixture fixture, params string[] extra) =>
    [
        "--repo", "owner/repository", "--app-id", "123", "--state", fixture.State,
        "--broker-url", "https://credentials.example.org", "--enrollment-file", fixture.EnrollmentFile,
        "--workspace", fixture.Directory, .. extra
    ];

    static async Task<JsonNode> Eventually(string state, Func<JsonNode, bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        var last = "";
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            var (exit, output, error) = await Controller("status", "--state", state);
            last = $"exit={exit} {output} {error}";
            if (exit == 0 && JsonNode.Parse(output) is { } status && condition(status)) return status;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException("Expected controller status was not reported: " + last);
    }

    static async Task<JsonNode> Status(string state)
    {
        var (exit, output, error) = await Controller("status", "--state", state);
        Assert.True(exit == 0, error);
        return JsonNode.Parse(output)!;
    }

    static async Task<string> RetainedLogs(string state)
    {
        var (exit, output, error) = await Controller("logs", "--state", state);
        Assert.True(exit == 0, error);
        return output;
    }

    // The operator-facing controller CLI in a separate process, like the container's status command.
    static async Task<(int Exit, string Output, string Error)> Controller(params string[] arguments)
    {
        var apphost = Path.Combine(AppContext.BaseDirectory, "RepoAgentController");
        var start = File.Exists(apphost) ? new ProcessStartInfo(apphost) : new ProcessStartInfo("dotnet")
        {
            ArgumentList = { Path.Combine(AppContext.BaseDirectory, "RepoAgentController.dll") }
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        return (process.ExitCode, await output, await error);
    }

    sealed class CapturedConsole : IDisposable
    {
        readonly TextWriter previous = Console.Out;
        readonly StringWriter writer = new();
        public CapturedConsole() => Console.SetOut(TextWriter.Synchronized(writer));
        public string Text { get { lock (writer) return writer.ToString(); } }
        public void Dispose() => Console.SetOut(previous);
    }

    // Installs controlled GitHub and native peers; native is first on PATH for this non-parallel collection.
    sealed class NativeEnvironment : IDisposable
    {
        readonly string? path = Environment.GetEnvironmentVariable("PATH");
        public NativeEnvironment(BrokerAuthenticationTests.Fixture fixture, string native, string github)
        {
            var tools = Path.Combine(fixture.Directory, "native-tools");
            Directory.CreateDirectory(tools);
            WriteExecutable(Path.Combine(tools, "codex"), native);
            WriteExecutable(fixture.Gh, github);
            Environment.SetEnvironmentVariable("PATH", tools + Path.PathSeparator + path);
        }
        public void Dispose() => Environment.SetEnvironmentVariable("PATH", path);
    }

    static void WriteExecutable(string path, string source)
    {
        File.WriteAllText(path, source);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    // Starts a session, reports a completed turn carrying transcript content, then rejects the
    // delivery with an error that echoes the controller's live installation token, obtained through its gh.
    const string FailingNative = $$$"""
        #!/usr/bin/python3
        import json,os,subprocess,sys
        for line in sys.stdin:
         request=json.loads(line);method=request.get('method')
         if 'id' not in request:continue
         if method=='turn/start':
          item={'type':'agentMessage','id':'item-1','text':'{{{TranscriptCanary}}}'}
          turn={'id':'earlier-turn','status':'completed','items':[item]}
          print(json.dumps(dict(method='turn/completed',params=dict(threadId='native-T1',turn=turn))),flush=True)
          token=subprocess.run(['gh','auth','token'],capture_output=True,text=True,check=True).stdout.strip()
          assert token.startswith('opaque-token-')
          error={'code':-32000,'message':'turn rejected while holding '+token}
          print(json.dumps(dict(id=request['id'],error=error)),flush=True)
          continue
         result={'codexHome':os.getcwd()} if method=='initialize' else {'thread':dict(id='native-T1',status=dict(type='idle'))}
         print(json.dumps({'id':request['id'],'result':result}),flush=True)
        """;

    const string IdleNative = """
        #!/usr/bin/python3
        import json,os,sys
        for line in sys.stdin:
         request=json.loads(line)
         if 'id' in request:print(json.dumps({'id':request['id'],'result':{'codexHome':os.getcwd()}}),flush=True)
        """;

    // Identity succeeds; every Discussion poll fails with the marker the test placed beside this stub.
    const string FailingPollGitHub = """
        #!/usr/bin/python3
        import json,os,sys
        a=sys.argv[1:]
        if a[:2]!=['api','graphql']:raise RuntimeError('Unexpected GitHub command')
        if 'query { viewer' in json.load(sys.stdin)['query']:
         print(json.dumps({'data':{'viewer':{'login':'test-app[bot]'}}}));sys.exit(0)
        sys.stderr.write(open(os.path.join(os.path.dirname(os.path.abspath(sys.argv[0])),'poll-failure')).read());sys.exit(1)
        """;

    const string QuietGitHub = """
        #!/usr/bin/python3
        import json,sys
        a=sys.argv[1:]
        if a[:2]!=['api','graphql']:raise RuntimeError('Unexpected GitHub command')
        page={'nodes':[],'pageInfo':{'hasNextPage':False,'endCursor':None}}
        q=json.load(sys.stdin)['query']
        print(json.dumps({'data':{'viewer':{'login':'test-app[bot]'}} if 'query { viewer' in q else {'repository':{'discussions':page}}}))
        """;

    const string EnrollingGitHub = """
        #!/usr/bin/python3
        import json,os,sys
        a=sys.argv[1:]
        if a==['auth','token']:print(os.environ.get('GH_TOKEN',''));sys.exit(0)
        if a[:2]!=['api','graphql']:raise RuntimeError('Unexpected GitHub command')
        q=json.load(sys.stdin)['query'];page={'nodes':[],'pageInfo':{'hasNextPage':False,'endCursor':None}}
        discussion={'id':'D1','number':1,'url':'https://github.com/owner/repository/discussions/1','body':'@test-app[bot] work','bodyHTML':'work','title':'fixture','author':{'__typename':'User','login':'participant'},'reactionGroups':[]}
        if 'query { viewer' in q:data={'viewer':{'login':'test-app[bot]'}}
        elif 'discussions(' in q:data={'repository':{'discussions':dict(page,nodes=[discussion])}}
        else:data={'node':{'comments' if 'comments(' in q else 'reactions':page}}
        print(json.dumps({'data':data}))
        """;
}
