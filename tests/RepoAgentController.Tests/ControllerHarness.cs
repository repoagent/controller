using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Win32.SafeHandles;
using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

/// <summary>
/// Runs the real controller entrypoint against a controlled GitHub world and native peer. The controller entrypoint, state,
/// repository-settings reader, App gh wrapper and process boundaries are real; GitHub responses and the native JSON-RPC
/// peer are controlled, so this is not live-provider evidence. Tests that use it belong to the "Managed checkout
/// environment" collection, since the controller reads PATH and the console.
/// </summary>
static class ControllerHarness
{
    public const string Setting = "REPOAGENT_MAX_ACTIVE_SESSIONS";
    public static string Url(int discussion) => "https://github.com/owner/repository/discussions/" + discussion;

    /// <summary>
    /// A loopback port that was free just now and is none of <paramref name="taken"/>, for a controller listener in a test.
    /// A probe that lands on a taken port stays open until this returns, so the next probe can't be given it again.
    /// </summary>
    public static int FreePort(params int[] taken)
    {
        var probes = new List<TcpListener>();
        try
        {
            while (true)
            {
                var probe = new TcpListener(System.Net.IPAddress.Loopback, 0);
                probes.Add(probe);
                probe.Start();
                var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
                if (!taken.Contains(port)) return port;
            }
        }
        finally { foreach (var probe in probes) probe.Dispose(); }
    }

    /// <summary>
    /// Reads a controlled gh's JSON file and, with <paramref name="write"/>, rewrites it in place, while holding the exclusive
    /// flock that gh takes on it, so neither sees the other's partial file or loses the other's change. A replacement would
    /// leave gh locking the old copy.
    /// </summary>
    public static T LockedJson<T>(string file, Func<JsonObject, T> use, bool write = false)
    {
        // On Unix, .NET emulates FileShare with a non-blocking flock, so File.ReadAllText or any FileStream it opens fails at
        // once ("being used by another process") while gh holds its lock, and a second open would conflict with this lock.
        // So the file is opened outside .NET, close-on-exec (a child the controller starts meanwhile must not inherit the
        // lock), and the lock is waited for as gh waits for it.
        using var handle = new SafeFileHandle(Open(file, OpenReadWrite | OpenCloseOnExec), ownsHandle: true);
        if (handle.IsInvalid) throw new IOException($"Could not open {file}: errno {Marshal.GetLastPInvokeError()}");
        while (Flock(handle, LockExclusive) != 0)
            if (Marshal.GetLastPInvokeError() is var error && error != Interrupted)
                throw new IOException($"Could not lock {file}: errno {error}");
        using var stream = new FileStream(handle, FileAccess.ReadWrite);
        var state = JsonNode.Parse(stream)!.AsObject();
        var result = use(state);
        if (write)
        {
            stream.SetLength(0);
            stream.Position = 0;
            stream.Write(Encoding.UTF8.GetBytes(state.ToJsonString()));
        }
        return result;
    }

    const int OpenReadWrite = 2, OpenCloseOnExec = 0x80000, LockExclusive = 2, Interrupted = 4; // Linux values.

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2101", Justification = "Linux open consumes UTF-8 paths, explicitly marshalled as LPUTF8Str; UTF-16 would call the native API incorrectly.")]
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    static extern int Flock(SafeFileHandle descriptor, int operation);

    /// <summary>A <see cref="Delivery.Signature"/> that sends no signature header.</summary>
    public const string Unsigned = "\0";
    static readonly HttpClient Client = new();

    /// <summary>A repository webhook delivery as GitHub sends it; unless a signature is given, it is signed with the registered secret.</summary>
    internal sealed record Delivery(string Body)
    {
        public string? Event { get; init; } = "discussion";
        public string? Id { get; init; } = Guid.NewGuid().ToString();
        public string? Signature { get; init; }
        public string Method { get; init; } = "POST";
        public string Path { get; init; } = "/github/webhook";
    }

    /// <summary>Sends a delivery to the controller's webhook receiver and returns its status code.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    internal static async Task<HttpStatusCode> Send(Controller controller, Delivery delivery)
    {
        using var request = new HttpRequestMessage(new HttpMethod(delivery.Method), $"http://127.0.0.1:{controller.WebhookPort}{delivery.Path}")
        {
            Content = new StringContent(delivery.Body, Encoding.UTF8, "application/json")
        };
        if (delivery.Event is not null) request.Headers.Add("X-GitHub-Event", delivery.Event);
        if (delivery.Id is not null) request.Headers.Add("X-GitHub-Delivery", delivery.Id);
        var signature = delivery.Signature ?? Signature(controller, delivery.Body);
        if (signature != Unsigned) request.Headers.TryAddWithoutValidation("X-Hub-Signature-256", signature);
        using var response = await Client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    /// <summary>The signature GitHub sends for <paramref name="body"/>, with the secret the controller registered.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    internal static string Signature(Controller controller, string body)
    {
        var secret = controller.Hooks[0]["input"]!["config"]!.Text("secret");
        return "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));
    }

    /// <summary>A Discussion event payload for the controlled repository, unless another is named.</summary>
    public static string Event(int? number, string action = "created", string repository = "owner/repository",
        (string Login, string Type)? sender = null) => new JsonObject
        {
            ["action"] = action,
            ["repository"] = new JsonObject { ["full_name"] = repository, ["id"] = 1 },
            ["discussion"] = number is null ? new JsonObject() : new JsonObject { ["number"] = number, ["user"] = new JsonObject { ["login"] = "participant", ["type"] = "User" }, ["body"] = "Hello" },
            ["sender"] = new JsonObject { ["login"] = sender?.Login ?? "participant", ["type"] = sender?.Type ?? "User" }
        }.ToJsonString();

    /// <summary>One native turn the controlled peer accepted, with the Discussion its envelope carried.</summary>
    internal sealed record Turn(string Thread, string Id, int Discussion, JsonNode Envelope);

    /// <summary>Runs the real controller entrypoint in polling or webhook mode against a controlled GitHub world and native peer.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")] // The App gh wrapper and native relay use Unix tools.
    internal sealed class Controller : IAsyncDisposable
    {
        static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);
        readonly RSA key = RSA.Create(2048);
        readonly string directory = Path.Combine(Path.GetTempPath(), "controller-" + Guid.NewGuid().ToString("N")[..12]);
        readonly string world, gh, privateKey, tools, state;
        readonly string? path = Environment.GetEnvironmentVariable("PATH");
        readonly JsonArray discussions = [];
        readonly JsonObject comments = [];
        readonly JsonObject labels = [];
        readonly Clock clock = new();
        readonly CancellationTokenSource shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        LogCapture? capture;
        Task<int>? run;
        Process? child;

        /// <summary>The port of the webhook receiver, once started in webhook mode.</summary>
        public int WebhookPort { get; private set; }
        /// <summary>The local port of the controller's status endpoint.</summary>
        public int StatusPort { get; } = FreePort();
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
            for (var number = 1; number <= discussions; number++) this.discussions.Add(Discussion(number));
            var socket = Path.Combine(directory, "native.sock");
            Native = new NativePeer(socket);
            WriteExecutable(gh, GitHub.Replace("WORLD", Quote(world)));
            WriteExecutable(Path.Combine(tools, "codex"), Relay.Replace("SOCKET", Quote(socket)));
        }

        /// <summary>Starts the controller in polling mode and returns after its first complete observation.</summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2000", Justification = "Run transfers the handler to its HttpClient, disposed by the controller task.")]
        public async Task Start(params string[] options)
        {
            Run(new RepositorySettingsTests.Provider(), options);
            await Logged(" poll-completed success=True");
        }

        /// <summary>
        /// Starts the controller in webhook mode on a free port and returns after its first complete reconciliation.
        /// Full reconciliations are an hour apart, so only deliveries observe Discussions after that.
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2000", Justification = "Run transfers the handler to its HttpClient, disposed by the controller task.")]
        public async Task StartWebhooks(params string[] options)
        {
            WebhookPort = FreePort(StatusPort);
            Run(new RepositorySettingsTests.Provider { HooksPermission = "write" },
                ["--webhook-url", "https://fixture.invalid", "--listen-port", WebhookPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "--reconcile-interval", "3600", .. options]);
            await Logged(" reconciliation-completed discussion=all success=True");
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2000", Justification = "The controller task owns and disposes this client after BridgeCommand returns.")]
        void Run(RepositorySettingsTests.Provider provider, string[] options)
        {
            Save();
            capture = new LogCapture();
            Environment.SetEnvironmentVariable("PATH", tools + Path.PathSeparator + path);
            var http = new HttpClient(provider, disposeHandler: true);
            run = Task.Run(async () =>
            {
                using (http) return await BridgeCommand.Run(["--repo", "owner/repository", "--app-id", "123", "--private-key", privateKey,
                    "--state", state, "--workspace", directory, "--interval", ((int)PollInterval.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "--status-port", StatusPort.ToString(System.Globalization.CultureInfo.InvariantCulture), .. options],
                    http, gh, clock, shutdown.Token);
            });
        }

        /// <summary>Starts a real child CLI invocation using this fixture's existing provider and native peer.</summary>
        public async Task StartChild()
        {
            Save();
            capture = new LogCapture();
            var start = new ProcessStartInfo(AppGitHubCommand.FindExecutable("dotnet"))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
#if CONTROLLER_SHUTDOWN_CHILD
            var childAssembly = typeof(ControllerShutdownTests).Assembly.Location;
#else
            var fixture = typeof(ControllerShutdownTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "ControllerShutdownFixture").Value!;
            var childAssembly = Path.Combine(AppContext.BaseDirectory, "shutdown-child", fixture, "RepoAgentController.Tests.dll");
#endif
            Assert.True(File.Exists(childAssembly), "The normal test build must prepare its complete shutdown child fixture.");
            foreach (var argument in new[] { childAssembly, "--controller-shutdown-child", gh,
                "--repo", "owner/repository", "--app-id", "123", "--private-key", privateKey,
                "--state", state, "--workspace", directory, "--interval", "3600", "--status-port",
                StatusPort.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
            start.Environment["PATH"] = tools + Path.PathSeparator + path;
            child = Process.Start(start) ?? throw new IOException("Could not start child controller");
            run = ChildResult();
            await Logged(" poll-completed success=True");
        }

        async Task<int> ChildResult()
        {
            var output = Capture(child!.StandardOutput);
            var errors = Capture(child.StandardError);
            await child.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, errors);
            return child.ExitCode;
        }

        async Task Capture(StreamReader reader)
        {
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line) capture!.WriteLine(line);
        }

        /// <summary>Signals only the fixture-owned controller PID, never the test runner or its process group.</summary>
        public void TerminateChild() => Assert.Equal(0, Kill(child!.Id, 15));

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        static extern int Kill(int process, int signal);

        public bool ChildExited => child!.HasExited;
        public Task<int> ChildExit => run!;
        public JsonNode DiscussionState(int discussion) => JsonNode.Parse(File.ReadAllText(state))!["discussions"]!["D" + discussion]!.DeepClone();

        /// <summary>Exercises the generated gh wrapper and real credential socket without exposing its bearer.</summary>
        public async Task<int> InvokeWrapper()
        {
            var start = new ProcessStartInfo(Path.Combine(state + ".github-app", "bin", "gh"))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("api");
            start.ArgumentList.Add("graphql");
            start.ArgumentList.Add("--input");
            start.ArgumentList.Add("-");
            start.RedirectStandardInput = true;
            using var wrapper = Process.Start(start) ?? throw new IOException("Could not start App wrapper");
            var output = wrapper.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var errors = wrapper.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await wrapper.StandardInput.WriteLineAsync("{\"query\":\"query { viewer { login } }\"}".AsMemory(), TestContext.Current.CancellationToken);
            wrapper.StandardInput.Close();
            try { await wrapper.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken); }
            catch { if (!wrapper.HasExited) wrapper.Kill(true); throw; }
            await Task.WhenAll(output, errors);
            return wrapper.ExitCode;
        }

        /// <summary>The controller's answer to GET /status.</summary>
        public async Task<JsonNode> Status()
        {
            using var client = new HttpClient();
            using var response = await client.GetAsync($"http://127.0.0.1:{StatusPort}/status", TestContext.Current.CancellationToken);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        }

        /// <summary>Stops the controller as an operator's shutdown does and returns its exit code.</summary>
        public async Task<int> Stop()
        {
            await shutdown.CancelAsync();
            return await run!.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
        }

        /// <summary>The repository webhook requests the controller sent through gh, in order: method, resource and input.</summary>
        public JsonObject[] Hooks => File.Exists(world + ".hooks")
            ? [.. File.ReadAllLines(world + ".hooks").Select(line => JsonNode.Parse(line)!.AsObject())] : [];

        /// <summary>Adds a Discussion that mentions the App; the controller observes it at its next observation or delivery.</summary>
        public void Open(int number)
        {
            discussions.Add(Discussion(number));
            Save();
        }

        /// <summary>Deletes a Discussion, as GitHub does; a delivery for it then finds it gone.</summary>
        public void Delete(int number)
        {
            discussions.Remove(discussions.Single(discussion => discussion!["number"]!.GetValue<int>() == number));
            Save();
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
            await Logged($" agent-event method=turn/completed thread={turn.Thread} turn={turn.Id} ");
        }

        /// <summary>The <paramref name="number"/>th native turn, once the controller has checkpointed its delivery.</summary>
        public async Task<Turn> Turn(int number)
        {
            var turn = await Native.Accepted(number);
            var deliveries = Native.Turns.Take(number).Count(earlier => earlier.Discussion == turn.Discussion);
            await Logged($" forwarded {Url(turn.Discussion)} session={turn.Thread} ", deliveries);
            return turn;
        }

        /// <summary>The operator status that the public status command prints for this controller's state.</summary>
        public async Task<JsonNode> OperatorStatus()
        {
            Assert.Equal(0, await BridgeCommand.Run(["status", "--state", state]));
            return JsonNode.Parse(Log.Last(line => line.StartsWith('{')))!;
        }

        /// <summary>Discussions with input waiting in the controller, from the public status command.</summary>
        public async Task<int[]> Waiting()
        {
            var status = await OperatorStatus();
            var waiting = status["sessions"]!.AsArray().Where(session => session!["waiting"] is not null).ToArray();
            Assert.Equal(waiting.Sum(session => session!["waiting"]!.GetValue<int>()), status["work"]!["waiting"]!.GetValue<int>());
            return [.. waiting.Select(session => int.Parse(session!["discussion"]!.GetValue<string>().Split('/')[^1], System.Globalization.CultureInfo.InvariantCulture))];
        }

        /// <summary>Replaces a Discussion's labels; the controller observes them at its next observation.</summary>
        public void Labels(int discussion, params string[] names)
        {
            labels["D" + discussion] = new JsonArray([.. names.Select(name => (JsonNode)JsonValue.Create(name))]);
            Save();
        }

        /// <summary>Advances controlled time without running the one-minute loops early, for delivery backoff.</summary>
        public void Elapse(TimeSpan by) => clock.Elapse(by);

        /// <summary>Completes when the controller next arms a timer of <paramref name="due"/>, such as a webhook body deadline.</summary>
        public Task Armed(TimeSpan due) => clock.Armed(due);

        public void Comment(int discussion, string id, string body)
        {
            if (comments["D" + discussion] is not JsonArray list) comments["D" + discussion] = list = [];
            list.Add(new JsonObject
            {
                ["id"] = id,
                ["url"] = Url(discussion) + "#discussioncomment-" + id,
                ["body"] = body,
                ["bodyHTML"] = "<p>" + body + "</p>",
                ["createdAt"] = "2026-10-23T00:00:00Z",
                ["updatedAt"] = "2026-10-23T00:00:00Z",
                ["author"] = new JsonObject { ["__typename"] = "User", ["login"] = "participant" },
                ["replyTo"] = null
            });
            Save();
        }

        static JsonObject Discussion(int number) => new()
        {
            ["id"] = "D" + number,
            ["number"] = number,
            ["url"] = Url(number),
            ["title"] = "Fixture " + number,
            ["body"] = "@test-app[bot] please look at fixture " + number,
            ["bodyHTML"] = "<p>fixture</p>",
            ["createdAt"] = "2026-10-23T00:00:00Z",
            ["updatedAt"] = "2026-10-23T00:00:00Z",
            ["closed"] = false,
            ["locked"] = false,
            ["answer"] = null,
            ["category"] = new JsonObject { ["id"] = "DIC1", ["name"] = "General" },
            ["author"] = new JsonObject { ["__typename"] = "User", ["login"] = "participant" + number }
        };

        void Save()
        {
            var temporary = world + ".tmp";
            File.WriteAllText(temporary, new JsonObject
            {
                ["discussions"] = discussions.DeepClone(),
                ["comments"] = comments.DeepClone(),
                ["labels"] = labels.DeepClone(),
                ["variables"] = JsonSerializer.SerializeToNode(Variables),
                ["statuses"] = JsonSerializer.SerializeToNode(Statuses)
            }.ToJsonString());
            File.Move(temporary, world, true);
        }

        public async ValueTask DisposeAsync()
        {
            await shutdown.CancelAsync();
            // A failing signal test may leave the drain waiting intentionally; force only its owned child during cleanup.
            if (child is not null && !child.HasExited) child.Kill(true);
            try { if (run is not null) await run.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None); }
            finally
            {
                await Native.DisposeAsync();
                child?.Dispose();
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

        // Answers the controller's gh calls from the controlled world; EYES and other writes succeed. Repository webhook
        // requests are recorded beside the world and answered for hook 7.
        const string GitHub = """
            #!/usr/bin/python3
            import json,sys
            a=sys.argv[1:]
            world=json.load(open(WORLD))
            endpoint=a[1]
            def reply(body,status=200):
             if '--include' in a:sys.stdout.write('HTTP/2.0 %d Fixture\r\nContent-Type: application/json\r\n\r\n'%status)
             sys.stdout.write(json.dumps(body));sys.exit(0 if status<400 else 1)
            prefix='repos/owner/repository/agents/variables/'
            if endpoint.startswith('repos/owner/repository/hooks'):
             body=json.load(sys.stdin)
             with open(WORLD+'.hooks','a') as hooks:hooks.write(json.dumps({'method':a[a.index('--method')+1],'resource':endpoint,'input':body})+'\n')
             reply({'id':7,'active':body.get('active',True)})
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
            elif 'discussion(number:' in query:
             found=[message(d) for d in world['discussions'] if d['number']==variables['number']]
             # GitHub's recorded response for a deleted Discussion in a resolved repository.
             reply({'data':{'repository':{'discussion':found[0]}}} if found else {'data':{'repository':{'discussion':None}},'errors':[{'type':'NOT_FOUND','path':['repository','discussion'],
              'locations':[{'line':1,'column':60}],'message':'Could not resolve to a Discussion with the number of %d.'%variables['number']}]})
            elif 'comments(' in query:reply({'data':{'node':{'comments':page([message(c) for c in world['comments'].get(variables['id'],[])])}}})
            elif 'replies(' in query:reply({'data':{'node':{'replies':page([])}}})
            elif 'reactions(' in query:reply({'data':{'node':{'reactions':page([])}}})
            elif 'labels(' in query:reply({'data':{'node':{'labels':page([{'name':name} for name in world['labels'].get(variables['id'],[])])}}})
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
    internal sealed class NativePeer : IAsyncDisposable
    {
        readonly Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        readonly List<Turn> accepted = [];
        readonly List<(int Number, TaskCompletionSource<Turn> Seen)> waiters = [];
        readonly List<string> methods = [];
        readonly HashSet<int> rejections = [];
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

        /// <summary>Refuses the next turn/start for the Discussion with a JSON-RPC error, as a native refusing input does.</summary>
        public void Reject(int discussion) { lock (sync) rejections.Add(discussion); }

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
                        var envelope = method == "turn/start" ? Envelope(parameters!) : null;
                        if (envelope is not null && rejections.Remove(envelope["discussion"]!["number"]!.GetValue<int>()))
                        {
                            Send(new JsonObject { ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = -32600, ["message"] = "Fixture refused this input" } });
                            continue;
                        }
                        JsonNode result = method switch
                        {
                            "initialize" => new JsonObject { ["codexHome"] = Environment.CurrentDirectory },
                            "thread/start" => new JsonObject { ["thread"] = new JsonObject { ["id"] = "S" + ++threads, ["status"] = new JsonObject { ["type"] = "idle" } } },
                            "thread/resume" => new JsonObject { ["thread"] = new JsonObject { ["id"] = parameters!["threadId"]!.DeepClone(), ["status"] = new JsonObject { ["type"] = "idle" } } },
                            "turn/start" => new JsonObject { ["turn"] = new JsonObject { ["id"] = "T" + (accepted.Count + 1), ["status"] = "inProgress" } },
                            // One selectable model with native defaults, as the pinned bundled catalogue reports it.
                            "model/list" => new JsonObject { ["data"] = new JsonArray(new JsonObject { ["model"] = "gpt-6.1-sol", ["isDefault"] = true, ["defaultReasoningEffort"] = "low", ["supportedReasoningEfforts"] = new JsonArray(new JsonObject { ["reasoningEffort"] = "low" }) }) },
                            "config/read" => new JsonObject { ["config"] = new JsonObject() },
                            _ => new JsonObject()
                        };
                        Send(new JsonObject { ["id"] = id.DeepClone(), ["result"] = result });
                        if (method != "turn/start") continue;
                        mostActive = Math.Max(mostActive, ++active);
                        var thread = parameters!["threadId"]!.GetValue<string>();
                        // Pinned native reports the thread active after acknowledging its turn.
                        Send(new JsonObject { ["method"] = "thread/status/changed", ["params"] = new JsonObject { ["threadId"] = thread, ["status"] = new JsonObject { ["type"] = "active" } } });
                        accepted.Add(new Turn(thread, "T" + (accepted.Count + 1), envelope!["discussion"]!["number"]!.GetValue<int>(), envelope));
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

        // The bridge envelope after the skill line of a turn/start input.
        static JsonNode Envelope(JsonNode parameters)
        {
            var text = parameters["input"]![0]!["text"]!.GetValue<string>();
            return JsonNode.Parse(text[(text.IndexOf('\n') + 1)..])!;
        }

        // Caller holds sync.
        void Send(JsonNode message) => writer!.WriteLine(message.ToJsonString());

        public async ValueTask DisposeAsync()
        {
            listener.Dispose();
            connection?.Dispose();
            await serving;
            writer?.Dispose();
        }
    }

    /// <summary>Controlled time: one-minute refresh loops advance on a fake clock; the poll interval fires only on request.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The test clock has managed-only signaling semaphores; timer callbacks and cancelled waits can outlive the observed operation. No wait handle is created.")]
    internal sealed class Clock : TimeProvider
    {
        readonly FakeTimeProvider time = new(DateTimeOffset.UtcNow);
        readonly SemaphoreSlim minuteTimers = new(0);
        readonly Channel<ManualTimer> polls = Channel.CreateUnbounded<ManualTimer>();
        readonly List<(TimeSpan Due, TaskCompletionSource Armed)> arming = [];
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
            lock (arming)
                foreach (var waiter in arming.Where(waiter => waiter.Due == dueTime).ToList())
                {
                    arming.Remove(waiter);
                    waiter.Armed.TrySetResult();
                }
            return timer;
        }

        public void Elapse(TimeSpan by) => time.Advance(by);

        public Task Armed(TimeSpan due)
        {
            var armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (arming) arming.Add((due, armed));
            return armed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
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

    internal sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        public void Fire() => callback(state);
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => default;
    }

    /// <summary>Observes the controller's operator log through its actual console writer; waits are event-driven.</summary>
    internal sealed class LogCapture : TextWriter
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2213", Justification = "Borrowed Console.Out is restored on disposal; its owner, the runner, must keep using it.")]
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
