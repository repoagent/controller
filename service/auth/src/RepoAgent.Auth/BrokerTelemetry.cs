using System.Diagnostics;
using System.Diagnostics.Metrics;
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
    private static readonly Action<ILogger, Exception?> CleanupError =
        LoggerMessage.Define(LogLevel.Error, new EventId(1), "Broker cleanup failure");
    public static void AddHttp(WebApplicationBuilder builder)
    {
        builder.Logging.AddFilter(null, LogLevel.Warning);
        builder.Logging.AddFilter<OpenTelemetryLoggerProvider>(null, LogLevel.Information);
        builder.Services.AddSingleton<DistributedContextPropagator>(new BrokerContextPropagator());
        Sdk.SetDefaultTextMapPropagator(new CompositeTextMapPropagator([]));
        Environment.SetEnvironmentVariable("APPLICATIONINSIGHTS_STATSBEAT_DISABLED", "true");
        if (TryAddLocal(builder)) return;
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
                .ConfigureResource(resource => resource.Clear().AddService("repoagent-auth", serviceVersion: BrokerVersion.Version, serviceInstanceId: "repoagent-auth"))
                .WithTracing(tracing => tracing
                    .SetSampler(new AlwaysOnSampler())
                    .AddAspNetCoreInstrumentation(value =>
                    {
                        value.RecordException = false;
                        value.Filter = context => context.Request.Path != "/health/live" && context.Request.Path != "/health/ready";
                    })
                    .AddProcessor(new BrokerActivityPrivacy())
                    .AddProcessor(new BrokerRequestExportProcessor(new AzureMonitorTraceExporter(options), errors: true))
                    .AddProcessor(new BrokerRequestExportProcessor(new AzureMonitorTraceExporter(RoutineOptions(options)), errors: false)))
                .WithMetrics(metrics => metrics.AddMeter(BrokerRejections.MeterName).AddAzureMonitorMetricExporter())
                .WithLogging(logging => logging.AddProcessor(new BrokerLogPrivacy())
                    .AddProcessor(new BrokerLogExportProcessor(new AzureMonitorLogExporter(options))));
        }
        catch (Exception)
        {
            // Malformed optional monitoring must not change broker composition or expose configuration.
            // The deployment preflight rejects malformed hosted monitoring before activation.
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The local TracerProvider owns its privacy processor and exporter; its registered lifetime owns provider disposal.")]
    private static bool TryAddLocal(WebApplicationBuilder builder)
    {
        try
        {
            if (!builder.Environment.IsDevelopment() ||
                RepoAgent.LocalTelemetry.LocalTraceSettings.Read(name => builder.Configuration[name], "auth") is not { } settings) return false;
            var tracing = Sdk.CreateTracerProviderBuilder()
                .AddAspNetCoreInstrumentation(options =>
                {
                    options.RecordException = false;
                    options.Filter = context => context.Request.Path != "/health/live" && context.Request.Path != "/health/ready";
                }).AddProcessor(new BrokerActivityPrivacy());
            if (RepoAgent.LocalTelemetry.LocalTraceExport.TryStart(tracing, settings, BrokerVersion.Version) is not { } lifetime) return false;
            builder.Services.AddSingleton(_ => lifetime);
            builder.Services.AddHostedService<LocalTraceOwner>();
            return true;
        }
        catch (Exception) { return false; }
    }

    private sealed class LocalTraceOwner(RepoAgent.LocalTelemetry.LocalTraceLifetime lifetime) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken)
        {
            lifetime.Dispose();
            return Task.CompletedTask;
        }
    }

    internal static AzureMonitorExporterOptions? CreateOptions(IConfiguration configuration, string? clientId)
    {
        if (BrokerSettings.Monitoring(configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"], clientId) is not { } monitoring) return null;
        return new AzureMonitorExporterOptions
        {
            ConnectionString = monitoring.Connection,
            Credential = new ManagedIdentityCredential(new ManagedIdentityCredentialOptions(ManagedIdentityId.FromUserAssignedClientId(monitoring.ClientId))
            { Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(1) } }),
            SamplingRatio = 1F,
            TracesPerSecond = null,
            EnableTraceBasedLogsSampler = false,
            EnableLiveMetrics = false,
            EnableStandardMetrics = false,
            EnablePerformanceCounters = false,
            DisableOfflineStorage = true,
            Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(1) }
        };
    }

    private static AzureMonitorExporterOptions RoutineOptions(AzureMonitorExporterOptions source)
    {
        var target = new AzureMonitorExporterOptions();
        CopyOptions(source, target);
        target.SamplingRatio = 0.01F;
        return target;
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
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA2000", Justification = "MeterProvider owns its reader, which owns the exporter; disposing them independently would duplicate ownership.")]
    public static void ExportCleanupOnce(IReadOnlyList<CleanupObservation> observations, AzureMonitorExporterOptions options,
        IReadOnlyList<Exception>? exceptions = null)
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
            if (exceptions is { Count: > 0 })
            {
                try { ExportCleanupErrors(exceptions, options); }
                catch (Exception) { } // Failed log export must not prevent the alert metric attempt.
            }
            using var meter = new Meter("RepoAgent.Auth.Cleanup");
            var reader = new BaseExportingMetricReader(new AzureMonitorMetricExporter(options))
            { TemporalityPreference = MetricReaderTemporalityPreference.Delta };
            using var provider = Sdk.CreateMeterProviderBuilder().AddMeter(meter.Name)
                .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("repoagent-auth-cleanup", serviceVersion: BrokerVersion.Version, serviceInstanceId: "repoagent-auth-cleanup"))
                .AddReader(reader).Build();
            foreach (var observation in observations) meter.CreateHistogram<double>(observation.Metric).Record(observation.Value);
            reader.Collect(2000); // This alone is not a deadline: the credential adapter also bounds SDK refresh.
        }
        catch (Exception)
        {
            // Preserve the already-written summary and authoritative exit code, including on disposal failure.
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The registered logger provider owns its processor and exporter; the service container disposes the provider.")]
    private static void ExportCleanupErrors(IReadOnlyList<Exception> exceptions, AzureMonitorExporterOptions options)
    {
        var services = new ServiceCollection();
        var processor = new BrokerLogExportProcessor(new AzureMonitorLogExporter(options));
        services.AddLogging(logging => logging.ClearProviders().AddOpenTelemetry(logging => logging
            .SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService("repoagent-auth-cleanup", serviceVersion: BrokerVersion.Version, serviceInstanceId: "repoagent-auth-cleanup"))
            .AddProcessor(new BrokerLogPrivacy())
            .AddProcessor(processor)));
        using var container = services.BuildServiceProvider();
        var factory = container.GetRequiredService<ILoggerFactory>();
        var logger = factory.CreateLogger("RepoAgent.Auth.Cleanup");
        foreach (var exception in exceptions)
            CleanupError(logger, exception);
        processor.ForceFlush(2000);
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
