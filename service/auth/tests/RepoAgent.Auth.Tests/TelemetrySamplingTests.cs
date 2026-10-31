using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class TelemetrySamplingTests
{
    [Theory]
    [InlineData(200, false, false, false)]
    [InlineData(200, false, true, true)]
    [InlineData(400, false, false, false)]
    [InlineData(400, false, true, true)]
    [InlineData(404, true, false, false)]
    [InlineData(429, false, false, false)]
    [InlineData(429, true, true, true)]
    [InlineData(500, false, false, true)]
    [InlineData(200, true, false, true)]
    public void Completed_errors_bypass_sampling_while_routine_requests_keep_one_percent(int status, bool error, bool sampled, bool retained)
    {
        using var exporter = new Requests();
        using var failures = new BrokerRequestExportProcessor(exporter, errors: true);
        using var routineExporter = new Requests();
        using var routine = new BrokerRequestExportProcessor(routineExporter, errors: false);
        using var activity = new Activity("request");
        activity.SetIdFormat(ActivityIdFormat.W3C)
            .SetParentId(ActivityTraceId.CreateFromString((sampled ? "00000000000000010000000000000001" : "ffffffffffffffff0000000000000001").AsSpan()),
                ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded).Start();
        activity.ActivityTraceFlags = ActivityTraceFlags.Recorded;
        activity.SetTag("http.response.status_code", status);
        if (error) activity.SetStatus(ActivityStatusCode.Error);
        activity.Stop();
        failures.OnEnd(activity);
        routine.OnEnd(activity);
        failures.ForceFlush();
        routine.ForceFlush();
        Assert.Equal(retained ? 1 : 0, exporter.Count + routineExporter.Count);
        Assert.Equal(status >= 500 || error && status < 400 ? 1 : 0, exporter.Count);
    }

    [Fact]
    public void Every_error_and_exception_exports_with_private_content_removed_even_without_a_sampled_trace()
    {
        using var exporter = new Logs();
        using var processor = new BrokerLogExportProcessor(exporter);
        using var factory = LoggerFactory.Create(builder => builder.ClearProviders().SetMinimumLevel(LogLevel.Information)
            .AddOpenTelemetry(options => options.AddProcessor(new BrokerLogPrivacy()).AddProcessor(processor)));
        var logger = factory.CreateLogger("RepoAgent.Auth.Private");
        using var activity = new Activity("unsampled");
        activity.Start();
        activity.ActivityTraceFlags = ActivityTraceFlags.None;
        var exception = new Azure.RequestFailedException(503, "private-exception");
        for (var i = 0; i < 100; i++)
        {
            logger.Log(LogLevel.Error, new EventId(7), "private-message", null, static (state, _) => state);
            logger.Log(LogLevel.Information, new EventId(8), "private-message", exception, static (state, _) => state);
        }
        processor.ForceFlush();
        Assert.Equal(200, exporter.Items.Count);
        Assert.All(exporter.Items, item =>
        {
            Assert.Equal("Broker error", item.Body);
            Assert.DoesNotContain("private", item.Exception ?? "", StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(100, exporter.Items.Count(item => item.Exception is not null));
        Assert.All(exporter.Items.Where(item => item.Exception is not null), item =>
        {
            Assert.Contains(new KeyValuePair<string, object?>("exception.kind", "azure-request"), item.Attributes);
            Assert.Contains(new KeyValuePair<string, object?>("azure.status", 503), item.Attributes);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Routine_logs_follow_the_broker_trace_selection(bool sampled)
    {
        using var exporter = new Logs();
        using var processor = new BrokerLogExportProcessor(exporter);
        using var factory = LoggerFactory.Create(builder => builder.ClearProviders().SetMinimumLevel(LogLevel.Information)
            .AddOpenTelemetry(options => options.AddProcessor(new BrokerLogPrivacy()).AddProcessor(processor)));
        var logger = factory.CreateLogger("RepoAgent.Auth");
        using var activity = new Activity("routine");
        activity.SetIdFormat(ActivityIdFormat.W3C).SetParentId(
            ActivityTraceId.CreateFromString((sampled ? "00000000000000010000000000000001" : "ffffffffffffffff0000000000000001").AsSpan()),
            ActivitySpanId.CreateRandom(), ActivityTraceFlags.None).Start();
        for (var i = 0; i < 100; i++)
            logger.Log(LogLevel.Information, new EventId(9), "private-message", null, static (state, _) => state);
        processor.ForceFlush();
        Assert.Equal(sampled ? 100 : 0, exporter.Items.Count);
    }

    private sealed class Requests : BaseExporter<Activity>
    {
        public int Count { get; private set; }
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var _ in batch) Count++;
            return ExportResult.Success;
        }
    }

    private sealed class Logs : BaseExporter<LogRecord>
    {
        public List<(string? Body, string? Exception, KeyValuePair<string, object?>[] Attributes)> Items { get; } = [];
        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var record in batch) Items.Add((record.Body, record.Exception?.ToString(), record.Attributes?.ToArray() ?? []));
            return ExportResult.Success;
        }
    }
}
