namespace RepoAgent.Auth;

// Optional overrides can only lower the reviewed ceilings. They cannot add quota partitions.
public sealed record IssuanceLimits(int RepositoryAdmissions = 12, int GlobalAdmissions = 120,
    int RepositoryReads = 60, int GlobalReads = 600, int RepositorySigns = 2, int GlobalSigns = 20,
    int RepositoryMints = 2, int GlobalMints = 20, int RepositoryAttempts = 8, int GlobalAttempts = 128)
{
    public static IssuanceLimits Read(IConfiguration configuration)
    {
        var section = configuration.GetSection("Broker:Issuance");
        return new(Limit(section, nameof(RepositoryAdmissions), 12), Limit(section, nameof(GlobalAdmissions), 120),
            Limit(section, nameof(RepositoryReads), 60), Limit(section, nameof(GlobalReads), 600),
            Limit(section, nameof(RepositorySigns), 2), Limit(section, nameof(GlobalSigns), 20),
            Limit(section, nameof(RepositoryMints), 2), Limit(section, nameof(GlobalMints), 20),
            Limit(section, nameof(RepositoryAttempts), 8), Limit(section, nameof(GlobalAttempts), 128));
    }
    private static int Limit(IConfiguration section, string name, int maximum)
    {
        if (section[name] is not { } value) return maximum;
        if (!int.TryParse(value, out var n) || n < 1 || n > maximum)
            throw new InvalidOperationException($"Broker:Issuance:{name} must be between 1 and {maximum}.");
        return n;
    }
}
