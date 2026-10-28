using RepoAgentController;

// Native AOT trimming is a separate compatibility boundary from the ordinary xUnit run. Which users GitHub's rendered HTML
// mentions is only checked here: shapes captured from GitHub's POST /markdown in GFM repository context.
var mention = new MentionUser("repoagent");
string[] mentions =
[
    """<p><a class="user-mention notranslate" href="https://github.com/repoagent">@repoagent</a></p>""",
    """<strong><a class="user-mention notranslate" href="https://github.com/repoagent">@repoagent</a></strong>""",
    """<p><strong><a class="user-mention notranslate" href="https://github.com/repoagent">@repoagent</a></strong></p>"""
];
string[] others =
[
    "<p><code>@repoagent</code></p>",
    "<pre><code>@repoagent</code></pre>",
    """<a href="mailto:support+@repoagent.example">support+@repoagent.example</a>""",
    """<a href="https://github.com/repoagent">profile</a>""",
    """<a class="user-mention" href="https://github.com/egil">@egil</a>""",
    """<a class="user-mention" href="https://github.com/repoagent-other">@repoagent-other</a>""",
    """<a class="user-mention" href="https://github.com.evil/repoagent">@repoagent</a>""",
    "<p>@repoagent</p>"
];
if (mentions.FirstOrDefault(html => !mention.IsMentioned(RenderedMentions.Users(html))) is { } missed)
    throw new InvalidOperationException("Native AOT mention enrollment missed GitHub's rendered mention " + missed);
if (others.FirstOrDefault(html => mention.IsMentioned(RenderedMentions.Users(html))) is { } other)
    throw new InvalidOperationException("Native AOT mention enrollment treated other rendered content as a mention: " + other);
Console.WriteLine("Native AOT rendered-mention smoke passed.");

var directory = Path.Combine(Path.GetTempPath(), "agent-lease-" + Guid.NewGuid().ToString("N"));
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
    // The lease holder's failed lifetime is reported through the trimmed status and retained-log commands.
    var status = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(state + ".status.json"))!;
    if (status["controller"]?["state"]?.GetValue<string>() != "failed" || !File.ReadAllText(Path.Combine(state + ".logs", "controller.log")).Contains(" fatal ") ||
        await BridgeCommand.Run(["status", "--state", state]) != 0 || await BridgeCommand.Run(["logs", "--state", state]) != 0)
        throw new InvalidOperationException("Native AOT status or retained logs did not report the failed lifetime.");
    Console.WriteLine("Native AOT status and retained-log smoke passed.");
    // The trimmed ASP.NET Core HTTP server serves GET /status through generated JSON metadata (ADR 0009).
    using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    probe.Start();
    var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    using var idle = StatusFiles.Open(Path.Combine(directory, "idle.json"), "storage-probe/example", "polling", TimeProvider.System);
    await using (await ControllerHttpServer.Start(port, () => StatusSnapshot.From(idle.Snapshot()), null, CancellationToken.None))
    {
        using var client = new HttpClient();
        var body = await client.GetStringAsync($"http://127.0.0.1:{port}/status");
        if (body != """{"state":"idle","work":{"sessions":0,"pending":0,"retrying":0,"waiting":0,"running":0,"limit":4,"admissionBlocked":null}}""")
            throw new InvalidOperationException("Native AOT status endpoint returned " + body);
    }
    Console.WriteLine("Native AOT status endpoint smoke passed.");
}
finally { Directory.Delete(directory, recursive: true); }
