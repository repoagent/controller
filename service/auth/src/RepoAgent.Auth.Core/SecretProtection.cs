namespace RepoAgent.Auth;

// Protects the secrets the broker stores, such as GitHub tokens and setup completions, so that only
// the broker's key ring reads them back, and only under the purpose that protected them.
public interface ISecretProtection
{
    // Throws SecretProtectionUnavailable when the key ring cannot protect the secret.
    string Protect(SecretPurpose purpose, string secret);

    // Throws SecretUnreadable unless the same key ring protected the value under the same purpose.
    string Unprotect(SecretPurpose purpose, string protectedSecret);
}

// A secret protected under one purpose chain is unreadable under any other. The chains are part of
// the persisted format: changing one element makes every secret stored under it unreadable.
public sealed class SecretPurpose(string root, params string[] chain)
{
    public string Root { get; } = root;
    public IReadOnlyList<string> Chain { get; } = [.. chain];
}

// The value was protected under another purpose or key ring, is damaged, or the key ring is unavailable.
public sealed class SecretUnreadable(Exception? cause = null) : Exception("The protected secret cannot be read.", cause);

public sealed class SecretProtectionUnavailable(Exception? cause = null) : Exception("The secret cannot be protected.", cause);
