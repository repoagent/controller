using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using static RepoAgent.Auth.IssuancePolicy;

namespace RepoAgent.Auth;

public sealed class ApprovalChanged : Exception;

// HttpClient must have redirects and retries disabled. Only fixed api.github.com paths are used.
public sealed class GitHubIssuance(HttpClient client, TimeProvider clock)
{
    public async Task<DateTimeOffset> ObserveAsync(GrantSnapshot grant, string jwt, string verifier, IssuanceWork work)
    {
        var started = clock.GetUtcNow();
        var repositoryPath = string.Join('/', grant.Repository.Split('/').Select(Uri.EscapeDataString));
        using var installation = await ReadAsync($"repos/{repositoryPath}/installation", jwt, work);
        var root = installation.RootElement;
        var installationId = root.GetProperty("id").GetInt64();
        if (installationId <= 0 || root.GetProperty("app_id").GetInt64() != AppId
            || root.GetProperty("app_slug").GetString() != AppSlug
            || root.GetProperty("suspended_at").ValueKind != JsonValueKind.Null) throw new IssuanceDenied();
        var permissions = root.GetProperty("permissions");
        foreach (var required in Permissions(grant.Profile).Concat(Permissions(grant.Approval.VerifierProfile)))
            if (!permissions.TryGetProperty(required.Key, out var level)
                || level.GetString() is not { } actual || actual != required.Value && !(actual == "write" && required.Value == "read"))
                throw new IssuanceDenied();
        if (installationId != grant.InstallationId)
        {
            work.Fresh(started);
            throw new ApprovalChanged();
        }
        using var repositories = await ReadAsync("installation/repositories?per_page=2", verifier, work);
        var repos = repositories.RootElement;
        if (repos.GetProperty("total_count").GetInt32() != 1) throw new IssuanceDenied();
        var accessible = repos.GetProperty("repositories");
        if (accessible.ValueKind != JsonValueKind.Array || accessible.GetArrayLength() != 1) throw new IssuanceDenied();
        var repositoryId = accessible[0].GetProperty("id").GetInt64();
        var repository = accessible[0].GetProperty("full_name").GetString();
        if (repositoryId <= 0 || !IsRepositoryName(repository)) throw new IssuanceDenied();
        if (repositoryId != Id(grant.RepositoryId) || !string.Equals(repository, grant.Repository, StringComparison.OrdinalIgnoreCase))
        {
            work.Fresh(started);
            throw new ApprovalChanged();
        }
        if (grant.Approval == ApprovalContract.Agent)
        {
            var approved = await ObserveAgentVariableAsync(grant.Repository, grant.Variable, grant.Marker, verifier, work);
            // A negative observation is authority to close only while all contributing
            // evidence is fresh. Stale evidence must leave the grant recoverable.
            work.Fresh(started);
            if (!approved) throw new ApprovalChanged();
            return started;
        }
        var environmentPath = $"repos/{repositoryPath}/environments/{Uri.EscapeDataString(grant.Environment)}";
        using var environment = await ReadAsync(environmentPath, verifier, work);
        if (environment.RootElement.GetProperty("id").GetInt64() != grant.EnvironmentId
            || environment.RootElement.GetProperty("name").GetString() != grant.Environment) throw new ApprovalChanged();
        using var marker = await ReadAsync(environmentPath + "/variables/" + Uri.EscapeDataString(grant.Variable), verifier, work);
        if (marker.RootElement.GetProperty("name").GetString() != grant.Variable
            || marker.RootElement.GetProperty("value").GetString() != grant.Marker) throw new ApprovalChanged();
        work.Fresh(started);
        return started;
    }

    internal async Task<bool> ObserveAgentVariableAsync(string repository, string selected, string marker, string token, IssuanceWork work)
    {
        var path = string.Join('/', repository.Split('/').Select(Uri.EscapeDataString));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int? total = null;
        bool? approved = null;
        for (var page = 1; page <= 2; page++)
        {
            using var response = await ReadAsync($"repos/{path}/agents/variables?per_page=30&page={page}", token, work);
            var root = response.RootElement;
            var count = root.GetProperty("total_count").GetInt32();
            if (count < 0 || count > 60 || total is not null && total != count) throw new IssuanceDenied();
            total = count;
            var variables = root.GetProperty("variables");
            if (variables.GetArrayLength() > 30) throw new IssuanceDenied();
            foreach (var variable in variables.EnumerateArray())
            {
                var name = variable.GetProperty("name").GetString();
                var value = variable.GetProperty("value").GetString();
                if (string.IsNullOrEmpty(name) || value is null || !seen.Add(name)) throw new IssuanceDenied();
                if (name == selected) approved = value == marker;
            }
            if (seen.Count == total)
            {
                // Offset pages are not a snapshot: an unchanged marker can shift to an
                // already-read page. Only one complete response can prove absence.
                if (approved is null && page != 1) throw new IssuanceDenied();
                return approved ?? false;
            }
            if (variables.GetArrayLength() < 30 || seen.Count > total) throw new IssuanceDenied();
        }
        throw new IssuanceDenied();
    }

    public Task<IssuedToken> MintAsync(GrantSnapshot grant, string profile, string jwt, CancellationToken ct) =>
        MintAsync(grant.RepositoryId, grant.Repository, grant.InstallationId, profile, jwt, ct);

    internal async Task<IssuedToken> MintAsync(string repositoryId, string repository, long installationId, string profile, string jwt, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", jwt);
        request.Content = JsonContent.Create(new { repository_ids = new[] { Id(repositoryId) }, permissions = Permissions(profile) });
        using var response = await SendAsync(request, HttpStatusCode.Created, ct);
        var root = response.RootElement;
        var token = root.GetProperty("token").GetString();
        if (token is null || !IsBearer(token)) throw new IssuanceDenied();
        // Once token material is known, return it even if scope/expiry fails: the caller durably
        // protects and attempts to revoke it, rather than losing a known excessive capability.
        var valid = false;
        var expiry = DateTimeOffset.MaxValue;
        try
        {
            expiry = root.GetProperty("expires_at").GetDateTimeOffset();
            ValidateRepository(root.GetProperty("repositories"), repositoryId, repository);
            var expected = Permissions(profile);
            var actual = root.GetProperty("permissions");
            if (actual.ValueKind != JsonValueKind.Object) throw new IssuanceDenied();
            var actualMap = actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
            valid = root.GetProperty("repository_selection").GetString() == "selected"
                && expected.All(p => actualMap.TryGetValue(p.Key, out var value) && value == p.Value)
                && actualMap.All(p => expected.TryGetValue(p.Key, out var value) && value == p.Value || p.Key == "metadata" && p.Value == "read")
                && expiry > clock.GetUtcNow().AddMinutes(5) && expiry <= clock.GetUtcNow().AddMinutes(65);
        }
        catch (Exception ex) when (ex is IssuanceDenied or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException) { }
        return new(token, expiry, valid);
    }

    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Delete, "installation/token", token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode != HttpStatusCode.NoContent) throw new IssuanceDenied();
    }

    internal async Task<JsonDocument> ReadAsync(string path, string token, IssuanceWork work)
    {
        work.Read();
        using var request = Request(HttpMethod.Get, path, token);
        return await SendAsync(request, HttpStatusCode.OK, work.Cancellation);
    }
    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, HttpStatusCode expected, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        // In particular, isolated 404 is not proof of approval withdrawal.
        if (response.StatusCode != expected || response.Content.Headers.ContentLength is > 65536) throw new IssuanceDenied();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, timeout.Token);
            if (count == 0) break;
            if (bytes.Length + count > 65536) throw new IssuanceDenied();
            bytes.Write(buffer, 0, count);
        }
        var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        try { RejectDuplicates(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }
    private static HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, new Uri("https://api.github.com/" + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("repoagent-credential-broker/1");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        return request;
    }
    private static bool IsRepositoryName(string? repository) => repository is not null
        && repository.Split('/') is [var owner, var name] && Segment(owner) && Segment(name);
    private static bool Segment(string value) => value.Length is > 0 and <= 100 && value is not ("." or "..")
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    internal static void ValidateRepository(JsonElement repositories, string repositoryId, string repository)
    {
        if (repositories.ValueKind != JsonValueKind.Array || repositories.GetArrayLength() != 1
            || repositories[0].GetProperty("id").GetInt64() != Id(repositoryId)
            || !string.Equals(repositories[0].GetProperty("full_name").GetString(), repository, StringComparison.OrdinalIgnoreCase))
            throw new IssuanceDenied();
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new IssuanceDenied();
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicates(child);
    }
}
