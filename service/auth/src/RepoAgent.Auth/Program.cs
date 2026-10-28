using Azure.Data.Tables;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RepoAgent.Auth;

if (args is ["--version"] or ["--version", "--json"])
{
    BrokerVersion.Write(args.Length == 2);
    return;
}

if (args.FirstOrDefault() == "cleanup")
{
    Environment.ExitCode = await BrokerCleanupCommand.RunAsync(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);
DevelopmentSettings.Add(builder.Configuration, builder.Configuration, builder.Environment.EnvironmentName);
BrokerTelemetry.AddHttp(builder);
builder.Services.AddSingleton(services => BrokerStorage.Options(
    services.GetRequiredService<IConfiguration>(), builder.Environment.EnvironmentName));
builder.Services.AddSingleton(services =>
{
    return BrokerStorage.Create(services.GetRequiredService<IConfiguration>(), builder.Environment.EnvironmentName,
        new TableClientOptions
        {
            Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(3) },
            Diagnostics = { IsLoggingContentEnabled = false }
        });
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IBrokerState>(services => new TableBrokerState(services.GetRequiredService<TableClient>()));
builder.Services.AddSingleton(services => new DeviceRevocation(services.GetRequiredService<IBrokerState>(),
    services.GetRequiredService<BrokerOptions>().DeploymentEpoch, services.GetRequiredService<TimeProvider>()));
RequestProtection.Add(builder);
var ready = BrokerSettings.Ready(IssuanceServices.Add(builder));
builder.Services.AddHealthChecks().AddCheck("composition", () => ready ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy());
var app = builder.Build();
BrokerVersion.Log(app.Logger, BrokerVersion.Version, BrokerVersion.Source);
_ = app.Services.GetRequiredService<BrokerOptions>(); // Fail startup on invalid security/budget configuration.
app.UseRouting();
app.UseRateLimiter();
app.UseRequestTimeouts();
app.MapGet("/health/live", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok();
}).DisableRateLimiting().DisableRequestTimeout();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    ResponseWriter = (context, _) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        return Task.CompletedTask;
    }
}).DisableRateLimiting().DisableRequestTimeout();
BrokerEndpoints.Map(app);
app.Run();
