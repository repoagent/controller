using static RepoAgent.Auth.IssuancePolicy;
using static RepoAgent.Auth.TableValues;

namespace RepoAgent.Auth;

// Candidate repository identity is never authority until both provider observations match.
internal sealed class EnrollmentGitHub(GitHubIssuance github, TimeProvider clock)
{
    public async Task<long> DiscoverAsync(EnrollmentSnapshot snapshot, string jwt, IssuanceWork work)
    {
        var p = snapshot.Pending;
        using var response = await github.ReadAsync($"repos/{RepositoryPath(Text(p, "RequestedRepository"))}/installation", jwt, work);
        var root = response.RootElement;
        if (root.GetProperty("app_id").GetInt64() != AppId || root.GetProperty("app_slug").GetString() != AppSlug
            || root.GetProperty("suspended_at").ValueKind != System.Text.Json.JsonValueKind.Null) throw new IssuanceDenied();
        foreach (var required in Permissions(Text(p, "Profile")).Concat(Permissions(snapshot.Approval.VerifierProfile)))
            if (!root.GetProperty("permissions").TryGetProperty(required.Key, out var level)
                || level.GetString() is not { } actual || actual != required.Value && !(actual == "write" && required.Value == "read")) throw new IssuanceDenied();
        var id = root.GetProperty("id").GetInt64();
        if (id <= 0) throw new IssuanceDenied();
        if (p.ContainsKey("InstallationId") && Number(p, "InstallationId") != id) throw new ApprovalChanged();
        return id;
    }

    public async Task<(string Repository, string? Environment, long? EnvironmentId)> BindAsync(EnrollmentSnapshot snapshot, string token, IssuanceWork work)
    {
        var p = snapshot.Pending;
        using var repositories = await github.ReadAsync("installation/repositories?per_page=2", token, work);
        var root = repositories.RootElement;
        if (root.GetProperty("total_count").GetInt32() != 1) throw new IssuanceDenied();
        var observed = GitHubIssuance.RepositoryIdentity(root.GetProperty("repositories"));
        if (observed.Id != Id(snapshot.RepositoryId)
            || !string.Equals(observed.Name, Text(p, "RequestedRepository"), StringComparison.OrdinalIgnoreCase))
        {
            if (p.ContainsKey("Marker")) throw new ApprovalChanged();
            throw new IssuanceDenied();
        }
        var canonical = observed.Name;
        if (snapshot.Approval.IsRepositoryVariable) return (canonical, null, null);
        using var environment = await github.ReadAsync($"repos/{RepositoryPath(canonical)}/environments/{Uri.EscapeDataString(Text(p, "Environment"))}", token, work);
        var id = environment.RootElement.GetProperty("id").GetInt64();
        var name = environment.RootElement.GetProperty("name").GetString();
        if (id <= 0 || name != Text(p, "Environment")) throw new IssuanceDenied();
        if (p.ContainsKey("EnvironmentId") && Number(p, "EnvironmentId") != id) throw new ApprovalChanged();
        return (canonical, name, id);
    }

    public async Task<(bool Approved, DateTimeOffset Started)> ObserveAsync(EnrollmentSnapshot snapshot, string jwt, string token, IssuanceWork work)
    {
        var started = clock.GetUtcNow();
        try
        {
            await DiscoverAsync(snapshot, jwt, work);
            await BindAsync(snapshot, token, work);
        }
        catch (ApprovalChanged)
        {
            // Early binding mismatches must satisfy the same oldest-read freshness
            // boundary as marker observations before the service may close a grant.
            work.Fresh(started);
            throw;
        }
        var p = snapshot.Pending;
        if (snapshot.Approval.IsRepositoryVariable)
        {
            var variableApproved = await github.ObserveRepositoryVariableAsync(snapshot.Approval, Text(p, "Repository"),Text(p, "Variable"), Text(p, "Marker"), token, work);
            work.Fresh(started);
            return (variableApproved, started);
        }
        var path = $"repos/{RepositoryPath(Text(p, "Repository"))}/environments/{Uri.EscapeDataString(Text(p, "Environment"))}/variables";
        // Complete listing supplies positive absence evidence; a lone 404 never does.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var approved = false;
        int? total = null;
        for (var page = 1; page <= 5; page++)
        {
            using var response = await github.ReadAsync(path + $"?per_page=100&page={page}", token, work);
            var root = response.RootElement;
            var count = root.GetProperty("total_count").GetInt32();
            if (count < 0 || count > 500 || total is not null && total != count) throw new IssuanceDenied();
            total = count;
            var variables = root.GetProperty("variables");
            if (variables.GetArrayLength() > 100) throw new IssuanceDenied();
            foreach (var variable in variables.EnumerateArray())
            {
                var name = variable.GetProperty("name").GetString() ?? throw new IssuanceDenied();
                if (!seen.Add(name)) throw new IssuanceDenied();
                if (name == Text(p, "Variable")) approved = variable.GetProperty("value").GetString() == Text(p, "Marker");
            }
            if (seen.Count == total && variables.GetArrayLength() < 100 || seen.Count == total && total == page * 100)
            {
                work.Fresh(started);
                return (approved, started);
            }
            if (variables.GetArrayLength() < 100 || seen.Count > total) throw new IssuanceDenied();
        }
        throw new IssuanceDenied();
    }
    private static string RepositoryPath(string repository) => string.Join('/', repository.Split('/').Select(Uri.EscapeDataString));
}
