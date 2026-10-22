using RepoAgentController;

// Native AOT trimming is a separate compatibility boundary from the ordinary xUnit run.
var mention = new MentionUser("repoagent");
var rendered = """<p><strong><a class="user-mention notranslate" href="https://github.com/repoagent">@repoagent</a></strong></p>""";
if (!mention.IsMentioned(rendered) || mention.IsMentioned("<p><code>@repoagent</code></p>"))
    throw new InvalidOperationException("Native AOT mention enrollment differs from GitHub-rendered mention semantics.");
Console.WriteLine("Native AOT rendered-mention smoke passed.");

var directory = Path.Combine(Path.GetTempPath(), "native-lease-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var state = Path.Combine(directory, "state.json");
    Directory.CreateDirectory(state + ".github-app");
    var cache = Path.Combine(state + ".github-app", "installation-token.json");
    File.WriteAllText(cache, "synthetic owner cache");
    string[] arguments = ["--repo", "storage-probe/example", "--app-id", "storage-probe", "--state", state,
        "--private-key", Path.Combine(directory, "missing-key.pem"), "--check-auth"];
    using (var owner = new FileStream(state + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
    {
        if (await BridgeCommand.Run(arguments) != 1 || !File.Exists(cache))
            throw new InvalidOperationException("Native AOT contender did not preserve the owner's cache.");
    }
    if (await BridgeCommand.Run(arguments) != 1 || File.Exists(cache))
        throw new InvalidOperationException("Native AOT lease was not acquired after disposal.");
    Console.WriteLine("Native AOT exclusive state lease smoke passed.");
}
finally { Directory.Delete(directory, recursive: true); }
