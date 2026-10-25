using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

// The Table adapter's listing, through the Table SDK in process: the one query a range page sends, the continuation it
// hands back, and the pages it refuses. Each listed row is mapped only when the caller reads it.
public sealed class TableBrokerStateListingTests
{
    private const string Scope = "global:enrollment-v1";
    private static readonly StateRange<CandidateRepository> Candidates = new(Scope, "repository:", "repository;");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_page_is_one_bounded_query_after_the_last_key_and_its_rows_are_read_only_when_asked()
    {
        var table = new ListedTable(_ => (
            [Row("repository:111", "W/\"datetime'2026-10-25T01%3A00%3A00.0000000Z'\""),
             Row("repository:222", "W/\"datetime'2026-10-25T02%3A00%3A00.0000000Z'\"", unknown: true)],
            ("global:enrollment-v1", "repository:333")));
        var state = new TableBrokerState(Client(table));

        var page = await state.ListAsync(Candidates, "repository:000", null, Token);

        var query = Assert.Single(table.Queries);
        Assert.Equal("PartitionKey eq 'global:enrollment-v1' and RowKey ge 'repository:' and RowKey lt 'repository;' and RowKey gt 'repository:000'",
            query["$filter"]);
        Assert.Equal("32", query["$top"]);
        Assert.Equal(["repository:111", "repository:222"], page.Records.Select(record => record.Key.Name));
        Assert.All(page.Records, record => Assert.Equal(Scope, record.Key.Scope));
        var first = page.Records[0].Read();
        Assert.Equal("111", first.Value.RepositoryId);
        Assert.Equal(new StateVersion("W/\"datetime'2026-10-25T01%3A00%3A00.0000000Z'\""), first.Version);
        Assert.Throws<BrokerRecordUnreadable>(() => page.Records[1].Read());
        Assert.NotNull(page.Continuation);

        var next = await state.ListAsync(Candidates, "repository:000", page.Continuation, Token);

        Assert.Equal("repository:333", table.Queries[1]["NextRowKey"]);
        Assert.Equal(query["$filter"], table.Queries[1]["$filter"]);
        Assert.Empty(next.Records);
        Assert.Null(next.Continuation);
    }

    [Theory]
    [InlineData("before-range")]
    [InlineData("past-range")]
    [InlineData("not-after-last-key")]
    [InlineData("other-scope")]
    [InlineData("no-version")]
    [InlineData("oversized-page")]
    [InlineData("unavailable")]
    public async Task A_page_the_query_could_not_have_answered_is_unavailable(string answer)
    {
        var table = new ListedTable(_ => answer switch
        {
            "before-range" => ([Row("repositor", Version)], null),
            "past-range" => ([Row("repository;", Version)], null),
            "not-after-last-key" => ([Row("repository:000", Version)], null),
            "other-scope" => ([Row("repository:111", Version, scope: "global:issuance-v1")], null),
            "no-version" => ([Row("repository:111", null)], null),
            "oversized-page" => ([.. Enumerable.Range(100, 33).Select(n => Row("repository:" + n, Version))], null),
            _ => throw new HttpRequestException("The store did not answer."),
        });

        await Assert.ThrowsAsync<BrokerStateUnavailable>(() => new TableBrokerState(Client(table)).ListAsync(Candidates, "repository:000", null, Token));
    }

    private const string Version = "W/\"datetime'2026-10-25T01%3A00%3A00.0000000Z'\"";

    private static JsonObject Row(string name, string? version, bool unknown = false, string scope = Scope)
    {
        var row = new JsonObject
        {
            ["PartitionKey"] = scope, ["RowKey"] = name, ["Timestamp"] = "2026-10-25T01:00:00.0000000Z",
            ["RepositoryId"] = name.Split(':').Last(), ["PendingCount@odata.type"] = "Edm.Int64", ["PendingCount"] = "0"
        };
        if (version is not null) row["odata.etag"] = version;
        if (unknown) row["Unrecognized"] = "kept by no record";
        return row;
    }

    private static TableClient Client(HttpMessageHandler handler) => new(new Uri("https://listing.table.core.windows.net"), "brokerstate",
        new TableSharedKeyCredential("listing", Convert.ToBase64String(new byte[32])),
        new TableClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)), Retry = { MaxRetries = 0 } });

    // Answers each query with the rows the test gives and, when given, the continuation headers of a next page; keeps
    // each query's parameters. Only the first query gets the given page; later ones get an empty final page.
    private sealed class ListedTable(Func<int, (JsonObject[] Rows, (string PartitionKey, string RowKey)? Next)> answer) : HttpMessageHandler
    {
        public List<Dictionary<string, string>> Queries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Queries.Add(request.RequestUri!.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2)).ToDictionary(pair => Uri.UnescapeDataString(pair[0]),
                    pair => Uri.UnescapeDataString(pair.ElementAtOrDefault(1) ?? "").Replace('+', ' ')));
            var (rows, next) = Queries.Count == 1 ? answer(Queries.Count) : ([], null);
            var body = new JsonObject { ["value"] = new JsonArray([.. rows.Select(row => (JsonNode)row.DeepClone())]) };
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            if (next is { } continuation)
            {
                response.Headers.Add("x-ms-continuation-NextPartitionKey", continuation.PartitionKey);
                response.Headers.Add("x-ms-continuation-NextRowKey", continuation.RowKey);
            }
            return Task.FromResult(response);
        }
    }
}
