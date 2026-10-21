using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.Exporter;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace RepoAgent.Auth;

/// <summary>Optional, privacy-limited observations; never part of broker authorization.</summary>
public static class BrokerTelemetry
{
    public static void AddHttp(WebApplicationBuilder builder)
    {
        builder.Logging.AddFilter(null, LogLevel.Warning);
        builder.Logging.AddFilter<OpenTelemetryLoggerProvider>(null, LogLevel.Warning);
        builder.Services.AddSingleton<DistributedContextPropagator>(new BrokerContextPropagator());
        Sdk.SetDefaultTextMapPropagator(new CompositeTextMapPropagator([]));
        Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED", "true");
        try
        {
            var options = CreateOptions(builder.Configuration, builder.Configuration["Broker:ManagedIdentityClientId"]);
            if (options is null) return;
            builder.Services.PostConfigure<AzureMonitorExporterOptions>(value => CopyOptions(options, value));
            builder.Services.Configure<OpenTelemetryLoggerOptions>(value =>
            {
                value.IncludeScopes = false;
                value.IncludeFormattedMessage = false;
            });
            builder.Services.AddOpenTelemetry()
                .ConfigureResource(resource => resource.Clear().AddService("repoagent-auth", serviceInstanceId: "repoagent-auth"))
                .WithTracing(tracing => tracing
                    .AddAspNetCoreInstrumentation(value =>
                    {
                        value.RecordException = false;
                        value.Filter = context => context.Request.Path != "/health/live" && context.Request.Path != "/health/ready";
                    })
                    .AddProcessor(new BrokerActivityPrivacy())
                    .AddAzureMonitorTraceExporter())
                .WithLogging(logging => logging.AddProcessor(new BrokerLogPrivacy()).AddAzureMonitorLogExporter());
        }
        catch (Exception)
        {
            // Malformed optional monitoring must not change broker composition or expose configuration.
            // The deployment preflight rejects malformed hosted monitoring before activation.
        }
    }

    internal static AzureMonitorExporterOptions? CreateOptions(IConfiguration configuration, string? clientId)
    {
        var connection = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        if (string.IsNullOrEmpty(connection)) return null;
        var fields = connection.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split('=', 2)).ToDictionary(value => value[0], value => value[1], StringComparer.OrdinalIgnoreCase);
        if (fields.Keys.Any(field => field is not ("InstrumentationKey" or "IngestionEndpoint" or "LiveEndpoint" or "ApplicationId"))
            || !fields.TryGetValue("InstrumentationKey", out var key) || !Guid.TryParse(key, out _)
            || !fields.TryGetValue("IngestionEndpoint", out var endpoint)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.Host.EndsWith(".in.applicationinsights.azure.com", StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/" || !Guid.TryParse(clientId, out _))
            throw new InvalidOperationException("Invalid monitoring configuration.");
        return new AzureMonitorExporterOptions
        {
            ConnectionString = connection,
            Credential = new ManagedIdentityCredential(new ManagedIdentityCredentialOptions(ManagedIdentityId.FromUserAssignedClientId(clientId))
            { Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(1) } }),
            SamplingRatio = 0.01F, TracesPerSecond = null, EnableTraceBasedLogsSampler = false,
            EnableLiveMetrics = false, EnableStandardMetrics = false, EnablePerformanceCounters = false,
            DisableOfflineStorage = true, Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(1) }
        };
    }

    private static void CopyOptions(AzureMonitorExporterOptions source, AzureMonitorExporterOptions target)
    {
        target.ConnectionString = source.ConnectionString;
        target.Credential = source.Credential;
        target.SamplingRatio = source.SamplingRatio;
        target.TracesPerSecond = null;
        target.EnableTraceBasedLogsSampler = false;
        target.EnableLiveMetrics = target.EnableStandardMetrics = target.EnablePerformanceCounters = false;
        target.DisableOfflineStorage = true;
        target.Retry.MaxRetries = 0;
        target.Retry.NetworkTimeout = TimeSpan.FromSeconds(1);
    }

    /// <summary>One completed invocation, including failures. SDK options are supplied by the trusted composition.</summary>
    public static void ExportCleanupOnce(JsonElement summary, AzureMonitorExporterOptions options)
    {
        try
        {
            Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED", "true");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
            if (options.Credential is null) return; // Never fall back to instrumentation-key authorization.
            options.Credential = new DeadlineCredential(options.Credential, deadline.Token);
            options.Retry.MaxRetries = 0;
            options.Retry.NetworkTimeout = TimeSpan.FromSeconds(1);
            options.DisableOfflineStorage = true;
            options.EnableLiveMetrics = options.EnableStandardMetrics = options.EnablePerformanceCounters = false;
            using var meter = new Meter("RepoAgent.Auth.Cleanup");
            var reader = new BaseExportingMetricReader(new AzureMonitorMetricExporter(options))
            { TemporalityPreference = MetricReaderTemporalityPreference.Delta };
            using var provider = Sdk.CreateMeterProviderBuilder().AddMeter(meter.Name)
                .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("repoagent-auth-cleanup", serviceInstanceId: "repoagent-auth-cleanup"))
                .AddReader(reader).Build();
            void Record(string name, double? value)
            {
                if (value is { } number) meter.CreateHistogram<double>("repoagent.auth.cleanup." + name).Record(number);
            }
            static double? Number(JsonElement item) => item.ValueKind == JsonValueKind.Number ? item.GetDouble() : null;
            var ages = summary.GetProperty("cursorAndCycleAges").EnumerateArray().ToArray();
            var cursor = ages.Select(age => Number(age.GetProperty("cursorAgeSeconds"))).Max();
            var cycle = ages.Select(age => Number(age.GetProperty("cycleAgeSeconds"))).Max();
            var overdue = Number(summary.GetProperty("oldestObservedOverduePayloadAgeSeconds"));
            Record("completed", 1);
            Record("failures", summary.GetProperty("failures").GetInt32());
            Record("unknown_deadlines", summary.GetProperty("unknownDeadlineCount").GetInt32());
            Record("cursor_age_max_seconds", cursor);
            Record("cycle_age_max_seconds", cycle);
            Record("cycle_age_unknown", ages.Count(age => age.GetProperty("cycleAgeSeconds").ValueKind == JsonValueKind.Null));
            Record("overdue_payload_age_max_seconds", overdue);
            Record("stale_cursor_with_overdue_observation", cursor >= 900 && overdue >= 900 ? 1 : 0);
            reader.Collect(2000); // This alone is not a deadline: the credential adapter also bounds SDK refresh.
        }
        catch (Exception)
        {
            // Preserve the already-written summary and authoritative exit code, including on disposal failure.
        }
    }

    private sealed class DeadlineCredential(TokenCredential inner, CancellationToken deadline) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline, cancellationToken);
            return inner.GetToken(context, linked.Token);
        }
        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline, cancellationToken);
            return await inner.GetTokenAsync(context, linked.Token).ConfigureAwait(false);
        }
    }
}
