namespace RepoAgent.Auth;

internal static class DevelopmentSettings
{
    internal static void Add(IConfigurationBuilder builder, IConfiguration configuration, string? environment)
    {
        if (BrokerSettings.DevelopmentSettingsFile(name => configuration[name], environment) is { } file)
            builder.AddJsonFile(Path.GetFullPath(file), optional: false);
    }
}
