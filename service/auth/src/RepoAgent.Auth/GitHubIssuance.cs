using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using static RepoAgent.Auth.IssuancePolicy;

namespace RepoAgent.Auth;

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
        var (repositoryId, repository) = RepositoryIdentity(repos.GetProperty("repositories"));
        if (repositoryId != Id(grant.RepositoryId) || !string.Equals(repository, grant.Repository, StringComparison.OrdinalIgnoreCase))
        {
            work.Fresh(started);
            throw new ApprovalChanged();
        }
        if (grant.Approval.IsRepositoryVariable)
        {
            var approved = await ObserveRepositoryVariableAsync(grant.Approval, grant.Repository, grant.Variable, grant.Marker, verifier, work);
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

    // Reads the selected variable by name from the direct repository location chosen only by the
    // durable approval kind: Actions variables for setup v3, Agent variables for setup v2. Work is at
    // most two reads, however many unrelated variables (CI, repository settings) share that location.
    internal async Task<bool> ObserveRepositoryVariableAsync(ApprovalContract approval, string repository, string selected, string marker, string token, IssuanceWork work)
    {
        var location = $"repos/{string.Join('/', repository.Split('/').Select(Uri.EscapeDataString))}/{approval.VariablesPath}";
        using (var variable = await ReadAsync($"{location}/{Uri.EscapeDataString(selected)}", token, work, absentOnNotFound: true))
        {
            if (variable is not null)
            {
                // GitHub answers with the stored name. Another name is malformed evidence, never a change.
                var root = variable.RootElement;
                if (root.GetProperty("name").GetString() != selected || root.GetProperty("value").GetString() is not { } value) throw new IssuanceDenied();
                return value == marker;
            }
        }
        // GitHub also answers 404 when this token cannot read the location (lost repository access or
        // permission), so an isolated 404 never proves withdrawal. A well-formed 200 listing page read
        // afterwards with the same token proves the location was readable, which confirms the 404 as
        // absence. Reading it after the 404 means access lost before or during the named read is caught.
        // The page is only an access proof: its count never matters, and a page showing the selected
        // name contradicts the 404 and proves nothing.
        using var listing = await ReadAsync($"{location}?per_page=1", token, work);
        var page = listing.RootElement;
        var variables = page.GetProperty("variables");
        if (page.GetProperty("total_count").GetInt32() < 0 || variables.GetArrayLength() > 1) throw new IssuanceDenied();
        foreach (var item in variables.EnumerateArray())
            if (item.GetProperty("name").GetString() is not { Length: > 0 } name || name == selected) throw new IssuanceDenied();
        return false;
    }

    public Task<IssuedToken> MintAsync(GrantSnapshot grant, string profile, string jwt, CancellationToken ct) =>
        MintAsync(grant.RepositoryId, grant.Repository, grant.InstallationId, profile, jwt, ct);

    internal async Task<IssuedToken> MintAsync(string repositoryId, string repository, long installationId, string profile, string jwt, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", jwt);
        request.Content = JsonContent.Create(new { repository_ids = new[] { Id(repositoryId) }, permissions = Permissions(profile) });
        using var response = await SendAsync(request, HttpStatusCode.Created, ct) ?? throw new IssuanceDenied();
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

    internal async Task<JsonDocument> ReadAsync(string path, string token, IssuanceWork work) =>
        await ReadAsync(path, token, work, absentOnNotFound: false) ?? throw new IssuanceDenied();
    // Null reports 404 only to a caller that separately proves access before treating it as absence.
    private async Task<JsonDocument?> ReadAsync(string path, string token, IssuanceWork work, bool absentOnNotFound)
    {
        work.Read();
        using var request = Request(HttpMethod.Get, path, token);
        return await SendAsync(request, HttpStatusCode.OK, work.Cancellation, absentOnNotFound);
    }
    private async Task<JsonDocument?> SendAsync(HttpRequestMessage request, HttpStatusCode expected, CancellationToken ct, bool absentOnNotFound = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        // An isolated 404 is never proof of approval withdrawal; it reaches only a caller that proves access separately.
        if (absentOnNotFound && response.StatusCode == HttpStatusCode.NotFound) return null;
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
        var observed = RepositoryIdentity(repositories);
        if (observed.Id != Id(repositoryId) || !string.Equals(observed.Name, repository, StringComparison.OrdinalIgnoreCase))
            throw new IssuanceDenied();
    }
    // Shape validation is separate from comparison: malformed evidence never proves withdrawal.
    internal static (long Id, string Name) RepositoryIdentity(JsonElement repositories)
    {
        if (repositories.ValueKind != JsonValueKind.Array || repositories.GetArrayLength() != 1) throw new IssuanceDenied();
        var id = repositories[0].GetProperty("id").GetInt64();
        var name = repositories[0].GetProperty("full_name").GetString();
        if (id <= 0 || !IsRepositoryName(name)) throw new IssuanceDenied();
        return (id, name!);
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
