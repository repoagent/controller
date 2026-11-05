using System.CommandLine;

namespace RepoAgentController;

internal static class AssetCommand
{
    internal static void Add(RootCommand root)
    {
        var directory = new Option<DirectoryInfo>("--credential-directory") { Required = true };
        var discussion = new Option<int>("--discussion") { Required = true, Description = "Discussion number in this controller's approved repository" };
        var file = new Argument<FileInfo>("file");
        var command = new Command("asset-publish", "Publish a PNG/JPEG image or SVG/Mermaid source on the approved repository's asset branch; prints Markdown")
        { directory, discussion, file };
        command.SetAction(async (result, ct) =>
        {
            try
            {
                var credentials = result.GetValue(directory)!.FullName;
                var input = result.GetValue(file)!;
                if (input.Length is 0 or > DiscussionAsset.MaximumBytes) throw new ArgumentException("Assets must contain 1 byte to 10 MiB");
                // Bound reads even if another process changes the file after stat.
                await using var stream = input.OpenRead();
                var bytes = new byte[DiscussionAsset.MaximumBytes + 1];
                var length = 0;
                while (length < bytes.Length)
                {
                    var count = await stream.ReadAsync(bytes.AsMemory(length), ct);
                    if (count == 0) break;
                    length += count;
                }
                var asset = new DiscussionAsset(input.Extension, bytes[..length]);
                var token = await CredentialHandoff.Request(credentials, ct);
                if (token.Repository is not { } repository || token.Profile?.EndsWith("-images", StringComparison.Ordinal) != true)
                    throw new InvalidOperationException("Enable image publishing with a freshly approved image-publishing profile");
                using var handler = new HttpClientHandler { AllowAutoRedirect = false };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
                var store = new GitHubDiscussionAssets(http, repository, cancel => CredentialHandoff.Request(credentials, cancel),
                    (rejected, cancel) => CredentialHandoff.Reject(credentials, rejected, cancel));
                var url = await new DiscussionAssets(store).Publish(result.GetValue(discussion), asset, ct);
                Console.WriteLine(asset.IsImage ? $"![Generated image]({url})" : $"[Editable diagram source]({url})");
                return 0;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { Console.Error.WriteLine(Log.Redact(error.Message)); return 1; }
        });
        root.Subcommands.Add(command);
    }
}
