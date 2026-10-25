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
    [InlineData("attempt")]
    [InlineData("repository-quota")]
    [InlineData("issuance-quota")]
    [InlineData("enrollment-quota")]
    [InlineData("candidate")]
    [InlineData("ledger")]
    [InlineData("slot")]
    [InlineData("pending")]
    [InlineData("enrollment-ledger")]
    [InlineData("release-issuance")]
    [InlineData("release-pending")]
    [InlineData("cleanup-payload")]
    [InlineData("cleanup-audit")]
    public async Task Recorded_row_is_read_and_written_back_unchanged(string shape)
    {
        var (recorded, written) = shape switch
        {
            "locator" => await RoundTripAsync(EnrollmentLocator.Key("baa0968867944f7bb5390b9ba1f9d0f1"), Locator),
            "gate" => await RoundTripAsync(RepositoryGate.Key("123456"), Gate),
            "device" => await RoundTripAsync(ControllerEnrollment.Key("123456", "baa0968867944f7bb5390b9ba1f9d0f1"), Device),
            "receipt" => await RoundTripAsync(RevocationReceipt.Key("123456", "702cd26275d2447f9150342e6ada61a4"), Receipt),
            "attempt" => await RoundTripAsync(IssuanceAttempt.Key("123456", "0dc263f60f6a40c7b05d5a49099c0dfc"), Attempt),
            "repository-quota" => await RoundTripAsync(RepositoryQuota.Key("123456"), RepositoryQuotaRow),
            "issuance-quota" => await RoundTripAsync(IssuanceQuota.Key, IssuanceQuotaRow),
            "enrollment-quota" => await RoundTripAsync(EnrollmentQuota.Key, EnrollmentQuotaRow),
            "candidate" => await RoundTripAsync(CandidateRepository.Key("123456"), Candidate),
            "ledger" => await RoundTripAsync(IssuanceLedger.Key("640f64df3b724a88a0b5a01e8f48c07f"), Ledger),
            "slot" => await RoundTripAsync(IssuanceSlot.Key(1), Slot),
            "pending" => await RoundTripAsync(PendingControllerEnrollment.Key("123456", "1f2679082c4748db9e733bb2b38bdc04"), Pending),
            "enrollment-ledger" => await RoundTripAsync(EnrollmentLedger.Key("4906a69f5b8c4ad680a6bbdcd60b8f57"), EnrollmentLedgerRow),
            "release-issuance" => await RoundTripAsync(ReleaseProof.Issuance("123456", "edc663a811034b8c856a1e670d819b22"), ReleaseIssuanceRow),
            "release-pending" => await RoundTripAsync(ReleaseProof.Enrollment("123456", "2b9e234dac464e35a926f19fb71c95b3"), ReleasePendingRow),
            "cleanup-payload" => await RoundTripAsync(CleanupCursor.Payload, CleanupPayloadRow),
            _ => await RoundTripAsync(CleanupCursor.Audit, CleanupAuditRow),
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

    // Recorded from pinned Azurite at 5230c77 by the broker's own flows: a setup v1 enrollment, acknowledged and issued
    // once, with every issuance row read back. No single flow stores every property a record maps, so two rows add the
    // ones other writers store, with the EDM types those writers use: the released runtime attempt adds PendingId
    // (enrollment's verification attempts), RowCredits (reclamation's and redaction's copies of the ledger), SettledAt,
    // SettlementReason and CapacityReleased (reclamation) and PayloadRedacted (redaction); the enrollment's verification
    // ledger adds CapacityState (reclamation), RetirementState and RetirementAuditAt (retirement).
    private const string Attempt = """{"odata.metadata":"http://127.0.0.1:45023/devstoreaccount1/$metadata#revocation77ebef7d7ba1413ea1ffd73d1bb03985/@Element","odata.etag":"W/\"datetime'2026-10-24T22%3A36%3A00.1474228Z'\"","PartitionKey":"repository:123456","RowKey":"attempt:0dc263f60f6a40c7b05d5a49099c0dfc","OperationId":"0dc263f60f6a40c7b05d5a49099c0dfc","Generation":"02da35d80fef462da08c3f9ae588462b","RepositoryId":"123456","DeviceId":"1bfda41ccc75440ca43706ea41a08cdf","Epoch":"test-epoch","Purpose":"runtime","Profile":"polling","InstallationId@odata.type":"Edm.Int64","InstallationId":"789","ApprovalKind":"environment-variable","SetupVersion@odata.type":"Edm.Int64","SetupVersion":"1","PolicyVersion":"repository-approval-v1","RepositoryCharged":true,"State":"released","ReservedAt@odata.type":"Edm.DateTime","ReservedAt":"2026-10-24T22:38:59.4821569Z","PayloadPurgeAt@odata.type":"Edm.DateTime","PayloadPurgeAt":"2026-10-25T22:38:59.4821569Z","JwtIssuedAt@odata.type":"Edm.DateTime","JwtIssuedAt":"2026-10-24T22:37:59.4821569Z","JwtExpiresAt@odata.type":"Edm.DateTime","JwtExpiresAt":"2026-10-24T22:40:59.4821569Z","GlobalSlot@odata.type":"Edm.Int64","GlobalSlot":"1","SendClaimedAt@odata.type":"Edm.DateTime","SendClaimedAt":"2026-10-24T22:38:59.4821569Z","ProtectedToken":"protected-f52048652b394f94a3b80c482f17df2a","ExpiresAt@odata.type":"Edm.DateTime","ExpiresAt":"2026-10-24T23:38:59.4821569Z","ScopeValid":true,"ReleasedAt@odata.type":"Edm.DateTime","ReleasedAt":"2026-10-24T22:38:59.4821569Z","FirstTerminalAt@odata.type":"Edm.DateTime","FirstTerminalAt":"2026-10-24T22:38:59.4821569Z","PendingId":"123456_6f1d0a3c2b7e4d5f8a9b0c1d2e3f4a5b","RowCredits@odata.type":"Edm.Int64","RowCredits":"4","SettledAt@odata.type":"Edm.DateTime","SettledAt":"2026-10-24T22:38:59.4821569Z","SettlementReason":"expired","CapacityReleased":true,"PayloadRedacted":true,"Timestamp":"2026-10-24T22:36:00.1474228Z"}""";
    private const string RepositoryQuotaRow = """{"odata.metadata":"http://127.0.0.1:45023/devstoreaccount1/$metadata#revocation77ebef7d7ba1413ea1ffd73d1bb03985/@Element","odata.etag":"W/\"datetime'2026-10-24T22%3A36%3A00.1163921Z'\"","PartitionKey":"repository:123456","RowKey":"quota","Window@odata.type":"Edm.Int64","Window":"29881358","Admissions@odata.type":"Edm.Int64","Admissions":"1","Reads@odata.type":"Edm.Int64","Reads":"8","Signs@odata.type":"Edm.Int64","Signs":"1","Mints@odata.type":"Edm.Int64","Mints":"1","Slots@odata.type":"Edm.Int64","Slots":"0","Timestamp":"2026-10-24T22:36:00.1163921Z"}""";
    private const string IssuanceQuotaRow = """{"odata.metadata":"http://127.0.0.1:45023/devstoreaccount1/$metadata#revocation77ebef7d7ba1413ea1ffd73d1bb03985/@Element","odata.etag":"W/\"datetime'2026-10-24T22%3A36%3A00.1053807Z'\"","PartitionKey":"global:issuance-v1","RowKey":"quota","Window@odata.type":"Edm.Int64","Window":"29881358","Admissions@odata.type":"Edm.Int64","Admissions":"1","Reads@odata.type":"Edm.Int64","Reads":"8","Signs@odata.type":"Edm.Int64","Signs":"1","Mints@odata.type":"Edm.Int64","Mints":"1","Slots@odata.type":"Edm.Int64","Slots":"2","RetentionSchemaVersion@odata.type":"Edm.Int64","RetentionSchemaVersion":"1","RetainedIssuanceCount@odata.type":"Edm.Int64","RetainedIssuanceCount":"2","Timestamp":"2026-10-24T22:36:00.1053807Z"}""";
    private const string EnrollmentQuotaRow = """{"odata.metadata":"http://127.0.0.1:45023/devstoreaccount1/$metadata#revocation77ebef7d7ba1413ea1ffd73d1bb03985/@Element","odata.etag":"W/\"datetime'2026-10-24T22%3A35%3A59.8571347Z'\"","PartitionKey":"global:enrollment-v1","RowKey":"quota","RetentionSchemaVersion@odata.type":"Edm.Int64","RetentionSchemaVersion":"1","CandidateCount@odata.type":"Edm.Int64","CandidateCount":"1","RetainedEnrollmentCount@odata.type":"Edm.Int64","RetainedEnrollmentCount":"1","Count@odata.type":"Edm.Int64","Count":"1","Timestamp":"2026-10-24T22:35:59.8571347Z"}""";
    private const string Candidate = """{"odata.metadata":"http://127.0.0.1:45023/devstoreaccount1/$metadata#revocation77ebef7d7ba1413ea1ffd73d1bb03985/@Element","odata.etag":"W/\"datetime'2026-10-24T22%3A35%3A59.8571356Z'\"","PartitionKey":"global:enrollment-v1","RowKey":"repository:123456","RepositoryId":"123456","PendingCount@odata.type":"Edm.Int64","PendingCount":"1","Timestamp":"2026-10-24T22:35:59.8571356Z"}""";
    private const string Ledger = """{"odata.metadata":"http://127.0.0.1:45023/devstoreaccount1/$metadata#revocation77ebef7d7ba1413ea1ffd73d1bb03985/@Element","odata.etag":"W/\"datetime'2026-10-24T22%3A35%3A59.9071824Z'\"","PartitionKey":"global:issuance-v1","RowKey":"operation:640f64df3b724a88a0b5a01e8f48c07f","OperationId":"640f64df3b724a88a0b5a01e8f48c07f","Generation":"348f16d3c1514f3fbeba93cc954a73f0","RepositoryId":"123456","Purpose":"verification","Epoch":"test-epoch","ReservedAt@odata.type":"Edm.DateTime","ReservedAt":"2026-10-24T22:35:59.4821569Z","PayloadPurgeAt@odata.type":"Edm.DateTime","PayloadPurgeAt":"2026-10-25T22:35:59.4821569Z","GlobalSlot@odata.type":"Edm.Int64","GlobalSlot":"0","DeviceId":"1bfda41ccc75440ca43706ea41a08cdf","AttemptRowKey":"attempt:640f64df3b724a88a0b5a01e8f48c07f","RowCredits@odata.type":"Edm.Int64","RowCredits":"4","PendingId":"123456_0507cf7e0aa74eec86823ddefb6f3cc4","ApprovalKind":"environment-variable","SetupVersion@odata.type":"Edm.Int64","SetupVersion":"1","PolicyVersion":"repository-approval-v1","CapacityState":"released","RetirementState":"retiring","RetirementAuditAt@odata.type":"Edm.DateTime","RetirementAuditAt":"2026-11-24T22:38:59.4821569Z","Timestamp":"2026-10-24T22:35:59.9071824Z"}""";
    private const string Slot = """{"odata.metadata":"http://127.0.0.1:45023/devstoreaccount1/$metadata#revocation77ebef7d7ba1413ea1ffd73d1bb03985/@Element","odata.etag":"W/\"datetime'2026-10-24T22%3A36%3A00.1053813Z'\"","PartitionKey":"global:issuance-v1","RowKey":"slot:001","OperationId":"0dc263f60f6a40c7b05d5a49099c0dfc","Generation":"02da35d80fef462da08c3f9ae588462b","RepositoryId":"123456","Purpose":"runtime","Epoch":"test-epoch","ReservedAt@odata.type":"Edm.DateTime","ReservedAt":"2026-10-24T22:38:59.4821569Z","PayloadPurgeAt@odata.type":"Edm.DateTime","PayloadPurgeAt":"2026-10-25T22:38:59.4821569Z","GlobalSlot@odata.type":"Edm.Int64","GlobalSlot":"1","ApprovalKind":"environment-variable","SetupVersion@odata.type":"Edm.Int64","SetupVersion":"1","PolicyVersion":"repository-approval-v1","Timestamp":"2026-10-24T22:36:00.1053813Z"}""";

    // Recorded from pinned Azurite at 8234572 by the broker's own flows. The pending enrollment is a setup v1 enrollment
    // that completed and was not yet acknowledged; it adds FirstTerminalAt (acknowledgment and closing),
    // PendingCapacityReleased (reclamation) and PayloadRedacted (redaction). The ledger is a setup v1 enrollment that
    // expired unapproved and was reclaimed; it adds RetirementState and RetirementAuditAt (retirement) and PayloadRedacted
    // (redaction). With those, each row holds every property its record maps, with the EDM types its writers use.
    private const string Pending = """{"odata.metadata":"http://127.0.0.1:46371/devstoreaccount1/$metadata#revocation1b564cd151084854ab7743ae5f79ba96/@Element","odata.etag":"W/\"datetime'2026-10-25T01%3A08%3A59.9975048Z'\"","PartitionKey":"repository:123456","RowKey":"pending:1f2679082c4748db9e733bb2b38bdc04","RepositoryId":"123456","PendingId":"123456_1f2679082c4748db9e733bb2b38bdc04","RecoveryHash":"66D34FBA71F8F450F7E45598853E53BFC23BBD129027CBB131A2F4FFD7878CD0","BodyHash":"1CFD98C36AC25E1DA90ED1D5A29B753603F61D16473C39CAD96140BC18629F15","Epoch":"test-epoch","CreatedAt@odata.type":"Edm.DateTime","CreatedAt":"2026-10-25T01:08:59.4241150Z","Deadline@odata.type":"Edm.DateTime","Deadline":"2026-10-25T01:23:59.4241150Z","PayloadPurgeAt@odata.type":"Edm.DateTime","PayloadPurgeAt":"2026-10-26T01:23:59.4241150Z","Generation":"726c1454225f45ee9f2e2e077263a8ad","RowCredits@odata.type":"Edm.Int64","RowCredits":"8","ReclamationVersion@odata.type":"Edm.Int64","ReclamationVersion":"1","DeviceId":"bd03b6669a9e4d4cbb93082dd4ab3ad9","Challenge":"x5kSpppA_dh0mF1aO2E7f5V3GTccRyUgU0o7T62I134","ApprovalKind":"environment-variable","SetupVersion@odata.type":"Edm.Int64","SetupVersion":"1","PolicyVersion":"repository-approval-v1","RequestedRepository":"owner/project","Repository":"owner/project","Environment":"approval/environment","Variable":"APPROVAL","Profile":"polling","State":"completed","RepositoryCharged":true,"NextWorkAt@odata.type":"Edm.DateTime","NextWorkAt":"2026-10-25T01:10:04.4241150Z","PositiveApproval":true,"WorkStartedAt@odata.type":"Edm.DateTime","WorkStartedAt":"2026-10-25T01:09:59.4241150Z","MintAdmissionStarted":true,"Attempt":"c4cf64b3550c443eb23fe1b3c33cc4cc","ObservationReservedAt@odata.type":"Edm.DateTime","ObservationReservedAt":"2026-10-25T01:09:59.4241150Z","ObservationJwtIssuedAt@odata.type":"Edm.DateTime","ObservationJwtIssuedAt":"2026-10-25T01:08:59.4241150Z","ObservationJwtExpiresAt@odata.type":"Edm.DateTime","ObservationJwtExpiresAt":"2026-10-25T01:11:59.4241150Z","InstallationId@odata.type":"Edm.Int64","InstallationId":"789","EnvironmentId@odata.type":"Edm.Int64","EnvironmentId":"987","Marker":"{\"version\":1,\"githubHost\":\"github.com\",\"appId\":5387625,\"appSlug\":\"repo-agent-connector\",\"repositoryId\":123456,\"repository\":\"owner/project\",\"installationId\":789,\"deviceId\":\"bd03b6669a9e4d4cbb93082dd4ab3ad9\",\"challenge\":\"x5kSpppA_dh0mF1aO2E7f5V3GTccRyUgU0o7T62I134\",\"profile\":\"polling\",\"policyVersion\":\"repository-approval-v1\",\"epoch\":\"test-epoch\",\"environment\":\"approval/environment\",\"environmentId\":987,\"variable\":\"APPROVAL\"}","CompletionId":"ab6250fcfc544715b525533d855ea0f3","ProtectedCompletion":"protected-57613c49d55c47fca9589705b2f91232","Timestamp":"2026-10-25T01:08:59.9975048Z","FirstTerminalAt@odata.type":"Edm.DateTime","FirstTerminalAt":"2026-10-25T01:09:59.4241150Z","PendingCapacityReleased":true,"PayloadRedacted":true}""";
    private const string EnrollmentLedgerRow = """{"odata.metadata":"http://127.0.0.1:46371/devstoreaccount1/$metadata#revocation49b73b596f884d7d81f6c24a4b9120b9/@Element","odata.etag":"W/\"datetime'2026-10-25T01%3A09%3A00.2887955Z'\"","PartitionKey":"global:enrollment-v1","RowKey":"pending:4906a69f5b8c4ad680a6bbdcd60b8f57","RepositoryId":"123456","PendingId":"123456_4906a69f5b8c4ad680a6bbdcd60b8f57","RecoveryHash":"66D34FBA71F8F450F7E45598853E53BFC23BBD129027CBB131A2F4FFD7878CD0","BodyHash":"1E101DA0216C5FF80EBE056C05FA4162B21CB7615956A73F61F2BC9B30CB7B5D","Epoch":"test-epoch","CreatedAt@odata.type":"Edm.DateTime","CreatedAt":"2026-10-25T01:09:00.1724892Z","Deadline@odata.type":"Edm.DateTime","Deadline":"2026-10-25T01:24:00.1724892Z","PayloadPurgeAt@odata.type":"Edm.DateTime","PayloadPurgeAt":"2026-10-26T01:24:00.1724892Z","Generation":"3814ee1cd94d496abb34e577ca53658f","RowCredits@odata.type":"Edm.Int64","RowCredits":"8","ReclamationVersion@odata.type":"Edm.Int64","ReclamationVersion":"1","DeviceId":"9aeb5b2e882d48a2b2d74b63f4a42bb4","Challenge":"sEG1ED5jliAcb6pyovadw4Y06YSIkUGCCOnyMV0yT7o","ApprovalKind":"environment-variable","SetupVersion@odata.type":"Edm.Int64","SetupVersion":"1","PolicyVersion":"repository-approval-v1","PendingCapacityReleased":true,"Timestamp":"2026-10-25T01:09:00.2887955Z","RetirementState":"retiring","RetirementAuditAt@odata.type":"Edm.DateTime","RetirementAuditAt":"2026-10-26T01:24:00.1724892Z","PayloadRedacted":true}""";

    // Recorded from pinned Azurite at b31d29a by the broker's own flows. The release proofs are an issuance (an enrollment
    // verification attempt, so it has PendingId) and a setup v2 pending enrollment, each released by reclamation and then
    // marked by retirement's second phase (RetirementTargetDeleted, RetirementAuditAt); together they hold every property
    // ReleaseProof maps. The cursors are after three cleanup runs: one processed each ledger, one completed each cycle and
    // one started the next issuance cycle. The audit cursor has no NextLane, which only the payload cursor stores. The
    // payload cursor adds EnrollmentContinuation and IssuanceContinuation, which only an empty nonterminal Azure page
    // writes (Azurite returns none), as SDK continuation strings, so that it holds every property CleanupCursor maps.
    private const string ReleaseIssuanceRow = """{"odata.metadata":"http://127.0.0.1:45117/devstoreaccount1/$metadata#revocationbe882576b5994e0f8e5bc22e9e5ff7d0/@Element","odata.etag":"W/\"datetime'2026-10-25T02%3A57%3A17.9581070Z'\"","PartitionKey":"repository:123456","RowKey":"release:edc663a811034b8c856a1e670d819b22","RepositoryId":"123456","DeviceId":"c5ffb2b4eee34b209ff5e27cfc3c8cbd","Epoch":"test-epoch","Generation":"4d5765bf4c274a59a24b2dfdbb08fe48","OperationId":"edc663a811034b8c856a1e670d819b22","Purpose":"verification","PendingId":"123456_734becdc269742338136f6de579bb115","GlobalSlot@odata.type":"Edm.Int64","GlobalSlot":"0","ReservedAt@odata.type":"Edm.DateTime","ReservedAt":"2026-10-25T02:57:17.7423971Z","PayloadPurgeAt@odata.type":"Edm.DateTime","PayloadPurgeAt":"2026-10-26T02:57:17.7423971Z","RowCredits@odata.type":"Edm.Int64","RowCredits":"4","ApprovalKind":"repository-agent-variable","SetupVersion@odata.type":"Edm.Int64","SetupVersion":"2","PolicyVersion":"repository-agent-approval-v2","SettlementReason":"expired","RepositorySettledAt@odata.type":"Edm.DateTime","RepositorySettledAt":"2026-10-25T04:02:17.7423971Z","RetirementTargetDeleted":true,"RetirementAuditAt@odata.type":"Edm.DateTime","RetirementAuditAt":"2026-11-24T04:02:17.7423971Z","Timestamp":"2026-10-25T02:57:17.9581070Z"}""";
    private const string ReleasePendingRow = """{"odata.metadata":"http://127.0.0.1:45117/devstoreaccount1/$metadata#revocation0b57302fb6eb4016a3c0da52735a622d/@Element","odata.etag":"W/\"datetime'2026-10-25T02%3A57%3A18.0642125Z'\"","PartitionKey":"repository:123456","RowKey":"release:pending:2b9e234dac464e35a926f19fb71c95b3","RepositoryId":"123456","DeviceId":"0b4fe95c44ef41db990248287a244767","Epoch":"test-epoch","Generation":"b2ee7e42122245769d9a140b6bbea5f9","PendingId":"123456_2b9e234dac464e35a926f19fb71c95b3","PayloadPurgeAt@odata.type":"Edm.DateTime","PayloadPurgeAt":"2026-10-26T03:12:17.9661541Z","CreatedAt@odata.type":"Edm.DateTime","CreatedAt":"2026-10-25T02:57:17.9661541Z","Deadline@odata.type":"Edm.DateTime","Deadline":"2026-10-25T03:12:17.9661541Z","RowCredits@odata.type":"Edm.Int64","RowCredits":"8","ReclamationVersion@odata.type":"Edm.Int64","ReclamationVersion":"1","ApprovalKind":"repository-agent-variable","SetupVersion@odata.type":"Edm.Int64","SetupVersion":"2","PolicyVersion":"repository-agent-approval-v2","RepositorySettledAt@odata.type":"Edm.DateTime","RepositorySettledAt":"2026-10-25T03:12:17.9661541Z","RetirementTargetDeleted":true,"RetirementAuditAt@odata.type":"Edm.DateTime","RetirementAuditAt":"2026-11-24T03:12:17.9661541Z","Timestamp":"2026-10-25T02:57:18.0642125Z"}""";
    private const string CleanupPayloadRow = """{"odata.metadata":"http://127.0.0.1:45117/devstoreaccount1/$metadata#revocation91ba70e98d3d420aad103eb4effec9a1/@Element","odata.etag":"W/\"datetime'2026-10-25T02%3A58%3A04.6437796Z'\"","PartitionKey":"global:enrollment-v1","RowKey":"cleanup:payload-v1","SchemaVersion@odata.type":"Edm.Int64","SchemaVersion":"1","NextFamily@odata.type":"Edm.Int64","NextFamily":"1","NextLane@odata.type":"Edm.Int64","NextLane":"0","EnrollmentLastKey":"","EnrollmentContinuation":"1!48!Z2xvYmFsOmVucm9sbG1lbnQtdjEBMDFkYzQ0N2Y5ZmQ0MjAwMA--","EnrollmentCycle@odata.type":"Edm.Int64","EnrollmentCycle":"2","EnrollmentStartedAt@odata.type":"Edm.DateTime","EnrollmentStartedAt":"2026-10-25T02:58:04.6392499Z","EnrollmentProgressAt@odata.type":"Edm.DateTime","EnrollmentProgressAt":"2026-10-25T02:58:04.6392500Z","IssuanceLastKey":"operation:20c24a4c940e4545a68bf1618e1f401d","IssuanceContinuation":"1!44!Z2xvYmFsOmlzc3VhbmNlLXYxATAxZGM0NDdmOWZkNDIwMDA-","IssuanceCycle@odata.type":"Edm.Int64","IssuanceCycle":"1","IssuanceStartedAt@odata.type":"Edm.DateTime","IssuanceStartedAt":"2026-10-25T02:58:04.2085410Z","IssuanceProgressAt@odata.type":"Edm.DateTime","IssuanceProgressAt":"2026-10-25T02:58:04.6098555Z","EnrollmentCompletedAt@odata.type":"Edm.DateTime","EnrollmentCompletedAt":"2026-10-25T02:58:04.6392493Z","IssuanceCompletedAt@odata.type":"Edm.DateTime","IssuanceCompletedAt":"2026-10-25T02:58:04.2085404Z","Timestamp":"2026-10-25T02:58:04.6437796Z"}""";
    private const string CleanupAuditRow = """{"odata.metadata":"http://127.0.0.1:45117/devstoreaccount1/$metadata#revocation91ba70e98d3d420aad103eb4effec9a1/@Element","odata.etag":"W/\"datetime'2026-10-25T02%3A58%3A04.6467833Z'\"","PartitionKey":"global:enrollment-v1","RowKey":"cleanup:audit-v1","SchemaVersion@odata.type":"Edm.Int64","SchemaVersion":"1","NextFamily@odata.type":"Edm.Int64","NextFamily":"1","EnrollmentLastKey":"","EnrollmentCycle@odata.type":"Edm.Int64","EnrollmentCycle":"2","EnrollmentStartedAt@odata.type":"Edm.DateTime","EnrollmentStartedAt":"2026-10-25T02:58:04.6463488Z","EnrollmentProgressAt@odata.type":"Edm.DateTime","EnrollmentProgressAt":"2026-10-25T02:58:04.6463490Z","IssuanceLastKey":"operation:20c24a4c940e4545a68bf1618e1f401d","IssuanceCycle@odata.type":"Edm.Int64","IssuanceCycle":"1","IssuanceStartedAt@odata.type":"Edm.DateTime","IssuanceStartedAt":"2026-10-25T02:58:04.2182568Z","IssuanceProgressAt@odata.type":"Edm.DateTime","IssuanceProgressAt":"2026-10-25T02:58:04.6330782Z","EnrollmentCompletedAt@odata.type":"Edm.DateTime","EnrollmentCompletedAt":"2026-10-25T02:58:04.6463483Z","IssuanceCompletedAt@odata.type":"Edm.DateTime","IssuanceCompletedAt":"2026-10-25T02:58:04.2182564Z","Timestamp":"2026-10-25T02:58:04.6467833Z"}""";
}
