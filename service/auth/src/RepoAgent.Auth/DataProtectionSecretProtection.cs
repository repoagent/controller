using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace RepoAgent.Auth;

// The ISecretProtection adapter over the shared Data Protection key ring (Blob storage wrapped by a
// Key Vault key). Each purpose maps to CreateProtector(root, chain...), the same call the stores made
// before the port, so values protected before and after it stay mutually readable.
internal sealed class DataProtectionSecretProtection(IDataProtectionProvider keyRing) : ISecretProtection
{
    public string Protect(SecretPurpose purpose, string secret)
    {
        // Data Protection reports every failure, including an unavailable key ring, as CryptographicException.
        try { return Protector(purpose).Protect(secret); }
        catch (CryptographicException ex) { throw new SecretProtectionUnavailable(ex); }
    }

    public string Unprotect(SecretPurpose purpose, string protectedSecret)
    {
        try { return Protector(purpose).Unprotect(protectedSecret); }
        catch (CryptographicException ex) { throw new SecretUnreadable(ex); }
    }

    private IDataProtector Protector(SecretPurpose purpose) => keyRing.CreateProtector(purpose.Root, [.. purpose.Chain]);
}
