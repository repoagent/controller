using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace RepoAgent.Auth;

// The exporters' processors over the core's redaction (TelemetryPrivacy).
internal sealed class BrokerActivityPrivacy : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity) => TelemetryPrivacy.Redact(activity);
}

internal sealed class BrokerLogPrivacy : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord record)
    {
        var exception = record.Exception is not null;
        var kind = record.Exception switch
        {
            StateConflict => "conflict",
            Azure.RequestFailedException => "azure-request",
            InvalidDataException => "invalid-state",
            OperationCanceledException => "cancelled",
            TimeoutException => "timeout",
            null => "none",
            _ => "other"
        };
        var status = record.Exception is Azure.RequestFailedException azure ? azure.Status : (int?)null;
        var log = TelemetryPrivacy.Redact(record.CategoryName, record.LogLevel >= LogLevel.Error || exception, record.EventId.Id);
        record.Body = record.FormattedMessage = log.Message;
        record.CategoryName = log.Origin;
        record.EventId = new EventId(record.EventId.Id);
        record.Attributes = exception
            ? [.. log.Attributes, new("exception.kind", kind), new("azure.status", status)]
            : log.Attributes;
        // Keep an exception occurrence without exporting its message, stack, inner exception or data.
        record.Exception = exception ? new InvalidOperationException("Broker exception") : null;
        record.TraceState = null;
    }
}
