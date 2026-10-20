using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace RepoAgent.Auth;

public static class IssuanceServices
{
    public static void Add(WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration.GetSection("Auth");
        if (configuration["SigningKeyUri"] is null) return; // Revoke-only deployment remains supported.
        var signing = KeyUri(configuration["SigningKeyUri"]);
        var wrapping = KeyUri(configuration["DataProtectionKeyUri"]);
        var blob = HttpsUri(configuration["DataProtectionBlobUri"]);
        var application = configuration["DataProtectionApplicationName"];
        if (string.IsNullOrWhiteSpace(application) || application.Length > 128)
            throw new InvalidOperationException("Auth:DataProtectionApplicationName must identify the shared key ring.");
        var options = BrokerOptions.Read(builder.Configuration);
        var identity = options.ManagedIdentityClientId is { } clientId
            ? ManagedIdentityId.FromUserAssignedClientId(clientId) : ManagedIdentityId.SystemAssigned;
        var credential = new ManagedIdentityCredential(identity);
        builder.Services.AddDataProtection().SetApplicationName(application)
            .PersistKeysToAzureBlobStorage(new BlobClient(blob, credential, new BlobClientOptions
            {
                Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(3) },
                Diagnostics = { IsLoggingContentEnabled = false }
            })).ProtectKeysWithAzureKeyVault(wrapping, new KeyResolver(credential, new CryptographyClientOptions
            {
                Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(3) },
                Diagnostics = { IsLoggingContentEnabled = false }
            }));
        builder.Services.AddSingleton(new CryptographyClient(signing, credential, new CryptographyClientOptions
        {
            Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(3) },
            Diagnostics = { IsLoggingContentEnabled = false }
        }));
        builder.Services.AddSingleton(new GitHubIssuance(new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, MaxConnectionsPerServer = 8,
            ConnectTimeout = TimeSpan.FromSeconds(3), MaxResponseHeadersLength = 16
        }) { Timeout = Timeout.InfiniteTimeSpan }, TimeProvider.System));
        builder.Services.AddSingleton(IssuanceLimits.Read(builder.Configuration));
        builder.Services.AddSingleton<IssuanceStore>();
        builder.Services.AddSingleton<AppJwtSigner>();
        builder.Services.AddSingleton<InstallationTokenIssuer>();
        if (options.BrokerOrigin is not null)
        {
            builder.Services.AddSingleton<EnrollmentStore>();
            builder.Services.AddSingleton<EnrollmentService>();
        }
    }
    private static Uri HttpsUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo) && uri.Query.Length == 0 && uri.Fragment.Length == 0
        ? uri : throw new InvalidOperationException("Auth resource settings must be HTTPS resource URIs without credentials.");
    private static Uri KeyUri(string? value)
    {
        var uri = HttpsUri(value);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3 || segments[0] != "keys")
            throw new InvalidOperationException("Auth Key Vault keys must pin a key version.");
        return uri;
    }
}
