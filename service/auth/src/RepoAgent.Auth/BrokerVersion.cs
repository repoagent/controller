using System.Reflection;
using System.Text.Json;

namespace RepoAgent.Auth;

/// <summary>The compiled service release, independent of enrollment protocols and retained-state schemas.</summary>
internal static partial class BrokerVersion
{
    private static readonly string Informational = typeof(BrokerVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    internal static string Version => Informational.Split('+', 2)[0];
    internal static string Source => Informational.Split('+', 2) is [_, var source] ? source : "unknown";

    internal static void Write(bool json) => Console.WriteLine(json
        ? JsonSerializer.Serialize(new { component = "broker", version = Version, source = Source })
        : $"RepoAgent credential broker {Version} (source {Source})");

    [LoggerMessage(EventId = 200, Level = LogLevel.Information, Message = "Credential broker version {Version}, source {Source}")]
    internal static partial void Log(ILogger logger, string version, string source);
}
