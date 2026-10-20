using System.Net.Http.Headers;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.AspNetCore.Http.Features;
using RepoAgent.Auth;

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
var app = builder.Build();
_ = app.Services.GetRequiredService<BrokerOptions>(); // Fail startup on invalid security/budget configuration.
app.UseRouting();
app.UseRateLimiter();
app.UseRequestTimeouts();
app.MapGet("/health/live", () => Results.Ok()).DisableRateLimiting().DisableRequestTimeout();
app.MapGet("/health/ready", () => Results.StatusCode(503)).DisableRateLimiting().DisableRequestTimeout();
app.MapDelete("/v1/devices/{deviceId}", async (string deviceId, HttpContext context, DeviceRevocation revocation) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
        return Results.StatusCode(StatusCodes.Status400BadRequest);
    var headers = context.Request.Headers.Authorization;
    if (headers.Count != 1 || headers[0]?.Length > 135
        || !AuthenticationHeaderValue.TryParse(headers[0], out var authorization)
        || !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        return Results.StatusCode(StatusCodes.Status401Unauthorized);
    return Results.StatusCode(await revocation.RevokeAsync(deviceId, authorization.Parameter, context.RequestAborted));
});
app.MapPost("/v1/devices/{deviceId}/installation-token", async (string deviceId, HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
        return Results.StatusCode(StatusCodes.Status400BadRequest);
    var headers = context.Request.Headers.Authorization;
    if (headers.Count != 1 || headers[0]?.Length > 135
        || !AuthenticationHeaderValue.TryParse(headers[0], out var authorization)
        || !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        return Results.StatusCode(StatusCodes.Status401Unauthorized);
    var issuer = context.RequestServices.GetService<InstallationTokenIssuer>();
    if (issuer is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    var result = await issuer.IssueAsync(deviceId, authorization.Parameter, context.RequestAborted);
    return result.Status == 200 ? Results.Json(result.Body) : Results.StatusCode(result.Status);
});
app.Run();

public partial class Program;
