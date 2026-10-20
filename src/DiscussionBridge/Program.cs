using System.CommandLine;
using System.Diagnostics;
using System.Text.Json.Nodes;
using DiscussionBridge;

var repository = new Option<string>("--repo") { Required = true, Description = "GitHub owner/repository" };
var appId = new Option<string>("--app-id") { Required = true, Description = "GitHub App ID or client ID" };
var privateKey = new Option<FileInfo>("--private-key") { Required = true, Description = "GitHub App PEM private-key file" };
var installationId = new Option<long?>("--installation-id") { Description = "Expected installation ID (otherwise discover it for --repo)" };
var mentionUser = new Option<string>("--mention-user") { Description = "Existing dedicated GitHub user to mention (without @); publication still uses the App" };
var workspace = new Option<DirectoryInfo>("--workspace") { DefaultValueFactory = _ => new(Environment.CurrentDirectory) };
var state = new Option<FileInfo>("--state") { DefaultValueFactory = _ => new(".bridge-state.json") };
var enroll = new Option<string[]>("--enroll") { Description = "Discussion URL to enroll (repeatable)", AllowMultipleArgumentsPerToken = true };
var interval = new Option<int>("--interval") { DefaultValueFactory = _ => 10, Description = "Seconds between polls" };
var webhookUrl = new Option<string?>("--webhook-url") { Description = "Public HTTPS origin; enables repository webhooks at /github/webhook" };
var listenPort = new Option<int>("--listen-port") { DefaultValueFactory = _ => 8080, Description = "Container HTTP listener port in webhook mode" };
var reconcileInterval = new Option<int>("--reconcile-interval") { DefaultValueFactory = _ => 300, Description = "Seconds between full reconciliations in webhook mode" };
var once = new Option<bool>("--once") { Description = "Observe one poll, then exit (native work may still be running)" };
var checkAuth = new Option<bool>("--check-auth") { Description = "Verify App installation identity and prepare authenticated gh, then exit without starting Codex" };
var root = new RootCommand("RepoAgent bridge: GitHub Discussions → native Codex sessions, authenticated as a GitHub App") { repository, appId, privateKey, installationId, mentionUser, workspace, state, enroll, interval, webhookUrl, listenPort, reconcileInterval, once, checkAuth };
var tokenFile = new Option<FileInfo>("--token-file") { Required = true };
var ghPath = new Option<FileInfo>("--gh-path") { Required = true };
var forwardedArguments = new Argument<string[]>("arguments") { Arity = ArgumentArity.ZeroOrMore };
var githubCommand = new Command("github-command", "Run gh with the bridge's current App credential (native wrapper entry point)") { tokenFile, ghPath, forwardedArguments };
githubCommand.SetAction(async (result, ct) =>
{
    try { return await AppGitHubCommand.Run(result.GetValue(ghPath)!.FullName, result.GetValue(tokenFile)!.FullName, result.GetValue(forwardedArguments) ?? [], ct); }
    catch (Exception error) { Log.Write("app-command-error", error.Message); return 1; }
});
root.Subcommands.Add(githubCommand);
root.SetAction(async (result, cancellationToken) =>
{
    try
    {
        var repo = result.GetValue(repository)!;
        if (!System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[\w.-]+/[\w.-]+$")) throw new ArgumentException("--repo must be owner/repository");
        if (result.GetValue(interval) < 1) throw new ArgumentException("--interval must be positive");
        if (string.IsNullOrWhiteSpace(result.GetValue(appId))) throw new ArgumentException("--app-id must not be empty");
        if (result.GetValue(installationId) is <= 0) throw new ArgumentException("--installation-id must be positive");
        var urlText = result.GetValue(webhookUrl);
        Uri? publicUrl = null;
        if (result.GetValue(listenPort) is < 1 or > 65535) throw new ArgumentException("--listen-port must be between 1 and 65535");
        if (result.GetValue(reconcileInterval) < 1) throw new ArgumentException("--reconcile-interval must be positive");
        if (urlText is not null && (!Uri.TryCreate(urlText, UriKind.Absolute, out publicUrl) || publicUrl.Scheme != "https" || publicUrl.UserInfo.Length != 0 ||
            publicUrl.Query.Length != 0 || publicUrl.Fragment.Length != 0 || publicUrl.AbsolutePath != "/"))
            throw new ArgumentException("--webhook-url must be a public HTTPS origin without a path, credentials, query or fragment; /github/webhook is added");
        if (publicUrl is not null && result.GetValue(once)) throw new ArgumentException("--once applies to polling mode only");
        var cwd = result.GetValue(workspace)!.FullName;
        if (!Directory.Exists(cwd)) throw new ArgumentException("--workspace must exist");
        var numbers = (result.GetValue(enroll) ?? []).Select(url =>
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host != "github.com" || uri.Scheme != "https")
                throw new ArgumentException("--enroll must be an https://github.com Discussion URL");
            var parts = uri.AbsolutePath.Trim('/').Split('/');
            if (parts.Length != 4 || !string.Equals($"{parts[0]}/{parts[1]}", repo, StringComparison.OrdinalIgnoreCase) || parts[2] != "discussions" || !int.TryParse(parts[3], out var number))
                throw new ArgumentException("Enrollment URL must belong to --repo and end in /discussions/NUMBER");
            return number;
        }).ToHashSet();
        var statePath = result.GetValue(state)!.FullName;
        using var store = new StateStore(statePath, repo);
        var credentialDirectory = statePath + ".github-app";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var credentials = new GitHubAppCredentials(http, result.GetValue(appId)!, result.GetValue(privateKey)!.FullName, repo, credentialDirectory, repositoryWebhooks: publicUrl is not null);
        await credentials.Initialize(result.GetValue(installationId), cancellationToken);
        var executable = AppGitHubCommand.FindExecutable("gh");
        var github = new GitHub(repo, credentials, executable);
        await github.VerifyIdentity(cancellationToken);
        await github.ConfigureMentionUser(result.GetValue(mentionUser), cancellationToken);
        store.VerifyIdentity(credentials.AgentLogin);
        var wrapper = AppGitHubCommand.InstallWrapper(Path.Combine(credentialDirectory, "bin"), executable, credentials.TokenFile);
        if (result.GetValue(checkAuth)) return 0;
        using var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var refresh = credentials.RefreshUntilCancelled(refreshCancellation.Token);
        try
        {
            await using var codex = new Codex(cwd, wrapper, Path.Combine(credentialDirectory, "gh-config"));
            await codex.Initialize(cancellationToken);
            var bridge = new Bridge(github, codex, store, numbers);
            if (publicUrl is not null)
            {
                await new DiscussionWebhooks(github, bridge, statePath,
                    new WebhookConfiguration(publicUrl, result.GetValue(listenPort), TimeSpan.FromSeconds(result.GetValue(reconcileInterval)))).Run(cancellationToken);
                return 0;
            }
            Log.Write("mode", $"polling interval-seconds={result.GetValue(interval)}");
            Log.Write("ready", $"repository={repo} actor={credentials.AgentLogin} mode=polling");
            do
            {
                var pollStarted = Stopwatch.GetTimestamp();
                var succeeded = await bridge.Poll(cancellationToken);
                Log.Write("poll-completed", $"success={succeeded} elapsed-ms={Stopwatch.GetElapsedTime(pollStarted).TotalMilliseconds:F0}");
                if (result.GetValue(once)) return succeeded ? 0 : 1;
                await Task.Delay(TimeSpan.FromSeconds(result.GetValue(interval)), cancellationToken);
            } while (!cancellationToken.IsCancellationRequested);
            return 0;
        }
        finally
        {
            await refreshCancellation.CancelAsync();
            await refresh;
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 0; }
    catch (Exception error) { Log.Write("fatal", error.Message); return 1; }
});
return await root.Parse(args).InvokeAsync();

namespace DiscussionBridge
{
    static class Log
    {
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> secrets = new();
        public static void Protect(string secret) => secrets.TryAdd(secret, 0);
        public static void Write(string kind, string message)
        {
            foreach (var secret in secrets.Keys) message = message.Replace(secret, "<redacted>", StringComparison.Ordinal);
            Console.WriteLine($"{DateTimeOffset.UtcNow:O} {kind} {message}");
        }
    }
    static class Json
    {
        public static string Text(this JsonNode node, string name) => node[name]?.GetValue<string>() ?? "";
        public static JsonNode Copy(this JsonNode node) => node.DeepClone();
        public static JsonArray Array(IEnumerable<JsonNode> nodes) => new(nodes.Select(n => n.Copy()).ToArray());
        public static JsonObject Object(params (string Name, JsonNode? Value)[] values) =>
            new(values.Select(value => new KeyValuePair<string, JsonNode?>(value.Name, value.Value)));
        public static string Encode(JsonNode value) => value.ToJsonString();
    }
}
