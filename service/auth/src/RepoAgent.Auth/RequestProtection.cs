using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.RateLimiting;

namespace RepoAgent.Auth;

internal static class RequestProtection
{
    public static void Add(WebApplicationBuilder builder)
    {
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Limits.MaxRequestBodySize = 64 * 1024;
            server.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            server.Limits.MaxRequestHeaderCount = 64;
            server.Limits.MaxConcurrentConnections = 128;
            server.Limits.MaxConcurrentUpgradedConnections = 0;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
            // Keep Kestrel's default minimum request/response data rates for slow clients.
        });
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Azure", LogLevel.Warning);
        builder.Services.AddRateLimiter(_ => { });
        builder.Services.AddOptions<RateLimiterOptions>().Configure<BrokerOptions>((limiter, options) =>
        {
            // One constant partition: attacker-controlled addresses/credentials never allocate limiter state.
            limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetConcurrencyLimiter(
                    "broker", _ => new ConcurrencyLimiterOptions
                    { PermitLimit = options.ConcurrentRequestLimit, QueueLimit = 0 })),
                PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetFixedWindowLimiter(
                    "broker", _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.RequestLimitPerMinute,
                        QueueLimit = 0,
                        Window = TimeSpan.FromMinutes(1),
                        AutoReplenishment = true
                    })));
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = BrokerRoutes.RetryAfter(
                    context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay) ? delay : null);
                return ValueTask.CompletedTask;
            };
        });
        builder.Services.AddRequestTimeouts();
        builder.Services.AddOptions<RequestTimeoutOptions>().Configure<BrokerOptions>((timeouts, options) =>
            timeouts.DefaultPolicy = new RequestTimeoutPolicy
            {
                Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
                TimeoutStatusCode = StatusCodes.Status503ServiceUnavailable
            });
    }
}
