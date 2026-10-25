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
        var log = TelemetryPrivacy.Redact(record.CategoryName, record.LogLevel >= LogLevel.Error, record.EventId.Id);
        record.Body = record.FormattedMessage = log.Message;
        record.CategoryName = log.Origin;
        record.EventId = new EventId(record.EventId.Id);
        record.Attributes = log.Attributes;
        record.Exception = null;
        record.TraceState = null;
    }
}
