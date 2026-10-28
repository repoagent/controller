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
    [InlineData("turn", false, "denied")]
    [InlineData("initialize", false, "denied")]
    [InlineData("turn", true, "denied")]
    [InlineData("turn", false, "malformed")]
    [InlineData("turn", false, "transport")]
    [InlineData("turn", false, "deadline")]
    [InlineData("turn", false, "unavailable")]
    public async Task Failed_broker_renewal_stops_owned_native_work_and_preserves_recovery(string stage, bool webhooks, string failure)
        => await Exercise(stage, webhooks, false, failure);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task User_cancellation_keeps_a_successful_exit_and_drains_native_work(bool webhooks)
        => AcceptedDrain(webhooks, brokerDenied: false, stopBeforeAcknowledgement: false);

    [Fact]
    public async Task Concurrent_broker_renewal_and_native_disposal_cannot_orphan_a_bearer_holder()
        => await Exercise("teardown", false, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Broker_loss_during_normal_drain_stops_owned_work_and_preserves_accepted_evidence(bool webhooks)
        => AcceptedDrain(webhooks, brokerDenied: true, stopBeforeAcknowledgement: false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Normal_stop_does_not_cancel_an_entered_polling_or_webhook_observation(bool webhooks)
        => AcceptedDrain(webhooks, brokerDenied: false, stopBeforeAcknowledgement: true);

    static async Task AcceptedDrain(bool webhooks, bool brokerDenied, bool stopBeforeAcknowledgement)
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new BrokerAuthenticationTests.Fixture(webhooks: webhooks);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var clock = new RenewalClock(fixture.Clock);
        using var log = new RenewalLog(clock);
        using var ready = new TcpListener(IPAddress.Loopback, 0);
        ready.Start();
        var settingsPath = Path.Combine(fixture.Directory, "native-settings.json");
        var history = Path.Combine(fixture.Directory, "history.jsonl");
        var requests = Path.Combine(fixture.Directory, "native-requests.jsonl");
        var eof = Path.Combine(fixture.Directory, "stdin-closed");
        var tools = Path.Combine(fixture.Directory, "native-tools");
        Directory.CreateDirectory(tools);
        File.WriteAllText(settingsPath, new JsonObject
        {
            ["port"] = ((IPEndPoint)ready.LocalEndpoint).Port,
            ["stage"] = "accepted",
            ["history"] = history,
            ["requests"] = requests,
            ["eof"] = eof,
            ["graceful"] = !brokerDenied
        }.ToJsonString());
        WriteExecutable(Path.Combine(tools, "codex"), Native);
        WriteExecutable(fixture.Gh, GitHub);
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var previousSettings = Environment.GetEnvironmentVariable("NATIVE_DRAIN_SETTINGS");
        Process? parent = null, child = null;
        TcpClient? connection = null;
        Task<int>? run = null;
        using var unrelated = Process.Start("sleep", "300")!;
        try
        {
            Environment.SetEnvironmentVariable("PATH", tools + Path.PathSeparator + previousPath);
            Environment.SetEnvironmentVariable("NATIVE_DRAIN_SETTINGS", settingsPath);
            var statusPort = ControllerHarness.FreePort();
            var args = new List<string>
            {
                "--repo", "owner/repository", "--app-id", "123", "--state", fixture.State,
                "--broker-url", "https://credentials.example.org", "--enrollment-file", fixture.EnrollmentFile,
                "--workspace", fixture.Directory, "--interval", "3600", "--status-port", statusPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            if (webhooks) args.AddRange(["--webhook-url", "https://controller.example.org", "--listen-port",
                ControllerHarness.FreePort(statusPort).ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            run = BridgeCommand.Run(args.ToArray(), fixture.Http, fixture.Gh, clock, shutdown.Token);
            connection = await ready.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            using var reader = new StreamReader(connection.GetStream(), leaveOpen: true);
            var proofLine = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var proof = JsonNode.Parse(proofLine!)!;
            parent = Process.GetProcessById(proof["parent"]!.GetValue<int>());
            child = Process.GetProcessById(proof["child"]!.GetValue<int>());
            Assert.True(proof["credentialCopied"]!.GetValue<bool>());
            Assert.NotNull(JsonNode.Parse(File.ReadAllText(fixture.State))!["discussions"]!["D1"]!["pending"]);
            if (stopBeforeAcknowledgement)
            {
                await shutdown.CancelAsync();
                Assert.False(run.IsCompleted);
            }
            // Acknowledgement and correlated completion are two independent native-peer commands.
            await connection.GetStream().WriteAsync(new byte[] { 1 }, TestContext.Current.CancellationToken);
            await log.WaitFor(" forwarded https://github.com/owner/repository/discussions/1 ");
            await log.WaitFor(webhooks ? " reconciliation-completed discussion=all success=True" : " poll-completed success=True");
            var held = File.ReadAllText(fixture.State);
            var record = JsonNode.Parse(held)!["discussions"]!["D1"]!;
            Assert.Null(record["pending"]);
            Assert.NotNull(record["running"]);
            var first = await fixture.InvokeWrapper();
            Assert.Equal(0, first.Exit);
            Assert.Contains("lease-generation-1", first.Output);
            if (!stopBeforeAcknowledgement) await shutdown.CancelAsync();
            await log.WaitFor(" shutdown-draining ");
            Assert.False(run.IsCompleted);
            Assert.False(parent.HasExited);
            Assert.False(child.HasExited);
            Assert.Equal(held, File.ReadAllText(fixture.State));
            Assert.Throws<IOException>(() => { using var conflicting = new FileStream(fixture.State + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None); });
            var enrollment = File.ReadAllText(fixture.EnrollmentFile);
            var nativeHistory = File.ReadAllText(history);
            var admitted = File.ReadAllText(requests);
            fixture.Transport.Requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Clock.Advance(TimeSpan.FromMinutes(56));
            await fixture.Transport.Requested.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            // Snapshot is taken synchronously at publication, before its next delay can race this test's continuation.
            var publishedAt = await log.Renewal;
            await clock.MinuteArmedAfter(publishedAt);
            Assert.Equal(2, fixture.Transport.Requests.Count);
            var renewed = await fixture.InvokeWrapper();
            Assert.Equal(0, renewed.Exit);
            Assert.Contains("lease-generation-2", renewed.Output);
            Assert.DoesNotContain("lease-generation-1", renewed.Output);
            Assert.False(run.IsCompleted);
            Assert.Equal(held, File.ReadAllText(fixture.State));
            if (brokerDenied)
            {
                fixture.Transport.Status = HttpStatusCode.Unauthorized;
                fixture.Transport.Requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Clock.Advance(TimeSpan.FromMinutes(56));
                await fixture.Transport.Requested.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.NotEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                Assert.Equal(3, fixture.Transport.Requests.Count);
                Assert.Equal(held, File.ReadAllText(fixture.State));
                Assert.False(File.Exists(eof)); // Authority loss kills the owned tree before EOF can orphan its child.
                Assert.DoesNotContain(log.Lines, line => line.Contains(" turn-ended ", StringComparison.Ordinal) && line.EndsWith("status=completed", StringComparison.Ordinal));
            }
            else
            {
                await connection.GetStream().WriteAsync(new byte[] { 2 }, TestContext.Current.CancellationToken);
                Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
                var settled = JsonNode.Parse(File.ReadAllText(fixture.State))!["discussions"]!["D1"]!;
                Assert.Null(settled["pending"]);
                Assert.Null(settled["running"]);
                Assert.Contains(log.Lines, line => line.Contains(" turn-ended ", StringComparison.Ordinal) && line.EndsWith("status=completed", StringComparison.Ordinal));
            }
            AssertStopped(parent);
            AssertStopped(child);
            Assert.False(unrelated.HasExited);
            Assert.Equal(enrollment, File.ReadAllText(fixture.EnrollmentFile));
            Assert.Equal(nativeHistory, File.ReadAllText(history));
            Assert.Equal(admitted, File.ReadAllText(requests));
            var denied = await fixture.InvokeWrapper();
            Assert.NotEqual(0, denied.Exit);
            Assert.DoesNotContain("gh-invoked", denied.Output);
            Assert.DoesNotContain("opaque-token-", fixture.RetainedLog);
            Assert.DoesNotContain("opaque-token-", File.ReadAllText(fixture.State));
            Assert.DoesNotContain("opaque-token-", nativeHistory);
            Assert.DoesNotContain("opaque-token-", admitted);
            Assert.DoesNotContain("DO-NOT-LOG", fixture.RetainedLog);
            using var released = new FileStream(fixture.State + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally
        {
            try
            {
                await shutdown.CancelAsync();
                // Failure cleanup releases only this fixture's held turn; it cannot satisfy the assertions above.
                if (run is { IsCompleted: false } && connection is not null)
                    try { await connection.GetStream().WriteAsync(new byte[] { 1, 2 }, CancellationToken.None); }
                    catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException) { }
                foreach (var process in new[] { parent, child, unrelated })
                    if (process is not null && !process.HasExited) process.Kill(true);
                if (run is not null) await run.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            finally
            {
                connection?.Dispose();
                parent?.Dispose();
                child?.Dispose();
                Environment.SetEnvironmentVariable("PATH", previousPath);
                Environment.SetEnvironmentVariable("NATIVE_DRAIN_SETTINGS", previousSettings);
            }
        }
    }

    // The broker fixture still owns time; this decorator observes timer rearming without advancing a stopped settings loop.
    sealed class RenewalClock(TimeProvider inner) : TimeProvider
    {
        readonly Lock sync = new();
        readonly List<(long Generation, TaskCompletionSource Armed)> waiters = [];
        long generation;
        public long Generation { get { lock (sync) return generation; } }
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override long GetTimestamp() => inner.GetTimestamp();
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromMinutes(1))
                lock (sync)
                {
                    generation++;
                    foreach (var waiter in waiters.Where(waiter => generation > waiter.Generation).ToArray())
                    {
                        waiters.Remove(waiter);
                        waiter.Armed.TrySetResult();
                    }
                }
            return timer;
        }
        public Task MinuteArmedAfter(long earlier)
        {
            lock (sync)
            {
                if (generation > earlier) return Task.CompletedTask;
                var armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.Add((earlier, armed));
                return armed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
        }
    }

    sealed class RenewalLog : TextWriter
    {
        readonly ControllerHarness.LogCapture capture = new();
        readonly RenewalClock clock;
        readonly TaskCompletionSource<long> renewal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int publications;
        public RenewalLog(RenewalClock clock) { this.clock = clock; Console.SetOut(this); }
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public IReadOnlyList<string> Lines => capture.Lines;
        public Task<long> Renewal => renewal.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        public Task<string> WaitFor(string fragment) => capture.WaitFor(fragment, 1);
        public override void WriteLine(string? value)
        {
            if (value is not null && value.Contains(" app-token-refreshed ", StringComparison.Ordinal) && Interlocked.Increment(ref publications) == 2)
                renewal.TrySetResult(clock.Generation);
            capture.WriteLine(value);
        }
        protected override void Dispose(bool disposing) { if (disposing) capture.Dispose(); base.Dispose(disposing); }
    }

    static async Task Exercise(string stage, bool webhooks, bool userCancellation, string failure = "denied")
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
            ["port"] = ((IPEndPoint)ready.LocalEndpoint).Port,
            ["stage"] = stage,
            ["history"] = history,
            ["requests"] = requests,
            ["eof"] = eof,
            ["graceful"] = userCancellation
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
        using var stalled = new BrokerAuthenticationTests.StalledBody();
        var renewalRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            foreach (var pair in environment) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            var statusPort = ControllerHarness.FreePort();
            var args = new List<string>
            {
                "--repo", "owner/repository", "--app-id", "123", "--state", fixture.State,
                "--broker-url", "https://credentials.example.org", "--enrollment-file", fixture.EnrollmentFile,
                "--workspace", fixture.Directory, "--interval", "3600", "--status-port", statusPort.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            if (stage == "teardown") args.Add("--once");
            if (webhooks)
                args.AddRange(["--webhook-url", "https://controller.example.org", "--listen-port",
                    ControllerHarness.FreePort(statusPort).ToString(System.Globalization.CultureInfo.InvariantCulture)]);
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
            var allowed = await fixture.InvokeWrapper();
            Assert.Equal(0, allowed.Exit);
            Assert.Contains("gh-invoked", allowed.Output);
            var state = File.ReadAllText(fixture.State);
            var enrollment = File.ReadAllText(fixture.EnrollmentFile);
            var nativeHistory = File.ReadAllText(history);
            var admitted = File.ReadAllText(requests);
            if (stage is "turn" or "teardown")
            {
                Assert.Contains("turn/start", admitted);
                Assert.NotNull(JsonNode.Parse(state)!["discussions"]!["D1"]!["pending"]);
            }
            Assert.Equal(1, await fixture.Start()); // Existing live controller still owns the actual state lock.
            if (userCancellation) await shutdown.CancelAsync();
            else
            {
                fixture.Transport.Status = failure == "unavailable" ? HttpStatusCode.ServiceUnavailable
                    : failure == "denied" ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;
                fixture.Transport.Defect = failure is "malformed" or "transport" ? failure : null;
                if (failure == "deadline") fixture.Transport.Body = stalled;
                if (stage == "teardown") fixture.Transport.ResponseGate = renewalRelease.Task;
                fixture.Transport.Requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Clock.Advance(TimeSpan.FromMinutes(56));
                await fixture.Transport.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                if (stage == "teardown")
                {
                    // Renewal is in flight. Let the accepted native turn finish and --once dispose.
                    await connection.GetStream().WriteAsync(new byte[] { 1 }, TestContext.Current.CancellationToken);
                    await parent.WaitForExitAsync(TestContext.Current.CancellationToken)
                        .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                    // The old implementation has closed stdin and orphaned the child by this point.
                    renewalRelease.TrySetResult();
                }
                if (failure == "deadline")
                {
                    await stalled.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                    fixture.Clock.Advance(TimeSpan.FromSeconds(60));
                }
            }
            // Real-time deadline is a failure watchdog, never the renewal clock.
            var exit = await run.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
            // A completed normal teardown can cancel renewal before denial arrives; either way no child may survive.
            if (stage != "teardown") Assert.Equal(userCancellation ? 0 : 1, exit);
            AssertStopped(parent);
            AssertStopped(child);
            Assert.False(unrelated.HasExited);
            if (stage == "teardown") Assert.Null(JsonNode.Parse(File.ReadAllText(fixture.State))!["discussions"]!["D1"]!["pending"]);
            else Assert.Equal(state, File.ReadAllText(fixture.State));
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
            renewalRelease.TrySetResult();
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

    // Only the unmanaged JSON-RPC peer is controlled; OS processes, pipes, the wrapper credential handoff and locks are real.
    const string Native = """"
        #!/usr/bin/python3
        import json,os,signal,subprocess,sys,threading
        s=json.load(open(os.environ['NATIVE_DRAIN_SETTINGS']))
        child=None
        acknowledged=threading.Event()
        completed=threading.Event()
        signal.signal(signal.SIGUSR1,lambda *_:acknowledged.set())
        signal.signal(signal.SIGUSR2,lambda *_:completed.set())
        def start_child():
         global child
         code=r"""
        import json,os,socket,subprocess,threading
        s=json.load(open(os.environ['NATIVE_DRAIN_SETTINGS']))
        token=subprocess.run(["gh","auth","token"],capture_output=True,text=True).stdout.strip()
        with socket.create_connection(('127.0.0.1',s['port'])) as ready:
         ready.sendall((json.dumps({'parent':os.getppid(),'child':os.getpid(),'credentialCopied':token.startswith('opaque-token-')})+'\n').encode())
         if s['stage'] in ('teardown','accepted'):ready.recv(1);os.kill(os.getppid(),signal.SIGUSR1)
         if s['stage']=='accepted':ready.recv(1);os.kill(os.getppid(),signal.SIGUSR2)
        threading.Event().wait()
        """
         with open(s['history'],'w') as history:history.write('retain native history\n')
         child=subprocess.Popen([sys.executable,'-c','import signal\n'+code],stdin=subprocess.DEVNULL,stdout=None if s['stage']=='teardown' else subprocess.DEVNULL)
         if s['stage'] in ('teardown','accepted'):acknowledged.wait()
        for line in sys.stdin:
         request=json.loads(line)
         with open(s['requests'],'a') as log:log.write(line)
         method=request['method']
         if method=='initialize' and s['stage']=='initialize':start_child();continue
         if method=='turn/start':
          start_child()
          if s['stage'] not in ('teardown','accepted'):continue
         if 'id' not in request:continue
         result={'codexHome':os.getcwd()} if method=='initialize' else {'data':[{'model':'gpt-6.1-sol','isDefault':True,'defaultReasoningEffort':'low','supportedReasoningEfforts':[{'reasoningEffort':'low'}]}]} if method=='model/list' else {'config':{}} if method=='config/read' else {'turn':{'id':'native-turn'}} if method=='turn/start' else {'thread':{'id':'native-T1','status':{'type':'idle'}}}
         print(json.dumps({'id':request['id'],'result':result}),flush=True)
         if method=='turn/start' and s['stage']=='accepted':
          print(json.dumps({'method':'thread/status/changed','params':{'threadId':'native-T1','status':{'type':'active'}}}),flush=True)
          def finish():
           completed.wait()
           print(json.dumps({'method':'thread/status/changed','params':{'threadId':'native-T1','status':{'type':'idle'}}}),flush=True)
           print(json.dumps({'method':'turn/completed','params':{'threadId':'native-T1','turn':{'id':'native-turn','status':'completed'}}}),flush=True)
          threading.Thread(target=finish,daemon=True).start()
        with open(s['eof'],'w') as marker:marker.write('EOF')
        if child and s['graceful']:child.terminate();child.wait()
        """";

    const string GitHub = """
        #!/usr/bin/python3
        import json,os,sys
        a=sys.argv[1:]
        if a[:2]==['api','graphql']:
         q=json.load(sys.stdin)['query'];page={'nodes':[],'pageInfo':{'hasNextPage':False,'endCursor':None}}
         discussion={'id':'D1','number':1,'url':'https://github.com/owner/repository/discussions/1','body':'@test-app[bot] work','bodyHTML':'work','title':'fixture','author':{'__typename':'User','login':'participant'},'reactionGroups':[{'content':'EYES','viewerHasReacted':True}]}
         if 'query { viewer' in q:data={'viewer':{'login':'test-app[bot]'}}
         elif 'discussions(' in q:data={'repository':{'discussions':dict(page,nodes=[discussion])}}
         else:data={'node':{'comments' if 'comments(' in q else 'labels' if 'labels(' in q else 'reactions':page}}
         print(json.dumps({'data':data}))
        elif len(a)>1 and a[1].startswith('repos/owner/repository/hooks'):
         body=json.load(sys.stdin);print(json.dumps({'id':1,'active':body.get('active',True)}))
        elif a==["capture"]:print("gh-invoked");print("lease-generation-"+os.environ.get("GH_TOKEN","").rsplit("-",1)[-1])
        elif a==["auth","token"]:print(os.environ.get("GH_TOKEN",""))
        else:raise RuntimeError('Unexpected GitHub command')
        """;
}
