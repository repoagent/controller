using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Keys.Cryptography;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.DataProtection;

namespace RepoAgent.Auth;

public static class IssuanceServices
{
    // Composes issuance, and enrollment when it has the broker's origin; returns the issuance settings, null without them.
    public static IssuanceSettings? Add(WebApplicationBuilder builder)
    {
        // Incomplete composition is supported; malformed supplied settings are not.
        if (BrokerSettings.Issuance(name => builder.Configuration[name], builder.Environment.EnvironmentName) is not { } settings) return null;
        var identity = settings.Options.ManagedIdentityClientId is { } clientId
            ? ManagedIdentityId.FromUserAssignedClientId(clientId) : ManagedIdentityId.SystemAssigned;
        TokenCredential credential = new ManagedIdentityCredential(identity);
        if (settings.DevelopmentVault is { } developmentVault)
            credential = new EnvironmentCredential(new EnvironmentCredentialOptions
            { AuthorityHost = developmentVault, DisableInstanceDiscovery = true });
        var blobOptions = new BlobClientOptions
        {
            Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(3) },
            Diagnostics = { IsLoggingContentEnabled = false }
        };
        var keyRing = settings.DevelopmentStorage is not { } developmentStorage ? new BlobClient(settings.KeyRing, credential, blobOptions)
            : new BlobClient(developmentStorage, "data-protection", "keyring.xml", blobOptions);
        settings.RequireEmulatedKeyRing(keyRing.Uri);
        CryptographyClientOptions CryptoOptions() => new()
        {
            DisableChallengeResourceVerification = settings.DevelopmentVault is not null,
            Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(3) },
            Diagnostics = { IsLoggingContentEnabled = false }
        };
        builder.Services.AddDataProtection().SetApplicationName(settings.ApplicationName)
            .PersistKeysToAzureBlobStorage(keyRing)
            .ProtectKeysWithAzureKeyVault(settings.WrappingKey, new KeyResolver(credential, CryptoOptions()));
        builder.Services.AddSingleton<ISecretProtection, DataProtectionSecretProtection>();
        builder.Services.AddSingleton(new CryptographyClient(settings.SigningKey, credential, CryptoOptions()));
        builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            MaxConnectionsPerServer = 8,
            ConnectTimeout = TimeSpan.FromSeconds(3),
            MaxResponseHeadersLength = 16
        })
        { Timeout = Timeout.InfiniteTimeSpan });
        builder.Services.AddSingleton(services => new GitHubRest(services.GetRequiredService<HttpClient>()));
        builder.Services.AddSingleton(IssuanceLimits.Read(builder.Configuration));
        builder.Services.AddSingleton(CreateStore);
        builder.Services.AddSingleton<IAppInstallations, GitHubAppInstallations>();
        builder.Services.AddSingleton<IControllerApprovals, GitHubControllerApprovals>();
        builder.Services.AddSingleton<IGitHubTokens, GitHubTokens>();
        builder.Services.AddSingleton<InstallationTokenIssuer>();
        if (settings.Enrolls) builder.Services.AddSingleton(CreateEnrollmentService);
        return settings;
    }
    // The issuance store over the broker state, for this deployment's epoch and retained-ledger ceiling.
    public static IssuanceStore CreateStore(IServiceProvider services)
    {
        var options = services.GetRequiredService<BrokerOptions>();
        return new(services.GetRequiredService<IBrokerState>(), options.DeploymentEpoch, options.RetainedIssuanceLimit,
            services.GetRequiredService<ISecretProtection>(), services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<IssuanceLimits>());
    }
    // Enrollment over the broker state, for this deployment's epoch, retained ceilings and public origin.
    public static EnrollmentService CreateEnrollmentService(IServiceProvider services)
    {
        var options = services.GetRequiredService<BrokerOptions>();
        var clock = services.GetRequiredService<TimeProvider>();
        var store = new EnrollmentStore(services.GetRequiredService<IBrokerState>(), options.DeploymentEpoch,
            new(options.CandidateRepositoryLimit, options.RetainedEnrollmentLimit, options.RetainedIssuanceLimit),
            services.GetRequiredService<ISecretProtection>(), clock, services.GetRequiredService<IssuanceLimits>());
        return new(store, services.GetRequiredService<IGitHubTokens>(), services.GetRequiredService<IAppInstallations>(),
            services.GetRequiredService<IControllerApprovals>(), options.BrokerOrigin, clock);
    }
}
