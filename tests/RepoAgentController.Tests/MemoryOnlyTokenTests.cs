using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

// Real controller lifetimes, wrapper processes, files and logs; only the broker, GitHub and agent peers are
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
        var clock = new PollClock(fixture.Clock, TimeSpan.FromSeconds(3600));
        var run = BridgeCommand.Run(Arguments(fixture, "--interval", "3600"), fixture.Http, fixture.Gh, clock, shutdown.Token);
        var bodySucceeded = false;
        try
        {
            // A fresh start: the agent's gh, launched by the agent session through its wrapper, and the
            // controller's own GitHub calls both carry the newly acquired App token.
            await peers.Eventually(calls => calls.Any(call => call.Agent));
            Assert.All(peers.Calls(), call => Assert.Equal(Canary + "1", call.Token));
            Assert.Contains(peers.Calls(), call => !call.Agent);
            AssertNotPersisted(fixture, peers);

            // Renewal while running: existing wrappers and later controller calls observe the new token. Time moves only
            // after the first poll has completed, so the next poll is due an interval after start, not after renewal.
            await Until(() => clock.PollsScheduled == 1);
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
            // Normal shutdown drains accepted work; this peer owns the correlated end of its held turn.
            await peers.Finish();
            bodySucceeded = true;
        }
        finally
        {
            try
            {
                if (!bodySucceeded && !run.IsCompleted) await peers.Finish();
                await shutdown.CancelAsync();
                Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
            }
            catch when (!bodySucceeded)
            {
                // Preserve the original assertion while bounding cleanup to this fixture's retained agent identity.
                try { peers.KillOwned(); }
                catch (Exception error) { Console.Error.WriteLine("Owned agent cleanup failed: " + error.GetType().Name); }
                await shutdown.CancelAsync();
                try { await run.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None); }
                catch (Exception error) when (error is TimeoutException or OperationCanceledException) { }
            }
        }
        var settled = JsonNode.Parse(File.ReadAllText(fixture.State))!["discussions"]!["D1"]!;
        Assert.Null(settled["pending"]);
        Assert.Null(settled["running"]);
        Assert.Contains("turn=agent-turn status=completed", fixture.RetainedLog);


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

    // Every file the controller and its agent peer own: state, logs, credential directory, workspace and history.
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
        "--workspace", fixture.Directory, "--status-port", ControllerHarness.FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture), .. extra
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

    // The fixture clock as the controller sees it. The controller schedules its next poll-interval delay only once a
    // poll has completed, so each registration of that delay observes a completed poll.
    sealed class PollClock(TimeProvider time, TimeSpan interval) : TimeProvider
    {
        int scheduled;
        public int PollsScheduled => Volatile.Read(ref scheduled);
        public override DateTimeOffset GetUtcNow() => time.GetUtcNow();
        public override long GetTimestamp() => time.GetTimestamp();
        public override long TimestampFrequency => time.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = time.CreateTimer(callback, state, dueTime, period);
            if (dueTime == interval) Interlocked.Increment(ref scheduled);
            return timer;
        }
    }

    // Controlled GitHub (gh) and agent peers. Observations go to a test-owned file outside every controller path.
    sealed class Peers : IDisposable
    {
        readonly string observations = Path.Combine(Path.GetTempPath(), "memory-token-observations-" + Guid.NewGuid().ToString("N"));
        readonly Dictionary<string, string?> previous = new();
        readonly string ready = Path.Combine(Path.GetTempPath(), "memory-token-agent-" + Guid.NewGuid().ToString("N"));
        readonly string agent;
        SafeFileHandle? identity;
        bool completionSent;

        public Peers(BrokerAuthenticationTests.Fixture fixture)
        {
            var tools = Path.Combine(fixture.Directory, "agent-tools");
            Directory.CreateDirectory(tools);
            agent = Path.Combine(tools, "codex");
            WriteExecutable(agent, Agent);
            WriteExecutable(fixture.Gh, GitHub);
            Set("PATH", tools + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
            Set("MEMORY_TOKEN_OBSERVATIONS", observations);
            Set("MEMORY_TOKEN_AGENT_READY", ready);
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

        public async Task Finish()
        {
            if (completionSent) return;
            await Until(() => File.Exists(ready)); // Published only after the peer installs its handler.
            RetainIdentity();
            Assert.Equal(0, Signal(identity!, 10, IntPtr.Zero, 0)); // SIGUSR1, through a pinned process identity.
            completionSent = true;
            await Until(() => File.Exists(ready + ".completed"));
        }

        void RetainIdentity()
        {
            if (identity is not null) return;
            var pid = int.Parse(File.ReadAllText(ready), System.Globalization.CultureInfo.InvariantCulture);
            var descriptor = OpenIdentity(pid, 0);
            Assert.True(descriptor >= 0, "The ready agent peer must still exist");
            var retained = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
            try
            {
                // Validate after opening the pidfd: even a PID reuse cannot redirect subsequent signals.
                Assert.Contains(agent, File.ReadAllText($"/proc/{pid}/cmdline").Split('\0'));
                identity = retained;
            }
            finally { if (identity is null) retained.Dispose(); }
        }

        public void KillOwned()
        {
            if (identity is null && File.Exists(ready)) RetainIdentity();
            if (identity is not null) _ = Signal(identity, 9, IntPtr.Zero, 0);
        }

        [DllImport("libc", EntryPoint = "pidfd_open", SetLastError = true)]
        static extern int OpenIdentity(int pid, uint flags);

        [DllImport("libc", EntryPoint = "pidfd_send_signal", SetLastError = true)]
        static extern int Signal(SafeFileHandle identity, int signal, IntPtr information, uint flags);

        public void Dispose()
        {
            identity?.Dispose();
            File.Delete(ready);
            File.Delete(ready + ".completed");
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            File.Delete(observations);
        }

        static void WriteExecutable(string path, string source)
        {
            File.WriteAllText(path, source);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // An agent turn asks its gh for the token, as an agent's own GitHub work would.
        const string Agent = """
            #!/usr/bin/python3
            import json,os,signal,subprocess,sys
            active=False;finished=False
            ready=os.environ['MEMORY_TOKEN_AGENT_READY']
            def complete():
             global active
             if not active:return
             active=False
             print(json.dumps({'method':'thread/status/changed','params':{'threadId':'agent-T1','status':{'type':'idle'}}}),flush=True)
             print(json.dumps({'method':'turn/completed','params':{'threadId':'agent-T1','turn':{'id':'agent-turn','status':'completed'}}}),flush=True)
             open(ready+'.completed','w').close()
            def finish(*_):
             global finished
             finished=True;complete()
            signal.signal(signal.SIGUSR1,finish)
            with open(ready+'.tmp','w') as output:output.write(str(os.getpid()))
            os.replace(ready+'.tmp',ready)
            for line in sys.stdin:
             request=json.loads(line);method=request.get('method')
             if 'id' not in request:continue
             if method=='turn/start':subprocess.run(['gh','auth','token'],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
             result={'codexHome':os.getcwd()} if method=='initialize' else {'data':[{'model':'gpt-6.1-sol','isDefault':True,'defaultReasoningEffort':'low','supportedReasoningEfforts':[{'reasoningEffort':'low'}]}]} if method=='model/list' else {'config':{}} if method=='config/read' else {'turn':{'id':'agent-turn'}} if method=='turn/start' else {'thread':{'id':'agent-T1','status':{'type':'idle'}}}
             print(json.dumps({'id':request['id'],'result':result}),flush=True)
             if method=='turn/start':
              active=True
              if finished:complete()
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
            else:data={'node':{'comments' if 'comments(' in q else 'labels' if 'labels(' in q else 'reactions':page}}
            print(json.dumps({'data':data}))
            """;
    }
}
