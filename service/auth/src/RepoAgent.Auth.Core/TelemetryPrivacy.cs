using System.Diagnostics;

namespace RepoAgent.Auth;

// What the broker's optional telemetry may export: of a request, its matched route, a known method and its status; of a
// log, its severity, event ID and origin. Public correlation is never trusted.
public static class TelemetryPrivacy
{
    // Keeps a request span's route ("unmatched" without one), its method when it is a standard one ("OTHER" otherwise)
    // and an integer status. Every other tag, the baggage, the trace state and the status description are cleared. A
    // span with events or links is not recorded: they have no approved export meaning.
    public static void Redact(Activity activity)
    {
        if (activity.Events.Any() || activity.Links.Any())
        {
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
            return;
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

    // A log as exported: a fixed message for its severity, and its origin and event ID as its only attributes.
    public static RedactedLog Redact(string? category, bool error, int eventId)
    {
        var origin = category switch
        {
            var name when name?.StartsWith("RepoAgent.Auth", StringComparison.Ordinal) == true => "broker",
            var name when name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true => "aspnet",
            var name when name?.StartsWith("Azure", StringComparison.Ordinal) == true => "azure",
            _ => "other"
        };
        return new(error ? "Broker error" : "Broker warning", origin, [new("event.id", eventId), new("origin", origin)]);
    }
}

public sealed record RedactedLog(string Message, string Origin, IReadOnlyList<KeyValuePair<string, object?>> Attributes);

// Hosting asks this before it creates a request's span: it extracts no trace context and no baggage, and injects none.
public sealed class BrokerContextPropagator : DistributedContextPropagator
{
    public override IReadOnlyCollection<string> Fields => Array.Empty<string>();
    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter) { }
    public override void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState)
        => (traceId, traceState) = (null, null);
    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter) => null;
}
