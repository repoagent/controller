namespace RepoAgent.Auth;

// Reads the issuance limit overrides from the Broker:Issuance configuration section. The core's defaults are the
// ceilings: an override may only lower them.
internal static class IssuanceLimitsConfiguration
{
    extension(IssuanceLimits)
    {
        public static IssuanceLimits Read(IConfiguration configuration)
        {
            var section = configuration.GetSection("Broker:Issuance");
            var ceiling = new IssuanceLimits();
            return new(Limit(section, nameof(ceiling.RepositoryAdmissions), ceiling.RepositoryAdmissions),
                Limit(section, nameof(ceiling.GlobalAdmissions), ceiling.GlobalAdmissions),
                Limit(section, nameof(ceiling.RepositoryReads), ceiling.RepositoryReads),
                Limit(section, nameof(ceiling.GlobalReads), ceiling.GlobalReads),
                Limit(section, nameof(ceiling.RepositorySigns), ceiling.RepositorySigns),
                Limit(section, nameof(ceiling.GlobalSigns), ceiling.GlobalSigns),
                Limit(section, nameof(ceiling.RepositoryMints), ceiling.RepositoryMints),
                Limit(section, nameof(ceiling.GlobalMints), ceiling.GlobalMints),
                Limit(section, nameof(ceiling.RepositoryAttempts), ceiling.RepositoryAttempts),
                Limit(section, nameof(ceiling.GlobalAttempts), ceiling.GlobalAttempts));
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
