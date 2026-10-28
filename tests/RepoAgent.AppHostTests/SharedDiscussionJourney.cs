using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Testing;
using Xunit;

namespace RepoAgent.AppHostTests;

/// <summary>The opt-in closed-box journey; all providers and the same AppHost used for debugging are real.</summary>
public sealed class SharedDiscussionJourney
{
    public static bool Enabled => Environment.GetEnvironmentVariable("REPOAGENT_APPHOST_LIVE") == "1";

    [Fact(Skip = "Set REPOAGENT_APPHOST_LIVE=1 only after configuring a disposable local-broker enrollment.", SkipUnless = nameof(Enabled), Timeout = 900_000)]
    public async Task A_mention_reaches_the_shared_controller_and_returns_an_App_reply_and_receipt()
    {
        Assert.False(OperatingSystem.IsWindows(), "The enrolled controller runs natively on Linux or macOS; use the same trusted host for the broker and controller.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(12));
        var ct = deadline.Token;
        using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.RepoAgent_Auth_AppHost>(
            ["--environment=Development", "DcpPublisher:RandomizePorts=false"], ct);
        var repository = builder.Configuration["Controller:Repository"] ?? "";
        Assert.StartsWith("repoagent/e2e-", repository, StringComparison.Ordinal);
        var enrollment = builder.Configuration["Parameters:controller-enrollment"];
        Assert.False(string.IsNullOrWhiteSpace(enrollment), "The private local-broker enrollment parameter is required.");
        using var binding = JsonDocument.Parse(enrollment);
        var actor = binding.RootElement.GetProperty("appSlug").GetString() + "[bot]";
        var canonical = binding.RootElement.TryGetProperty("controllerSecret", out var controllerSecret);
        var legacy = binding.RootElement.TryGetProperty("deviceSecret", out var legacyControllerSecret);
        Assert.NotEqual(canonical, legacy); // Exactly one naming generation; mixed aliases are ambiguous.
        var secret = (canonical ? controllerSecret : legacyControllerSecret).GetString();
        Assert.False(string.IsNullOrWhiteSpace(secret), "The private controller secret is required.");
        Assert.Equal(repository, binding.RootElement.GetProperty("repository").GetString());
        var coordinates = repository.Split('/');
        var lookup = await Api("query($owner:String!,$name:String!){viewer{login} repository(owner:$owner,name:$name){id discussionCategories(first:20){nodes{id isAnswerable}}}}",
            new { owner = coordinates[0], name = coordinates[1] }, ct);
        Assert.Equal("repoagent", lookup["viewer"]!["login"]!.GetValue<string>());
        var repo = lookup["repository"] ?? throw new InvalidOperationException("The disposable repository is not accessible.");
        var category = repo["discussionCategories"]!["nodes"]!.AsArray().FirstOrDefault(item => item?["isAnswerable"]?.GetValue<bool>() == true)
            ?? throw new InvalidOperationException("The disposable repository requires an answerable Discussion category.");
        await using var app = await builder.BuildAsync(ct);
        await app.StartAsync(ct);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("auth", ct);
        // A normal HttpClient deliberately retains certificate verification; no permissive test TLS handler.
        using var broker = new HttpClient { BaseAddress = app.GetEndpoint("auth", "https") };
        using var ready = await broker.GetAsync("/health/ready", ct);
        ready.EnsureSuccessStatusCode();
        await app.ResourceNotifications.WaitForResourceHealthyAsync("controller", ct);
        using var controller = new HttpClient { BaseAddress = app.GetEndpoint("controller", "status") };
        var marker = "APPHOST_REPLY_" + Guid.NewGuid().ToString("N");
        string? discussion = null;
        try
        {
            var created = await Api("mutation($repository:ID!,$category:ID!,$title:String!,$body:String!){createDiscussion(input:{repositoryId:$repository,categoryId:$category,title:$title,body:$body}){discussion{id}}}",
                new
                {
                    repository = repo["id"]!.GetValue<string>(),
                    category = category["id"]!.GetValue<string>(),
                    title = "Shared AppHost acceptance " + marker,
                    body = $"@{actor} Authorized disposable acceptance fixture. Reply once with exactly {marker}, using the authenticated Discussion reply skill. Perform only this fixture's request."
                }, ct);
            discussion = created["createDiscussion"]!["discussion"]!["id"]!.GetValue<string>();
            var completed = false;
            while (!completed)
            {
                ct.ThrowIfCancellationRequested();
                var observed = await Api("query($id:ID!){node(id:$id){... on Discussion{comments(first:20){nodes{body author{login}}} reactions(first:100){nodes{content user{login}}}}}}", new { id = discussion }, ct);
                var current = observed["node"] ?? throw new InvalidOperationException("The fixture Discussion disappeared before acceptance.");
                var replied = current["comments"]!["nodes"]!.AsArray().Any(comment =>
                    comment?["author"]?["login"]?.GetValue<string>() == actor && comment["body"]!.GetValue<string>().Contains(marker, StringComparison.Ordinal));
                var receipted = current["reactions"]!["nodes"]!.AsArray().Any(reaction =>
                    reaction?["content"]?.GetValue<string>() == "EYES" && reaction["user"]?["login"]?.GetValue<string>() == actor);
                var status = await controller.GetStringAsync("/status", ct);
                Assert.False(status.Contains(secret, StringComparison.Ordinal), "The controller status exposed its controller secret.");
                using var snapshot = JsonDocument.Parse(status);
                completed = replied && receipted && snapshot.RootElement.GetProperty("state").GetString() == "idle";
                if (!completed) await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        finally
        {
            if (discussion is not null)
            {
                // Cleanup is independent of an expired journey deadline, but bounded and cancellation-aware.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await Api("mutation($id:ID!){deleteDiscussion(input:{id:$id}){clientMutationId}}", new { id = discussion }, cleanup.Token);
            }
        }
    }

    static async Task<JsonNode> Api(string query, object variables, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new("gh") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }
        };
        foreach (var argument in new[] { "api", "graphql", "--input", "-" }) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new IOException("Could not start the participant GitHub CLI.");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var error = process.StandardError.ReadToEndAsync(ct);
            var request = JsonSerializer.Serialize(new { query, variables });
            await process.StandardInput.WriteAsync(request.AsMemory(), ct);
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0) throw new IOException("The participant GitHub operation failed: " + await error);
            var response = JsonNode.Parse(await output) ?? throw new IOException("GitHub returned no JSON.");
            if (response["errors"] is not null) throw new IOException("GitHub refused the fixture operation.");
            return response["data"] ?? throw new IOException("GitHub returned no data.");
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
    }
}
