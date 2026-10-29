using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace RepoAgent.LocalTelemetry;

/// <summary>Best-effort local batches. Overflow and export failure drop telemetry, never application work.</summary>
public static class LocalTraceExport
{
    public static LocalTraceLifetime? TryStart(TracerProviderBuilder builder, LocalTraceSettings settings, string version)
    {
        if (Environment.GetEnvironmentVariable("OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY") is not null) return null;
        try { return new LocalTraceLifetime(AddTo(builder, settings, version).Build()); }
        catch (Exception) { return null; }
    }

    public static TracerProviderBuilder AddTo(TracerProviderBuilder builder, LocalTraceSettings settings, string version)
        => builder.SetResourceBuilder(ResourceBuilder.CreateEmpty().AddService(settings.Service, serviceVersion: version,
            autoGenerateServiceInstanceId: false, serviceInstanceId: settings.Instance))
            .SetSampler(new AlwaysOnSampler())
            .AddOtlpExporter(options =>
            {
                options.Protocol = settings.Protocol == "grpc" ? OtlpExportProtocol.Grpc : OtlpExportProtocol.HttpProtobuf;
                options.Endpoint = settings.Protocol == "grpc" ? settings.Endpoint : new Uri(settings.Endpoint, "v1/traces");
                options.Headers = settings.Headers;
                options.TimeoutMilliseconds = 1000;
                options.ExportProcessorType = ExportProcessorType.Batch;
                options.BatchExportProcessorOptions = new BatchExportProcessorOptions<Activity>
                {
                    MaxQueueSize = 256,
                    MaxExportBatchSize = 32,
                    ScheduledDelayMilliseconds = 1000,
                    ExporterTimeoutMilliseconds = 1000
                };
            });
}

/// <summary>Finite best-effort shutdown, followed by SDK disposal; neither can replace an application result.</summary>
public sealed class LocalTraceLifetime(TracerProvider provider) : IDisposable
{
    int disposed;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { provider.Shutdown(1000); }
        catch (Exception) { }
        try { provider.Dispose(); }
        catch (Exception) { }
    }
}
