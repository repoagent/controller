using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RepoAgentController;

sealed class GitHub(string repository, GitHubAppCredentials credentials, string executable)
{
    public string Repository { get; } = repository;
    public string AgentLogin => credentials.AgentLogin;
    public MentionUser? MentionUser { get; private set; }
    public AppIdentity Identity => credentials.Identity with { MentionUser = MentionUser };
    internal const string PageInfo = "pageInfo { hasNextPage endCursor }";

    public async Task<JsonNode> Query(string query, JsonObject variables, CancellationToken ct)
    {
        var response = JsonNode.Parse(await Api("POST", "graphql", Json.Object(("query", query), ("variables", variables)), ct)) ?? throw new IOException("Empty GraphQL response");
        if (response["errors"] is { } errors) throw new IOException($"GraphQL errors: {errors}");
        return response["data"] ?? throw new IOException("No GraphQL data");
    }

    public async Task<JsonNode?> Rest(string method, string resource, JsonNode? input, CancellationToken ct)
    {
        var text = await Api(method, $"repos/{Repository}/{resource}", input, ct);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    internal sealed class CheckoutIdentityException(string message) : IOException(message);

    public async Task<RepositoryCheckout> CheckoutRepository(CancellationToken ct)
    {
        var value = JsonNode.Parse(await Api("GET", $"repos/{Repository}", null, ct)) ?? throw new IOException("Missing repository metadata");
        var id = value["id"]?.GetValue<long>() ?? 0;
        var name = value.Text("full_name");
        var remote = value.Text("clone_url");
        var branch = value.Text("default_branch");
        if (id <= 0 || !Regex.IsMatch(name, @"\A[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}\z") ||
            name.Split('/').Any(part => part is "." or "..") || !string.Equals(name, Repository, StringComparison.OrdinalIgnoreCase) ||
            remote != $"https://github.com/{name}.git" || string.IsNullOrWhiteSpace(branch) || branch.StartsWith('-') ||
            branch.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || branch.Contains("..", StringComparison.Ordinal) ||
            branch.IndexOfAny(['~', '^', ':', '?', '*', '[', '\\']) >= 0 || branch.Contains("@{", StringComparison.Ordinal) ||
            branch.Split('/').Any(part => part.Length == 0 || part.StartsWith('.') || part.EndsWith('.') || part.EndsWith(".lock", StringComparison.Ordinal)))
            throw new CheckoutIdentityException("Repository metadata does not match the approved checkout");
        return new(id, name, remote, branch);
    }

    public async Task<string> CheckoutCommit(string branch, CancellationToken ct)
    {
        var reference = await Rest("GET", "git/ref/heads/" + Uri.EscapeDataString(branch), null, ct);
        var sha = reference?["object"]?.Text("sha") ?? "";
        if (reference?.Text("ref") != "refs/heads/" + branch || reference?["object"]?.Text("type") != "commit" ||
            !Regex.IsMatch(sha, "\\A[0-9a-f]{40}\\z")) throw new IOException("Invalid authoritative default-branch commit");
        return sha;
    }

    /// <summary>One REST GET with gh --include: GitHub's HTTP status and body, whatever the status.</summary>
    public async Task<(int Status, string Body)> Get(string resource, string subject, CancellationToken ct) =>
        Included(await Api("GET", resource, null, ct, include: true), subject);

    static (int Status, string Body) Included(string text, string subject) =>
        Included(text) is { } response ? (response.Status, response.Body) : throw new IOException($"Incomplete GitHub {subject} response");

    /// <summary>gh --include output: the HTTP status line and headers, a blank line, then the body; null for any other output.</summary>
    internal static (int Status, Dictionary<string, string> Headers, string Body)? Included(string text)
    {
        var head = Regex.Match(text, @"\AHTTP/[0-9.]+ (?<status>[0-9]{3})[^\r\n]*\r?\n(?<headers>(?:[^\r\n]+\r?\n)*)\r?\n");
        if (!head.Success) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in head.Groups["headers"].Value.Split('\n'))
            if (line.Split(':', 2) is [var name, var field]) headers[name.Trim()] = field.Trim();
        return (int.Parse(head.Groups["status"].Value, System.Globalization.CultureInfo.InvariantCulture), headers, text[head.Length..]);
    }

    async Task<string> Api(string method, string resource, JsonNode? input, CancellationToken ct, bool include = false)
    {
        var credential = await credentials.GetCredential(ct);
        var arguments = new List<string> { "api", resource, "--hostname", "github.com", "--method", method,
            "--header", "X-GitHub-Api-Version: 2026-03-10" };
        if (include) arguments.Add("--include");
        if (input is not null) arguments.AddRange(["--input", "-"]);
        var start = AppGitHubCommand.Create(executable, credential, credentials.CredentialDirectory, arguments);
        start.RedirectStandardInput = input is not null;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new IOException("Could not start gh");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(Json.Encode(input));
            process.StandardInput.Close();
        }
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var text = await output;
        // With --include, gh prints any HTTP response it received, including rejections, for classification.
        if (process.ExitCode != 0 && !(include && text.StartsWith("HTTP/", StringComparison.Ordinal))) throw new IOException($"gh failed: {await error}");
        return text;
    }

    public async Task VerifyIdentity(CancellationToken ct)
    {
        var viewer = (await Query("query { viewer { login } }", new JsonObject(), ct))["viewer"]!.Text("login");
        if (!string.Equals(viewer, AgentLogin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Authenticated GitHub viewer {viewer} differs from the configured App identity {AgentLogin}");
        Log.Write("identity", viewer);
    }

    public async Task ConfigureMentionUser(string? login, CancellationToken ct)
    {
        if (login is null) return;
        if (!Regex.IsMatch(login, @"\A[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?\z"))
            throw new ArgumentException("--mention-user must be a GitHub username without @");
        var user = (await Query("query($login:String!) { user(login:$login) { login } }", Json.Object(("login", login)), ct))["user"];
        if (user is null) throw new ArgumentException("--mention-user must identify an existing GitHub user, not an organization");
        MentionUser = new MentionUser(user.Text("login"));
        Log.Write("mention-user", $"@{MentionUser.Login} publishes-as={AgentLogin}");
    }

    internal static string? Next(JsonNode connection) => connection["pageInfo"]!["hasNextPage"]!.GetValue<bool>() ? connection["pageInfo"]!.Text("endCursor") : null;

    // Controller-owned requests: classify by HTTP status, rate-limit headers and GraphQL error type.
    internal async Task<JsonNode> Send(string query, JsonObject variables, CancellationToken ct) => Classify(await Post(query, variables, ct));

    // The gh --include output of one GraphQL request, for classification.
    internal async Task<string> Post(string query, JsonObject variables, CancellationToken ct)
    {
        try { return await Api("POST", "graphql", Json.Object(("query", query), ("variables", variables)), ct, include: true); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // No HTTP response was observed, so the request may still have been applied.
            throw new GitHubRequestException(GitHubFailure.Transient, error.Message, uncertain: true);
        }
    }

    /// <summary>
    /// gh --include output as the facts the core decides from, and its JSON body: the status, the rate-limit and retry headers
    /// as numbers and times (null when missing or unusable), each GraphQL error's type and path, GitHub's message, whether data
    /// came back, and what the data holds at <paramref name="lookup"/>. The response is null for output that is no HTTP response.
    /// </summary>
    internal static (GitHubResponse? Response, JsonNode? Body) Read(string response, params string[] lookup)
    {
        if (Included(response) is not { } included) return (null, null);
        var (status, headers, text) = included;
        // A body or error that is no JSON object carries no fields.
        JsonObject? body = null;
        try { body = JsonNode.Parse(text) as JsonObject; } catch (System.Text.Json.JsonException) { }
        var errors = (body?["errors"] as JsonArray ?? []).Select(error => error as JsonObject).ToArray();
        var read = errors.Select(error => new GitHubResponseError(error?["type"].OptionalText() ?? "",
            error?["path"] is JsonArray path ? [.. path.Select(segment => segment.OptionalText())] : [])).ToArray();
        var message = (errors.FirstOrDefault()?["message"] ?? body?["message"]).OptionalText() ?? "";
        var data = body?["data"];
        var field = ResponseField.Missing;
        if (lookup.Length != 0)
        {
            var parent = data;
            foreach (var name in lookup[..^1]) parent = (parent as JsonObject)?[name];
            if (parent is JsonObject resolved && resolved.TryGetPropertyValue(lookup[^1], out var value)) field = value is null ? ResponseField.Null : ResponseField.Value;
        }
        return (new GitHubResponse(status, read, message, data is not null)
        {
            RateLimitRemaining = int.TryParse(headers.GetValueOrDefault("X-RateLimit-Remaining"), NumberStyles.None, CultureInfo.InvariantCulture, out var remaining) ? remaining : null,
            RateLimitReset = long.TryParse(headers.GetValueOrDefault("X-RateLimit-Reset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
                && epoch >= DateTimeOffset.MinValue.ToUnixTimeSeconds() && epoch <= DateTimeOffset.MaxValue.ToUnixTimeSeconds() ? DateTimeOffset.FromUnixTimeSeconds(epoch) : null,
            RetryAfter = int.TryParse(headers.GetValueOrDefault("Retry-After"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
                ? TimeSpan.FromSeconds(seconds) : null,
            Field = field
        }, body);
    }

    /// <summary>Returns GraphQL data from gh --include output, or throws the classified failure.</summary>
    internal static JsonNode Classify(string response)
    {
        var (read, body) = Read(response);
        GitHubResponses.Check(read);
        return body!["data"]!;
    }
}
