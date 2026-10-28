using System.Diagnostics;
using Xunit;

namespace RepoAgent.Auth.Tests;

// What the broker's optional telemetry may export of a request span and a log, and that public trace context is never
// trusted.
public sealed class TelemetryPrivacyTests
{
    [Theory]
    [InlineData("/v2/devices/{enrollmentId}/installation-token", "POST", 401, "POST /v2/devices/{enrollmentId}/installation-token")]
    [InlineData(null, "GET", 404, "GET unmatched")]
    [InlineData("/v1/devices/{enrollmentId}", "DELETE", 204, "DELETE /v1/devices/{enrollmentId}")]
    [InlineData("/v1/enrollments/{id}", "PROPFIND", 405L, "OTHER /v1/enrollments/{id}")]
    [InlineData("/v1/enrollments", null, "200", "OTHER /v1/enrollments")]
    public void A_request_span_keeps_only_its_route_a_known_method_and_an_integer_status(string? route, string? method, object status, string name)
    {
        using var activity = Span(route, method, status);

        TelemetryPrivacy.Redact(activity);

        Assert.Equal(name, activity.DisplayName);
        var expected = new List<KeyValuePair<string, object?>> { new("http.route", route ?? "unmatched"), new("http.request.method", name.Split(' ')[0]) };
        if (status is int or long) expected.Add(new("http.response.status_code", status));
        Assert.Equal(expected, activity.TagObjects);
        Assert.Empty(activity.Baggage);
        Assert.Null(activity.TraceStateString);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Null(activity.StatusDescription);
        Assert.True(activity.Recorded);
    }

    [Theory]
    [InlineData("event")]
    [InlineData("link")]
    public void A_request_span_with_an_event_or_a_link_is_not_recorded(string addition)
    {
        using var activity = Span("/v1/enrollments", "POST", 401);
        if (addition == "event") activity.AddEvent(new ActivityEvent("private-event"));
        else activity.AddLink(new ActivityLink(new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded)));

        TelemetryPrivacy.Redact(activity);

        Assert.False(activity.Recorded);
    }

    [Theory]
    [InlineData("RepoAgent.Auth", false, "broker")]
    [InlineData("RepoAgent.Auth.private-category", true, "broker")]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics", false, "aspnet")]
    [InlineData("Azure.Core", true, "azure")]
    [InlineData("repoagent.auth", false, "other")]
    [InlineData("System.Net.Http.HttpClient", false, "other")]
    [InlineData("Microsoft.Extensions.Hosting.Internal.Host", false, "other")]
    [InlineData(null, true, "other")]
    public void A_log_exports_a_fixed_message_its_origin_and_its_event_ID(string? category, bool error, string origin)
    {
        var log = TelemetryPrivacy.Redact(category, error, 7);

        Assert.Equal((error ? "Broker error" : "Broker warning", origin), (log.Message, log.Origin));
        Assert.Equal(new KeyValuePair<string, object?>[] { new("event.id", 7), new("origin", origin) }, log.Attributes);
    }

    [Fact]
    public void Public_trace_context_and_baggage_are_never_extracted_or_injected()
    {
        var propagator = new BrokerContextPropagator();
        var carrier = new Dictionary<string, string>
        {
            ["traceparent"] = "00-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa-bbbbbbbbbbbbbbbb-01",
            ["tracestate"] = "private=state",
            ["baggage"] = "private=baggage",
            ["Request-Id"] = "|private.parent.",
            ["Correlation-Context"] = "private=context"
        };
        void Get(object? from, string name, out string? value, out IEnumerable<string>? values) =>
            (value, values) = (((Dictionary<string, string>)from!).GetValueOrDefault(name), null);

        propagator.ExtractTraceIdAndState(carrier, Get, out var traceId, out var traceState);
        Assert.Null(traceId);
        Assert.Null(traceState);
        Assert.Null(propagator.ExtractBaggage(carrier, Get));
        Assert.Empty(propagator.Fields);
        using var activity = Span("/v1/enrollments", "POST", 401);
        activity.Start();
        propagator.Inject(activity, carrier, (_, _, _) => Assert.Fail("Nothing is injected."));
    }

    // A server span with the tags, baggage, trace state and status description that must not leave the broker.
    private static Activity Span(string? route, string? method, object status)
    {
        var activity = new Activity("Microsoft.AspNetCore.Hosting.HttpRequestIn") { ActivityTraceFlags = ActivityTraceFlags.Recorded };
        if (route is not null) activity.SetTag("http.route", route);
        if (method is not null) activity.SetTag("http.request.method", method);
        activity.SetTag("http.response.status_code", status);
        activity.SetTag("url.full", "https://broker.example/v1/enrollments?secret=private-query");
        activity.SetTag("client.address", "203.0.113.7");
        activity.SetTag("user_agent.original", "private-agent");
        activity.SetBaggage("private", "private-baggage");
        activity.TraceStateString = "private=private-tracestate";
        activity.SetStatus(ActivityStatusCode.Error, "private-status-description");
        return activity;
    }
}
