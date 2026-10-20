using System.Security.Cryptography;
using Azure.Data.Tables;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class RevocationFixture : IAsyncLifetime
{
    public const string Epoch = "test-epoch";
    public const string RepositoryId = "123456";
    public const string Partition = "repository:" + RepositoryId;
    public string TableName { get; } = "revocation" + Guid.NewGuid().ToString("N");
    public TableClient Table => CreateClient();

    // Only tests use shared development credentials. Production receives an SDK client via DI.
    public TableClient CreateClient() => new("UseDevelopmentStorage=true", TableName,
        new TableClientOptions { Retry = { MaxRetries = 0 } });

    public async ValueTask InitializeAsync() => await Table.CreateAsync();
    public async ValueTask DisposeAsync() => await Table.DeleteAsync();

    public WebApplicationFactory<Program> CreateHost() => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Broker:DeploymentEpoch"] = Epoch,
                    ["Broker:TableEndpoint"] = "https://example.table.core.windows.net",
                    ["Broker:TableName"] = TableName
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TableClient>();
                services.AddSingleton(CreateClient());
            });
        });

    public async Task<EnrolledDevice> EnrollAsync()
    {
        var deviceId = Base64Url(RandomNumberGenerator.GetBytes(24));
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var secret = Base64Url(secretBytes);
        var table = Table;
        await table.AddEntityAsync(new TableEntity("locator:" + deviceId, "locator")
        {
            ["RepositoryId"] = RepositoryId, ["DeviceId"] = deviceId
        });
        await table.SubmitTransactionAsync(new[]
        {
            new TableTransactionAction(TableTransactionActionType.Add, new TableEntity(Partition, "gate")
            {
                ["RepositoryId"] = RepositoryId, ["Epoch"] = Epoch, ["OwnerDeviceId"] = deviceId,
                ["State"] = "active", ["Sequence"] = 7L, ["OutstandingTokenCount"] = 1L
            }),
            new TableTransactionAction(TableTransactionActionType.Add, new TableEntity(Partition, "device:" + deviceId)
            {
                ["RepositoryId"] = RepositoryId, ["DeviceId"] = deviceId, ["Epoch"] = Epoch,
                ["SecretHash"] = Convert.ToHexString(SHA256.HashData(secretBytes)),
                ["ExpiresAt"] = DateTimeOffset.UtcNow.AddHours(1), ["State"] = "active",
                ["OutstandingTokenCount"] = 1L
            }),
            new TableTransactionAction(TableTransactionActionType.Add, new TableEntity(Partition, "attempt:one")
            {
                ["RepositoryId"] = RepositoryId, ["DeviceId"] = deviceId,
                ["Epoch"] = Epoch, ["State"] = "outstanding"
            })
        });
        return new(deviceId, secret);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record EnrolledDevice(string DeviceId, string Secret);
