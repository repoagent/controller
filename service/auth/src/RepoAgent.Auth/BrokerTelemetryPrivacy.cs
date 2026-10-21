using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace RepoAgent.Auth;

// Hosting invokes this before creating a server Activity. Public correlation is not trusted.
internal sealed class BrokerContextPropagator : DistributedContextPropagator
{
    public override IReadOnlyCollection<string> Fields => Array.Empty<string>();
    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter) { }
    public override void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState)
        => (traceId, traceState) = (null, null);
    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter) => null;
}

internal sealed class BrokerActivityPrivacy : BaseProcessor<Activity>
{
    public override void OnEnd(Activity activity)
    {
        if (activity.Events.Any() || activity.Links.Any())
        {
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
            return; // Unexpected event/link payloads have no approved export meaning.
        }
        var route = activity.GetTagItem("http.route") as string ?? "unmatched";
        var method = activity.GetTagItem("http.request.method") as string;
        method = method is "GET" or "POST" or "DELETE" or "PUT" or "PATCH" or "HEAD" or "OPTIONS" ? method : "OTHER";
        var status = activity.GetTagItem("http.response.status_code");
        foreach (var tag in activity.TagObjects.ToArray()) activity.SetTag(tag.Key, null);
        foreach (var baggage in activity.Baggage.ToArray()) activity.SetBaggage(baggage.Key, null);
        activity.TraceStateString = null;
        activity.SetStatus(activity.Status);
        activity.DisplayName = method + " " + route;
        activity.SetTag("http.route", route);
        activity.SetTag("http.request.method", method);
        if (status is int or long) activity.SetTag("http.response.status_code", status);
    }
}

internal sealed class BrokerLogPrivacy : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord record)
    {
        var origin = record.CategoryName switch
        {
            var name when name?.StartsWith("RepoAgent.Auth", StringComparison.Ordinal) == true => "broker",
            var name when name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true => "aspnet",
            var name when name?.StartsWith("Azure", StringComparison.Ordinal) == true => "azure",
            _ => "other"
        };
        record.Body = record.FormattedMessage = record.LogLevel >= LogLevel.Error ? "Broker error" : "Broker warning";
        record.CategoryName = origin;
        record.EventId = new EventId(record.EventId.Id);
        record.Attributes = [new("event.id", record.EventId.Id), new("origin", origin)];
        record.Exception = null;
        record.TraceState = null;
    }
}
