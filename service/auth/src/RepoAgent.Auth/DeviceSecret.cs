using System.Security.Cryptography;

namespace RepoAgent.Auth;

internal static class DeviceSecret
{
    public static bool IsDeviceId(string value) => value.Length is >= 1 and <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static byte[]? Hash(string? secret)
    {
        // Match the frozen v1 opaque base64url grammar. Enrollment must generate at least 256 random bits.
        if (secret is null || secret.Length is < 43 or > 128
            || !secret.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
        return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));
    }

    public static bool Matches(byte[] suppliedHash, string storedHash)
    {
        if (storedHash.Length != 64 || !storedHash.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Invalid stored secret hash.");
        return CryptographicOperations.FixedTimeEquals(suppliedHash, Convert.FromHexString(storedHash));
    }
}
