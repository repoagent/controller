using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        Assert.Equal("read", token["permissions"]!["agent_variables"]!.GetValue<string>());
        var read = Assert.Single(fixture.GitHubRequests(), request => request.Endpoint.EndsWith("/" + Setting, StringComparison.Ordinal));
        Assert.Equal(("GET", "repos/owner/repository/agents/variables/" + Setting, "installation-token-1"),
            (read.Method, read.Endpoint, read.Token));
    }

    [Fact]
    public async Task Without_explicit_opt_in_the_runtime_neither_requests_nor_reads_repository_variables()
    {
        using var fixture = new Fixture();
        fixture.Variables[Setting] = "4";

        Assert.Equal(0, await fixture.Check());

        var token = Assert.Single(fixture.Provider.TokenRequests);
        Assert.Equal(["discussions"], token["permissions"]!.AsObject().Select(permission => permission.Key));
        Assert.DoesNotContain(fixture.GitHubRequests(), request => request.Endpoint.Contains("/variables"));
        Assert.Contains(fixture.Log, line => line.Contains(" repository-settings state=disabled "));
        Assert.DoesNotContain(fixture.Log, line => line.Contains(" repository-setting name="));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("read")]
    public async Task The_runtime_requests_only_Agent_variables_read_even_when_the_installation_can_write(string granted)
    {
        using var fixture = new Fixture();
        fixture.Provider.VariablesPermission = granted;

        Assert.Equal(0, await fixture.Check("--repository-settings"));

        var permissions = Assert.Single(fixture.Provider.TokenRequests)["permissions"]!.AsObject();
        Assert.Equal(new Dictionary<string, string> { ["discussions"] = "write", ["agent_variables"] = "read" },
            permissions.ToDictionary(permission => permission.Key, permission => permission.Value!.GetValue<string>()));
        Assert.All(fixture.GitHubRequests(), request => Assert.Contains(request.Method, new[] { "GET", "POST" }));
        Assert.DoesNotContain(fixture.GitHubRequests(), request => request.Method == "POST" && request.Endpoint != "graphql");
    }

    [Fact]
    public async Task An_installation_without_Agent_variables_read_still_starts_and_keeps_the_last_settings()
    {
        using var fixture = new Fixture();
        fixture.Variables[Setting] = "4";
        Assert.Equal(0, await fixture.Check("--repository-settings"));

        // Actions-variables (Variables: read) access is not Agent-variables access.
        fixture.Provider.VariablesPermission = null;
        fixture.Provider.ActionsVariablesPermission = "read";
        fixture.Variables[Setting] = "8";
        Assert.Equal(0, await fixture.Check("--repository-settings"));

        // The token is not widened past the installation, and nothing reads variables it cannot read.
        Assert.Equal(["discussions"], fixture.Provider.TokenRequests[1]["permissions"]!.AsObject().Select(permission => permission.Key));
        Assert.Single(fixture.GitHubRequests(), request => request.Endpoint.Contains("/variables/" + Setting));
        Assert.Contains(fixture.Log, line => line.EndsWith(
            $" repository-setting name={Setting} state=read-failed value=4 reason=\"the App installation does not grant Agent variables: read\""));
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
        // Every catalogue read is a direct Agent-variable GET with the brokered lease.
        var reads = fixture.GitHubRequests().Where(request => request.Endpoint != "graphql").ToArray();
        Assert.Contains(reads, read => read.Endpoint == "repos/owner/repository/agents/variables/" + Setting);
        Assert.All(reads, read =>
        {
            Assert.Equal(("GET", "opaque-token-1"), (read.Method, read.Token));
            Assert.StartsWith("repos/owner/repository/agents/variables/REPOAGENT_", read.Endpoint);
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
        Assert.Contains(fixture.Log, line => line.Contains(" fatal ") && line.Contains("--repository-settings requires a controller enrollment with a repository-settings profile"));
    }

    internal sealed record GitHubRequest(string Method, string Endpoint, string? Token);

    sealed class Fixture : IDisposable
    {
        readonly RSA key = RSA.Create(2048);
        readonly LogCapture capture = new();
        readonly string model, requests, gh, privateKey;
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "repository-settings-" + Guid.NewGuid().ToString("N"));
        public Dictionary<string, string> Variables { get; } = [];
        public Provider Provider { get; } = new();
        public IReadOnlyList<string> Log => capture.Lines;

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

        public void Save() => File.WriteAllText(model, JsonSerializer.Serialize(new { variables = Variables }));

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

        string[] Arguments() => ["--repo", "owner/repository", "--app-id", "123", "--private-key", privateKey,
            "--state", Path.Combine(Directory, "bridge.json"), "--workspace", Directory, "--status-port", ControllerHarness.FreePort().ToString()];

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
        prefix='repos/owner/repository/agents/variables/'
        if method=='GET' and endpoint.startswith(prefix):
         name=endpoint[len(prefix):]
         status=200 if name in model['variables'] else 404
         body={'name':name,'value':model['variables'][name],'created_at':'2026-10-23T00:00:00Z','updated_at':'2026-10-23T00:00:00Z'} if status==200 else {'message':'Fixture failure','status':str(status)}
         if '--include' in a:sys.stdout.write('HTTP/2.0 %d Fixture\r\nContent-Type: application/json\r\n\r\n'%status)
         sys.stdout.write(json.dumps(body))
         if status>=400:sys.stderr.write('gh: Fixture failure (HTTP %d)\n'%status);sys.exit(1)
         sys.exit(0)
        sys.stderr.write('Unexpected GitHub command\n');sys.exit(2)
        """;

    // An external transport stub for App authentication; it is not GitHub compatibility evidence.
    internal sealed class Provider : HttpMessageHandler
    {
        public List<JsonNode> TokenRequests { get; } = [];
        /// <summary>The installation's Agent variables permission, which repository settings need.</summary>
        public string? VariablesPermission { get; set; } = "read";
        /// <summary>The installation's Actions variables (Variables) permission, which repository settings never use.</summary>
        public string? ActionsVariablesPermission { get; set; }
        /// <summary>The installation's Webhooks permission; webhook mode requires write.</summary>
        public string? HooksPermission { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            object result;
            switch (request.RequestUri.AbsolutePath)
            {
                case "/app": result = new { id = 123, slug = "test-app" }; break;
                case "/repos/owner/repository/installation":
                    var permissions = new Dictionary<string, string> { ["discussions"] = "write" };
                    if (VariablesPermission is not null) permissions["agent_variables"] = VariablesPermission;
                    if (ActionsVariablesPermission is not null) permissions["actions_variables"] = ActionsVariablesPermission;
                    if (HooksPermission is not null) permissions["repository_hooks"] = HooksPermission;
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
        public LogCapture() => Console.SetOut(this);
        public override Encoding Encoding => Encoding.UTF8;
        public IReadOnlyList<string> Lines { get { lock (lines) return lines.ToList(); } }
        public override void WriteLine(string? value)
        {
            lock (lines) lines.Add(value ?? "");
            previous.WriteLine(value);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Console.SetOut(previous);
            base.Dispose(disposing);
        }
    }
}
