using System.Text.Json;
using System.Text.Json.Nodes;
using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

// The controller state file in a private temporary directory.
public sealed class StateStoreTests : IDisposable
{
    const string Repository = "owner/repository";
    readonly string directory = Path.Combine(Path.GetTempPath(), "state-store-" + Guid.NewGuid().ToString("N"));
    string State => Path.Combine(directory, "bridge.json");

    [Fact]
    public void Discussions_whose_removal_failed_to_save_stay_in_memory_and_a_retry_removes_them_from_the_file()
    {
        using (var store = new StateStore(State, Repository))
        {
            store.Discussions["D1"] = new JsonObject { ["url"] = "https://github.com/owner/repository/discussions/1", ["sessionId"] = "S1" };
            store.Discussions["D2"] = new JsonObject { ["url"] = "https://github.com/owner/repository/discussions/2", ["sessionId"] = "S2" };
            store.Save();
            Directory.CreateDirectory(State + ".tmp"); // The next save cannot write its replacement file.

            var error = Record.Exception(() => store.Remove(["D1"]));

            Assert.True(error is IOException or UnauthorizedAccessException, $"Expected a failed save, not {error}");
            Assert.Equal(["D1", "D2"], store.Discussions.Select(entry => entry.Key).Order());
            Directory.Delete(State + ".tmp");
            store.Remove(["D1"]);
        }
        using var restarted = new StateStore(State, Repository);
        Assert.Equal(["D2"], restarted.Discussions.Select(entry => entry.Key));
    }

    // As a startup opens it: the lease is taken and the file read, then the App identity is checked and saved.
    Exception? Open(string recorded, string login = "test-app[bot]")
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(State, recorded);
        return Record.Exception(() =>
        {
            using var store = new StateStore(State, Repository);
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
        Assert.Equal("The node must be of type 'JsonObject'.", Assert.IsType<InvalidOperationException>(Open("[]")).Message);
        Assert.Null(Open("""{"version":1,"repository":"owner/repository","discussions":{}}"""));
    }

    [Fact]
    public void A_state_of_another_App_login_or_with_a_login_that_is_not_text_stops_startup()
    {
        Assert.StartsWith("State belongs to a different GitHub identity;", Assert.IsType<InvalidOperationException>(
            Open("""{"version":1,"repository":"owner/repository","discussions":{},"agentLogin":"other-app[bot]"}""")).Message);
        Assert.Equal("An element of type 'Number' cannot be converted to a 'System.String'.", Assert.IsType<InvalidOperationException>(
            Open("""{"version":1,"repository":"owner/repository","discussions":{},"agentLogin":7}""")).Message);
    }

    [Fact]
    public void A_state_whose_Discussion_records_are_not_an_object_passes_the_identity_check_and_keeps_its_keys_until_they_are_read()
    {
        Assert.Null(Open("""{"version":1,"repository":"owner/repository","discussions":[],"later":true}"""));

        using var store = new StateStore(State, Repository);
        Assert.ThrowsAny<Exception>(() => store.Discussions.Count);
        var saved = JsonNode.Parse(File.ReadAllText(State))!;
        Assert.Equal("[]", saved["discussions"]!.ToJsonString());
        Assert.Equal("test-app[bot]", saved["agentLogin"]!.GetValue<string>());
        Assert.True(saved["later"]!.GetValue<bool>());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
