using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

// Console output and PATH are process-wide, so share the serialized environment collection.
[Collection("Managed checkout environment")]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")] // The App gh wrapper is Linux/macOS only.
public sealed class RepositorySettingsTests
{
    const string Setting = "REPOAGENT_MAX_ACTIVE_SESSIONS";

    [Fact]
    public async Task A_repository_setting_is_read_with_the_read_only_App_runtime_credential_and_reported()
    {
        using var fixture = new Fixture();
        fixture.Variables[Setting] = "4";

        Assert.Equal(0, await fixture.Check("--repository-settings"));

        Assert.Contains(fixture.Log, line => line.EndsWith($" repository-setting name={Setting} state=configured value=4"));
        var token = Assert.Single(fixture.Provider.TokenRequests);
        Assert.Equal("read", token["permissions"]!["actions_variables"]!.GetValue<string>());
        var read = Assert.Single(fixture.GitHubRequests(), request => request.Endpoint.EndsWith("/" + Setting, StringComparison.Ordinal));
        Assert.Equal(("GET", "repos/owner/repository/actions/variables/" + Setting, "installation-token-1"),
            (read.Method, read.Endpoint, read.Token));
    }

    [Theory]
    [InlineData(null, null, "state=absent")]
    [InlineData("4", 403, "state=read-failed reason=\"HTTP 403\"")]
    [InlineData("4", 502, "state=read-failed reason=\"HTTP 502\"")]
    [InlineData(null, 401, "state=read-failed reason=\"HTTP 401\"")]
    public async Task Confirmed_absence_is_reported_separately_from_a_failed_read(string? value, int? status, string reported)
    {
        using var fixture = new Fixture();
        if (value is not null) fixture.Variables[Setting] = value;
        if (status is not null) fixture.Statuses[Setting] = status.Value;

        Assert.Equal(0, await fixture.Check("--repository-settings"));

        Assert.Contains(fixture.Log, line => line.EndsWith($" repository-setting name={Setting} {reported}"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65")]
    [InlineData("-1")]
    [InlineData("four")]
    [InlineData("1.5")]
    [InlineData("")]
    public async Task An_invalid_value_is_reported_and_never_consumed(string value)
    {
        using var fixture = new Fixture();
        fixture.Variables[Setting] = value;

        Assert.Equal(0, await fixture.Check("--repository-settings"));

        Assert.Contains(fixture.Log, line => line.EndsWith($" repository-setting name={Setting} state=invalid reason=\"must be an integer from 1 to 64\""));
    }

    [Fact]
    public async Task Without_explicit_opt_in_the_runtime_neither_requests_nor_reads_repository_variables()
    {
        using var fixture = new Fixture();
        fixture.Variables[Setting] = "4";

        Assert.Equal(0, await fixture.Check());

        var token = Assert.Single(fixture.Provider.TokenRequests);
        Assert.Equal(["discussions"], token["permissions"]!.AsObject().Select(permission => permission.Key));
        Assert.DoesNotContain(fixture.GitHubRequests(), request => request.Endpoint.Contains("/actions/variables"));
        Assert.Contains(fixture.Log, line => line.Contains(" repository-settings state=disabled "));
        Assert.DoesNotContain(fixture.Log, line => line.Contains(" repository-setting name="));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("read")]
    public async Task The_runtime_requests_only_Variables_read_even_when_the_installation_can_write(string granted)
    {
        using var fixture = new Fixture();
        fixture.Provider.VariablesPermission = granted;

        Assert.Equal(0, await fixture.Check("--repository-settings"));

        var permissions = Assert.Single(fixture.Provider.TokenRequests)["permissions"]!.AsObject();
        Assert.Equal(new Dictionary<string, string> { ["discussions"] = "write", ["actions_variables"] = "read" },
            permissions.ToDictionary(permission => permission.Key, permission => permission.Value!.GetValue<string>()));
        Assert.All(fixture.GitHubRequests(), request => Assert.Contains(request.Method, new[] { "GET", "POST" }));
        Assert.DoesNotContain(fixture.GitHubRequests(), request => request.Method == "POST" && request.Endpoint != "graphql");
    }

    [Fact]
    public async Task An_installation_without_Variables_read_cannot_enable_settings_or_mint_a_token()
    {
        using var fixture = new Fixture();
        fixture.Provider.VariablesPermission = null;

        Assert.Equal(1, await fixture.Check("--repository-settings"));

        Assert.Empty(fixture.Provider.TokenRequests);
        Assert.Empty(fixture.GitHubRequests());
        Assert.Contains(fixture.Log, line => line.Contains(" fatal ") && line.Contains("Variables: read"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_repository_settings_broker_enrollment_reads_settings_with_its_brokered_lease(bool clone)
    {
        using var fixture = new Fixture();
        using var broker = new BrokerAuthenticationTests.Fixture(clone: clone, settings: true);
        fixture.Variables[Setting] = "4";

        Assert.Equal(0, await fixture.CheckBroker(broker, [.. clone ? ["--clone"] : Array.Empty<string>(), "--repository-settings"]));

        Assert.Contains(fixture.Log, line => line.EndsWith($" repository-setting name={Setting} state=configured value=4"));
        Assert.Equal("/v3/devices/device-1/installation-token", Assert.Single(broker.Transport.Requests).AbsolutePath);
        // Every catalogue read is a direct Actions-variable GET with the brokered lease.
        var reads = fixture.GitHubRequests().Where(request => request.Endpoint != "graphql").ToArray();
        Assert.Contains(reads, read => read.Endpoint == "repos/owner/repository/actions/variables/" + Setting);
        Assert.All(reads, read =>
        {
            Assert.Equal(("GET", "opaque-token-1"), (read.Method, read.Token));
            Assert.StartsWith("repos/owner/repository/actions/variables/REPOAGENT_", read.Endpoint);
        });
        Assert.Empty(fixture.Provider.TokenRequests); // No App key or direct token request exists in broker mode.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Frozen_broker_enrollments_still_refuse_repository_settings_and_are_not_widened(bool clone)
    {
        using var fixture = new Fixture();
        using var broker = new BrokerAuthenticationTests.Fixture(clone: clone);
        fixture.Variables[Setting] = "4";

        Assert.Equal(1, await fixture.CheckBroker(broker, [.. clone ? ["--clone"] : Array.Empty<string>(), "--repository-settings"]));

        Assert.Empty(broker.Transport.Requests);
        Assert.Empty(fixture.GitHubRequests());
        Assert.Contains(fixture.Log, line => line.Contains(" fatal ") && line.Contains("--repository-settings requires a repository-settings enrollment"));
    }

    [Fact]
    public async Task A_running_controller_consumes_operator_changes_on_its_bounded_refresh_without_restart()
    {
        using var fixture = new Fixture();
        fixture.Variables[Setting] = "4";
        await using var controller = await fixture.Run("--repository-settings");
        await fixture.WaitForLog(" poll-completed success=True");
        Assert.Contains(fixture.Log, line => line.EndsWith($"name={Setting} state=configured value=4"));

        fixture.Variables[Setting] = "6";
        await controller.Refresh();
        await fixture.WaitForLog($" repository-setting name={Setting} state=configured value=6");

        fixture.Variables[Setting] = "unlimited";
        await controller.Refresh();
        await fixture.WaitForLog($" repository-setting name={Setting} state=invalid value=6 reason=\"must be an integer from 1 to 64\"");

        fixture.Statuses[Setting] = 503;
        await controller.Refresh();
        await fixture.WaitForLog($" repository-setting name={Setting} state=read-failed value=6 reason=\"HTTP 503\"");

        fixture.Statuses.Clear();
        fixture.Variables.Clear();
        await controller.Refresh();
        await fixture.WaitForLog($" repository-setting name={Setting} state=absent");

        Assert.Equal(0, await controller.Stop());
        var reads = fixture.GitHubRequests().Where(request => request.Endpoint.Contains("/actions/variables/")).ToList();
        Assert.Equal(5, reads.Count(request => request.Endpoint.EndsWith("/" + Setting, StringComparison.Ordinal)));
        Assert.All(reads, request => Assert.Equal("GET", request.Method));
        Assert.Single(fixture.Provider.TokenRequests); // Refresh reuses the unexpired read-only runtime token.
    }

    [Fact]
    public async Task After_a_restart_a_failed_or_invalid_read_keeps_the_last_known_value_and_status_reports_the_problem()
    {
        using var fixture = new Fixture();
        fixture.Variables[Setting] = "6";
        Assert.Equal(0, await fixture.Check("--repository-settings"));
        Assert.Equal(("configured", "6"), Reported(await OperatorStatusTests.Status(fixture.State)));

        // A later lifetime cannot read the variable: the value the earlier one read still applies.
        fixture.Statuses[Setting] = 503;
        Assert.Equal(0, await fixture.Check("--repository-settings"));
        Assert.Contains(fixture.Log, line => line.EndsWith($" repository-setting name={Setting} state=read-failed value=6 reason=\"HTTP 503\""));
        var unreadable = await OperatorStatusTests.Status(fixture.State);
        Assert.Equal(("read-failed", "6"), Reported(unreadable));
        Assert.Equal("HTTP 503", unreadable["settings"]![Setting]!["reason"]!.GetValue<string>());

        fixture.Statuses.Clear();
        fixture.Variables[Setting] = "unlimited";
        Assert.Equal(0, await fixture.Check("--repository-settings"));
        Assert.Contains(fixture.Log, line => line.EndsWith($" repository-setting name={Setting} state=invalid value=6 reason=\"must be an integer from 1 to 64\""));
        var invalid = await OperatorStatusTests.Status(fixture.State);
        Assert.Equal(("invalid", "6"), Reported(invalid));
        Assert.Equal("must be an integer from 1 to 64", invalid["settings"]![Setting]!["reason"]!.GetValue<string>());
        Assert.DoesNotContain("unlimited", invalid.ToJsonString()); // Invalid raw values are never echoed.

        static (string?, string?) Reported(JsonNode status) =>
            (status["settings"]?[Setting]?["state"]?.GetValue<string>(), status["settings"]?[Setting]?["value"]?.GetValue<string>());
    }

    internal sealed record GitHubRequest(string Method, string Endpoint, string? Token);

    sealed class RunningController(Task<int> run, CancellationTokenSource shutdown, Clock clock, Fixture fixture, string? previousPath) : IAsyncDisposable
    {
        // Both one-minute loops (credential renewal and settings refresh) must be armed before time moves.
        public async Task Refresh()
        {
            fixture.Save();
            await clock.WaitForMinuteTimers(2);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        public async Task<int> Stop()
        {
            await shutdown.CancelAsync();
            return await run.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await shutdown.CancelAsync();
            try { await run.WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None); }
            finally
            {
                shutdown.Dispose();
                Environment.SetEnvironmentVariable("PATH", previousPath);
            }
        }
    }

    sealed class Clock : TimeProvider
    {
        readonly FakeTimeProvider time = new(DateTimeOffset.UtcNow);
        readonly SemaphoreSlim minuteTimers = new(0);
        public override DateTimeOffset GetUtcNow() => time.GetUtcNow();
        public override long GetTimestamp() => time.GetTimestamp();
        public override long TimestampFrequency => time.TimestampFrequency;
        public void Advance(TimeSpan duration) => time.Advance(duration);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = time.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromMinutes(1)) minuteTimers.Release();
            return timer;
        }

        public async Task WaitForMinuteTimers(int count)
        {
            for (var i = 0; i < count; i++)
                await minuteTimers.WaitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }
    }

    sealed class Fixture : IDisposable
    {
        readonly RSA key = RSA.Create(2048);
        readonly LogCapture capture = new();
        readonly string model, requests, gh, privateKey;
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "repository-settings-" + Guid.NewGuid().ToString("N"));
        public Dictionary<string, string> Variables { get; } = [];
        public Dictionary<string, int> Statuses { get; } = [];
        public Provider Provider { get; } = new();
        public IReadOnlyList<string> Log => capture.Lines;
        public string State => Path.Combine(Directory, "bridge.json");

        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            privateKey = Path.Combine(Directory, "app.pem");
            File.WriteAllText(privateKey, key.ExportRSAPrivateKeyPem());
            model = Path.Combine(Directory, "github-model.json");
            requests = Path.Combine(Directory, "github-requests.jsonl");
            gh = Path.Combine(Directory, "gh");
            Save();
            File.WriteAllText(gh, GitHub.Replace("MODEL_PATH", model).Replace("LOG_PATH", requests));
            File.SetUnixFileMode(gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Save() => File.WriteAllText(model, JsonSerializer.Serialize(new { variables = Variables, statuses = Statuses }));

        public async Task<int> Check(params string[] options)
        {
            Save();
            using var http = new HttpClient(Provider, disposeHandler: false);
            return await BridgeCommand.Run([.. Arguments(), "--check-auth", .. options], http, gh, shutdownToken: TestContext.Current.CancellationToken);
        }

        public async Task<int> CheckBroker(BrokerAuthenticationTests.Fixture broker, params string[] options)
        {
            Save();
            return await BridgeCommand.Run(["--repo", "owner/repository", "--app-id", "123", "--broker-url", "https://credentials.example.org",
                "--enrollment-file", broker.EnrollmentFile, "--state", Path.Combine(Directory, "bridge.json"), "--workspace", Directory,
                "--check-auth", .. options], broker.Http, gh, shutdownToken: TestContext.Current.CancellationToken);
        }

        public async Task<RunningController> Run(params string[] options)
        {
            Save();
            var tools = Path.Combine(Directory, "native-tools");
            System.IO.Directory.CreateDirectory(tools);
            File.WriteAllText(Path.Combine(tools, "codex"), Native);
            File.SetUnixFileMode(Path.Combine(tools, "codex"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var previousPath = Environment.GetEnvironmentVariable("PATH");
            Environment.SetEnvironmentVariable("PATH", tools + Path.PathSeparator + previousPath);
            var clock = new Clock();
            var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var http = new HttpClient(Provider, disposeHandler: false);
            var run = Task.Run(async () =>
            {
                using (http) return await BridgeCommand.Run([.. Arguments(), "--interval", "3600", .. options], http, gh, clock, shutdown.Token);
            });
            await Task.Yield();
            return new RunningController(run, shutdown, clock, this, previousPath);
        }

        public Task WaitForLog(string text) => capture.WaitFor(line => line.Contains(text, StringComparison.Ordinal));

        string[] Arguments() => ["--repo", "owner/repository", "--app-id", "123", "--private-key", privateKey,
            "--state", Path.Combine(Directory, "bridge.json"), "--workspace", Directory];

        public IReadOnlyList<GitHubRequest> GitHubRequests() => File.Exists(requests)
            ? File.ReadAllLines(requests).Select(line => JsonSerializer.Deserialize<GitHubRequest>(line, JsonSerializerOptions.Web)!).ToList()
            : [];

        public void Dispose()
        {
            capture.Dispose();
            key.Dispose();
            System.IO.Directory.Delete(Directory, true);
        }
    }

    // Only GitHub's REST/GraphQL responses are controlled; the gh process, App token file and controller are real.
    const string GitHub = """
        #!/usr/bin/python3
        import json,os,sys
        a=sys.argv[1:]
        model=json.load(open('MODEL_PATH'))
        method=a[a.index('--method')+1]
        endpoint=a[1]
        with open('LOG_PATH','a') as log:log.write(json.dumps({'method':method,'endpoint':endpoint,'token':os.environ.get('GH_TOKEN')})+'\n')
        if endpoint=='graphql':
         query=json.load(sys.stdin)['query']
         page={'nodes':[],'pageInfo':{'hasNextPage':False,'endCursor':None}}
         data={'viewer':{'login':'test-app[bot]'}} if 'query { viewer' in query else {'repository':{'discussions':page}}
         print(json.dumps({'data':data}));sys.exit(0)
        prefix='repos/owner/repository/actions/variables/'
        if method=='GET' and endpoint.startswith(prefix):
         name=endpoint[len(prefix):]
         status=model['statuses'].get(name,200 if name in model['variables'] else 404)
         body={'name':name,'value':model['variables'][name],'created_at':'2026-10-23T00:00:00Z','updated_at':'2026-10-23T00:00:00Z'} if status==200 else {'message':'Fixture failure','status':str(status)}
         if '--include' in a:sys.stdout.write('HTTP/2.0 %d Fixture\r\nContent-Type: application/json\r\n\r\n'%status)
         sys.stdout.write(json.dumps(body))
         if status>=400:sys.stderr.write('gh: Fixture failure (HTTP %d)\n'%status);sys.exit(1)
         sys.exit(0)
        sys.stderr.write('Unexpected GitHub command\n');sys.exit(2)
        """;

    // Only the native JSON-RPC peer is controlled; it acknowledges startup requests and receives no turns here.
    const string Native = """
        #!/usr/bin/python3
        import json,os,sys
        for line in sys.stdin:
         request=json.loads(line)
         if 'id' in request:print(json.dumps({'id':request['id'],'result':{'codexHome':os.getcwd()} if request.get('method')=='initialize' else {}}),flush=True)
        """;

    // An external transport stub for App authentication; it is not GitHub compatibility evidence.
    internal sealed class Provider : HttpMessageHandler
    {
        public List<JsonNode> TokenRequests { get; } = [];
        public string? VariablesPermission { get; set; } = "read";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            object result;
            switch (request.RequestUri.AbsolutePath)
            {
                case "/app": result = new { id = 123, slug = "test-app" }; break;
                case "/repos/owner/repository/installation":
                    var permissions = new Dictionary<string, string> { ["discussions"] = "write" };
                    if (VariablesPermission is not null) permissions["actions_variables"] = VariablesPermission;
                    result = new { id = 42, app_id = 123, permissions }; break;
                case "/app/installations/42/access_tokens":
                    TokenRequests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!);
                    result = new { token = "installation-token-" + TokenRequests.Count, expires_at = DateTimeOffset.UtcNow.AddHours(1) }; break;
                default: throw new InvalidOperationException("Unexpected authentication request");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json") };
        }
    }

    // Observes the controller's operator log through its actual console writer.
    sealed class LogCapture : TextWriter
    {
        readonly TextWriter previous = Console.Out;
        readonly List<string> lines = [];
        readonly List<(Func<string, bool> Match, TaskCompletionSource Seen)> waiters = [];
        public LogCapture() => Console.SetOut(this);
        public override Encoding Encoding => Encoding.UTF8;
        public IReadOnlyList<string> Lines { get { lock (lines) return lines.ToList(); } }
        public override void WriteLine(string? value)
        {
            lock (lines)
            {
                lines.Add(value ?? "");
                foreach (var waiter in waiters.Where(waiter => waiter.Match(value ?? "")).ToList())
                {
                    waiters.Remove(waiter);
                    waiter.Seen.TrySetResult();
                }
            }
            previous.WriteLine(value);
        }

        // Event-driven: completes when the controller writes a matching line; the timeout is only a watchdog.
        public Task WaitFor(Func<string, bool> match)
        {
            var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (lines)
            {
                if (lines.Any(match)) return Task.CompletedTask;
                waiters.Add((match, seen));
            }
            return seen.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Console.SetOut(previous);
            base.Dispose(disposing);
        }
    }
}
