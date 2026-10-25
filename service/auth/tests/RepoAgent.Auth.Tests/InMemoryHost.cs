using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace RepoAgent.Auth.Tests;

// The broker host with an in-memory broker state in place of the Table adapter, so no request reaches a store. The
// Table settings are only there for startup validation.
internal static class InMemoryHost
{
    public static WebApplicationFactory<Program> Create(IBrokerState state, Dictionary<string, string?>? settings = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            var configured = new Dictionary<string, string?>
            {
                ["Broker:DeploymentEpoch"] = InMemoryBroker.Epoch,
                ["Broker:TableEndpoint"] = "https://example.table.core.windows.net",
                ["Broker:TableName"] = "brokerstate"
            };
            foreach (var setting in settings ?? []) configured[setting.Key] = setting.Value;
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(configured));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IBrokerState>();
                services.AddSingleton(state);
            });
        });

    // The host over the in-memory broker's state, clock, GitHub, secret protection and limits, so an operation the
    // core ran directly and a request the host answers see the same broker.
    public static WebApplicationFactory<Program> Create(InMemoryBroker f) =>
        Create(f.State, new() { ["Broker:BrokerOrigin"] = f.BrokerOrigin?.AbsoluteUri, ["Broker:DeploymentEpoch"] = f.DeploymentEpoch })
            .WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(f.Clock);
                services.AddSingleton<ISecretProtection>(f.Secrets);
                services.AddSingleton(f.Limits);
                services.AddSingleton<IGitHubTokens>(f.GitHub);
                services.AddSingleton<IAppInstallations>(f.GitHub);
                services.AddSingleton<IControllerApprovals>(f.GitHub);
                services.AddSingleton(IssuanceServices.CreateStore);
                services.AddSingleton<InstallationTokenIssuer>();
                services.AddSingleton(IssuanceServices.CreateEnrollmentService);
            }));
}

// The broker state as a host's storage calls reach it: counts every call, and can hold one until the test releases it,
// observing the caller's cancellation as a store request would.
internal sealed class ObservedState(IBrokerState inner) : IBrokerState
{
    private int calls;
    public int Calls => calls;
    public Func<CancellationToken, Task>? Before { get; init; }

    public async Task<Versioned<T>?> ReadAsync<T>(StateKey<T> key, CancellationToken ct) where T : BrokerRecord, new()
    {
        await Observe(ct);
        return await inner.ReadAsync(key, ct);
    }

    public async Task<IReadOnlyList<StateVersion?>> CommitAsync(StateTransaction transaction, CancellationToken ct)
    {
        await Observe(ct);
        return await inner.CommitAsync(transaction, ct);
    }

    public async Task<StatePage<T>> ListAsync<T>(StateRange<T> range, string after, string? continuation, CancellationToken ct)
        where T : BrokerRecord, new()
    {
        await Observe(ct);
        return await inner.ListAsync(range, after, continuation, ct);
    }

    private async Task Observe(CancellationToken ct)
    {
        Interlocked.Increment(ref calls);
        if (Before is not null) await Before(ct);
    }
}
