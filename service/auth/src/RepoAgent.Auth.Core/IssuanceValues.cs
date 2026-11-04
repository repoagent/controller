namespace RepoAgent.Auth;

// Durable values a decision requires. A missing or out-of-range value denies the operation with 503.
internal static class IssuanceValues
{
    public static string Text(string? value) => value is { Length: > 0 and <= 8192 } text ? text : throw new IssuanceDenied();
    public static long Number(long? value) => value is { } number && number >= 0 ? number : throw new IssuanceDenied();
    public static DateTimeOffset Date(DateTimeOffset? value) => value ?? throw new IssuanceDenied();
}

