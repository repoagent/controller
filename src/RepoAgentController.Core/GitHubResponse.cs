namespace RepoAgentController;

/// <summary>
/// What GitHub answered one controller-owned request, as the adapter read it: the HTTP status, the GraphQL errors, GitHub's
/// message, whether data came back, the rate-limit and retry headers, and for a lookup what its field held. A header that is
/// missing or unusable is null.
/// </summary>
public sealed record GitHubResponse(int Status, IReadOnlyList<GitHubResponseError> Errors, string Message, bool HasData)
{
    /// <summary>The requests left in the rate limit's window.</summary>
    public int? RateLimitRemaining { get; init; }

    /// <summary>When the rate limit's window resets.</summary>
    public DateTimeOffset? RateLimitReset { get; init; }

    /// <summary>How long GitHub asked the controller to wait.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>What the data held at the field the request looked up.</summary>
    public ResponseField Field { get; init; }
}

/// <summary>One GraphQL error: its type and the path it names, each empty when it has none.</summary>
public sealed record GitHubResponseError(string Type, IReadOnlyList<string?> Path);

/// <summary>A looked-up field: missing, or inside a parent that did not resolve; null inside a resolved parent; or a value.</summary>
public enum ResponseField { Missing, Null, Value }

/// <summary>The controller's decisions over what GitHub answered: success, a confirmed absence, or a classified failure.</summary>
public static class GitHubResponses
{
    static readonly string[] Rejections = ["NOT_FOUND", "FORBIDDEN", "UNPROCESSABLE"];

    /// <summary>Returns when GitHub answered with data and no errors; otherwise throws the classified failure. A null response is
    /// output that is no HTTP response.</summary>
    public static void Check(GitHubResponse? response)
    {
        if (response is null) throw new GitHubRequestException(GitHubFailure.Transient, "unrecognized gh response", uncertain: true);
        var status = response.Status;
        var types = response.Errors.Select(error => error.Type).ToArray();
        var detail = response.Message;
        var description = $"HTTP {status}{(types.FirstOrDefault(t => t.Length != 0) is { } first ? " " + first : "")}{(detail.Length != 0 ? ": " + detail : "")}";
        var exhausted = response.RateLimitRemaining == 0;
        var retryAfter = response.RetryAfter;
        var reset = exhausted ? response.RateLimitReset : null;
        if (status == 429 || types.Contains("RATE_LIMITED") || (status == 403 && (retryAfter is not null || exhausted ||
            detail.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || detail.Contains("abuse", StringComparison.OrdinalIgnoreCase))))
            throw new GitHubRequestException(GitHubFailure.RateLimited, description, retryAfter: retryAfter, resetAt: reset);
        if (status is >= 200 and < 300)
        {
            if (types.Length == 0)
            {
                if (response.HasData) return;
                throw new GitHubRequestException(GitHubFailure.Transient, description + ": no data", uncertain: true);
            }
            if (types.All(Rejections.Contains)) throw new GitHubRequestException(GitHubFailure.Permanent, description);
            throw new GitHubRequestException(GitHubFailure.Transient, description, uncertain: true);
        }
        // 401: renewal may recover the credential. 408, 409 and 5xx may have been applied before failing.
        if (status == 401) throw new GitHubRequestException(GitHubFailure.Transient, description);
        if (status is 400 or 403 or 404 or 410 or 422) throw new GitHubRequestException(GitHubFailure.Permanent, description);
        throw new GitHubRequestException(GitHubFailure.Transient, description, uncertain: true);
    }

    /// <summary>
    /// True only when GitHub's response is its typed NOT_FOUND for exactly the field at <paramref name="path"/>, inside a parent
    /// that resolved: that object is confirmed absent. Message text, other error types and incomplete data confirm nothing.
    /// </summary>
    public static bool ConfirmsAbsent(GitHubResponse? response, IReadOnlyList<string> path) =>
        response is { Status: 200, Errors: [{ Type: "NOT_FOUND", Path: var located }], Field: ResponseField.Null } && located.SequenceEqual(path);

    /// <summary>
    /// A lookup of the field at <paramref name="path"/>, whose value the response's <see cref="GitHubResponse.Field"/> describes: true
    /// when GitHub returned it, false when GitHub confirmed it absent; any other response throws its classified failure, and data
    /// without the field is an incomplete <paramref name="subject"/> response.
    /// </summary>
    public static bool Found(GitHubResponse? response, IReadOnlyList<string> path, string subject)
    {
        if (ConfirmsAbsent(response, path)) return false;
        Check(response);
        if (response!.Field != ResponseField.Value) throw new GitHubRequestException(GitHubFailure.Transient, $"incomplete {subject} response");
        return true;
    }
}
