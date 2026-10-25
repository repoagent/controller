using Azure.Data.Tables;

namespace RepoAgent.Auth;

// Typed reads of broker Table row values. A missing or out-of-range value denies the operation.
internal static class TableValues
{
    public static string Text(TableEntity e, string key) => e.TryGetValue(key, out var v) && v is string s && s.Length is > 0 and <= 8192 ? s : throw new IssuanceDenied();
    public static long Number(TableEntity e, string key) => e.TryGetValue(key, out var v) && v is long n && n >= 0 ? n : throw new IssuanceDenied();
    public static DateTimeOffset Date(TableEntity e, string key) => e.TryGetValue(key, out var v) && v is DateTimeOffset d ? d : throw new IssuanceDenied();
}
