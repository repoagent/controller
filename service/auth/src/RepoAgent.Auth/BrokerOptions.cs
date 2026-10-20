namespace RepoAgent.Auth;

public sealed record BrokerOptions(Uri TableEndpoint, string TableName, string DeploymentEpoch,
    string? ManagedIdentityClientId, int RequestLimitPerMinute, int ConcurrentRequestLimit, int RequestTimeoutSeconds, Uri? BrokerOrigin = null)
{
    public int CandidateRepositoryLimit { get; init; } = 128;
    public int RetainedEnrollmentLimit { get; init; } = 256;
    public int RetainedIssuanceLimit { get; init; } = 4096;

    public static BrokerOptions Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("Broker");
        if (!Uri.TryCreate(section["TableEndpoint"], UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != "https" || !string.IsNullOrEmpty(endpoint.UserInfo)
            || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new InvalidOperationException("Broker:TableEndpoint must be an HTTPS Table service origin.");
        var table = section["TableName"];
        if (table is null || table.Length is < 3 or > 63 || !char.IsAsciiLetter(table[0])
            || !table.All(char.IsAsciiLetterOrDigit))
            throw new InvalidOperationException("Broker:TableName must be a valid existing Azure table name.");
        var epoch = section["DeploymentEpoch"];
        if (epoch is null || epoch.Length is < 1 or > 128
            || !epoch.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new InvalidOperationException("Broker:DeploymentEpoch must be an externally supplied epoch identifier.");
        var clientId = section["ManagedIdentityClientId"];
        if (clientId is not null && !Guid.TryParse(clientId, out _))
            throw new InvalidOperationException("Broker:ManagedIdentityClientId must be a client ID.");
        Uri? origin = null;
        if (section["BrokerOrigin"] is { } configuredOrigin
            && (!Uri.TryCreate(configuredOrigin, UriKind.Absolute, out origin) || origin.Scheme != "https"
                || origin.HostNameType != UriHostNameType.Dns || origin.UserInfo.Length != 0
                || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0))
            throw new InvalidOperationException("Broker:BrokerOrigin must be a canonical HTTPS origin.");
        return new(endpoint, table, epoch, clientId,
            Limit(section, "RequestLimitPerMinute", 120, 10_000),
            Limit(section, "ConcurrentRequestLimit", 8, 128),
            Limit(section, "RequestTimeoutSeconds", 15, 60), origin)
        {
            CandidateRepositoryLimit = Limit(section, nameof(CandidateRepositoryLimit), 128, 128),
            RetainedEnrollmentLimit = Limit(section, nameof(RetainedEnrollmentLimit), 256, 256),
            RetainedIssuanceLimit = Limit(section, nameof(RetainedIssuanceLimit), 4096, 4096)
        };
    }

    private static int Limit(IConfiguration section, string name, int fallback, int maximum)
    {
        var value = section[name];
        if (value is null) return fallback;
        if (!int.TryParse(value, out var parsed) || parsed < 1 || parsed > maximum)
            throw new InvalidOperationException($"Broker:{name} must be between 1 and {maximum}.");
        return parsed;
    }
}
