namespace RepoAgent.Auth;

public sealed record BrokerOptions(Uri TableEndpoint, string TableName, string DeploymentEpoch,
    string? ManagedIdentityClientId, int RequestLimitPerMinute, int ConcurrentRequestLimit, int RequestTimeoutSeconds, Uri? BrokerOrigin = null)
{
    public int CandidateRepositoryLimit { get; init; } = 128;
    public int RetainedEnrollmentLimit { get; init; } = 256;
    public int RetainedIssuanceLimit { get; init; } = 4096;

    // The Broker settings, by their configuration path ("Broker:TableEndpoint"). Development storage may name a loopback
    // emulator's table endpoint.
    public static BrokerOptions Read(Func<string, string?> setting, bool allowDevelopmentLoopback = false)
    {
        string? Broker(string name) => setting("Broker:" + name);
        if (!Uri.TryCreate(Broker("TableEndpoint"), UriKind.Absolute, out var endpoint)
            || !string.IsNullOrEmpty(endpoint.UserInfo) || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || !(endpoint.Scheme == "https" && endpoint.AbsolutePath == "/"
                || allowDevelopmentLoopback && IsDevelopmentTableEndpoint(endpoint)))
            throw new InvalidOperationException("Broker:TableEndpoint must be an HTTPS Table service origin.");
        var table = Broker("TableName");
        if (table is null || table.Length is < 3 or > 63 || !char.IsAsciiLetter(table[0])
            || !table.All(char.IsAsciiLetterOrDigit))
            throw new InvalidOperationException("Broker:TableName must be a valid existing Azure table name.");
        var epoch = Broker("DeploymentEpoch");
        if (epoch is null || epoch.Length is < 1 or > 128
            || !epoch.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new InvalidOperationException("Broker:DeploymentEpoch must be an externally supplied epoch identifier.");
        var clientId = Broker("ManagedIdentityClientId");
        if (clientId is not null && !Guid.TryParse(clientId, out _))
            throw new InvalidOperationException("Broker:ManagedIdentityClientId must be a client ID.");
        Uri? origin = null;
        if (Broker("BrokerOrigin") is { } configuredOrigin
            && (!Uri.TryCreate(configuredOrigin, UriKind.Absolute, out origin) || origin.Scheme != "https"
                || origin.HostNameType != UriHostNameType.Dns || origin.UserInfo.Length != 0
                || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0))
            throw new InvalidOperationException("Broker:BrokerOrigin must be a canonical HTTPS origin.");
        return new(endpoint, table, epoch, clientId,
            Limit(Broker, "RequestLimitPerMinute", 120, 10_000),
            Limit(Broker, "ConcurrentRequestLimit", 8, 128),
            Limit(Broker, "RequestTimeoutSeconds", 15, 60), origin)
        {
            CandidateRepositoryLimit = Limit(Broker, nameof(CandidateRepositoryLimit), 128, 128),
            RetainedEnrollmentLimit = Limit(Broker, nameof(RetainedEnrollmentLimit), 256, 256),
            RetainedIssuanceLimit = Limit(Broker, nameof(RetainedIssuanceLimit), 4096, 4096)
        };
    }

    public static bool IsDevelopmentTableEndpoint(Uri endpoint) =>
        endpoint.Scheme == "http" && endpoint.IsLoopback
        && endpoint.AbsolutePath.Length > 1
        && endpoint.AbsolutePath[1..].All(char.IsAsciiLetterOrDigit);

    private static int Limit(Func<string, string?> section, string name, int fallback, int maximum)
    {
        var value = section(name);
        if (value is null) return fallback;
        if (!int.TryParse(value, out var parsed) || parsed < 1 || parsed > maximum)
            throw new InvalidOperationException($"Broker:{name} must be between 1 and {maximum}.");
        return parsed;
    }
}
