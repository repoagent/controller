using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;

namespace RepoAgent.Auth;

internal static class EnrollmentEndpoints
{
    public static void Map(WebApplication app)
    {
        MapVersion(app, 1);
        MapVersion(app, 2);
        MapVersion(app, 3);
    }
    private static void MapVersion(WebApplication app, int version)
    {
        app.MapPost($"/v{version}/enrollments", async (HttpContext context) =>
        {
            var secret = Credential(context);
            if (secret is null) return Results.StatusCode(401);
            var service = context.RequestServices.GetService<EnrollmentService>();
            if (service is null) return Results.StatusCode(503);
            try
            {
                using var body = await BodyAsync(context);
                var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                return version == 1
                    ? Result(await service.StartAsync(body.RootElement.Deserialize<EnrollmentRequest>(json) ?? throw new JsonException(), secret, context.RequestAborted))
                    : Result(await service.StartRepositoryVariableAsync(body.RootElement.Deserialize<RepositoryVariableEnrollmentRequest>(json) ?? throw new JsonException(), secret, context.RequestAborted, version));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or BadHttpRequestException) { return Results.StatusCode(400); }
        });
        app.MapGet($"/v{version}/enrollments/{{id}}", async (string id, HttpContext context) =>
        {
            var secret = Credential(context);
            if (secret is null) return Results.StatusCode(401);
            if (HasBody(context)) return Results.StatusCode(400);
            var service = context.RequestServices.GetService<EnrollmentService>();
            return service is null ? Results.StatusCode(503) : Result(await service.StatusAsync(id, secret, context.RequestAborted, version));
        });
        app.MapPost($"/v{version}/enrollments/{{id}}/completion", async (string id, HttpContext context) =>
        {
            var secret = Credential(context);
            if (secret is null) return Results.StatusCode(401);
            if (HasBody(context)) return Results.StatusCode(400);
            var service = context.RequestServices.GetService<EnrollmentService>();
            return service is null ? Results.StatusCode(503) : Result(await service.CompleteAsync(id, secret, context.RequestAborted, version));
        });
        app.MapPost($"/v{version}/devices/{{id}}/acknowledgment", async (string id, HttpContext context) =>
        {
            var secret = Credential(context);
            if (secret is null) return Results.StatusCode(401);
            var service = context.RequestServices.GetService<EnrollmentService>();
            if (service is null) return Results.StatusCode(503);
            try
            {
                using var body = await BodyAsync(context);
                var properties = body.RootElement.EnumerateObject().ToArray();
                if (properties.Length != 1 || properties[0].Name != "completionId" || properties[0].Value.ValueKind != JsonValueKind.String) return Results.StatusCode(400);
                return Result(await service.AcknowledgeAsync(id, properties[0].Value.GetString()!, secret, context.RequestAborted, version));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or BadHttpRequestException or InvalidOperationException) { return Results.StatusCode(400); }
        });
    }
    private static bool HasBody(HttpContext context) => context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true;
    private static string? Credential(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        var headers = context.Request.Headers.Authorization;
        if (headers.Count != 1 || headers[0]?.Length > 135 || !AuthenticationHeaderValue.TryParse(headers[0], out var auth)
            || !string.Equals(auth.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || DeviceSecret.Hash(auth.Parameter) is null) return null;
        return auth.Parameter;
    }
    private static async Task<JsonDocument> BodyAsync(HttpContext context)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        while (true)
        {
            var count = await context.Request.Body.ReadAsync(bytes, context.RequestAborted);
            if (count == 0) break;
            if (buffer.Length + count > 8192) throw new InvalidDataException();
            buffer.Write(bytes, 0, count);
        }
        var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject()) if (!names.Add(property.Name)) throw new JsonException();
            return document;
        }
        catch { document.Dispose(); throw; }
    }
    private static IResult Result(IssuanceResult result) => result.Body is null ? Results.StatusCode(result.Status) : Results.Json(result.Body, statusCode: result.Status);
}
