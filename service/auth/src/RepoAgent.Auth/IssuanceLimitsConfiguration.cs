namespace RepoAgent.Auth;

// Reads the issuance limit overrides from the Broker:Issuance configuration section.
internal static class IssuanceLimitsConfiguration
{
    extension(IssuanceLimits)
    {
        public static IssuanceLimits Read(IConfiguration configuration)
        {
            var section = configuration.GetSection("Broker:Issuance");
            return new(Limit(section, nameof(IssuanceLimits.RepositoryAdmissions), 12), Limit(section, nameof(IssuanceLimits.GlobalAdmissions), 120),
                Limit(section, nameof(IssuanceLimits.RepositoryReads), 60), Limit(section, nameof(IssuanceLimits.GlobalReads), 600),
                Limit(section, nameof(IssuanceLimits.RepositorySigns), 2), Limit(section, nameof(IssuanceLimits.GlobalSigns), 20),
                Limit(section, nameof(IssuanceLimits.RepositoryMints), 2), Limit(section, nameof(IssuanceLimits.GlobalMints), 20),
                Limit(section, nameof(IssuanceLimits.RepositoryAttempts), 8), Limit(section, nameof(IssuanceLimits.GlobalAttempts), 128));
        }
    }

    private static int Limit(IConfiguration section, string name, int maximum)
    {
        if (section[name] is not { } value) return maximum;
        if (!int.TryParse(value, out var n) || n < 1 || n > maximum)
            throw new InvalidOperationException($"Broker:Issuance:{name} must be between 1 and {maximum}.");
        return n;
    }
}
