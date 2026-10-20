using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Data.Tables;
using Azure.Security.KeyVault.Keys.Cryptography;
using static RepoAgent.Auth.IssuancePolicy;

namespace RepoAgent.Auth;

// Key Vault owns the private key. The attempt owns the fixed JWT timestamps; no renewal/retry.
public sealed class AppJwtSigner(CryptographyClient key, TimeProvider clock)
{
    public async Task<string> SignAsync(TableEntity attempt, CancellationToken ct)
    {
        var issued = Date(attempt, "JwtIssuedAt");
        var expiry = Date(attempt, "JwtExpiresAt");
        var reserved = Date(attempt, "ReservedAt");
        if (Text(attempt, "State") != "reserved" || issued != reserved.AddSeconds(-60)
            || expiry != reserved.AddSeconds(120) || expiry <= clock.GetUtcNow()) throw new IssuanceDenied();
        var header = Base64(Encoding.UTF8.GetBytes("{\"alg\":\"RS256\",\"typ\":\"JWT\"}"));
        var payload = Base64(JsonSerializer.SerializeToUtf8Bytes(new { iat = issued.ToUnixTimeSeconds(), exp = expiry.ToUnixTimeSeconds(), iss = AppId }));
        var unsigned = header + "." + payload;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var signature = await key.SignAsync(SignatureAlgorithm.RS256, SHA256.HashData(Encoding.ASCII.GetBytes(unsigned)), timeout.Token);
        ct.ThrowIfCancellationRequested();
        if (expiry <= clock.GetUtcNow() || signature.Algorithm != SignatureAlgorithm.RS256) throw new IssuanceDenied();
        return unsigned + "." + Base64(signature.Signature);
    }
    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
