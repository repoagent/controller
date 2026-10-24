using System.Collections.Concurrent;

namespace RepoAgent.Auth.Tests;

// Keeps the key ring guarantees the stores rely on: a protected value never contains its secret, and it
// reads back only under the exact purpose chain that protected it, otherwise SecretUnreadable.
internal sealed class InMemorySecretProtection : ISecretProtection
{
    private readonly ConcurrentDictionary<string, (string[] Purpose, string Secret)> protectedSecrets = new();

    public string Protect(SecretPurpose purpose, string secret)
    {
        var protectedSecret = "protected-" + Guid.NewGuid().ToString("N");
        protectedSecrets[protectedSecret] = (Chain(purpose), secret);
        return protectedSecret;
    }

    public string Unprotect(SecretPurpose purpose, string protectedSecret) =>
        protectedSecrets.TryGetValue(protectedSecret, out var value) && value.Purpose.SequenceEqual(Chain(purpose))
            ? value.Secret : throw new SecretUnreadable();

    private static string[] Chain(SecretPurpose purpose) => [purpose.Root, .. purpose.Chain];
}
