using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

// The controller entrypoint, state, repository-settings reader, App gh wrapper and process boundaries are real.
// GitHub responses and the native JSON-RPC peer are controlled; that is not live-provider evidence.
[Collection("Managed checkout environment")]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")] // The App gh wrapper and native relay use Unix tools.
public sealed class ActiveSessionLimitTests
{
    const string Setting = "REPOAGENT_MAX_ACTIVE_SESSIONS";

    [Fact]
    public async Task Concurrent_Discussions_never_exceed_the_configured_limit_and_finished_turns_admit_waiting_ones()
    {
        await using var controller = new Controller(discussions: 4);
        controller.Variables[Setting] = "2";
        await controller.Start("--repository-settings"); // All four Discussions arrive in one observation.

        Assert.Equal(2, controller.Native.TurnCount);
        var held = await controller.Waiting();
        Assert.Equal(2, held.Length);
        Assert.All(held, discussion => Assert.Contains(controller.Log,
            line => line.Contains($" input-waiting {Url(discussion)} ") && line.EndsWith(" active=2 limit=2")));

        // A completed turn releases its slot to one waiting Discussion; the other keeps waiting.
        await controller.Finish(await controller.Turn(1));
        var third = await controller.Turn(3);
        Assert.Contains(third.Discussion, held);
        await controller.Poll();
        Assert.Equal(3, controller.Native.TurnCount);
        Assert.Equal(held.Except([third.Discussion]), await controller.Waiting());

        // A failed turn releases its slot too, even though its session is not idle afterwards.
        await controller.Finish(await controller.Turn(2), failed: true);
        var fourth = await controller.Turn(4);
        Assert.Equal(held.Order(), new[] { third.Discussion, fourth.Discussion }.Order());
        Assert.Equal(2, controller.Native.MostActive);
        Assert.Empty(await controller.Waiting());
    }

    [Fact]
    public async Task A_changed_limit_applies_to_admission_without_interrupting_running_turns_and_idle_sessions_take_no_slot()
    {
        await using var controller = new Controller(discussions: 3);
        controller.Variables[Setting] = "1";
        await controller.Start("--repository-settings");
        var running = await controller.Turn(1);
        Assert.Equal(1, controller.Native.TurnCount);

        // Raising the limit admits waiting Discussions at the next settings refresh, while the first turn still runs.
        await controller.ChangeSetting("3");
        var raised = new[] { await controller.Turn(2), await controller.Turn(3) };
        Assert.Equal(3, controller.Native.MostActive);

        // Lowering it stops no running turn; new input waits until running turns fall below the new limit.
        await controller.ChangeSetting("1");
        await controller.Finish(running);
        controller.Comment(running.Discussion, "C1", "A follow-up while two other turns run.");
        await controller.Poll();
        Assert.Contains(controller.Log, line => line.Contains($" input-waiting {Url(running.Discussion)} ") && line.EndsWith(" active=2 limit=1"));
        await controller.Finish(raised[0]);
        await controller.Poll();
        Assert.Equal(3, controller.Native.TurnCount);
        Assert.Equal(new[] { running.Discussion }, await controller.Waiting());

        // The two idle stored sessions outnumber the limit, yet the follow-up runs once no turn is running.
        await controller.Finish(raised[1]);
        var followUp = await controller.Turn(4);
        Assert.Equal(running.Discussion, followUp.Discussion);
        Assert.Equal("C1", followUp.Envelope["changes"]![0]!["subject"]!["id"]!.GetValue<string>());
        // No turn was interrupted or steered: the native only ever received new turns.
        Assert.DoesNotContain(controller.Native.Methods, method => method.StartsWith("turn/", StringComparison.Ordinal) && method != "turn/start");
    }

    [Theory]
    [InlineData(null, null)] // Disabled, as in broker mode.
    [InlineData("--repository-settings", null)] // Confirmed absent.
    [InlineData("--repository-settings", 503)] // Unreadable at startup.
    public async Task Without_an_effective_setting_four_native_turns_run_and_further_Discussions_wait(string? option, int? status)
    {
        await using var controller = new Controller(discussions: 5);
        if (status is not null) controller.Statuses[Setting] = status.Value;
        await controller.Start(option is null ? [] : [option]);

        Assert.Equal(4, controller.Native.TurnCount);
        var held = Assert.Single(await controller.Waiting());
        Assert.Contains(controller.Log, line => line.Contains($" input-waiting {Url(held)} ") && line.EndsWith(" active=4 limit=4"));

        await controller.Finish(await controller.Turn(1));
        Assert.Equal(held, (await controller.Turn(5)).Discussion);
        Assert.Equal(4, controller.Native.MostActive);
        Assert.Empty(await controller.Waiting());
    }

    static string Url(int discussion) => "https://github.com/owner/repository/discussions/" + discussion;

    /// <summary>One native turn the controlled peer accepted, with the Discussion its envelope carried.</summary>
    sealed record Turn(string Thread, string Id, int Discussion, JsonNode Envelope);

    /// <summary>Runs the real controller entrypoint in polling mode against a controlled GitHub world and native peer.</summary>
    sealed class Controller : IAsyncDisposable
    {
        static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);
        readonly RSA key = RSA.Create(2048);
        readonly string directory = Path.Combine(Path.GetTempPath(), "active-sessions-" + Guid.NewGuid().ToString("N")[..12]);
        readonly string world, gh, privateKey, tools, state;
        readonly string? path = Environment.GetEnvironmentVariable("PATH");
        readonly JsonArray discussions = [];
        readonly JsonObject comments = [];
        readonly Clock clock = new();
        readonly CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        LogCapture? capture;
        Task<int>? run;

        public Dictionary<string, string> Variables { get; } = [];
        public Dictionary<string, int> Statuses { get; } = [];
        public NativePeer Native { get; }
        public IReadOnlyList<string> Log => capture!.Lines;

        public Controller(int discussions)
        {
            Directory.CreateDirectory(directory);
            privateKey = Path.Combine(directory, "app.pem");
            File.WriteAllText(privateKey, key.ExportRSAPrivateKeyPem());
            world = Path.Combine(directory, "github-world.json");
            gh = Path.Combine(directory, "gh");
            tools = Path.Combine(directory, "native-tools");
            state = Path.Combine(directory, "bridge.json");
            Directory.CreateDirectory(tools);
            for (var number = 1; number <= discussions; number++)
                this.discussions.Add(new JsonObject
                {
                    ["id"] = "D" + number, ["number"] = number, ["url"] = Url(number), ["title"] = "Fixture " + number,
                    ["body"] = "@test-app[bot] please look at fixture " + number, ["bodyHTML"] = "<p>fixture</p>",
                    ["createdAt"] = "2026-10-23T00:00:00Z", ["updatedAt"] = "2026-10-23T00:00:00Z", ["closed"] = false, ["locked"] = false,
                    ["answer"] = null, ["category"] = new JsonObject { ["id"] = "DIC1", ["name"] = "General" },
                    ["author"] = new JsonObject { ["__typename"] = "User", ["login"] = "participant" + number }
                });
            var socket = Path.Combine(directory, "native.sock");
            Native = new NativePeer(socket);
            WriteExecutable(gh, GitHub.Replace("WORLD", Quote(world)));
            WriteExecutable(Path.Combine(tools, "codex"), Relay.Replace("SOCKET", Quote(socket)));
        }

        /// <summary>Starts the controller and returns after its first complete observation.</summary>
        public async Task Start(params string[] options)
        {
            Save();
            capture = new LogCapture();
            Environment.SetEnvironmentVariable("PATH", tools + Path.PathSeparator + path);
            var http = new HttpClient(new RepositorySettingsTests.Provider(), disposeHandler: true);
            run = Task.Run(async () =>
            {
                using (http) return await BridgeCommand.Run(["--repo", "owner/repository", "--app-id", "123", "--private-key", privateKey,
                    "--state", state, "--workspace", directory, "--interval", ((int)PollInterval.TotalSeconds).ToString(), .. options],
                    http, gh, clock, shutdown.Token);
            });
            await Logged(" poll-completed success=True");
        }

        /// <summary>Waits for the <paramref name="occurrence"/>th log line containing <paramref name="fragment"/>, failing fast if the controller exits.</summary>
        public async Task<string> Logged(string fragment, int occurrence = 1)
        {
            var seen = capture!.WaitFor(fragment, occurrence);
            if (await Task.WhenAny(seen, run!) != seen)
                Assert.Fail($"Controller exited with {await run!} before '{fragment}':\n{string.Join('\n', Log)}");
            return await seen;
        }

        /// <summary>Runs one more complete observation. Observation and dispatch share one gate, so earlier dispatch has finished.</summary>
        public async Task Poll()
        {
            var completed = capture!.Count(" poll-completed ");
            (await clock.NextPoll()).Fire();
            await Logged(" poll-completed success=True", completed + 1);
        }

        /// <summary>Changes the repository setting and runs the controller's next bounded settings refresh.</summary>
        public async Task ChangeSetting(string value)
        {
            var reported = $" repository-setting name={Setting} state=configured value={value}";
            var earlier = capture!.Count(reported);
            Variables[Setting] = value;
            Save();
            await clock.AdvanceMinute();
            await Logged(reported, earlier + 1);
        }

        /// <summary>Finishes a native turn and waits until the controller has observed its completion.</summary>
        public async Task Finish(Turn turn, bool failed = false)
        {
            Native.Finish(turn, failed);
            await Logged($" native-event method=turn/completed thread={turn.Thread} turn={turn.Id} ");
        }

        /// <summary>The <paramref name="number"/>th native turn, once the controller has checkpointed its delivery.</summary>
        public async Task<Turn> Turn(int number)
        {
            var turn = await Native.Accepted(number);
            var deliveries = Native.Turns.Take(number).Count(earlier => earlier.Discussion == turn.Discussion);
            await Logged($" forwarded {Url(turn.Discussion)} session={turn.Thread} ", deliveries);
            return turn;
        }

        /// <summary>Discussions with input waiting in the controller, from the public status command.</summary>
        public async Task<int[]> Waiting()
        {
            Assert.Equal(0, await BridgeCommand.Run(["status", "--state", state]));
            var status = JsonNode.Parse(Log.Last(line => line.StartsWith('{')))!;
            var waiting = status["sessions"]!.AsArray().Where(session => session!["waiting"] is not null).ToArray();
            Assert.Equal(waiting.Sum(session => session!["waiting"]!.GetValue<int>()), status["work"]!["waiting"]!.GetValue<int>());
            return [.. waiting.Select(session => int.Parse(session!["discussion"]!.GetValue<string>().Split('/')[^1]))];
        }

        public void Comment(int discussion, string id, string body)
        {
            if (comments["D" + discussion] is not JsonArray list) comments["D" + discussion] = list = [];
            list.Add(new JsonObject
            {
                ["id"] = id, ["url"] = Url(discussion) + "#discussioncomment-" + id, ["body"] = body, ["bodyHTML"] = "<p>" + body + "</p>",
                ["createdAt"] = "2026-10-23T00:00:00Z", ["updatedAt"] = "2026-10-23T00:00:00Z",
                ["author"] = new JsonObject { ["__typename"] = "User", ["login"] = "participant" }, ["replyTo"] = null
            });
            Save();
        }

        void Save()
        {
            var temporary = world + ".tmp";
            File.WriteAllText(temporary, new JsonObject
            {
                ["discussions"] = discussions.DeepClone(), ["comments"] = comments.DeepClone(),
                ["variables"] = JsonSerializer.SerializeToNode(Variables), ["statuses"] = JsonSerializer.SerializeToNode(Statuses)
            }.ToJsonString());
            File.Move(temporary, world, true);
        }

        public async ValueTask DisposeAsync()
        {
            await shutdown.CancelAsync();
            try { if (run is not null) await run.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None); }
            finally
            {
                await Native.DisposeAsync();
                capture?.Dispose();
                Environment.SetEnvironmentVariable("PATH", path);
                shutdown.Dispose();
                key.Dispose();
                Directory.Delete(directory, true);
            }
        }

        static string Quote(string value) => JsonValue.Create(value).ToJsonString();

        static void WriteExecutable(string file, string source)
        {
            File.WriteAllText(file, source);
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Answers the controller's gh calls from the controlled world; EYES and other writes succeed.
        const string GitHub = """
            #!/usr/bin/python3
            import json,sys
            a=sys.argv[1:]
            world=json.load(open(WORLD))
            endpoint=a[1]
            def reply(body,status=200):
             if '--include' in a:sys.stdout.write('HTTP/2.0 %d Fixture\r\nContent-Type: application/json\r\n\r\n'%status)
             sys.stdout.write(json.dumps(body));sys.exit(0 if status<400 else 1)
            prefix='repos/owner/repository/actions/variables/'
            if endpoint.startswith(prefix):
             name=endpoint[len(prefix):]
             status=world['statuses'].get(name,200 if name in world['variables'] else 404)
             reply({'name':name,'value':world['variables'][name]} if status==200 else {'message':'Fixture failure'},status)
            request=json.load(sys.stdin);query=request['query'];variables=request.get('variables') or {}
            page=lambda nodes:{'nodes':nodes,'pageInfo':{'hasNextPage':False,'endCursor':None}}
            message=lambda item:dict(item,reactionGroups=[])
            if 'addReaction' in query:reply({'data':{'addReaction':{'reaction':{'id':'R-'+variables['id']}}}})
            elif 'query { viewer' in query:reply({'data':{'viewer':{'login':'test-app[bot]'}}})
            elif 'discussions(' in query:reply({'data':{'repository':{'discussions':page([message(d) for d in world['discussions']])}}})
            elif 'comments(' in query:reply({'data':{'node':{'comments':page([message(c) for c in world['comments'].get(variables['id'],[])])}}})
            elif 'replies(' in query:reply({'data':{'node':{'replies':page([])}}})
            elif 'reactions(' in query:reply({'data':{'node':{'reactions':page([])}}})
            sys.stderr.write('Unexpected GitHub request\n');sys.exit(2)
            """;

        // The controller starts this as codex app-server; it relays JSON-RPC lines to the test's native peer.
        const string Relay = """
            #!/usr/bin/python3
            import socket,sys,threading
            peer=socket.socket(socket.AF_UNIX,socket.SOCK_STREAM);peer.connect(SOCKET)
            def forward():
             for line in sys.stdin.buffer:peer.sendall(line)
             peer.shutdown(socket.SHUT_WR)
            threading.Thread(target=forward,daemon=True).start()
            for line in peer.makefile('rb'):sys.stdout.buffer.write(line);sys.stdout.buffer.flush()
            """;
    }

    /// <summary>
    /// The controlled native app-server: it accepts every turn the controller starts and finishes turns only when the
    /// test says so. It counts running turns from its own side, independently of the controller.
    /// </summary>
    sealed class NativePeer : IAsyncDisposable
    {
        readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        readonly List<Turn> accepted = [];
        readonly List<(int Number, TaskCompletionSource<Turn> Seen)> waiters = [];
        readonly List<string> methods = [];
        readonly Lock sync = new();
        readonly Task serving;
        Socket? connection;
        StreamWriter? writer;
        int threads, active, mostActive;

        public NativePeer(string socket)
        {
            listener.Bind(new UnixDomainSocketEndPoint(socket));
            listener.Listen(1);
            serving = Serve();
        }

        public int TurnCount { get { lock (sync) return accepted.Count; } }
        public Turn[] Turns { get { lock (sync) return [.. accepted]; } }
        public int MostActive { get { lock (sync) return mostActive; } }
        public string[] Methods { get { lock (sync) return [.. methods]; } }

        /// <summary>Completes when the <paramref name="number"/>th turn (1-based, in acceptance order) is accepted.</summary>
        public Task<Turn> Accepted(int number)
        {
            lock (sync)
            {
                if (accepted.Count >= number) return Task.FromResult(accepted[number - 1]);
                var seen = new TaskCompletionSource<Turn>(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.Add((number, seen));
                return seen.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }
        }

        /// <summary>Reports the turn's end in pinned native order: thread status first, then turn/completed.</summary>
        public void Finish(Turn turn, bool failed)
        {
            lock (sync)
            {
                active--;
                Send(new JsonObject { ["method"] = "thread/status/changed", ["params"] = new JsonObject { ["threadId"] = turn.Thread, ["status"] = new JsonObject { ["type"] = failed ? "systemError" : "idle" } } });
                Send(new JsonObject { ["method"] = "turn/completed", ["params"] = new JsonObject { ["threadId"] = turn.Thread, ["turn"] = new JsonObject { ["id"] = turn.Id, ["status"] = failed ? "failed" : "completed" } } });
            }
        }

        async Task Serve()
        {
            try
            {
                connection = await listener.AcceptAsync();
                using var stream = new NetworkStream(connection, ownsSocket: false);
                using var reader = new StreamReader(stream);
                lock (sync) writer = new StreamWriter(stream) { AutoFlush = true };
                while (await reader.ReadLineAsync() is { } line)
                {
                    var message = JsonNode.Parse(line)!;
                    if (message["id"] is not { } id) continue;
                    var method = message["method"]!.GetValue<string>();
                    var parameters = message["params"];
                    lock (sync)
                    {
                        methods.Add(method);
                        JsonNode result = method switch
                        {
                            "initialize" => new JsonObject { ["codexHome"] = Environment.CurrentDirectory },
                            "thread/start" => new JsonObject { ["thread"] = new JsonObject { ["id"] = "S" + ++threads, ["status"] = new JsonObject { ["type"] = "idle" } } },
                            "thread/resume" => new JsonObject { ["thread"] = new JsonObject { ["id"] = parameters!["threadId"]!.DeepClone(), ["status"] = new JsonObject { ["type"] = "idle" } } },
                            "turn/start" => new JsonObject { ["turn"] = new JsonObject { ["id"] = "T" + (accepted.Count + 1), ["status"] = "inProgress" } },
                            _ => new JsonObject()
                        };
                        Send(new JsonObject { ["id"] = id.DeepClone(), ["result"] = result });
                        if (method != "turn/start") continue;
                        mostActive = Math.Max(mostActive, ++active);
                        var thread = parameters!["threadId"]!.GetValue<string>();
                        var text = parameters["input"]![0]!["text"]!.GetValue<string>();
                        var envelope = JsonNode.Parse(text[(text.IndexOf('\n') + 1)..])!;
                        // Pinned native reports the thread active after acknowledging its turn.
                        Send(new JsonObject { ["method"] = "thread/status/changed", ["params"] = new JsonObject { ["threadId"] = thread, ["status"] = new JsonObject { ["type"] = "active" } } });
                        accepted.Add(new Turn(thread, "T" + (accepted.Count + 1), envelope["discussion"]!["number"]!.GetValue<int>(), envelope));
                        foreach (var waiter in waiters.Where(waiter => waiter.Number == accepted.Count).ToList())
                        {
                            waiters.Remove(waiter);
                            waiter.Seen.TrySetResult(accepted[^1]);
                        }
                    }
                }
            }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        }

        // Caller holds sync.
        void Send(JsonNode message) => writer!.WriteLine(message.ToJsonString());

        public async ValueTask DisposeAsync()
        {
            listener.Dispose();
            connection?.Dispose();
            await serving;
        }
    }

    /// <summary>Controlled time: one-minute refresh loops advance on a fake clock; the poll interval fires only on request.</summary>
    sealed class Clock : TimeProvider
    {
        readonly FakeTimeProvider time = new(DateTimeOffset.UtcNow);
        readonly SemaphoreSlim minuteTimers = new(0);
        readonly Channel<ManualTimer> polls = Channel.CreateUnbounded<ManualTimer>();
        public override DateTimeOffset GetUtcNow() => time.GetUtcNow();
        public override long GetTimestamp() => time.GetTimestamp();
        public override long TimestampFrequency => time.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == TimeSpan.FromHours(1))
            {
                var poll = new ManualTimer(callback, state);
                polls.Writer.TryWrite(poll);
                return poll;
            }
            var timer = time.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromMinutes(1)) minuteTimers.Release();
            return timer;
        }

        public async Task<ManualTimer> NextPoll() => await polls.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        /// <summary>Waits until both one-minute loops (credential renewal and settings refresh) are armed, then runs them once.</summary>
        public async Task AdvanceMinute()
        {
            for (var i = 0; i < 2; i++)
                await minuteTimers.WaitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromMinutes(1));
        }
    }

    sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => default;
    }

    /// <summary>Observes the controller's operator log through its actual console writer; waits are event-driven.</summary>
    sealed class LogCapture : TextWriter
    {
        readonly TextWriter previous = Console.Out;
        readonly List<string> lines = [];
        readonly List<(string Fragment, int Occurrence, TaskCompletionSource<string> Seen)> waiters = [];
        public LogCapture() => Console.SetOut(this);
        public override Encoding Encoding => Encoding.UTF8;
        public IReadOnlyList<string> Lines { get { lock (lines) return [.. lines]; } }
        public int Count(string fragment) { lock (lines) return lines.Count(line => line.Contains(fragment, StringComparison.Ordinal)); }

        public override void WriteLine(string? value)
        {
            lock (lines)
            {
                lines.Add(value ?? "");
                foreach (var waiter in waiters.Where(waiter => (value ?? "").Contains(waiter.Fragment, StringComparison.Ordinal) &&
                    lines.Count(line => line.Contains(waiter.Fragment, StringComparison.Ordinal)) >= waiter.Occurrence).ToList())
                {
                    waiters.Remove(waiter);
                    waiter.Seen.TrySetResult(value ?? "");
                }
            }
            previous.WriteLine(value);
        }

        /// <summary>Completes on the <paramref name="occurrence"/>th line containing the fragment; the timeout is only a watchdog.</summary>
        public Task<string> WaitFor(string fragment, int occurrence)
        {
            var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (lines)
            {
                var matches = lines.Where(line => line.Contains(fragment, StringComparison.Ordinal)).ToList();
                if (matches.Count >= occurrence) return Task.FromResult(matches[occurrence - 1]);
                waiters.Add((fragment, occurrence, seen));
            }
            return seen.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Console.SetOut(previous);
            base.Dispose(disposing);
        }
    }
}
