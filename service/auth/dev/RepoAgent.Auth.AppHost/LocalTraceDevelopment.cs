using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace RepoAgent.Auth.Development;

internal static class LocalTraceDevelopment
{
    public static IResourceBuilder<T> WithPrivateLocalTracing<T>(this IResourceBuilder<T> resource) where T : IResourceWithEnvironment
        // AddProject already supplies Aspire's allocated endpoint, private collector headers and service identity.
        // This callback runs after those defaults, replacing their content-capture and URL-redaction bypasses.
        => resource.WithEnvironment(context =>
        {
            // Aspire also defaults SDK retries to in_memory. Remove that opt-in; empty/false is not a supported SDK retry policy.
            context.EnvironmentVariables.Remove("OTEL_DOTNET_EXPERIMENTAL_OTLP_RETRY");
            context.EnvironmentVariables["REPOAGENT_LOCAL_TRACING"] = "1";
            context.EnvironmentVariables["OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT"] = "false";
            context.EnvironmentVariables["OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION"] = "false";
            context.EnvironmentVariables["OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION"] = "false";
        });
}
