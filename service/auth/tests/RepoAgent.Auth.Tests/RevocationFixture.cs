using System.Security.Cryptography;
using System.Text;
using Azure.Core.Pipeline;
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
    public TableClient CreateClient(HttpPipelineTransport? transport = null)
    {
        var options = new TableClientOptions { Retry = { MaxRetries = 0 } };
        if (transport is not null) options.Transport = transport;
        var endpoint = Environment.GetEnvironmentVariable("AUTH_TEST_TABLE_ENDPOINT") ?? "http://127.0.0.1:10002/devstoreaccount1";
        return new TableClient(new Uri(endpoint), TableName, new TableSharedKeyCredential("devstoreaccount1",
            "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="), options);
    }

    public async ValueTask InitializeAsync()
    {
        await Table.CreateAsync();
        // Offline provisioning, only after successful creation of this dedicated fresh table.
        await Table.AddEntityAsync(new TableEntity("global:enrollment-v1", "quota")
        {
            ["RetentionSchemaVersion"] = 1L, ["CandidateCount"] = 0L,
            ["RetainedEnrollmentCount"] = 0L, ["Count"] = 0L
        });
        await Table.AddEntityAsync(new TableEntity("global:issuance-v1", "quota")
        {
            ["RetentionSchemaVersion"] = 1L, ["RetainedIssuanceCount"] = 0L,
            ["Window"] = 0L, ["Admissions"] = 0L, ["Reads"] = 0L,
            ["Signs"] = 0L, ["Mints"] = 0L, ["Slots"] = 0L
        });
    }
    public async ValueTask DisposeAsync() => await Table.DeleteAsync();

    public WebApplicationFactory<Program> CreateHost(HttpPipelineTransport? transport = null, Dictionary<string, string?>? settings = null) => new WebApplicationFactory<Program>()
        .WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Broker:DeploymentEpoch"] = Epoch,
                    ["Broker:TableEndpoint"] = "https://example.table.core.windows.net",
                    ["Broker:TableName"] = TableName
                }.Concat(settings ?? new Dictionary<string, string?>())));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TableClient>();
                services.AddSingleton(CreateClient(transport));
            });
        });

    public async Task<EnrolledDevice> EnrollAsync(string? suppliedDeviceId = null, string? suppliedSecret = null)
    {
        var deviceId = suppliedDeviceId ?? Base64Url(RandomNumberGenerator.GetBytes(24));
        var secretBytes = RandomNumberGenerator.GetBytes(32);
        var secret = suppliedSecret ?? Base64Url(secretBytes);
        var table = Table;
        var created = DateTimeOffset.UtcNow;
        var pendingId = await ChargeOfflineEnrollmentAsync(RepositoryId, deviceId, created);
        var generation = Guid.NewGuid().ToString("N");
        var ledger = new TableEntity("global:issuance-v1", "operation:one")
        {
            ["OperationId"] = "one", ["Generation"] = generation, ["RepositoryId"] = RepositoryId,
            ["DeviceId"] = deviceId, ["Purpose"] = "runtime", ["Epoch"] = Epoch,
            ["ReservedAt"] = created, ["PayloadPurgeAt"] = created.AddHours(24),
            ["GlobalSlot"] = 0L, ["AttemptRowKey"] = "attempt:one", ["RowCredits"] = 4L,
            ["ApprovalKind"] = "environment-variable", ["SetupVersion"] = 1L,
            ["PolicyVersion"] = IssuancePolicy.Version
        };
        var slot = new TableEntity("global:issuance-v1", "slot:000");
        foreach (var field in new[] { "OperationId", "Generation", "RepositoryId", "Purpose", "Epoch", "ReservedAt",
            "PayloadPurgeAt", "GlobalSlot", "ApprovalKind", "SetupVersion", "PolicyVersion" }) slot[field] = ledger[field];
        var quota = (await table.GetEntityAsync<TableEntity>("global:issuance-v1", "quota")).Value;
        quota["RetainedIssuanceCount"] = 1L;
        quota["Slots"] = 1L;
        await table.SubmitTransactionAsync(new[]
        {
            new TableTransactionAction(TableTransactionActionType.UpdateReplace, quota, quota.ETag),
            new TableTransactionAction(TableTransactionActionType.Add, ledger),
            new TableTransactionAction(TableTransactionActionType.Add, slot)
        });
        await table.AddEntityAsync(new TableEntity("locator:" + deviceId, "locator")
        {
            ["RepositoryId"] = RepositoryId, ["DeviceId"] = deviceId, ["PendingId"] = pendingId
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
                ["SecretHash"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
                ["ExpiresAt"] = DateTimeOffset.UtcNow.AddHours(1), ["State"] = "active",
                ["OutstandingTokenCount"] = 1L
            }),
            new TableTransactionAction(TableTransactionActionType.Add, new TableEntity(Partition, "attempt:one")
            {
                ["RepositoryId"] = RepositoryId, ["DeviceId"] = deviceId,
                ["Epoch"] = Epoch, ["State"] = "outstanding", ["Purpose"] = "runtime",
                ["OperationId"] = "one", ["Generation"] = generation, ["ReservedAt"] = created,
                ["PayloadPurgeAt"] = created.AddHours(24), ["GlobalSlot"] = 0L,
                ["ApprovalKind"] = "environment-variable", ["SetupVersion"] = 1L,
                ["PolicyVersion"] = IssuancePolicy.Version
            })
        });
        return new(deviceId, secret);
    }

    // Explicit offline import, before any host admission; this does not claim an HTTP enrollment occurred.
    internal async Task<string> ChargeOfflineEnrollmentAsync(string repository, string device, DateTimeOffset created)
    {
        var id = Guid.NewGuid().ToString("N");
        var pending = repository + "_" + id;
        var quota = (await Table.GetEntityAsync<TableEntity>("global:enrollment-v1", "quota")).Value;
        quota["CandidateCount"] = quota.GetInt64("CandidateCount")!.Value + 1;
        quota["RetainedEnrollmentCount"] = quota.GetInt64("RetainedEnrollmentCount")!.Value + 1;
        await Table.SubmitTransactionAsync(new[]
        {
            new TableTransactionAction(TableTransactionActionType.UpdateReplace, quota, quota.ETag),
            new TableTransactionAction(TableTransactionActionType.Add, new TableEntity("global:enrollment-v1", "repository:" + repository)
            { ["RepositoryId"] = repository, ["PendingCount"] = 0L }),
            new TableTransactionAction(TableTransactionActionType.Add, new TableEntity("global:enrollment-v1", "pending:" + id)
            {
                ["RepositoryId"] = repository, ["PendingId"] = pending, ["DeviceId"] = device, ["Epoch"] = Epoch,
                ["Generation"] = Guid.NewGuid().ToString("N"), ["RowCredits"] = 8L, ["CreatedAt"] = created,
                ["Deadline"] = created.AddMinutes(15), ["PayloadPurgeAt"] = created.AddMinutes(15).AddHours(24)
            })
        });
        return pending;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record EnrolledDevice(string DeviceId, string Secret);
