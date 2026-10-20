using System.Reflection;
using Azure;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class BrokerTableBoundsTests
{
    // Bind the internal concrete write boundary without adding a test-only production API.
    // No private state or methods are inspected; the claim is rejection before Table I/O.
    private static readonly Func<TableClient, IReadOnlyList<TableTransactionAction>, CancellationToken,
        Task<Response<IReadOnlyList<Response>>>> Submit = typeof(IssuanceStore).Assembly
        .GetType("RepoAgent.Auth.BrokerTable", throwOnError: true)!
        .GetMethod("SubmitAsync", BindingFlags.NonPublic | BindingFlags.Static)!
        .CreateDelegate<Func<TableClient, IReadOnlyList<TableTransactionAction>, CancellationToken,
            Task<Response<IReadOnlyList<Response>>>>>();

    [Theory]
    [InlineData("properties")]
    [InlineData("row-bytes")]
    [InlineData("action-count")]
    [InlineData("aggregate-bytes")]
    public async Task Oversized_write_is_rejected_before_any_Table_request_or_partial_allocation(string budget)
    {
        await using var storage = new RevocationFixture();
        await storage.InitializeAsync();
        var transport = new ObservedTransport();
        var actions = new List<TableTransactionAction>();
        var count = budget == "action-count" ? 33 : budget == "aggregate-bytes" ? 10 : 1;
        for (var i = 0; i < count; i++)
        {
            var row = new TableEntity("bounds", "allocation:" + i);
            if (budget == "properties")
                for (var j = 0; j < 129; j++) row["Field" + j] = j;
            if (budget == "row-bytes") row["Payload"] = new string('x', 66 * 1024);
            if (budget == "aggregate-bytes") row["Payload"] = new string('x', 50 * 1024);
            actions.Add(new(TableTransactionActionType.Add, row));
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => Submit(storage.CreateClient(transport), actions, TestContext.Current.CancellationToken));
        Assert.Equal(0, transport.Calls);
        var allocated = new List<TableEntity>();
        await foreach (var row in storage.Table.QueryAsync<TableEntity>(r => r.PartitionKey == "bounds", cancellationToken: TestContext.Current.CancellationToken))
            allocated.Add(row);
        Assert.Empty(allocated);

        // A small valid allocation crosses the same boundary and really commits.
        await Submit(storage.CreateClient(transport), [new(TableTransactionActionType.Add, new TableEntity("bounds", "valid"))], TestContext.Current.CancellationToken);
        Assert.Equal(1, transport.Calls);
        Assert.True((await storage.Table.GetEntityIfExistsAsync<TableEntity>("bounds", "valid", cancellationToken: TestContext.Current.CancellationToken)).HasValue);
    }
}
