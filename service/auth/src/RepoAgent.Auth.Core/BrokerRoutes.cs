using System.Globalization;
using System.Text.Json;

namespace RepoAgent.Auth;

// An Authorization value as the HTTP adapter parsed it: its length, and its scheme and parameter. Scheme is null when
// the value isn't a credential.
public sealed record PresentedAuthorization(int Length, string? Scheme, string? Parameter);

// A request to a public route: its Authorization values, whether it can carry a body, how to read the body and the
// caller's cancellation. Body(count) is the adapter's read of at most count bytes, on the caller's cancellation; the core
// asks for it only when the route takes a body, and decides what the bytes are.
public sealed record BrokerRequest(IReadOnlyList<PresentedAuthorization> Authorization, bool CanHaveBody,
    Func<int, Task<byte[]>> Body, CancellationToken Aborted);

// The installation-token operation of a composed issuer: InstallationTokenIssuer.IssueAsync.
public delegate Task<IssuanceResult> TokenIssuance(string enrollmentId, string? secret, CancellationToken caller, int runtimeVersion);

// What each public route decides around the operation it calls: the credential and body it accepts, in the order it
// checks them, and which answers carry a body. The routes write every answer uncached, and its body as web JSON.
public static class BrokerRoutes
{
    private const int BodyLimit = 8192;

    // DELETE /v{runtimeVersion}/devices/{enrollmentId}. A body is refused before the credential is read.
    public static async Task<IssuanceResult> RevokeAsync(BrokerRequest request, string enrollmentId, DeviceRevocation revocation,
        int runtimeVersion)
    {
        if (request.CanHaveBody) return new(400);
        if (!Bearer(request, out var secret)) return new(401);
        return new(await revocation.RevokeAsync(enrollmentId, secret, request.Aborted, runtimeVersion));
    }

    // POST /v{runtimeVersion}/devices/{enrollmentId}/installation-token. Only a 200 carries the issuer's body.
    public static async Task<IssuanceResult> IssueAsync(BrokerRequest request, string enrollmentId,
        Func<TokenIssuance?> issuer, int runtimeVersion)
    {
        if (request.CanHaveBody) return new(400);
        if (!Bearer(request, out var secret)) return new(401);
        if (issuer() is not { } issue) return new(503);
        var result = await issue(enrollmentId, secret, request.Aborted, runtimeVersion);
        return result.Status == 200 ? result : new(result.Status);
    }

    // POST /v{setupVersion}/enrollments. Setup v1 takes the approval environment's body; v2 to v4 the approval variable's.
    public static async Task<IssuanceResult> StartAsync(BrokerRequest request, Func<EnrollmentService?> enrollment, int setupVersion)
    {
        if (Secret(request) is not { } secret) return new(401);
        if (enrollment() is not { } service) return new(503);
        try
        {
            using var body = await BodyAsync(request);
            return setupVersion == 1
                ? await service.StartAsync(Read<EnrollmentRequest>(body), secret, request.Aborted)
                : await service.StartRepositoryVariableAsync(Read<RepositoryVariableEnrollmentRequest>(body), secret, request.Aborted, setupVersion);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { return new(400); }
    }

    // GET /v{setupVersion}/enrollments/{id}.
    public static Task<IssuanceResult> StatusAsync(BrokerRequest request, string id, Func<EnrollmentService?> enrollment, int setupVersion) =>
        BodilessAsync(request, enrollment, (service, secret) => service.StatusAsync(id, secret, request.Aborted, setupVersion));

    // POST /v{setupVersion}/enrollments/{id}/completion.
    public static Task<IssuanceResult> CompleteAsync(BrokerRequest request, string id, Func<EnrollmentService?> enrollment, int setupVersion) =>
        BodilessAsync(request, enrollment, (service, secret) => service.CompleteAsync(id, secret, request.Aborted, setupVersion));

    // POST /v{setupVersion}/devices/{enrollmentId}/acknowledgment. The body is exactly {"completionId": "..."}.
    public static async Task<IssuanceResult> AcknowledgeAsync(BrokerRequest request, string enrollmentId,
        Func<EnrollmentService?> enrollment, int setupVersion)
    {
        if (Secret(request) is not { } secret) return new(401);
        if (enrollment() is not { } service) return new(503);
        try
        {
            using var body = await BodyAsync(request);
            if (body.RootElement.EnumerateObject().ToArray() is not [{ Name: "completionId", Value.ValueKind: JsonValueKind.String } completion])
                return new(400);
            return await service.AcknowledgeAsync(enrollmentId, completion.Value.GetString()!, secret, request.Aborted, setupVersion);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException) { return new(400); }
    }

    // The Retry-After of a request the request budget rejects: the limiter's delay in whole seconds, at least 1.
    public static string RetryAfter(TimeSpan? delay) =>
        (delay is { } wait ? Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)) : 1).ToString(CultureInfo.InvariantCulture);

    private static async Task<IssuanceResult> BodilessAsync(BrokerRequest request, Func<EnrollmentService?> enrollment,
        Func<EnrollmentService, string, Task<IssuanceResult>> operation)
    {
        if (Secret(request) is not { } secret) return new(401);
        if (request.CanHaveBody) return new(400);
        return enrollment() is { } service ? await operation(service, secret) : new(503);
    }

    // Exactly one Authorization value, at most 135 characters, with the Bearer scheme in any case. The parameter may be
    // missing or malformed: the operation decides.
    private static bool Bearer(BrokerRequest request, out string? credential)
    {
        credential = null;
        if (request.Authorization is not [{ Length: <= 135, Scheme: { } scheme } authorization]
            || !string.Equals(scheme, "Bearer", StringComparison.OrdinalIgnoreCase)) return false;
        credential = authorization.Parameter;
        return true;
    }

    // A Bearer credential in the secret grammar: the setup secret, or the controller secret that acknowledges.
    private static string? Secret(BrokerRequest request) =>
        Bearer(request, out var credential) && DeviceSecret.Hash(credential) is not null ? credential : null;

    // At most 8 KiB of JSON at most 4 deep, whose root is an object that names no property twice in any case. The adapter
    // reads one byte past the limit, so a longer body is known to be too large without reading it all.
    private static async Task<JsonDocument> BodyAsync(BrokerRequest request)
    {
        var body = await request.Body(BodyLimit + 1);
        if (body.Length > BodyLimit) throw new InvalidDataException();
        var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 4 });
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject()) if (!names.Add(property.Name)) throw new JsonException();
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    private static T Read<T>(JsonDocument body) => body.RootElement.Deserialize<T>(JsonSerializerOptions.Web) ?? throw new JsonException();
}
