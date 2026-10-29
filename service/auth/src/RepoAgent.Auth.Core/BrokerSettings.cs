namespace RepoAgent.Auth;

// The production guards over the broker's settings, read by their configuration path ("Auth:SigningKeyUri"), and its
// environment. Local settings files, Development storage and the emulated Key Vault exist only in Development; the Key
// Vault keys, the key ring and the monitoring endpoint must be the HTTPS resources they claim to be. A malformed setting
// throws InvalidOperationException (or UriFormatException), so the host doesn't start.
public static class BrokerSettings
{
    // The local settings file to add, which only the Development environment may name.
    public static string? DevelopmentSettingsFile(Func<string, string?> setting, string? environment)
    {
        if (setting("Broker:DevelopmentSettingsFile") is not { } file) return null;
        if (environment != "Development")
            throw new InvalidOperationException("Local settings require the Development environment.");
        return file;
    }

    // The storage emulator's connection string, which only the Development environment may name.
    public static string? DevelopmentConnection(Func<string, string?> setting, string? environment)
    {
        var connection = setting("Broker:DevelopmentConnectionString") ?? setting("Broker:Cleanup:DevelopmentConnectionString");
        if (connection is not null && environment != "Development")
            throw new InvalidOperationException("Development storage requires Development environment.");
        return connection;
    }

    public static BrokerOptions Options(Func<string, string?> setting, string? environment) =>
        BrokerOptions.Read(setting, DevelopmentConnection(setting, environment) is not null);

    // Ready only when the broker both issues and enrolls; a broker that only revokes isn't.
    public static bool Ready(IssuanceSettings? issuance) => issuance?.Enrolls == true;

    // A settings URI names its resource and nothing else: no user information, query or fragment.
    public static bool IsBare(Uri uri) => uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    // Persisted local key-ring ciphertext retains its wrapping key's old published port. Only the configured
    // emulator's wrapping key name may resolve through its current endpoint; historical versions stay pinned.
    public static Uri DevelopmentWrappingKey(Uri vault, Uri wrappingKey, string recordedKey)
    {
        var currentVault = HttpsUri(vault.AbsoluteUri);
        var current = KeyUri(wrappingKey.AbsoluteUri);
        var recorded = KeyUri(recordedKey);
        if (!currentVault.IsLoopback || currentVault.AbsolutePath != "/"
            || current.GetLeftPart(UriPartial.Authority) != currentVault.GetLeftPart(UriPartial.Authority)
            || !recorded.IsLoopback || recorded.Segments[2] != current.Segments[2])
            throw new InvalidOperationException("Local key-ring keys must name the configured emulator wrapping key.");
        return new Uri(currentVault, recorded.AbsolutePath);
    }

    // The issuance composition: null without a signing key, the supported incomplete composition that only revokes. Every
    // supplied setting must still be well formed.
    public static IssuanceSettings? Issuance(Func<string, string?> setting, string? environment)
    {
        var development = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);
        var signingCredential = setting("Auth:DevelopmentSigningCredential");
        if (signingCredential is not null && (!development || signingCredential != "AzureCli"))
            throw new InvalidOperationException("Development signing requires Development and the AzureCli credential.");
        var developerSigning = signingCredential is not null;
        Uri? developmentVault = null;
        if (setting("Auth:DevelopmentVaultUri") is { } localVault)
        {
            developmentVault = HttpsUri(localVault);
            if (!development || !developmentVault.IsLoopback || developmentVault.AbsolutePath != "/")
                throw new InvalidOperationException("Emulated Key Vault requires Development and a loopback HTTPS origin.");
        }
        if (developerSigning && (developmentVault is null || setting("Auth:SigningKeyUri") is null))
            throw new InvalidOperationException("Development signing requires a shared signing key and a local wrapping vault.");
        if (setting("Auth:SigningKeyUri") is null)
        {
            if (setting("Auth:DataProtectionKeyUri") is { } key) _ = KeyUri(key);
            if (setting("Auth:DataProtectionBlobUri") is { } uri) _ = HttpsUri(uri);
            if (setting("Auth:DataProtectionApplicationName") is { } name && (string.IsNullOrWhiteSpace(name) || name.Length > 128))
                throw new InvalidOperationException("Auth:DataProtectionApplicationName must identify the shared key ring.");
            return null;
        }
        var signing = KeyUri(setting("Auth:SigningKeyUri"));
        var wrapping = KeyUri(setting("Auth:DataProtectionKeyUri"));
        var developmentStorage = setting("Auth:DevelopmentBlobConnectionString") ?? DevelopmentConnection(setting, environment);
        if (developmentStorage is not null && !development)
            throw new InvalidOperationException("Development storage requires Development environment.");
        var keyRing = developmentStorage is null ? HttpsUri(setting("Auth:DataProtectionBlobUri"))
            : new Uri(setting("Auth:DataProtectionBlobUri") ?? "", UriKind.Absolute);
        var application = setting("Auth:DataProtectionApplicationName");
        if (string.IsNullOrWhiteSpace(application) || application.Length > 128)
            throw new InvalidOperationException("Auth:DataProtectionApplicationName must identify the shared key ring.");
        var options = Options(setting, environment);
        if (developmentVault is not null
            && ((!developerSigning && signing.GetLeftPart(UriPartial.Authority) != developmentVault.GetLeftPart(UriPartial.Authority))
                || wrapping.GetLeftPart(UriPartial.Authority) != developmentVault.GetLeftPart(UriPartial.Authority)
                || setting("AZURE_AUTHORITY_HOST")?.TrimEnd('/') != developmentVault.AbsoluteUri.TrimEnd('/')))
            throw new InvalidOperationException("Development keys and credential authority must use the local vault.");
        if (developerSigning && (signing.IsLoopback || signing.HostNameType != UriHostNameType.Dns || !signing.IsDefaultPort
            || developmentStorage is null || DevelopmentConnection(setting, environment) is null
            || !options.TableEndpoint.IsLoopback || options.TableEndpoint.Scheme != "http"
            || !keyRing.IsLoopback || !IsBare(keyRing)))
            throw new InvalidOperationException("Development signing requires a shared HTTPS key and local Table/Blob storage.");
        return new(signing, wrapping, keyRing, application, developmentVault, developmentStorage, options)
        { UsesDeveloperSigning = developerSigning };
    }

    // Optional monitoring: null when no connection string is set. Otherwise the connection names only an instrumentation
    // key and an Azure Monitor ingestion origin, and the managed identity that authorizes the export is a client ID.
    public static MonitoringSettings? Monitoring(string? connection, string? clientId)
    {
        if (string.IsNullOrEmpty(connection)) return null;
        var fields = connection.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split('=', 2)).ToDictionary(value => value[0], value => value[1], StringComparer.OrdinalIgnoreCase);
        if (fields.Keys.Any(field => field is not ("InstrumentationKey" or "IngestionEndpoint" or "LiveEndpoint" or "ApplicationId"))
            || !fields.TryGetValue("InstrumentationKey", out var key) || !Guid.TryParse(key, out _)
            || !fields.TryGetValue("IngestionEndpoint", out var endpoint)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.Host.EndsWith(".in.applicationinsights.azure.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort || !IsBare(uri) || uri.AbsolutePath != "/" || !Guid.TryParse(clientId, out _))
            throw new InvalidOperationException("Invalid monitoring configuration.");
        return new(connection, clientId);
    }

    private static Uri HttpsUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && IsBare(uri)
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

// A composition that signs and issues: the pinned signing and key-ring wrapping keys, the key ring and its application
// name, and in Development the emulated vault and the storage emulator.
public sealed record IssuanceSettings(Uri SigningKey, Uri WrappingKey, Uri KeyRing, string ApplicationName, Uri? DevelopmentVault,
    string? DevelopmentStorage, BrokerOptions Options)
{
    // An explicit Development-only choice; wrapping still uses the local emulator's credential.
    public bool UsesDeveloperSigning { get; init; }

    // Enrollment needs the broker's public origin as well.
    public bool Enrolls => Options.BrokerOrigin is not null;

    // With the storage emulator, the key ring the emulator names must be the configured one, on loopback.
    public void RequireEmulatedKeyRing(Uri emulated)
    {
        if (DevelopmentStorage is not null && (KeyRing != emulated || !KeyRing.IsLoopback || !BrokerSettings.IsBare(KeyRing)))
            throw new InvalidOperationException("Development key ring must use the local storage emulator.");
    }
}

public sealed record MonitoringSettings(string Connection, string ClientId);
