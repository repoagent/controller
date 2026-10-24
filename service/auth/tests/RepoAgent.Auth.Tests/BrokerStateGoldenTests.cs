using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core.Pipeline;
using Azure.Data.Tables;
using Xunit;

namespace RepoAgent.Auth.Tests;

// The golden tests of the broker table's row format: one per row shape the IBrokerState records map. Each recorded row
// goes through the Table SDK and the adapter in process, is read as its record and written back by a Replace, and the
// row the SDK sends must equal the recorded row: every property, value and EDM type, and the ETag as the condition.
public sealed class BrokerStateGoldenTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("locator")]
    [InlineData("gate")]
    [InlineData("device")]
    [InlineData("receipt")]
    public async Task Recorded_row_is_read_and_written_back_unchanged(string shape)
    {
        var (recorded, written) = shape switch
        {
            "locator" => await RoundTripAsync(EnrollmentLocator.Key("baa0968867944f7bb5390b9ba1f9d0f1"), Locator),
            "gate" => await RoundTripAsync(RepositoryGate.Key("123456"), Gate),
            "device" => await RoundTripAsync(ControllerEnrollment.Key("123456", "baa0968867944f7bb5390b9ba1f9d0f1"), Device),
            _ => await RoundTripAsync(RevocationReceipt.Key("123456", "702cd26275d2447f9150342e6ada61a4"), Receipt),
        };

        Assert.Equal(Stored(recorded.Entity), Stored(written.Entity));
        Assert.Equal(recorded.Entity["odata.etag"]!.GetValue<string>(), written.IfMatch);
    }

    private static async Task<((JsonObject Entity, string? IfMatch) Recorded, (JsonObject Entity, string? IfMatch) Written)> RoundTripAsync<T>(
        StateKey<T> key, string recorded) where T : BrokerRecord, new()
    {
        var handler = new RecordedTable(recorded);
        var state = new TableBrokerState(Client(handler));
        var read = (await state.ReadAsync(key, Token))!;
        await state.CommitAsync(new StateTransaction().Replace(key, read.Value, read.Version), Token);
        var lines = Assert.Single(handler.Batches).Split("\r\n");
        var ifMatch = lines.Single(line => line.StartsWith("If-Match: ", StringComparison.Ordinal))["If-Match: ".Length..];
        return ((JsonNode.Parse(recorded)!.AsObject(), null), (JsonNode.Parse(lines.Single(line => line.StartsWith('{')))!.AsObject(), ifMatch));
    }

    // Each stored property as its EDM type and value. Table's system properties are not stored by the broker.
    private static SortedDictionary<string, string> Stored(JsonObject entity)
    {
        var stored = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in entity)
        {
            if (name.StartsWith("odata.", StringComparison.Ordinal) || name.EndsWith("@odata.type", StringComparison.Ordinal) || name == "Timestamp") continue;
            var type = entity[name + "@odata.type"]?.GetValue<string>() ?? value!.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.String => "Edm.String",
                System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => "Edm.Boolean",
                _ => value.ToJsonString().Contains('.') ? "Edm.Double" : "Edm.Int32",
            };
            stored[name] = type + " " + (type == "Edm.DateTime"
                ? DateTimeOffset.Parse(value!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime().ToString("O")
                : value!.ToJsonString());
        }
        return stored;
    }

    private static TableClient Client(HttpMessageHandler handler) => new(new Uri("https://golden.table.core.windows.net"), "brokerstate",
        new TableSharedKeyCredential("golden", Convert.ToBase64String(new byte[32])),
        new TableClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)), Retry = { MaxRetries = 0 } });

    // Answers every read with the recorded row and every transaction with success, keeping each transaction's body.
    private sealed class RecordedTable(string row) : HttpMessageHandler
    {
        public List<string> Batches { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent(row, Encoding.UTF8, "application/json") };
            var body = await request.Content!.ReadAsStringAsync(ct);
            Batches.Add(body);
            var response = new StringBuilder("--batchresponse_golden\r\nContent-Type: multipart/mixed; boundary=changesetresponse_golden\r\n\r\n");
            foreach (var _ in body.Split("\r\n").Where(line => line.EndsWith(" HTTP/1.1", StringComparison.Ordinal)))
                response.Append("--changesetresponse_golden\r\nContent-Type: application/http\r\nContent-Transfer-Encoding: binary\r\n\r\n")
                    .Append("HTTP/1.1 204 No Content\r\nETag: W/\"datetime'2026-10-24T21%3A00%3A00.0000000Z'\"\r\n\r\n");
            response.Append("--changesetresponse_golden--\r\n--batchresponse_golden--\r\n");
            var content = new StringContent(response.ToString());
            content.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/mixed; boundary=batchresponse_golden");
            return new(HttpStatusCode.Accepted) { Content = content };
        }
    }

    // Recorded from pinned Azurite at b2bdf4e by the broker's own flows: a setup v1 enrollment, acknowledged, issued
    // twice (so the runtime token was published and observed again) and then revoked; and an Agent-variable owner that
    // was revoked, settled and released. The device row adds PayloadRedacted, which payload redaction writes, so that
    // it holds every property its record maps.
    private const string Locator = """{"odata.metadata":"http://127.0.0.1:46299/devstoreaccount1/$metadata#revocation9b200264127e47f9a2e299be5fe60664/@Element","odata.etag":"W/\"datetime'2026-10-24T20%3A52%3A46.2891199Z'\"","PartitionKey":"locator:baa0968867944f7bb5390b9ba1f9d0f1","RowKey":"locator","RepositoryId":"123456","DeviceId":"baa0968867944f7bb5390b9ba1f9d0f1","PendingId":"123456_1a2c019f964c4f34a4c6e228b2bcb192","Timestamp":"2026-10-24T20:52:46.2891199Z"}""";
    private const string Gate = """{"odata.metadata":"http://127.0.0.1:46299/devstoreaccount1/$metadata#revocation9b200264127e47f9a2e299be5fe60664/@Element","odata.etag":"W/\"datetime'2026-10-24T20%3A52%3A46.4492794Z'\"","PartitionKey":"repository:123456","RowKey":"gate","RepositoryId":"123456","Epoch":"test-epoch","State":"active","Sequence@odata.type":"Edm.Int64","Sequence":"19","OutstandingTokenCount@odata.type":"Edm.Int64","OutstandingTokenCount":"2","PendingCount@odata.type":"Edm.Int64","PendingCount":"1","OwnerDeviceId":"baa0968867944f7bb5390b9ba1f9d0f1","VerifierAttempt":"82b3f9b3ea3d48cbb8dc8b50398d7e4b","Timestamp":"2026-10-24T20:52:46.4492794Z"}""";
    private const string Device = """{"odata.metadata":"http://127.0.0.1:46299/devstoreaccount1/$metadata#revocation9b200264127e47f9a2e299be5fe60664/@Element","odata.etag":"W/\"datetime'2026-10-24T20%3A52%3A46.4682992Z'\"","PartitionKey":"repository:123456","RowKey":"device:baa0968867944f7bb5390b9ba1f9d0f1","RepositoryId":"123456","Repository":"owner/project","DeviceId":"baa0968867944f7bb5390b9ba1f9d0f1","Epoch":"test-epoch","InstallationId@odata.type":"Edm.Int64","InstallationId":"789","Variable":"APPROVAL","Profile":"polling","Challenge":"A-E_jX5JlQiRWuUNd-PToEfu4AG9UfAaKUQB2j7JBiA","Marker":"{\"version\":1,\"githubHost\":\"github.com\",\"appId\":5387625,\"appSlug\":\"repo-agent-connector\",\"repositoryId\":123456,\"repository\":\"owner/project\",\"installationId\":789,\"deviceId\":\"baa0968867944f7bb5390b9ba1f9d0f1\",\"challenge\":\"A-E_jX5JlQiRWuUNd-PToEfu4AG9UfAaKUQB2j7JBiA\",\"profile\":\"polling\",\"policyVersion\":\"repository-approval-v1\",\"epoch\":\"test-epoch\",\"environment\":\"approval/environment\",\"environmentId\":987,\"variable\":\"APPROVAL\"}","Environment":"approval/environment","EnvironmentId@odata.type":"Edm.Int64","EnvironmentId":"987","ApprovalKind":"environment-variable","SetupVersion@odata.type":"Edm.Int64","SetupVersion":"1","PolicyVersion":"repository-approval-v1","AppId@odata.type":"Edm.Int64","AppId":"5387625","AppSlug":"repo-agent-connector","GitHubHost":"github.com","SecretHash":"4E301860FF36D50E4BA4F394B7DCC18995E10008E38E44AC24D09865F1D8C0E8","Acknowledged":true,"State":"revoked","ExpiresAt@odata.type":"Edm.DateTime","ExpiresAt":"2027-01-22T20:56:45.7373539Z","OutstandingTokenCount@odata.type":"Edm.Int64","OutstandingTokenCount":"1","CompletionId":"65c38567e3df4922b9620950b66dfdf3","PendingId":"123456_1a2c019f964c4f34a4c6e228b2bcb192","LastActivityAt@odata.type":"Edm.DateTime","LastActivityAt":"2026-10-24T20:56:45.7373539Z","RuntimeAttempt":"e04482ddbe4141f0a58d6b4af9f719bd","RuntimeExpiresAt@odata.type":"Edm.DateTime","RuntimeExpiresAt":"2026-10-24T21:55:45.7373539Z","RuntimeObservation":"b85e452d68c64b5aa30673495319cc7e","RuntimeObservationReservedAt@odata.type":"Edm.DateTime","RuntimeObservationReservedAt":"2026-10-24T20:56:45.7373539Z","RuntimeObservationJwtIssuedAt@odata.type":"Edm.DateTime","RuntimeObservationJwtIssuedAt":"2026-10-24T20:55:45.7373539Z","RuntimeObservationJwtExpiresAt@odata.type":"Edm.DateTime","RuntimeObservationJwtExpiresAt":"2026-10-24T20:58:45.7373539Z","FirstTerminalAt@odata.type":"Edm.DateTime","FirstTerminalAt":"2026-10-24T20:56:45.7373539Z","PayloadRedacted":true,"Timestamp":"2026-10-24T20:52:46.4682992Z"}""";
    private const string Receipt = """{"odata.metadata":"http://127.0.0.1:46299/devstoreaccount1/$metadata#revocationc4c0335fcd5344cc9d382ac190227ea6/@Element","odata.etag":"W/\"datetime'2026-10-24T20%3A52%3A46.8907206Z'\"","PartitionKey":"repository:123456","RowKey":"revocation:702cd26275d2447f9150342e6ada61a4","RepositoryId":"123456","DeviceId":"702cd26275d2447f9150342e6ada61a4","Epoch":"test-epoch","RevokedAt@odata.type":"Edm.DateTime","RevokedAt":"2026-10-24T20:56:46.5028575Z","OwnerReleasedAt@odata.type":"Edm.DateTime","OwnerReleasedAt":"2026-10-24T22:02:46.5028575Z","Reason":"revoked-acknowledged","Timestamp":"2026-10-24T20:52:46.8907206Z"}""";
}
