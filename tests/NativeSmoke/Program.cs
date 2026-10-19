using DiscussionBridge;

// Native AOT trimming is a separate compatibility boundary from the ordinary xUnit run.
var mention = new MentionUser("repoagent");
var rendered = """<p><strong><a class="user-mention notranslate" href="https://github.com/repoagent">@repoagent</a></strong></p>""";
if (!mention.IsMentioned(rendered) || mention.IsMentioned("<p><code>@repoagent</code></p>"))
    throw new InvalidOperationException("Native AOT mention enrollment differs from GitHub-rendered mention semantics.");
Console.WriteLine("Native AOT rendered-mention smoke passed.");
