using System.Buffers.Binary;
using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace RepoAgent.Auth;

// Decide only after status is known. Separate exporters retain the correct 100%/1% sampling weights.
internal sealed class BrokerRequestExportProcessor(BaseExporter<Activity> exporter, bool errors)
    : BatchActivityExportProcessor(exporter)
{
    public override void OnEnd(Activity data)
    {
        if (!data.Recorded) return; // The privacy processor can reject an unsafe span.
        var status = data.GetTagItem("http.response.status_code") switch
        {
            int value => value,
            long value => value,
            _ => 0L
        };
        // A client response stays in the routine sample even if instrumentation marks its span as an error.
        var error = status is not (>= 400 and < 500) && (status >= 500 || data.Status == ActivityStatusCode.Error);
        if (error != errors || !error && !BrokerTelemetrySampling.KeepRoutine(data.TraceId)) return;
        base.OnEnd(data);
    }
}

internal sealed class BrokerLogExportProcessor(BaseExporter<LogRecord> exporter) : BatchLogRecordExportProcessor(exporter)
{
    public override void OnEnd(LogRecord data)
    {
        if (data.LogLevel >= LogLevel.Error || data.Exception is not null ||
            (data.TraceId == default ? Random.Shared.Next(100) == 0 : BrokerTelemetrySampling.KeepRoutine(data.TraceId)))
            base.OnEnd(data);
    }
}

internal static class BrokerTelemetrySampling
{
    internal static bool KeepRoutine(ActivityTraceId traceId)
    {
        Span<byte> bytes = stackalloc byte[16];
        traceId.CopyTo(bytes);
        return BinaryPrimitives.ReadUInt64BigEndian(bytes) < ulong.MaxValue / 100;
    }
}
