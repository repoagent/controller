using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace RepoAgent.LocalTelemetry;

/// <summary>Closed RPC acknowledgement measurements, never model-turn duration or transcript content.</summary>
public static class AgentTrace
{
    public const string SourceName = "RepoAgent.Controller.Local";
    static readonly ActivitySource source = new(SourceName);

    public static IDisposable? Start(string method)
    {
        var name = Operation(method);
        if (name is null) return null;
        try { return source.StartActivity(name) is { } activity ? new Measurement(activity) : null; }
        catch (Exception) { return null; }
    }

    public static string? Operation(string method) => method switch
    {
        "initialize" => "agent.initialize",
        "thread/start" => "agent.session.start",
        "thread/resume" => "agent.session.resume",
        "turn/start" => "agent.turn.start",
        _ => null
    };

    /// <summary>Only a closed name and status may leave the controller, even under an ambient Activity listener.</summary>
    public static void Redact(Activity activity)
    {
        if (activity.DisplayName is not ("agent.initialize" or "agent.session.start" or "agent.session.resume" or "agent.turn.start") ||
            activity.Events.Any() || activity.Links.Any()) activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        foreach (var tag in activity.TagObjects.ToArray()) activity.SetTag(tag.Key, null);
        foreach (var item in activity.Baggage.ToArray()) activity.SetBaggage(item.Key, null);
        activity.TraceStateString = null;
        activity.SetStatus(activity.Status);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "The built TracerProvider owns the privacy processor and batch exporter; independently disposing a transferred processor would duplicate ownership.")]
    public static IDisposable? Configure(Func<string, string?> configuration, string version)
    {
        try
        {
            if (LocalTraceSettings.Read(configuration, "controller") is not { } settings) return null;
            var builder = Sdk.CreateTracerProviderBuilder().AddSource(SourceName).AddProcessor(new Privacy());
            return LocalTraceExport.TryStart(builder, settings, version);
        }
        catch (Exception) { return null; }
    }

    sealed class Privacy : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity) => Redact(activity);
    }

    sealed class Measurement(Activity activity) : IDisposable
    {
        public void Dispose()
        {
            try { activity.Dispose(); }
            catch (Exception) { }
        }
    }

}
