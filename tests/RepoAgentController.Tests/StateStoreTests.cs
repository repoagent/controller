using System.Text.Json;
using System.Text.Json.Nodes;
using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

// The controller's Discussion state, opened as a startup opens the state file in a private temporary directory.
public sealed class StateStoreTests : IDisposable
{
    const string Repository = "owner/repository";
    readonly string directory = Path.Combine(Path.GetTempPath(), "state-store-" + Guid.NewGuid().ToString("N"));
    string State => Path.Combine(directory, "bridge.json");

    [Fact]
    public void Discussions_whose_removal_failed_to_save_stay_in_memory_and_a_retry_removes_them_from_the_file()
    {
        using (var leased = new LeasedStateStore(State, Repository))
        {
            var store = leased.Store;
            store.Discussions["D1"] = new("https://github.com/owner/repository/discussions/1") { SessionId = "S1" };
            store.Discussions["D2"] = new("https://github.com/owner/repository/discussions/2") { SessionId = "S2" };
            store.Save();
            Directory.CreateDirectory(State + ".tmp"); // The next save cannot write its replacement file.

            var error = Record.Exception(() => store.Remove(["D1"]));

            // The exceptions the port documents for a failed write, which its callers filter on.
            Assert.True(error is IOException or UnauthorizedAccessException, $"Expected a failed save, not {error}");
            Assert.Equal(["D1", "D2"], store.Discussions.Keys.Order());
            Directory.Delete(State + ".tmp");
            store.Remove(["D1"]);
        }
        using var reopened = new LeasedStateStore(State, Repository);
        var restarted = reopened.Store;
        Assert.Equal(["D2"], restarted.Discussions.Keys);
    }

    // As a startup opens it: the lease is taken and the file read, then the App identity is checked and saved.
    Exception? Open(string recorded, string login = "test-app[bot]")
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(State, recorded);
        return Record.Exception(() =>
        {
            using var leased = new LeasedStateStore(State, Repository);
            var store = leased.Store;
            store.VerifyIdentity(login);
        });
    }

    [Theory]
    [InlineData("""{"version":2,"repository":"owner/repository","discussions":{}}""")]
    [InlineData("""{"version":1,"repository":"owner/other","discussions":{}}""")]
    public void A_state_of_another_version_or_repository_stops_startup(string recorded)
    {
        var error = Assert.IsType<InvalidOperationException>(Open(recorded));
        Assert.Equal("State belongs to a different repository or unsupported version", error.Message);
    }

    [Fact]
    public void A_state_file_that_is_not_a_JSON_object_stops_startup_as_its_parser_reports_and_releases_the_lease()
    {
        Assert.IsAssignableFrom<JsonException>(Open("""{"version":1,"""));
        Assert.Equal(Reported(() => JsonNode.Parse("[]")!.AsObject()), Assert.IsType<InvalidOperationException>(Open("[]")).Message);
        Assert.Null(Open("""{"version":1,"repository":"owner/repository","discussions":{}}"""));
    }

    [Fact]
    public void A_state_of_another_App_login_or_with_a_login_that_is_not_text_stops_startup()
    {
        Assert.StartsWith("State belongs to a different GitHub identity;", Assert.IsType<InvalidOperationException>(
            Open("""{"version":1,"repository":"owner/repository","discussions":{},"agentLogin":"other-app[bot]"}""")).Message);
        Assert.Equal(Reported(() => JsonNode.Parse("7")!.GetValue<string>()), Assert.IsType<InvalidOperationException>(
            Open("""{"version":1,"repository":"owner/repository","discussions":{},"agentLogin":7}""")).Message);
    }

    [Fact]
    public void A_state_whose_Discussion_records_are_not_an_object_passes_the_identity_check_and_keeps_its_keys_until_they_are_read()
    {
        Assert.Null(Open("""{"version":1,"repository":"owner/repository","discussions":[],"later":true}"""));

        using var leased = new LeasedStateStore(State, Repository);

        var store = leased.Store;
        Assert.Equal("the state file's discussions are missing or not an object", Assert.Throws<InvalidDataException>(() => store.Discussions.Count).Message);
        var saved = JsonNode.Parse(File.ReadAllText(State))!;
        Assert.Equal("[]", saved["discussions"]!.ToJsonString());
        Assert.Equal("test-app[bot]", saved["agentLogin"]!.GetValue<string>());
        Assert.True(saved["later"]!.GetValue<bool>());
    }

    [Fact]
    public void Category_enrollment_starts_that_are_not_all_text_are_kept_as_they_were()
    {
        const string Recorded = """{"version":1,"repository":"owner/repository","discussions":{},"enrollmentCategories":{"Agents":"2026-10-24T08:00:00Z","Q&A":7}}""";

        Assert.Null(Open(Recorded));

        Assert.Equal("""{"Agents":"2026-10-24T08:00:00Z","Q\u0026A":7}""", JsonNode.Parse(File.ReadAllText(State))!["enrollmentCategories"]!.ToJsonString());
    }

    [Fact]
    public void A_record_holds_a_session_or_a_pending_delivery_where_its_keys_say_so_even_when_they_are_not_in_a_form_a_writer_leaves()
    {
        Assert.Null(Open("""
            {"version":1,"repository":"owner/repository","discussions":{
              "D1":{"url":"u1","sources":{},"sessionId":"S1"},"D2":{"url":"u2","sources":{},"sessionId":""},"D3":{"url":"u3","sources":{},"sessionId":4},
              "D4":{"url":"u4","sources":{},"pending":{"operation":"send","since":"2026-10-25T09:00:00.0000000+00:00","sourceIds":[]}},
              "D5":{"url":"u5","sources":{},"pending":{"operation":5}},"D6":{"url":"u6","sources":{}},"D7":7}}
            """));

        using var leased = new LeasedStateStore(State, Repository);

        var store = leased.Store;
        Assert.Equal(["D1", "D3", "D7"], store.Discussions.Where(entry => entry.Value.HoldsSession).Select(entry => entry.Key));
        Assert.Equal(["D4", "D5", "D7"], store.Discussions.Where(entry => entry.Value.HoldsPending).Select(entry => entry.Key));
        Assert.Equal(7, JsonNode.Parse(File.ReadAllText(State))!["discussions"]!["D7"]!.GetValue<int>()); // Saved as read.
    }

    // What the BCL reports for this read, so its wording is not pinned here.
    static string Reported(Action read) => Assert.IsType<InvalidOperationException>(Record.Exception(read)).Message;

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
