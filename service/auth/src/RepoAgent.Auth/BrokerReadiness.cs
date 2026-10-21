using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace RepoAgent.Auth;

// Inspect registrations without constructing services or touching provider state.
public sealed class BrokerReadiness(IServiceProviderIsService services) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default) => Task.FromResult(
            services.IsService(typeof(EnrollmentService)) && services.IsService(typeof(InstallationTokenIssuer))
                ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy());
}
