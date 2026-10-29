namespace RepoAgent.LocalTelemetry;

/// <summary>The explicitly local Aspire collector binding; no environment or process resource detection.</summary>
public sealed class LocalTraceSettings
{
    public const string EnabledVariable = "REPOAGENT_LOCAL_TRACING";
    public Uri Endpoint { get; }
    public string Protocol { get; }
    internal string Headers { get; }
    public string Service { get; }
    public string Instance { get; }

    private LocalTraceSettings(Uri endpoint, string protocol, string headers, string service, string instance)
        => (Endpoint, Protocol, Headers, Service, Instance) = (endpoint, protocol, headers, service, instance);

    /// <summary>Invalid or missing optional configuration disables export, never the application.</summary>
    public static LocalTraceSettings? Read(Func<string, string?> configuration, string service)
    {
        if (configuration("OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY") is not null || configuration(EnabledVariable) != "1" || configuration("OTEL_SERVICE_NAME") != service || !Identifier(service) ||
            !Uri.TryCreate(configuration("OTEL_EXPORTER_OTLP_ENDPOINT"), UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") || !endpoint.IsLoopback || endpoint.UserInfo.Length != 0 ||
            endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            configuration("OTEL_EXPORTER_OTLP_PROTOCOL") is not ("grpc" or "http/protobuf") ||
            configuration("OTEL_EXPORTER_OTLP_HEADERS") is not { Length: > 0 and <= 1024 } headers ||
            !headers.StartsWith("x-otlp-api-key=", StringComparison.Ordinal)) return null;
        var key = Uri.UnescapeDataString(headers[15..]);
        if (key.Length is 0 or > 512 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '+' or '/' or '='))) return null;
        var attributes = configuration("OTEL_RESOURCE_ATTRIBUTES");
        if (attributes is null || attributes.Length > 4096) return null;
        var instances = attributes.Split(',').Where(value => value.StartsWith("service.instance.id=", StringComparison.Ordinal)).ToArray();
        if (instances.Length != 1 || !Identifier(instances[0][20..])) return null;
        return new(endpoint, configuration("OTEL_EXPORTER_OTLP_PROTOCOL")!, headers, service, instances[0][20..]);
    }

    /// <summary>Collector authority belongs to the supervisor, never an agent or authenticated tool.</summary>
    public static bool IsSupervisorVariable(string name) => name.StartsWith("OTEL_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("ASPIRE_", StringComparison.OrdinalIgnoreCase) || name.Equals(EnabledVariable, StringComparison.OrdinalIgnoreCase);

    public static void RemoveFrom(IDictionary<string, string?> environment)
    {
        foreach (var name in environment.Keys.Where(IsSupervisorVariable).ToArray()) environment.Remove(name);
    }

    static bool Identifier(string value) => value.Length is > 0 and <= 100 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
