using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RepoAgent.Auth.Tests;

// The broker's public route table, as the host maps it.
public sealed class BrokerEndpointsTests
{
    // Setup reaches v4; runtime stays v1 to v3, because the v4 policies reuse the runtime versions (ADR 0012).
    [Fact]
    public void Setup_routes_reach_v5_and_canonical_runtime_aliases_preserve_legacy_routes()
    {
        var builder = WebApplication.CreateSlimBuilder();
        // Registered only so the revocation handler's parameter binds as a service; no route runs here.
        builder.Services.AddSingleton<ControllerSelfRevocation>(_ => throw new InvalidOperationException());
        var app = builder.Build();

        BrokerEndpoints.Map(app);

        var mapped = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
            .Select(e => string.Join(',', e.Metadata.GetRequiredMetadata<IHttpMethodMetadata>().HttpMethods) + " " + e.RoutePattern.RawText);
        var runtime = Enumerable.Range(1, 3).SelectMany(v => new[] { $"DELETE /v{v}/devices/{{enrollmentId}}", $"POST /v{v}/devices/{{enrollmentId}}/installation-token",
            $"DELETE /v{v}/controller-enrollments/{{enrollmentId}}", $"POST /v{v}/controller-enrollments/{{enrollmentId}}/github-token" });
        var setup = Enumerable.Range(1, 5).SelectMany(v => new[] { $"POST /v{v}/enrollments", $"GET /v{v}/enrollments/{{id}}",
            $"POST /v{v}/enrollments/{{id}}/completion", v == 5 ? $"POST /v{v}/controller-enrollments/{{id}}/acknowledgment" : $"POST /v{v}/devices/{{id}}/acknowledgment" });
        Assert.Equal(runtime.Concat(setup).Order(StringComparer.Ordinal), mapped.Order(StringComparer.Ordinal));
    }
}
