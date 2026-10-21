using System.Net.Http.Headers;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Features;
using RepoAgent.Auth;

if (args.FirstOrDefault() == "cleanup")
{
    Environment.ExitCode = await BrokerCleanupCommand.RunAsync(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(services => BrokerOptions.Read(services.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(services =>
{
    var options = services.GetRequiredService<BrokerOptions>();
    var identity = options.ManagedIdentityClientId is { } clientId
        ? ManagedIdentityId.FromUserAssignedClientId(clientId) : ManagedIdentityId.SystemAssigned;
    return new TableClient(options.TableEndpoint, options.TableName, new ManagedIdentityCredential(identity),
        new TableClientOptions
        {
            Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(3) },
            Diagnostics = { IsLoggingContentEnabled = false }
        });
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DeviceRevocation>();
RequestProtection.Add(builder);
IssuanceServices.Add(builder);
builder.Services.AddHealthChecks().AddCheck<BrokerReadiness>("composition");
var app = builder.Build();
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
foreach (var runtimeVersion in new[] { 1, 2 })
{
app.MapDelete($"/v{runtimeVersion}/devices/{{deviceId}}", async (string deviceId, HttpContext context, DeviceRevocation revocation) =>
{
    var (rejection, secret) = ReadDeviceCredential(context);
    if (rejection != 0) return Results.StatusCode(rejection);
    return Results.StatusCode(await revocation.RevokeAsync(deviceId, secret, context.RequestAborted, runtimeVersion));
});
app.MapPost($"/v{runtimeVersion}/devices/{{deviceId}}/installation-token", async (string deviceId, HttpContext context) =>
{
    var (rejection, secret) = ReadDeviceCredential(context);
    if (rejection != 0) return Results.StatusCode(rejection);
    var issuer = context.RequestServices.GetService<InstallationTokenIssuer>();
    if (issuer is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    var result = await issuer.IssueAsync(deviceId, secret, context.RequestAborted, runtimeVersion);
    return result.Status == 200 ? Results.Json(result.Body) : Results.StatusCode(result.Status);
});
}
EnrollmentEndpoints.Map(app);
app.Run();

static (int Rejection, string? Secret) ReadDeviceCredential(HttpContext context)
{
    context.Response.Headers.CacheControl = "no-store";
    if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
        return (StatusCodes.Status400BadRequest, null);
    var headers = context.Request.Headers.Authorization;
    if (headers.Count != 1 || headers[0]?.Length > 135
        || !AuthenticationHeaderValue.TryParse(headers[0], out var authorization)
        || !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        return (StatusCodes.Status401Unauthorized, null);
    return (0, authorization.Parameter);
}

public partial class Program;
