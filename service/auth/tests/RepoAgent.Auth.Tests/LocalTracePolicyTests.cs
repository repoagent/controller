using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using RepoAgent.LocalTelemetry;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class LocalTracePolicyTests
{
    [Fact]
    public void Optional_configuration_failure_does_not_escape_the_controller_lifetime()
        => Assert.Null(AgentTrace.Configure(_ => throw new InvalidOperationException("private-configuration"), "test"));

    [Theory]
    [InlineData("initialize", "agent.initialize")]
    [InlineData("thread/start", "agent.session.start")]
    [InlineData("thread/resume", "agent.session.resume")]
    [InlineData("turn/start", "agent.turn.start")]
    [InlineData("private-method/transcript", null)]
    public void Agent_RPC_measurements_use_only_closed_operation_names(string method, string? name)
        => Assert.Equal(name, AgentTrace.Operation(method));

    [Theory]
    [InlineData("safe", true)]
    [InlineData("event", false)]
    [InlineData("link", false)]
    [InlineData("unknown", false)]
    public void Agent_measurements_cannot_export_private_attributes_or_payloads(string addition, bool recorded)
    {
        using var activity = new Activity("agent.initialize") { ActivityTraceFlags = ActivityTraceFlags.Recorded };
        activity.SetTag("url.full", "https://private/?secret=private");
        activity.SetTag("process.command_args", "private-arguments");
        activity.SetBaggage("private", "private");
        activity.TraceStateString = "private=private";
        activity.SetStatus(ActivityStatusCode.Error, "private-error");
        if (addition == "event") activity.AddEvent(new ActivityEvent("private-transcript"));
        if (addition == "link") activity.AddLink(new ActivityLink(default));
        if (addition == "unknown") activity.DisplayName = "private-method";

        AgentTrace.Redact(activity);

        Assert.Equal(recorded, activity.Recorded);
        Assert.Empty(activity.TagObjects);
        Assert.Empty(activity.Baggage);
        Assert.Null(activity.TraceStateString);
        Assert.Null(activity.StatusDescription);
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Production", false)]
    public void The_local_broker_exporter_is_composed_only_in_Development(string environment, bool enabled)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(Configuration());
        BrokerTelemetry.AddHttp(builder);
        using var services = builder.Services.BuildServiceProvider();

        Assert.Equal(enabled, services.GetService<LocalTraceLifetime>() is not null);
    }

    [Fact]
    public void An_explicit_local_collector_uses_only_the_named_service_instance()
    {
        var values = Configuration();
        var settings = LocalTraceSettings.Read(name => values.GetValueOrDefault(name), "auth");

        Assert.NotNull(settings);
        Assert.Equal(new Uri("http://localhost:4317"), settings.Endpoint);
        Assert.Equal("grpc", settings.Protocol);
        Assert.Equal("auth", settings.Service);
        Assert.Equal("auth-instance", settings.Instance);
        Assert.DoesNotContain("private", settings.ToString());
    }

    [Theory]
    [InlineData("REPOAGENT_LOCAL_TRACING", null)]
    [InlineData("REPOAGENT_LOCAL_TRACING", "false")]
    [InlineData("OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY", "disk")]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "https://public.example")]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317/private")]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "http://user@localhost:4317")]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317/?secret=private")]
    [InlineData("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317/#private")]
    [InlineData("OTEL_EXPORTER_OTLP_PROTOCOL", "invalid")]
    [InlineData("OTEL_EXPORTER_OTLP_HEADERS", null)]
    [InlineData("OTEL_EXPORTER_OTLP_HEADERS", "authorization=private")]
    [InlineData("OTEL_EXPORTER_OTLP_HEADERS", "x-otlp-api-key=private%0d%0aextra")]
    [InlineData("OTEL_SERVICE_NAME", "another-service")]
    [InlineData("OTEL_RESOURCE_ATTRIBUTES", "service.instance.id=/private/path")]
    public void Missing_or_unsafe_optional_collector_configuration_disables_export(string key, string? value)
    {
        var values = Configuration();
        values[key] = value;

        Assert.Null(LocalTraceSettings.Read(name => values.GetValueOrDefault(name), "auth"));
    }

    [Theory]
    [InlineData("OTEL_EXPORTER_OTLP_HEADERS", true)]
    [InlineData("OTEL_EXPORTER_OTLP_TRACES_HEADERS", true)]
    [InlineData("OTEL_RESOURCE_ATTRIBUTES", true)]
    [InlineData("ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL", true)]
    [InlineData("REPOAGENT_LOCAL_TRACING", true)]
    [InlineData("PATH", false)]
    [InlineData("CODEX_HOME", false)]
    [InlineData("SSL_CERT_DIR", false)]
    public void Only_supervisor_telemetry_configuration_is_removed_from_agent_tools(string name, bool removed)
        => Assert.Equal(removed, LocalTraceSettings.IsSupervisorVariable(name));

    static Dictionary<string, string?> Configuration() => new()
    {
        ["REPOAGENT_LOCAL_TRACING"] = "1",
        ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317",
        ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "grpc",
        ["OTEL_EXPORTER_OTLP_HEADERS"] = "x-otlp-api-key=private-canary",
        ["OTEL_SERVICE_NAME"] = "auth",
        ["OTEL_RESOURCE_ATTRIBUTES"] = "private=private-canary,service.instance.id=auth-instance"
    };
}
