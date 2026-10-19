using System.CommandLine;
using System.Text.Json.Nodes;
using DiscussionBridge;

var repository = new Option<string>("--repo") { Required = true, Description = "GitHub owner/repository" };
var appId = new Option<string>("--app-id") { Required = true, Description = "GitHub App ID or client ID" };
var privateKey = new Option<FileInfo>("--private-key") { Required = true, Description = "GitHub App PEM private-key file" };
var installationId = new Option<long?>("--installation-id") { Description = "Expected installation ID (otherwise discover it for --repo)" };
var workspace = new Option<DirectoryInfo>("--workspace") { DefaultValueFactory = _ => new(Environment.CurrentDirectory) };
var state = new Option<FileInfo>("--state") { DefaultValueFactory = _ => new(".bridge-state.json") };
var enroll = new Option<string[]>("--enroll") { Description = "Discussion URL to enroll (repeatable)", AllowMultipleArgumentsPerToken = true };
var interval = new Option<int>("--interval") { DefaultValueFactory = _ => 10, Description = "Seconds between polls" };
var once = new Option<bool>("--once") { Description = "Observe one poll, then exit (native work may still be running)" };
var checkAuth = new Option<bool>("--check-auth") { Description = "Verify App installation identity and prepare authenticated gh, then exit without starting Codex" };
var root = new RootCommand("GitHub Discussions → native Codex sessions, authenticated as a GitHub App") { repository, appId, privateKey, installationId, workspace, state, enroll, interval, once, checkAuth };
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
        using var credentials = new GitHubAppCredentials(http, result.GetValue(appId)!, result.GetValue(privateKey)!.FullName, repo, credentialDirectory);
        await credentials.Initialize(result.GetValue(installationId), cancellationToken);
        var executable = AppGitHubCommand.FindExecutable("gh");
        var github = new GitHub(repo, credentials, executable);
        await github.VerifyIdentity(cancellationToken);
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
            do
            {
                var succeeded = await bridge.Poll(cancellationToken);
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
        public static string Encode(object value) => System.Text.Json.JsonSerializer.Serialize(value);
    }
}
