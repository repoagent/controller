using Azure.Core;
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
        Uri? developmentVault = null;
        if (configuration["DevelopmentVaultUri"] is { } localVault)
        {
            developmentVault = HttpsUri(localVault);
            if (!builder.Environment.IsDevelopment() || !developmentVault.IsLoopback
                || developmentVault.AbsolutePath != "/")
                throw new InvalidOperationException("Emulated Key Vault requires Development and a loopback HTTPS origin.");
        }
        if (configuration["SigningKeyUri"] is null)
        {
            // Incomplete composition is supported; malformed supplied settings are not.
            if (configuration["DataProtectionKeyUri"] is { } key) _ = KeyUri(key);
            if (configuration["DataProtectionBlobUri"] is { } uri) _ = HttpsUri(uri);
            if (configuration["DataProtectionApplicationName"] is { } name
                && (string.IsNullOrWhiteSpace(name) || name.Length > 128))
                throw new InvalidOperationException("Auth:DataProtectionApplicationName must identify the shared key ring.");
            return;
        }
        var signing = KeyUri(configuration["SigningKeyUri"]);
        var wrapping = KeyUri(configuration["DataProtectionKeyUri"]);
        var developmentStorage = configuration["DevelopmentBlobConnectionString"]
            ?? BrokerStorage.DevelopmentConnection(builder.Configuration, builder.Environment.EnvironmentName);
        if (developmentStorage is not null && !builder.Environment.IsDevelopment())
            throw new InvalidOperationException("Development storage requires Development environment.");
        var blob = developmentStorage is null ? HttpsUri(configuration["DataProtectionBlobUri"])
            : new Uri(configuration["DataProtectionBlobUri"] ?? "", UriKind.Absolute);
        var application = configuration["DataProtectionApplicationName"];
        if (string.IsNullOrWhiteSpace(application) || application.Length > 128)
            throw new InvalidOperationException("Auth:DataProtectionApplicationName must identify the shared key ring.");
        var options = BrokerStorage.Options(builder.Configuration, builder.Environment.EnvironmentName);
        var identity = options.ManagedIdentityClientId is { } clientId
            ? ManagedIdentityId.FromUserAssignedClientId(clientId) : ManagedIdentityId.SystemAssigned;
        TokenCredential credential = new ManagedIdentityCredential(identity);
        if (developmentVault is not null)
        {
            if (signing.GetLeftPart(UriPartial.Authority) != developmentVault.GetLeftPart(UriPartial.Authority)
                || wrapping.GetLeftPart(UriPartial.Authority) != developmentVault.GetLeftPart(UriPartial.Authority)
                || builder.Configuration["AZURE_AUTHORITY_HOST"]?.TrimEnd('/') != developmentVault.AbsoluteUri.TrimEnd('/'))
                throw new InvalidOperationException("Development keys and credential authority must use the local vault.");
            credential = new EnvironmentCredential(new EnvironmentCredentialOptions
            { AuthorityHost = developmentVault, DisableInstanceDiscovery = true });
        }
        var blobOptions = new BlobClientOptions
        {
            Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(3) },
            Diagnostics = { IsLoggingContentEnabled = false }
        };
        var keyRing = developmentStorage is null ? new BlobClient(blob, credential, blobOptions)
            : new BlobClient(developmentStorage, "data-protection", "keyring.xml", blobOptions);
        if (developmentStorage is not null && (blob != keyRing.Uri || !blob.IsLoopback
            || blob.UserInfo.Length != 0 || blob.Query.Length != 0 || blob.Fragment.Length != 0))
            throw new InvalidOperationException("Development key ring must use the local storage emulator.");
        CryptographyClientOptions CryptoOptions() => new()
        {
            DisableChallengeResourceVerification = developmentVault is not null,
            Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(3) },
            Diagnostics = { IsLoggingContentEnabled = false }
        };
        builder.Services.AddDataProtection().SetApplicationName(application)
            .PersistKeysToAzureBlobStorage(keyRing)
            .ProtectKeysWithAzureKeyVault(wrapping, new KeyResolver(credential, CryptoOptions()));
        builder.Services.AddSingleton<ISecretProtection, DataProtectionSecretProtection>();
        builder.Services.AddSingleton(new CryptographyClient(signing, credential, CryptoOptions()));
        builder.Services.AddSingleton(new GitHubRest(new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, MaxConnectionsPerServer = 8,
            ConnectTimeout = TimeSpan.FromSeconds(3), MaxResponseHeadersLength = 16
        }) { Timeout = Timeout.InfiniteTimeSpan }));
        builder.Services.AddSingleton(IssuanceLimits.Read(builder.Configuration));
        builder.Services.AddSingleton(CreateStore);
        builder.Services.AddSingleton<IAppInstallations, GitHubAppInstallations>();
        builder.Services.AddSingleton<IControllerApprovals, GitHubControllerApprovals>();
        builder.Services.AddSingleton<IGitHubTokens, GitHubTokens>();
        builder.Services.AddSingleton<InstallationTokenIssuer>();
        if (options.BrokerOrigin is not null)
        {
            builder.Services.AddSingleton<EnrollmentStore>();
            builder.Services.AddSingleton<EnrollmentService>();
        }
    }
    // The issuance store over the broker state, for this deployment's epoch and retained-ledger ceiling.
    public static IssuanceStore CreateStore(IServiceProvider services)
    {
        var options = services.GetRequiredService<BrokerOptions>();
        return new(services.GetRequiredService<IBrokerState>(), options.DeploymentEpoch, options.RetainedIssuanceLimit,
            services.GetRequiredService<ISecretProtection>(), services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<IssuanceLimits>());
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
