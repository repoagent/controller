namespace RepoAgent.Auth;

internal static class DevelopmentSettings
{
    internal static void Add(IConfigurationBuilder builder, IConfiguration configuration, string? environment)
    {
        if (configuration["Broker:DevelopmentSettingsFile"] is not { } file) return;
        if (environment != "Development")
            throw new InvalidOperationException("Local settings require the Development environment.");
        builder.AddJsonFile(Path.GetFullPath(file), optional: false);
    }
}
