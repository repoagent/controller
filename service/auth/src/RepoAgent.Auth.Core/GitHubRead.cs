namespace RepoAgent.Auth;

// What one GitHub read answered, as a fact for the core to decide on. Adapters translate responses into
// these facts and decide nothing: whether an answer proves a change, a withdrawal or nothing is the core's call.
public abstract record GitHubRead<T>
{
    private GitHubRead() { }

    // A well-formed answer.
    public sealed record Found(T Value) : GitHubRead<T>;

    // GitHub answered 404. GitHub also answers 404 when the token lost access, so this alone proves no absence.
    public sealed record NotFound : GitHubRead<T>;

    // GitHub answered with another status, such as 403 or a redirect.
    public sealed record Refused(int Status) : GitHubRead<T>;

    // GitHub answered, but the answer cannot be read as T.
    public sealed record Malformed(GitHubDefect Defect) : GitHubRead<T>;
}

public enum GitHubDefect
{
    // The body is not JSON, or nests too deeply.
    Unparseable,
    // An object names one member twice, so which value GitHub meant is unknown.
    DuplicateMember,
    // The body is larger than any answer the broker reads.
    Oversized,
    // The JSON lacks a member, or a member has another kind or range than GitHub documents.
    Shape,
}

// GitHub could not be reached or did not answer in time. Callers treat it like any unverifiable read.
public sealed class GitHubUnavailable(string message, Exception? cause = null) : Exception(message, cause);

// A GitHub installation token the broker holds. Its value never appears in logs or exception text.
public sealed record GitHubToken(string Value)
{
    public override string ToString() => "GitHubToken { Value = [redacted] }";
}

public static class GitHubRead
{
    public static GitHubRead<T> Found<T>(T value) => new GitHubRead<T>.Found(value);
}
