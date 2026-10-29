using System.Globalization;
using RepoAgent.RestoreTable;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class RestoreSnapshotTests
{
    [Theory]
    [InlineData("repoagentauth")]
    [InlineData("20261029-ABCDEF")]
    [InlineData("20260230-abcdef")]
    [InlineData("20261029-abcdef\n")]
    public void Only_valid_run_owned_accounts_can_be_selected(string runId)
        => Assert.Throws<InvalidDataException>(() => RestoreRun.Parse(runId));

    [Theory]
    [InlineData("https://repoagentauth.table.core.windows.net")]
    [InlineData("http://localhost:10002/devstoreaccount1")]
    [InlineData("http://127.0.0.1:10002/other")]
    [InlineData("http://127.0.0.1:10002/devstoreaccount1?sig=anything")]
    public void Local_mode_cannot_select_cloud_or_redirected_credentials(string endpoint)
        => Assert.Throws<InvalidDataException>(() => RestoreRun.LocalEndpoint(endpoint));

    [Theory]
    [InlineData("https://ra230202610299f3a76.table.core.windows.net/brokerstate", "GET")]
    [InlineData("https://other.table.core.windows.net/brokerrehearsal", "GET")]
    [InlineData("https://ra230202610299f3a76.table.core.windows.net/brokerrehearsal", "DELETE")]
    [InlineData("https://ra230202610299f3a76.table.core.windows.net/brokerrehearsalOther", "POST")]
    public void Transport_scope_rejects_other_tables_origins_and_rewrite_methods(string request, string method)
    {
        var run = RestoreRun.Parse("20261029-9f3a76");
        Assert.False(RestoreRun.AllowsRequest(run.Endpoint, RestoreRun.SourceTable, new Uri(request), method));
    }

    [Fact]
    public void Foundation_table_is_excluded_from_every_operator_transport_scope()
    {
        var run = RestoreRun.Parse("20261029-9f3a76");
        Assert.False(RestoreRun.AllowsRequest(run.Endpoint, "brokerstate", new Uri(run.Endpoint, "brokerstate"), "POST"));
        Assert.True(RestoreRun.AllowsRequest(run.Endpoint, "brokerrestore", new Uri(run.Endpoint, "brokerrestore"), "POST"));
    }

    [Fact]
    public void Typed_snapshot_preserves_integer_width_binary_and_original_clocks()
    {
        var run = RestoreRun.Parse("20261029-9f3a76");
        var row = SnapshotRow.From("repository:123", "device:abc", new Dictionary<string, object>
        {
            ["Count"] = 0L,
            ["Small"] = 0,
            ["ExpiresAt"] = DateTimeOffset.Parse("2026-10-29T00:00:00Z", CultureInfo.InvariantCulture),
            ["Payload"] = new byte[] { 0, 255 },
            ["Revoked"] = true,
            ["Ratio"] = -0d,
            ["Generation"] = Guid.Parse("12345678-1234-1234-1234-123456789abc"),
            ["a"] = "lower",
            ["Z"] = "upper"
        });
        var snapshot = SnapshotDocument.Create(run, run.Endpoint, [row], 1);
        var restored = SnapshotDocument.Read(snapshot.Write(), run, run.Endpoint).Rows[0].Properties;
        Assert.IsType<long>(restored["Count"].Decode());
        Assert.IsType<int>(restored["Small"].Decode());
        Assert.Equal(new byte[] { 0, 255 }, Assert.IsType<byte[]>(restored["Payload"].Decode()));
        Assert.Equal("2026-10-29T00:00:00.0000000+00:00", restored["ExpiresAt"].Value);
        Assert.Equal(true, restored["Revoked"].Decode());
    }

    [Fact]
    public void Corrupt_foreign_duplicate_or_incomplete_snapshots_are_refused()
    {
        var run = RestoreRun.Parse("20261029-9f3a76");
        var row = SnapshotRow.From("repository:123", "device:abc", new Dictionary<string, object> { ["Count"] = 0L });
        var snapshot = SnapshotDocument.Create(run, run.Endpoint, [row], 1);
        var bytes = snapshot.Write();
        Assert.Throws<InvalidDataException>(() => SnapshotDocument.Read(bytes, RestoreRun.Parse("20261029-abcdef"), run.Endpoint));
        Assert.Throws<InvalidDataException>(() => SnapshotDocument.Read(bytes, run, new Uri("https://other.table.core.windows.net/")));
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Throws<InvalidDataException>(() => SnapshotDocument.Read(System.Text.Encoding.UTF8.GetBytes(text.Replace(
            "\"Int64\"", "\"Int32\"", StringComparison.Ordinal)), run, run.Endpoint));
        Assert.Throws<InvalidDataException>(() => SnapshotDocument.Read(System.Text.Encoding.UTF8.GetBytes(text.Replace(
            "\"schema\":1", "\"schema\":1,\"schema\":1", StringComparison.Ordinal)), run, run.Endpoint));
        Assert.Throws<InvalidDataException>(() => SnapshotDocument.Create(run, run.Endpoint, [row, row], 1).Validate(run, run.Endpoint));
        Assert.Throws<InvalidDataException>(() => (snapshot with { Pages = 0 }).Validate(run, run.Endpoint));
    }

    [Fact]
    public void Unknown_lossy_or_noncanonical_property_values_are_refused()
    {
        Assert.Throws<InvalidDataException>(() => EdmProperty.Encode(1m));
        Assert.Throws<InvalidDataException>(() => EdmProperty.Encode(double.NaN));
        Assert.Throws<InvalidDataException>(() => new EdmProperty("Int64", "01").Decode());
        Assert.Throws<InvalidDataException>(() => new EdmProperty("Boolean", "True").Decode());
        Assert.Throws<InvalidDataException>(() => new EdmProperty("FutureType", "anything").Decode());
    }
}
