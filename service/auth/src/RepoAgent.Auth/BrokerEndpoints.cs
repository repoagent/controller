using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http.Features;

namespace RepoAgent.Auth;

// The public routes. Each hands the core its request (BrokerRoutes decides) and writes the answer uncached.
internal static class BrokerEndpoints
{
    public static void Map(WebApplication app)
    {
        // Runtime v1: frozen non-clone, v2: frozen clone, v3: repository-settings profiles.
        foreach (var version in new[] { 1, 2, 3 })
        {
            app.MapDelete($"/v{version}/devices/{{deviceId}}", (string deviceId, HttpContext context, DeviceRevocation revocation) =>
                AnswerAsync(context, request => BrokerRoutes.RevokeAsync(request, deviceId, revocation, version)));
            app.MapPost($"/v{version}/devices/{{deviceId}}/installation-token", (string deviceId, HttpContext context) =>
                AnswerAsync(context, request => BrokerRoutes.IssueAsync(request, deviceId, () =>
                    context.RequestServices.GetService<InstallationTokenIssuer>() is { } issuer ? issuer.IssueAsync : null, version)));
        }
        // Setup v1: approval environment, v2: repository Agent variable, v3: repository Actions variable,
        // v4: repository Agent variable for every profile. Its policies reuse the runtime versions above.
        foreach (var version in new[] { 1, 2, 3, 4 })
        {
            // The explicit return type keeps this handler off the RequestDelegate overload, which would discard the answer.
            app.MapPost($"/v{version}/enrollments", Task<IResult> (HttpContext context) =>
                AnswerAsync(context, request => BrokerRoutes.StartAsync(request, Service<EnrollmentService>(context), version)));
            app.MapGet($"/v{version}/enrollments/{{id}}", (string id, HttpContext context) =>
                AnswerAsync(context, request => BrokerRoutes.StatusAsync(request, id, Service<EnrollmentService>(context), version)));
            app.MapPost($"/v{version}/enrollments/{{id}}/completion", (string id, HttpContext context) =>
                AnswerAsync(context, request => BrokerRoutes.CompleteAsync(request, id, Service<EnrollmentService>(context), version)));
            app.MapPost($"/v{version}/devices/{{id}}/acknowledgment", (string id, HttpContext context) =>
                AnswerAsync(context, request => BrokerRoutes.AcknowledgeAsync(request, id, Service<EnrollmentService>(context), version)));
        }
    }

    // Resolved only when the route asks, after the credential.
    private static Func<T?> Service<T>(HttpContext context) where T : class => () => context.RequestServices.GetService<T>();

    private static async Task<IResult> AnswerAsync(HttpContext context, Func<BrokerRequest, Task<IssuanceResult>> route)
    {
        context.Response.Headers.CacheControl = "no-store";
        var request = new BrokerRequest([.. context.Request.Headers.Authorization.Select(Present)],
            context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true,
            count => ReadAsync(context.Request.Body, count, context.RequestAborted), context.RequestAborted);
        IssuanceResult answer;
        try { answer = await route(request); }
        catch (BadHttpRequestException) { return Results.StatusCode(StatusCodes.Status400BadRequest); } // A body Kestrel can't deliver.
        return answer.Body is null ? Results.StatusCode(answer.Status) : Results.Json(answer.Body, statusCode: answer.Status);
    }

    // At most count bytes of the body, on the request's cancellation, so the request timeout also stops a stalled body.
    private static async Task<byte[]> ReadAsync(Stream body, int count, CancellationToken aborted)
    {
        var buffer = new byte[count];
        var length = 0;
        for (int read; length < count && (read = await body.ReadAsync(buffer.AsMemory(length), aborted)) > 0;) length += read;
        return buffer[..length];
    }

    private static PresentedAuthorization Present(string? value) => AuthenticationHeaderValue.TryParse(value, out var parsed)
        ? new(value!.Length, parsed.Scheme, parsed.Parameter) : new(value?.Length ?? 0, null, null);
}
