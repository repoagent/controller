using System.CommandLine;
using System.Text.Json.Nodes;
using DiscussionBridge;

var repository = new Option<string>("--repo") { Required = true, Description = "GitHub owner/repository" };
var login = new Option<string>("--agent-login") { Required = true, Description = "Expected authenticated GitHub agent account" };
var workspace = new Option<DirectoryInfo>("--workspace") { DefaultValueFactory = _ => new(Environment.CurrentDirectory) };
var state = new Option<FileInfo>("--state") { DefaultValueFactory = _ => new(".bridge-state.json") };
var enroll = new Option<string[]>("--enroll") { Description = "Discussion URL to enroll (repeatable)", AllowMultipleArgumentsPerToken = true };
var interval = new Option<int>("--interval") { DefaultValueFactory = _ => 10, Description = "Seconds between polls" };
var once = new Option<bool>("--once") { Description = "Observe one poll, then exit (native work may still be running)" };
var root = new RootCommand("GitHub Discussions → native Codex sessions") { repository, login, workspace, state, enroll, interval, once };
root.SetAction(async (result, cancellationToken) =>
{
    try
    {
        var repo = result.GetValue(repository)!;
        if (!System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[\w.-]+/[\w.-]+$")) throw new ArgumentException("--repo must be owner/repository");
        if (result.GetValue(interval) < 1) throw new ArgumentException("--interval must be positive");
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
        using var store = new StateStore(result.GetValue(state)!.FullName, repo, result.GetValue(login)!);
        var github = new GitHub(repo, result.GetValue(login)!);
        await github.VerifyIdentity(cancellationToken);
        await using var codex = new Codex(cwd);
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
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 0; }
    catch (Exception error) { Log.Write("fatal", error.Message); return 1; }
});
return await root.Parse(args).InvokeAsync();

namespace DiscussionBridge
{
    static class Log
    {
        public static void Write(string kind, string message) => Console.WriteLine($"{DateTimeOffset.UtcNow:O} {kind} {message}");
    }
    static class Json
    {
        public static string Text(this JsonNode node, string name) => node[name]?.GetValue<string>() ?? "";
        public static JsonNode Copy(this JsonNode node) => node.DeepClone();
        public static JsonArray Array(IEnumerable<JsonNode> nodes) => new(nodes.Select(n => n.Copy()).ToArray());
        public static string Encode(object value) => System.Text.Json.JsonSerializer.Serialize(value);
    }
}
